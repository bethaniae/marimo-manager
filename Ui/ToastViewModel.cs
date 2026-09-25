using System;
using System.Threading.Tasks;
using Avalonia.Threading;

namespace MarimoLauncher.Ui;

/// <summary>One toast in the bottom-right stack; auto-dismisses after a while.</summary>
public sealed class ToastViewModel : ObservableObject
{
    private readonly MainViewModel _parent;
    private readonly int _durationMs;

    public ToastViewModel(MainViewModel parent, string message, bool isError)
    {
        _parent = parent;
        Message = message;
        IsError = isError;
        _durationMs = isError ? 10000 : 6000;
        CloseCommand = new RelayCommand(_ => _parent.Dismiss(this));
        ScheduleDismissal();
    }

    public string Message { get; }

    public bool IsError { get; }

    public RelayCommand CloseCommand { get; }

    private void ScheduleDismissal()
    {
        Dispatcher.UIThread.Post(async () =>
        {
            await Task.Delay(_durationMs);
            _parent.Dismiss(this);
        });
    }
}
