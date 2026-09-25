using System;
using System.IO;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Platform.Storage;

namespace MarimoLauncher.Ui;

public partial class MainWindow : Window
{
    /// <summary>Set when the daemon is actually shutting down (tray Quit).</summary>
    public static bool AcceptClose { get; set; }

    public MainViewModel? ViewModel => DataContext as MainViewModel;

    public MainWindow()
    {
        InitializeComponent();

        DataContext = new MainViewModel(this);

        Icon = new WindowIcon(AssetLoader.Open(new Uri("avares://MarimoLauncher/Assets/icon.ico")));

        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DropEvent, OnDragDrop);
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    private void OnDragDrop(object? sender, DragEventArgs e)
    {
        var files = e.DataTransfer.TryGetFiles();
        if (files is null)
        {
            return;
        }

        var paths = files
            .Select(f => f.TryGetLocalPath())
            .Where(p => p is not null)
            .Select(p => p!)
            .Where(File.Exists)
            .ToList();

        if (paths.Count == 0)
        {
            return;
        }

        var notebooks = paths.Where(p => p.EndsWith(".py", StringComparison.OrdinalIgnoreCase)).ToList();
        var skipped = paths.Count - notebooks.Count;

        if (notebooks.Count > 0)
        {
            ViewModel?.AddPaths(notebooks);
        }

        if (skipped > 0 && notebooks.Count == 0)
        {
            ViewModel?.SetStatus("That's not a marimo notebook — only .py files can be added.", isError: true);
        }
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (!AcceptClose)
        {
            // Hide to tray; the daemon keeps running.
            e.Cancel = true;
            Hide();
        }

        base.OnClosing(e);
    }
}
