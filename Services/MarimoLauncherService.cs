using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using MarimoLauncher.Storage;

namespace MarimoLauncher.Services;

public enum MarimoMode { RunOnly, Edit }

/// <summary>A notebook currently served by a marimo process the daemon started.</summary>
public sealed record RunningNotebook(string Path, int Port, MarimoMode Mode)
{
    public Process? Process { get; init; }

    public int ProcessId => Process?.Id ?? -1;

    /// <summary>Kills the marimo process (and its children).</summary>
    public void Stop()
    {
        if (Process is null || Process.HasExited)
        {
            return;
        }

        try
        {
            Process.Kill(entireProcessTree: true);
        }
        catch (Exception)
        {
            // Already gone.
        }
    }

    /// <summary>Page URL, filled once marimo reports it (includes the edit token when present).</summary>
    public string? Url { get; internal set; }

    public string FallbackUrl => $"http://localhost:{Port}";
}

    /// <summary>
    /// Starts marimo as an external process (edit or run mode, headless, on a
    /// port we pick) and tracks accurately whether a notebook is currently
    /// running. Readiness comes primarily from marimo's own "URL:" banner;
    /// the TCP port probe is only a fallback. The browser opens as soon as
    /// the page URL is known.
    /// </summary>
public sealed class MarimoLauncherService
{
    // Only trust marimo's own banner line (e.g. "➜  URL: http://localhost:2718").
    // A generic http regex would grab marketing links from the banner.
    private static readonly Regex UrlRegex = new(@"URL:\s*(\S+)", RegexOptions.Compiled);
    private static readonly TimeSpan ReadyTimeout = TimeSpan.FromSeconds(30);

    private readonly Dictionary<string, RunningNotebook> _running = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<string>> _outputTail = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Application-owned Python environment marimo runs from.</summary>
    public ManagedMarimoEnvironment PythonEnvironment { get; } = new();

    /// <summary>Friendly progress message for the UI status bar.</summary>
    public event Action<string>? StatusMessage;

    /// <summary>This launch failed before marimo could start.</summary>
    public event Action<string>? LaunchFailed;

    /// <summary>Raised whenever a notebook goes online / offline or gets its URL.</summary>
    public event Action? RunningChanged;

    public bool IsRunning(string path)
    {
        lock (_running)
        {
            return _running.ContainsKey(Normalize(path));
        }
    }

    public RunningNotebook? FindRunning(string path)
    {
        lock (_running)
        {
            return _running.TryGetValue(Normalize(path), out var info) ? info : null;
        }
    }

    /// <summary>Kills the marimo process serving the notebook, if any.</summary>
    public void Stop(string path)
    {
        var info = FindRunning(path);
        info?.Stop();
    }

    public void Launch(NotebookEntry entry, MarimoMode mode)
    {
        var key = Normalize(entry.Path);

        lock (_running)
        {
            if (_running.TryGetValue(key, out var already))
            {
                var url = already.Url ?? $"http://localhost:{already.Port}";
                OpenUrl(url);
                StatusMessage?.Invoke($"\"{entry.DisplayName}\" is already running — its page is open in your browser.");
                return;
            }
        }

        _ = StartOnceEnvironmentReadyAsync(entry, mode, key);
        return;
    }

    private async Task StartOnceEnvironmentReadyAsync(NotebookEntry entry, MarimoMode mode, string key)
    {
        string? marimoExe;
        try
        {
            marimoExe = await PythonEnvironment.EnsureReadyAsync();
        }
        catch (Exception ex)
        {
            marimoExe = null;
            StatusMessage?.Invoke($"Could not prepare the built-in marimo environment: {ex.Message}");
        }

        marimoExe ??= ResolveMarimoExecutable();

        var port = GetFreePort();

        var psi = new ProcessStartInfo
        {
            FileName = marimoExe,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            CreateNoWindow = !RuntimeInformation.IsOSPlatform(OSPlatform.Windows),
        };
        psi.ArgumentList.Add(mode == MarimoMode.Edit ? "edit" : "run");
        psi.ArgumentList.Add(entry.Path);
        psi.ArgumentList.Add("--headless");
        psi.ArgumentList.Add("--no-token");
        psi.ArgumentList.Add("-p");
        psi.ArgumentList.Add(port.ToString());

        // Serve the file browser from the notebook's own folder, not the
        // launcher app's directory.
        psi.WorkingDirectory = Path.GetDirectoryName(entry.Path);

        // marimo does not need the venv activated: its scripts already point
        // at the environment's own interpreter. But its sandbox feature looks
        // for `uv` (and tooling) on PATH, so the venv's bin dir must be there.
        var venvBin = Path.GetDirectoryName(marimoExe);
        if (venvBin is not null && Directory.Exists(venvBin))
        {
            var currentPath = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
            psi.Environment["PATH"] = $"{venvBin}{Path.PathSeparator}{currentPath}";
        }

        Process? process;
        try
        {
            process = Process.Start(psi);
        }
        catch (Exception ex)
        {
            LaunchFailed?.Invoke($"Could not start marimo: {ex.Message}");
            return;
        }

        if (process is null)
        {
            LaunchFailed?.Invoke("marimo did not start. Is it installed and on PATH?");
            return;
        }

        StatusMessage?.Invoke($"Starting \"{entry.DisplayName}\" — the notebook page will open in your browser in a moment.");

        RunningNotebook info;
        lock (_running)
        {
            info = new RunningNotebook(key, port, mode) { Process = process };
            _running[key] = info;
        }
        RunningChanged?.Invoke();

        process.EnableRaisingEvents = true;
        process.Exited += (_, _) => OnProcessExited(key);
        process.OutputDataReceived += (_, e) => OnMarimoLine(e.Data);
        process.ErrorDataReceived += (_, e) => OnMarimoLine(e.Data);
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        // marimo sometimes asks confirmation questions on stdin (e.g. "Run in
        // a sandboxed venv containing this notebook's dependencies [Y/n]?").
        // There is no console to answer on, which would hang forever, so we
        // always answer "yes" and keep the pipe open (closing it could surface
        // as EOF while marimo is still reading).
        _ = process.StandardInput.WriteLineAsync("y");

        _ = WatchReadyAsync();

        return;

        async Task WatchReadyAsync()
        {
            var fallbackVisible = false;
            var announced = 0;

            void AnnounceReady(bool viaFallback)
            {
                if (Interlocked.Exchange(ref announced, 1) == 0)
                {
                    StatusMessage?.Invoke($"\"{entry.DisplayName}\" is running at {info.Url ?? info.FallbackUrl}");
                    OpenUrl(info.Url ?? info.FallbackUrl);
                    _ = viaFallback;
                    RunningChanged?.Invoke();
                }
                else
                {
                    RunningChanged?.Invoke();
                }
            }

            // Ready as soon as either the stdout banner URL shows up
            // (authoritative) or the port is actually serving — whichever
            // comes first. Only ONE announce/browser open, whichever wins.
            while (true)
            {
                if (ProcessHasExited(process))
                {
                    Interlocked.Exchange(ref announced, 1); // disable any pending announce
                    HandleEarlyExit(info.Path, entry.DisplayName, info, process);
                    RunningChanged?.Invoke();
                    return;
                }

                if (info.Url is not null)
                {
                    AnnounceReady(false);
                    return;
                }

                if (!fallbackVisible && await IsPortOpenAsync(info.Port))
                {
                    fallbackVisible = true;
                    _ = Task.Run(async () =>
                    {
                        // Give the banner a moment to land first; it is the
                        // nicer URL (though with --no-token they are equal).
                        await Task.Delay(1500);
                        AnnounceReady(true);
                    });
                }

                await Task.Delay(300);
            }
        }

        void OnMarimoLine(string? line)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                return;
            }

            RecordOutput(key, line);

            var match = UrlRegex.Match(line);
            if (!match.Success)
            {
                return;
            }

            lock (_running)
            {
                if (!_running.TryGetValue(key, out var current) || current.Url is not null)
                {
                    return;
                }

                current.Url = match.Groups[1].Value;
            }
        }
    }

    private void RecordOutput(string key, string line)
    {
        lock (_running)
        {
            if (!_outputTail.TryGetValue(key, out var tail))
            {
                tail = new List<string>();
                _outputTail[key] = tail;
            }

            tail.Add(line.Trim());
            while (tail.Count > 8)
            {
                tail.RemoveAt(0);
            }
        }
    }

    private string OutputTailOf(string key)
    {
        lock (_running)
        {
            if (!_outputTail.TryGetValue(key, out var tail) || tail.Count == 0)
            {
                return string.Empty;
            }

            var lines = tail.Skip(Math.Max(0, tail.Count - 4))
                .Select(l => l.Length > 160 ? l[..160] + "…" : l);
            return string.Join(Environment.NewLine, lines);
        }
    }

    private void HandleEarlyExit(string key, string displayName, RunningNotebook info, Process process)
    {
        var tail = OutputTailOf(key);
        RemoveRunning(key);

        string? code = null;
        try
        {
            code = process.ExitCode.ToString();
        }
        catch (InvalidOperationException)
        {
        }

        // Served for a while, then closed: normal shutdown.
        if (info.Url is not null)
        {
            StatusMessage?.Invoke($"\"{displayName}\" was closed (exit code {code}).");
            return;
        }

        var message = $"\"{displayName}\" did not start (exit code {code ?? "?"}).";
        if (!string.IsNullOrWhiteSpace(tail))
        {
            message += "\n" + tail;
        }
        else
        {
            message += " The file may not be a valid marimo notebook; try `marimo edit <file>` in a terminal to see the error.";
        }

        LaunchFailed?.Invoke(message);
    }


    private void OnProcessExited(string key)
    {
        if (RemoveRunning(key))
        {
            RunningChanged?.Invoke();
        }
    }

    private bool RemoveRunning(string key)
    {
        lock (_running)
        {
            if (_running.Remove(key))
            {
                _outputTail.Remove(key);
                RunningChanged?.Invoke();
                return true;
            }

            return false;
        }
    }

    internal static bool ProcessHasExited(Process process)
    {
        try
        {
            process.Refresh();
            return process.HasExited;
        }
        catch (InvalidOperationException)
        {
            return true;
        }
    }

    private static async Task<bool> IsPortOpenAsync(int port, string host = "127.0.0.1")
    {
        using var client = new TcpClient();
        try
        {
            var connect = client.ConnectAsync(host, port);
            var timeout = Task.Delay(400);
            var finished = await Task.WhenAny(connect, timeout);
            return finished == connect && client.Connected;
        }
        catch
        {
            return false;
        }
    }

    private static string Normalize(string path)
    {
        try
        {
            return Path.GetFullPath(path).ToLowerInvariant();
        }
        catch (Exception)
        {
            return path.ToLowerInvariant();
        }
    }

    private static int GetFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }

    public static string ResolveMarimoExecutable()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            var local = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".local", "bin", "marimo");
            if (File.Exists(local))
            {
                return local;
            }
        }

        return RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "marimo.cmd" : "marimo";
    }

    public static void OpenUrl(string url)
    {
        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                Process.Start(new ProcessStartInfo("xdg-open", url) { UseShellExecute = true });
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                Process.Start("open", url);
            }
        }
        catch (Exception)
        {
            // Browser opening is best-effort.
        }
    }
}
