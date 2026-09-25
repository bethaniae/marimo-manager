using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using MarimoLauncher.Services;
using MarimoLauncher.Storage;

namespace MarimoLauncher.Ui;

public sealed class MainViewModel : ObservableObject
{
    private readonly NotebookRegistry _registry;
    private readonly MarimoLauncherService _launcher;
    private readonly Window _window;
    private readonly IStorageProvider? _storageProvider;

    public ObservableCollection<NotebookCardViewModel> Notebooks { get; } = new();

    public ObservableCollection<ToastViewModel> Toasts { get; } = new();

    public AsyncRelayCommand AddNotebookCommand { get; }

    public void SetStatus(string message, bool isError = false)
    {
        Dispatcher.UIThread.Post(() =>
        {
            Toasts.Add(new ToastViewModel(this, message, isError));
            OnPropertyChanged(nameof(HasToasts));
        });
    }

    public void Dismiss(ToastViewModel toast)
    {
        Toasts.Remove(toast);
        OnPropertyChanged(nameof(HasToasts));
    }

    public MainViewModel(Window window)
    {
        _window = window;
        _storageProvider = window.StorageProvider;
        _registry = new NotebookRegistry();
        _launcher = new MarimoLauncherService();
        _launcher.StatusMessage += message => SetStatus(message);
        _launcher.LaunchFailed += message => SetStatus(message, isError: true);
        _launcher.RunningChanged += OnRunningChanged;
        _launcher.PythonEnvironment.StatusMessage += message => SetStatus(message, isError: false);

        // Provision the built-in marimo environment in the background so the
        // first notebook launch does not have to wait for the full setup.
        _ = WarmUpEnvironmentAsync();

        AddNotebookCommand = new AsyncRelayCommand(AddByPickerAsync);

        LinuxDesktopIntegration.EnsureDesktopEntry();
        Reload();
    }

    public NotebookRegistry Registry => _registry;

    public MarimoLauncherService Launcher => _launcher;

    public bool HasNotebooks => Notebooks.Count > 0;

    public bool HasToasts => Toasts.Count > 0;

    private async Task WarmUpEnvironmentAsync()
    {
        try
        {
            await Task.Yield(); // let the UI paint before any setup work starts
            await _launcher.PythonEnvironment.EnsureReadyAsync();
        }
        catch (Exception)
        {
            // Failures are reported through PythonEnvironment.StatusMessage; a
            // system-wide marimo on PATH remains the fallback.
        }
    }

    private void OnRunningChanged()
        => Dispatcher.UIThread.Post(Reload);

    /// <summary>
    /// Called when a notebook path was pushed to the daemon (single instance,
    /// drag-and-drop alternative, or "Open as marimo notebook" from files).
    /// </summary>
    public void ActivateNotebook(string path)
    {
        try
        {
            path = Path.GetFullPath(path);
        }
        catch (Exception)
        {
            SetStatus($"Could not resolve path: {path}", isError: true);
            return;
        }

        if (!File.Exists(path))
        {
            SetStatus($"File no longer exists: {path}", isError: true);
            return;
        }

        if (!path.EndsWith(".py", StringComparison.OrdinalIgnoreCase))
        {
            SetStatus("Only .py files can be opened as marimo notebooks.", isError: true);
            return;
        }

        var entry = _registry.FindByPath(path);
        if (entry is null)
        {
            entry = _registry.AddOrUpdate(path);
            Reload();
            SetStatus($"Added \"{entry.DisplayName}\" to your notebooks.");
        }
        else
        {
            Reload();
            SetStatus($"\"{entry.DisplayName}\" is already in your list.");
        }
    }

    private async Task AddByPickerAsync()
    {
        if (_storageProvider is null)
        {
            return;
        }

        var results = await _storageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Select marimo notebook (.py)",
            AllowMultiple = true,
            FileTypeFilter = new List<FilePickerFileType>
            {
                new("Python file") { Patterns = new[] { "*.py" } },
            },
        });

        var paths = results
            .Select(f => f.TryGetLocalPath())
            .Where(p => p is not null)
            .Select(p => p!)
            .ToList();

        AddPaths(paths);
    }

    public void AddPaths(IReadOnlyList<string> paths)
    {
        var notebookPaths = paths
            .Where(p => p.EndsWith(".py", StringComparison.OrdinalIgnoreCase))
            .ToList();
        var skipped = paths.Count - notebookPaths.Count;

        if (skipped > 0)
        {
            SetStatus($"{skipped} file(s) skipped — only .py notebooks can be added.", isError: true);
            return;
        }

        if (notebookPaths.Count == 0)
        {
            return;
        }

        var added = 0;
        foreach (var path in notebookPaths)
        {
            if (_registry.FindByPath(path) is null)
            {
                _registry.AddOrUpdate(path);
                added++;
            }
        }

        Reload();

        if (added > 0)
        {
            SetStatus($"Added {added} notebook{(added == 1 ? "" : "s")}.");
        }
        else
        {
            SetStatus("Those notebooks are already in your list.");
        }
    }

    public void LaunchEntry(NotebookCardViewModel card, MarimoMode mode)
    {
        if (!File.Exists(card.Entry.Path))
        {
            SetStatus($"File no longer exists: {card.Entry.Path}", isError: true);
            return;
        }

        _launcher.Launch(card.Entry, mode);
        _registry.Touch(card.Entry);
        Reload();
    }

    public void CopyLink(NotebookCardViewModel card)
    {
        var url = card.RunningInfo?.Url ?? card.RunningInfo?.FallbackUrl;
        if (string.IsNullOrEmpty(url))
        {
            return;
        }

        var clipboard = TopLevel.GetTopLevel(_window)?.Clipboard;
        if (clipboard is null)
        {
            SetStatus($"No clipboard available. The URL is {url}.");
            return;
        }

        var transfer = new Avalonia.Input.DataTransfer();
        transfer.Add(Avalonia.Input.DataTransferItem.CreateText(url));
        clipboard.SetDataAsync(transfer);
        SetStatus($"Copied \"{card.Entry.DisplayName}\" link: {url}");
    }

    public void OpenLink(NotebookCardViewModel card)
    {
        var info = card.RunningInfo;
        if (info is null)
        {
            return;
        }

        var url = info.Url ?? info.FallbackUrl;
        MarimoLauncherService.OpenUrl(url);
        SetStatus($"Opened \"{card.Entry.DisplayName}\" in your browser: {url}");
    }

    public void StopEntry(NotebookCardViewModel card)
    {
        if (!card.IsRunning)
        {
            return;
        }

        _launcher.Stop(card.Entry.Path);
        SetStatus($"Stopped \"{card.Entry.DisplayName}\". The marimo process was killed.");
    }

    public void RemoveEntry(NotebookCardViewModel card)
    {
        _registry.Remove(card.Entry);
        Reload();
        SetStatus($"Removed \"{card.Entry.DisplayName}\" from the list. The file was not touched.");
    }

    private void Reload()
    {
        var entries = _registry.Entries;

        // Reuse existing card VMs so nothing user-visible resets across reloads.
        var byPath = Notebooks
            .GroupBy(c => c.Entry.Path, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        Notebooks.Clear();
        foreach (var entry in entries)
        {
            if (!byPath.TryGetValue(entry.Path, out var card))
            {
                card = new NotebookCardViewModel(entry, this);
            }

            Notebooks.Add(card);
            card.RefreshAll();
        }

        OnPropertyChanged(nameof(HasNotebooks));
    }
}
