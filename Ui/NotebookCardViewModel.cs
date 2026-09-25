using System;
using System.IO;
using System.Linq;
using System.Windows.Input;
using MarimoLauncher.Services;
using MarimoLauncher.Storage;

namespace MarimoLauncher.Ui;

/// <summary>View model for one notebook card in the main list.</summary>
public sealed class NotebookCardViewModel : ObservableObject
{
    private readonly NotebookEntry _entry;
    private readonly MainViewModel _parent;

    private bool _isRenaming;
    private string _renameText = string.Empty;

    public NotebookCardViewModel(NotebookEntry entry, MainViewModel parent)
    {
        _entry = entry;
        _parent = parent;
        LaunchRunCommand = new RelayCommand(_ => _parent.LaunchEntry(this, MarimoMode.RunOnly));
        LaunchEditCommand = new RelayCommand(_ => _parent.LaunchEntry(this, MarimoMode.Edit));
        CopyLinkCommand = new RelayCommand(_ => _parent.CopyLink(this));
        OpenLinkCommand = new RelayCommand(_ => _parent.OpenLink(this));
        StopCommand = new RelayCommand(_ => _parent.StopEntry(this));
        BeginRenameCommand = new RelayCommand(_ => BeginRename());
        ConfirmRenameCommand = new RelayCommand(_ => ConfirmRename());
        CancelRenameCommand = new RelayCommand(_ => CancelRename());
        RemoveCommand = new RelayCommand(_ => _parent.RemoveEntry(this));
    }

    public NotebookEntry Entry => _entry;

    public ICommand LaunchRunCommand { get; }

    public ICommand LaunchEditCommand { get; }

    public ICommand CopyLinkCommand { get; }

    public ICommand OpenLinkCommand { get; }

    public ICommand StopCommand { get; }

    public ICommand BeginRenameCommand { get; }

    public ICommand ConfirmRenameCommand { get; }

    public ICommand CancelRenameCommand { get; }

    public ICommand RemoveCommand { get; }

    public string DisplayName => _entry.DisplayName;

    public string PathText => _entry.Path;

    public string LastUsedText => FormatLastUsed(_entry.LastUsedAt);

    public bool FileMissing => !File.Exists(_entry.Path);

    public bool IsRunning => _parent.Launcher.IsRunning(_entry.Path);

    public RunningNotebook? RunningInfo => _parent.Launcher.FindRunning(_entry.Path);

    public bool HasReadyUrl => RunningInfo?.Url is not null;

    public string RunningUrl => RunningInfo?.Url ?? RunningInfo?.FallbackUrl ?? string.Empty;

    /// <summary>Card status line while the notebook is being served.</summary>
    public string RunningStateText
    {
        get
        {
            var info = RunningInfo;
            if (info is null)
            {
                return string.Empty;
            }

            return info.Url is not null
                ? $"Running at {info.Url}"
                : $"Starting marimo (process {info.ProcessId}, port {info.Port})…";
        }
    }

    public bool IsRenaming
    {
        get => _isRenaming;
        private set => SetProperty(ref _isRenaming, value);
    }

    public string RenameText
    {
        get => _renameText;
        set => SetProperty(ref _renameText, value);
    }

    private void BeginRename()
    {
        RenameText = _entry.DisplayName;
        IsRenaming = true;
    }

    private void ConfirmRename()
    {
        if (!_isRenaming)
        {
            return;
        }

        var name = RenameText?.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            _parent.SetStatus("Display name cannot be empty.", isError: true);
            return;
        }

        _parent.Registry.Rename(_entry, name!);
        IsRenaming = false;
        _parent.SetStatus($"Renamed to \"{name}\" (the file on disk is unchanged).");
        RefreshAll();
    }

    private void CancelRename()
    {
        IsRenaming = false;
    }

    public void RefreshAll()
    {
        OnPropertyChanged(nameof(DisplayName));
        OnPropertyChanged(nameof(PathText));
        OnPropertyChanged(nameof(LastUsedText));
        OnPropertyChanged(nameof(FileMissing));
        OnPropertyChanged(nameof(IsRunning));
        OnPropertyChanged(nameof(HasReadyUrl));
        OnPropertyChanged(nameof(RunningUrl));
        OnPropertyChanged(nameof(RunningStateText));
        OnPropertyChanged(nameof(RunningInfo));
    }

    private static string FormatLastUsed(DateTime lastUsed)
        => lastUsed == DateTime.MinValue
            ? "Never opened"
            : "Last opened " + lastUsed.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
}
