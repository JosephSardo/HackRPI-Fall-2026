using System;
using CommunityToolkit.Mvvm.ComponentModel;

namespace PathFinder.ViewModels;

/// <summary>Owns which screen is showing. Screens are view models; ViewLocator finds their views.</summary>
public partial class MainWindowViewModel : ViewModelBase, IDisposable
{
    readonly LobbyViewModel _lobby;

    [ObservableProperty] private ViewModelBase _currentScreen;

    public MainWindowViewModel()
    {
        _lobby = new LobbyViewModel(ShowScreen);
        _currentScreen = _lobby;
    }

    /// <summary>Shows a round screen, or the lobby when given null.</summary>
    void ShowScreen(ViewModelBase? screen) => CurrentScreen = screen ?? _lobby;

    // Round screens own timers; stop them when they're replaced. The lobby lives for the whole session.
    partial void OnCurrentScreenChanged(ViewModelBase? oldValue, ViewModelBase newValue)
    {
        if (oldValue != _lobby && oldValue != newValue) (oldValue as IDisposable)?.Dispose();
    }

    public void Dispose() => _lobby.Dispose();
}
