using System;
using System.IO;
using System.Runtime.InteropServices;

namespace MarimoLauncher.Services;

/// <summary>
/// On Linux, registers a freedesktop .desktop entry so a right-click on a
/// .py file offers "Open With → Marimo Launcher" (hence "Open as marimo
/// notebook"). Writing it teaches the file manager about the launcher;
/// notebook files themselves are never touched.
/// </summary>
public static class LinuxDesktopIntegration
{
    public static void EnsureDesktopEntry()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            return;
        }

        try
        {
            var exePath = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exePath) || !File.Exists(exePath))
            {
                return;
            }

            var appsDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".local", "share", "applications");
            Directory.CreateDirectory(appsDir);

            var target = Path.Combine(appsDir, "marimo-launcher.desktop");
            var content = $"""
                [Desktop Entry]
                Type=Application
                Name=Marimo Launcher (open as notebook)
                Comment=Open this file as a marimo notebook with the Marimo Launcher
                Exec={exePath} %f
                TryExec={exePath}
                Terminal=false
                NoDisplay=true
                MimeType=text/x-python;application/x-python;application/x-python3;
                Categories=Development;Science;
                StartupNotify=false
                """;

            if (!File.Exists(target) || File.ReadAllText(target) != content)
            {
                File.WriteAllText(target, content);
            }
        }
        catch (Exception)
        {
            // Desktop integration is best-effort.
        }
    }
}
