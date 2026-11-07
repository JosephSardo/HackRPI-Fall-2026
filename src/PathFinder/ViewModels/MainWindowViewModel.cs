using System;
using CommunityToolkit.Mvvm.ComponentModel;

namespace PathFinder.ViewModels;

/// <summary>Owns which screen is showing. Screens are view models; ViewLocator finds their views.</summary>
public partial class MainWindowViewModel : ViewModelBase, IDisposable
{
    [ObservableProperty] private ViewModelBase _currentScreen = new LobbyViewModel();

    public void Dispose() => (CurrentScreen as IDisposable)?.Dispose();
}
