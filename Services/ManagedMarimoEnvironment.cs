using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace MarimoLauncher.Services;

/// <summary>
/// Manages an application-owned Python environment so users never have to
/// install Python packages themselves. On first use the service finds a
/// suitable system Python, creates a private virtual environment inside the
/// app's data folder, and installs marimo into it. Every notebook is then
/// served from that environment, no matter what else the user has installed.
/// </summary>
public sealed class ManagedMarimoEnvironment
{
    private static readonly SemaphoreSlim SetupLock = new(1, 1);

    private readonly string _venvDir;
    private readonly string _markerFile;

    public ManagedMarimoEnvironment()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var root = Path.Combine(appData, "MarimoLauncher");
        _venvDir = Path.Combine(root, "env");
        _markerFile = Path.Combine(root, "env-ready.txt");
    }

    /// <summary>Root directory of the managed virtual environment.</summary>
    public string VenvDir => _venvDir;

    /// <summary>The venv's python interpreter, or null when not created yet.</summary>
    public string? VenvPython => PythonExecutablePath(_venvDir) is { } p && File.Exists(p) ? p : null;

    /// <summary>The managed marimo executable, or null when not installed yet.</summary>
    public string? MarimoExecutable => MarimoExecutablePath(_venvDir) is { } p && File.Exists(p) ? p : null;

    /// <summary>True when setup already completed successfully (at least once).</summary>
    public bool IsMarkedReady => File.Exists(_markerFile);

    /// <summary>
    /// Makes sure the managed environment exists and contains marimo, launching
    /// the one-time setup when needed. Returns the marimo executable to use, or
    /// null when no environment could be provisioned (caller should fall back
    /// to a system-wide marimo on PATH).
    /// </summary>
    public async Task<string?> EnsureReadyAsync()
    {
        if (MarimoExecutable is not null)
        {
            return MarimoExecutable;
        }

        await SetupLock.WaitAsync();
        try
        {
            // Another task may have finished setup while we waited.
            if (MarimoExecutable is not null)
            {
                return MarimoExecutable;
            }

            return await InstallAsync();
        }
        finally
        {
            SetupLock.Release();
        }
    }

    private async Task<string?> InstallAsync()
    {
        var basePython = FindBasePython();
        if (basePython is null)
        {
            return null;
        }

        if (VenvPython is null)
        {
            // A broken half-created venv would block re-creation.
            try
            {
                if (Directory.Exists(_venvDir))
                {
                    Directory.Delete(_venvDir, recursive: true);
                }
            }
            catch (Exception)
            {
                // Best effort; creation below will report real errors.
            }
        }

        var steps = VenvPython is null
            ? new[] { ("Creating a private Python environment…", CreateVenvAsync(basePython)),
                      ("Installing marimo (this can take a minute)…", InstallMarimoAsync()) }
            : new[] { ("Installing marimo (this can take a minute)…", InstallMarimoAsync()) };

        foreach (var (message, step) in steps)
        {
            StatusMessage?.Invoke(message);
            if (!await step)
            {
                return null;
            }
        }

        if (MarimoExecutable is null)
        {
            return null;
        }

        try
        {
            await File.WriteAllTextAsync(_markerFile, DateTime.UtcNow.ToString("O"));
        }
        catch (Exception)
        {
            // The marker is only an optimization for later startups.
        }

        return MarimoExecutable;
    }

    private async Task<bool> CreateVenvAsync(string basePython)
    {
        var psi = PythonProcess(basePython, "-m", "venv", _venvDir);
        return await RunQuietlyAsync(psi, "Could not create a Python environment");
    }

    private async Task<bool> InstallMarimoAsync()
    {
        var python = VenvPython;
        if (python is null)
        {
            return false;
        }

        var psi = PythonProcess(python, "-m", "pip", "install", "--disable-pip-version-check", "marimo");
        return await RunQuietlyAsync(psi, "Could not install marimo");
    }

    private static ProcessStartInfo PythonProcess(string python, params string[] args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = python,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in args)
        {
            psi.ArgumentList.Add(arg);
        }

        return psi;
    }

    private async Task<bool> RunQuietlyAsync(ProcessStartInfo psi, string errorPrefix)
    {
        psi.RedirectStandardError = true;
        using var process = Process.Start(psi);
        if (process is null)
        {
            return false;
        }

        var stderr = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        if (process.ExitCode == 0)
        {
            return true;
        }

        StatusMessage?.Invoke($"{errorPrefix} (exit code {process.ExitCode}).");
        if (!string.IsNullOrWhiteSpace(stderr))
        {
            var lines = stderr.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(l => l.TrimEnd())
                .Where(l => l.Length > 0)
                .TakeLast(3);
            StatusMessage?.Invoke(string.Join(Environment.NewLine, lines));
        }

        return false;
    }

    /// <summary>
    /// Finds a Python interpreter we may use to build the environment. Prefers
    /// CPython 3 with the venv module; never touches a user's default config.
    /// </summary>
    private static string? FindBasePython()
    {
        foreach (var candidate in CandidatePythons())
        {
            var located = File.Exists(candidate) ? candidate : TryLocateOnPath(candidate);
            if (located is not null && ProbePython(located))
            {
                return located;
            }
        }

        return null;
    }

    private static string? TryLocateOnPath(string name)
    {
        var pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(pathEnv))
        {
            return null;
        }

        var extensions = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? new[] { ".exe", ".cmd", "" }
            : new[] { "" };

        return pathEnv
            .Split(Path.PathSeparator)
            .Select(dir => extensions
                .Select(ext => Path.Combine(dir.Trim(), name + ext))
                .FirstOrDefault(File.Exists))
            .FirstOrDefault(p => p is not null);
    }

    private static string[] CandidatePythons()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return new[] { "python3", "python", "py" };
        }

        // Prefer explicit versioned interpreters, then the generic names.
        return new[] { "python3.14", "python3.13", "python3.12", "python3.11", "python3.10", "python3", "python" };
    }

    /// <summary>Check both that the interpreter runs and that venv is usable.</summary>
    private static bool ProbePython(string python)
    {
        try
        {
            var psi = PythonProcess(python, "-c", "import sys, venv; print(sys.version_info >= (3, 10) and not sys.platform.startswith('freebsd'))");
            psi.RedirectStandardOutput = true;

            using var process = Process.Start(psi);
            if (process is null)
            {
                return false;
            }

            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(10_000);

            return process.ExitCode == 0 && output.Trim().EndsWith("True", StringComparison.Ordinal);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static string? PythonExecutablePath(string venvDir)
    {
        return RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? Path.Combine(venvDir, "Scripts", "python.exe")
            : Path.Combine(venvDir, "bin", "python3");
    }

    private static string? MarimoExecutablePath(string venvDir)
    {
        return RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? Path.Combine(venvDir, "Scripts", "marimo.exe")
            : Path.Combine(venvDir, "bin", "marimo");
    }

    /// <summary>Friendly progress / error messages for the UI status bar.</summary>
    public event Action<string>? StatusMessage;

}
