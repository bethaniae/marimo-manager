using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;

namespace MarimoLauncher.Ui;

public partial class NotebookCard : UserControl
{
    public NotebookCard()
    {
        InitializeComponent();
    }

    private void OnDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is not NotebookCardViewModel vm || vm.IsRenaming)
        {
            return;
        }

        if (vm.IsRunning)
        {
            if (vm.CopyLinkCommand.CanExecute(null))
            {
                vm.CopyLinkCommand.Execute(null);
            }
        }
        else if (vm.LaunchRunCommand.CanExecute(null))
        {
            vm.LaunchRunCommand.Execute(null);
        }
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }
}
