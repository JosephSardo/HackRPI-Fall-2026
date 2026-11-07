using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using PathFinder.ViewModels;

namespace PathFinder.Views;

public partial class LobbyView : UserControl
{
    public LobbyView()
    {
        InitializeComponent();
    }

    // Clipboard access needs the window, so this lives in the view rather than the view model.
    async void CopyGameCode_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is LobbyViewModel { GameCode: { } code } && TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
            await clipboard.SetTextAsync(code);
    }
}
