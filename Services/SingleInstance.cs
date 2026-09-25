using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace MarimoLauncher.Services;

/// <summary>
/// Lets a second invocation of the launcher (e.g. "Open as marimo notebook"
/// on a .py file) hand the notebook path to the already-running daemon and
/// exit, so only one instance ever runs.
/// </summary>
public static class SingleInstance
{
    private const int Port = 42116;

    /// <summary>Raised for every forwarded request. null means "just show" (tray click).</summary>
    public static event Action<string?>? NotebookRequested;

    public static void ForwardOrListen(string? notebookPath)
    {
        if (notebookPath is not null && TryForward(notebookPath))
        {
            Environment.Exit(0);
        }

        StartListening();
    }

    private static bool TryForward(string path)
    {
        try
        {
            using var client = new TcpClient();
            client.Connect(IPAddress.Loopback, Port);
            var bytes = Encoding.UTF8.GetBytes(path + "\n");
            using var stream = client.GetStream();
            stream.Write(bytes, 0, bytes.Length);
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
        catch (IOException)
        {
            return false;
        }
    }

    public static void StartListening()
    {
        var listener = new TcpListener(IPAddress.Loopback, Port);
        try
        {
            listener.Start();
        }
        catch (SocketException)
        {
            // Port taken right after our forward failed; nothing sane to do.
            return;
        }

        var thread = new Thread(AcceptLoop)
        {
            IsBackground = true,
            Name = "SingleInstanceListener",
        };
        thread.Start(listener);
    }

    private static void AcceptLoop(object? state)
    {
        var listener = (TcpListener)state!;
        while (true)
        {
            TcpClient client;
            try
            {
                client = listener.AcceptTcpClient();
            }
            catch (SocketException)
            {
                return;
            }

            using (client)
            {
                try
                {
                    using var stream = client.GetStream();
                    using var reader = new StreamReader(stream, Encoding.UTF8);
                    var line = reader.ReadLine();
                    var path = string.IsNullOrWhiteSpace(line) ? null : line;
                    NotebookRequested?.Invoke(path);
                }
                catch (IOException)
                {
                    // Malformed caller; ignore.
                }
            }
        }
    }
}
