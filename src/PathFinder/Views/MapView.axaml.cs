using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Mapsui;
using Mapsui.Manipulations;
using PathFinder.Mapping;
using PathFinder.ViewModels;

namespace PathFinder.Views;

/// <summary>
/// Hosts the Mapsui control and forwards input to <see cref="MapViewModel"/>. Mapsui already
/// handles dragging and wheel or pinch zoom; this adds keyboard controls and full screen.
/// </summary>
public partial class MapView : UserControl
{
    MapViewModel? _viewModel;

    public MapView()
    {
        InitializeComponent();
        MapControl.MapTapped += OnMapTapped;
        MapControl.MapPointerMoved += OnMapPointerMoved;
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        _viewModel = DataContext as MapViewModel;
        MapControl.Map = _viewModel?.Map ?? new Map();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Focus(); // so keyboard shortcuts work without clicking the map first
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        // Leaving the map shouldn't leave the window stuck in full screen.
        if (TopLevel.GetTopLevel(this) is Window { WindowState: WindowState.FullScreen } window)
            window.WindowState = WindowState.Normal;
        base.OnDetachedFromVisualTree(e);
    }

    void OnMapTapped(object? sender, MapEventArgs e)
    {
        if (_viewModel is null) return;
        var isDoubleTap = e.GestureType == GestureType.DoubleTap;
        e.Handled = _viewModel.OnMapTapped(WebMercator.ToGeo(e.WorldPosition), isDoubleTap);
        Focus(); // clicking the map gives keyboard focus back to the view
    }

    void OnMapPointerMoved(object? sender, MapEventArgs e) =>
        _viewModel?.OnPointerMoved(WebMercator.ToGeo(e.WorldPosition));

    /// <summary>
    /// Keyboard controls: arrows/WASD pan, +/- zoom, Home shows the world, F11 toggles full screen.
    /// Add or change shortcuts here.
    /// </summary>
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (_viewModel is not { } vm || e.Handled) return;

        const double step = 0.25; // fraction of the screen per arrow press
        var camera = vm.Camera;
        e.Handled = true;
        switch (e.Key)
        {
            case Key.Left or Key.A: camera.PanBy(-step, 0); break;
            case Key.Right or Key.D: camera.PanBy(step, 0); break;
            case Key.Up or Key.W: camera.PanBy(0, step); break;
            case Key.Down or Key.S: camera.PanBy(0, -step); break;
            case Key.OemPlus or Key.Add: camera.ZoomIn(); break;
            case Key.OemMinus or Key.Subtract: camera.ZoomOut(); break;
            case Key.Home: camera.ShowWholeWorld(); break;
            case Key.F11: ToggleFullScreen(); break;
            case Key.Escape when IsFullScreen: ToggleFullScreen(); break;
            default: e.Handled = false; break;
        }
    }

    bool IsFullScreen => TopLevel.GetTopLevel(this) is Window { WindowState: WindowState.FullScreen };

    void ToggleFullScreen()
    {
        if (TopLevel.GetTopLevel(this) is not Window window) return;
        window.WindowState = window.WindowState == WindowState.FullScreen ? WindowState.Normal : WindowState.FullScreen;
    }
}
