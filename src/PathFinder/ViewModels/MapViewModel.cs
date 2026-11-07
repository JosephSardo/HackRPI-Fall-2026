using System;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Mapsui;
using Mapsui.Layers;
using Mapsui.Styles;
using PathFinder.Mapping;

namespace PathFinder.ViewModels;

/// <summary>
/// A full-window satellite map of the world. Dragging pans and the scroll wheel zooms out of the box.
/// Put zoom and pan restrictions in <see cref="Rules"/>, camera moves in <see cref="Camera"/>,
/// and reactions to the player's input in the On... methods below.
/// </summary>
public partial class MapViewModel : ViewModelBase, IDisposable
{
    readonly Action? _back;
    readonly Action<GeoPoint>? _confirm;
    readonly MemoryLayer _selectionLayer = new("Selection")
    {
        Style = new SymbolStyle
        {
            SymbolScale = 0.6,
            Fill = new Brush(Color.FromArgb(255, 230, 60, 40)),
            Outline = new Pen(Color.White, 3),
        },
    };

    /// <param name="back">Called by the Back button; the button is hidden when null.</param>
    /// <param name="start">Where to center the map at first; the whole world when null.</param>
    /// <param name="startZoom">Zoom level to start at, used only with <paramref name="start"/>.</param>
    /// <param name="confirm">
    /// Turns on location picking: double-clicking sets <see cref="Coordinates"/>, and the OK button
    /// passes them here. Null for a plain map where double-click just zooms.
    /// </param>
    public MapViewModel(Action? back = null, GeoPoint? start = null, double startZoom = 17,
                        Action<GeoPoint>? confirm = null)
    {
        _back = back;
        _confirm = confirm;

        // Dark backdrop past the edge of the world, so white overlays stay readable there.
        Map = new Map { BackColor = Color.FromArgb(255, 16, 24, 32) };
        Map.Layers.Add(SatelliteLayer.Create());
        Map.Layers.Add(_selectionLayer);
        Map.Navigator.Limiter = Rules;

        Camera = new MapCamera(Map.Navigator);
        Camera.Changed += UpdateReadout;

        // Position the map once the control knows its size; anything earlier gets overridden by
        // Mapsui's own first-layout positioning.
        void Initialize(object? sender, EventArgs e)
        {
            Map.ViewportInitialized -= Initialize;
            if (start is { } point) Camera.CenterOn(point, startZoom, animate: false);
            else Camera.ShowWholeWorld(animate: false);
        }
        Map.ViewportInitialized += Initialize;
    }

    /// <summary>The Mapsui map. Add layers here, e.g. streets and intersections drawn over the imagery.</summary>
    public Map Map { get; }

    /// <summary>Moves and zooms the map from code.</summary>
    public MapCamera Camera { get; }

    /// <summary>Zoom limits and the optional play area. Applies to every move, by the player or by code.</summary>
    public MapNavigationRules Rules { get; } = new();

    public string Attribution => SatelliteLayer.Attribution;
    public bool CanGoBack => _back is not null;

    /// <summary>Center coordinates and zoom, shown in the corner.</summary>
    [ObservableProperty] private string _readout = "";

    /// <summary>Coordinates under the mouse pointer.</summary>
    [ObservableProperty] private string _pointerReadout = "";

    // ---- Location picking (only when a confirm callback was given) ----

    /// <summary>The point the player chose by double-clicking, or null if they haven't yet.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanConfirm), nameof(CoordinatesText))]
    [NotifyCanExecuteChangedFor(nameof(ConfirmCommand))]
    private GeoPoint? _coordinates;

    /// <summary>True when this map is being used to pick a location.</summary>
    public bool IsPicking => _confirm is not null;

    /// <summary>Shows the OK button: a point has been chosen and there's somewhere to send it.</summary>
    public bool CanConfirm => IsPicking && Coordinates is not null;

    public string CoordinatesText => Coordinates?.ToString() ?? "";

    /// <summary>Moves the marker to the chosen point (or removes it).</summary>
    partial void OnCoordinatesChanged(GeoPoint? value)
    {
        _selectionLayer.Features = value is { } point ? [new PointFeature(WebMercator.ToWorld(point))] : [];
        _selectionLayer.DataHasChanged();
    }

    [RelayCommand(CanExecute = nameof(CanConfirm))]
    private void Confirm()
    {
        if (Coordinates is { } point) _confirm?.Invoke(point);
    }

    [RelayCommand] private void ZoomIn() => Camera.ZoomIn();
    [RelayCommand] private void ZoomOut() => Camera.ZoomOut();
    [RelayCommand] private void ShowWholeWorld() => Camera.ShowWholeWorld();
    [RelayCommand] private void Back() => _back?.Invoke();

    // ---- Player input. The view forwards these from the map control. ----

    /// <summary>The player clicked or tapped the map. Return true to stop Mapsui's default handling.</summary>
    public bool OnMapTapped(GeoPoint point, bool isDoubleTap)
    {
        if (isDoubleTap && IsPicking)
        {
            Coordinates = point;
            return true; // choose the point instead of Mapsui's default double-click zoom
        }

        // Hook for other game logic, e.g. pick the street nearest to the click.
        return false;
    }

    /// <summary>The pointer moved over the map (without necessarily dragging).</summary>
    public void OnPointerMoved(GeoPoint point) => PointerReadout = point.ToString();

    void UpdateReadout() => Readout = $"{Camera.Center}  ·  zoom {Camera.Zoom:0.0}";

    public void Dispose()
    {
        Camera.Changed -= UpdateReadout;
        Camera.Dispose();
        Map.Dispose();
    }
}
