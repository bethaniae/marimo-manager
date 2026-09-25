using System;
using System.Collections.Generic;
using MarimoLauncher.Services;
using MarimoLauncher.Storage;
using MarimoLauncher.Ui;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;

namespace MarimoLauncher;

public class App : Application
{
    private const string AppTitle = "Marimo Launcher";

    private static MainWindow? _window;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        SingleInstance.NotebookRequested += PostNotebookRequest;

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // Daemon behaviour: closing the window hides to tray; exit only
            // via the tray menu's Quit.
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            desktop.ShutdownRequested += (_, _) => MainWindow.AcceptClose = true;

            _window = new MainWindow();
            desktop.MainWindow = _window;

            RegisterTray(desktop);

            var notebook = Program.FindNotebookPath(desktop.Args ?? Array.Empty<string>());
            if (notebook is not null)
            {
                PostNotebookRequest(notebook);
            }
            else if (!NotebookRegistry.StoreExists())
            {
                // Very first launch ever: show the window once so the user
                // sees the app; from then on it lives in the tray.
                _window.Show();
            }
        }

        base.OnFrameworkInitializationCompleted();
    }

    public static void PostNotebookRequest(string? path)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (_window is null)
            {
                return;
            }

            if (path is not null)
            {
                _window.ViewModel?.ActivateNotebook(path);
            }

            _window.Show();
            _window.Activate();
        });
    }

    private static void RegisterTray(IClassicDesktopStyleApplicationLifetime desktop)
    {
        var open = new NativeMenuItem("Open Marimo Launcher");
        open.Click += (_, _) => PostNotebookRequest(null);

        var quit = new NativeMenuItem("Quit");
        quit.Click += (_, _) =>
        {
            MainWindow.AcceptClose = true;
            desktop.Shutdown();
        };

        var menu = new NativeMenu();
        menu.Items.Add(open);
        menu.Items.Add(new NativeMenuItemSeparator());
        menu.Items.Add(quit);

        var icons = new TrayIcons();
        icons.Add(new TrayIcon
        {
            Icon = LoadTrayIcon(),
            ToolTipText = AppTitle,
            Menu = menu,
        });

        TrayIcon.SetIcons(Application.Current!, icons);
    }

    private static WindowIcon? LoadTrayIcon()
    {
        try
        {
            using var stream = AssetLoader.Open(new Uri("avares://MarimoLauncher/Assets/icon.ico"));
            var bitmap = new Bitmap(stream);
            return new WindowIcon(bitmap);
        }
        catch (Exception)
        {
            return null;
        }
    }
}
