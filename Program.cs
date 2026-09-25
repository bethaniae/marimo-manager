using System;
using System.IO;
using Avalonia;
using MarimoLauncher.Services;

namespace MarimoLauncher;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        var notebook = FindNotebookPath(args);
        SingleInstance.ForwardOrListen(notebook);
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    internal static string? FindNotebookPath(string[] args)
    {
        foreach (var arg in args)
        {
            if (!arg.EndsWith(".py", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            try
            {
                var full = Path.GetFullPath(arg);
                if (File.Exists(full))
                {
                    return full;
                }
            }
            catch
            {
                // fall through
            }
        }

        return null;
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
