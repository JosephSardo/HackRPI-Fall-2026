using System;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Mapsui;
using Mapsui.Layers;
using Mapsui.Nts;
using NetTopologySuite.Geometries;
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
    readonly Action<GeoPoint, double>? _confirm;
    readonly MemoryLayer _selectionLayer = new("Selection")
    {
        Style = new SymbolStyle
        {
            SymbolScale = 0.6,
            Fill = new Brush(Color.FromArgb(255, 230, 60, 40)),
            Outline = new Pen(Color.White, 3),
        },
    };
    // The red circle showing the area a map would be loaded from: it follows the pointer until a
    // point is chosen, then stays on that point (the slider still resizes it).
    readonly MemoryLayer _radiusLayer = new("Radius")
    {
        Style = new VectorStyle
        {
            Fill = new Brush(Color.FromArgb(40, 255, 0, 0)),
            Outline = new Pen(Color.FromArgb(255, 230, 30, 30), 2),
        },
    };
    GeoPoint? _pointer;

    /// <summary>Range of the radius slider, in meters. Bigger areas take longer to download.</summary>
    public const double MinRadiusMeters = 100, MaxRadiusMeters = 10_000;

    /// <param name="back">Called by the Back button; the button is hidden when null.</param>
    /// <param name="start">Where to center the map at first; the whole world when null.</param>
    /// <param name="startZoom">Zoom level to start at, used only with <paramref name="start"/>.</param>
    /// <param name="confirm">
    /// Turns on location picking: double-clicking sets <see cref="Coordinates"/>, and the OK button
    /// and <see cref="RadiusMeters"/> here. Null for a plain map where double-click just zooms.
    /// </param>
    /// <param name="radiusMeters">Where the radius slider starts when picking.</param>
    public MapViewModel(Action? back = null, GeoPoint? start = null, double startZoom = 17,
                        Action<GeoPoint, double>? confirm = null, double radiusMeters = 300)
    {
        _back = back;
        _confirm = confirm;

        // Dark backdrop past the edge of the world, so white overlays stay readable there.
        Map = new Map { BackColor = Color.FromArgb(255, 16, 24, 32) };
        Map.Layers.Add(SatelliteLayer.Create());
        Map.Layers.Add(_radiusLayer);
        Map.Layers.Add(_selectionLayer); // after the circle, so the marker draws on top
        Map.Navigator.Limiter = Rules;

        Camera = new MapCamera(Map.Navigator);
        Camera.Changed += UpdateReadout;
        RadiusMeters = Math.Clamp(radiusMeters, MinRadiusMeters, MaxRadiusMeters);

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

    /// <summary>Radius of the area to load around the chosen point, set by the slider.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RadiusText))]
    private double _radiusMeters;

    public string RadiusText => $"{RadiusMeters:0} m";

    partial void OnRadiusMetersChanged(double value) => UpdateRadiusCircle();

    /// <summary>
    /// Redraws the red circle at the current radius (only while picking), around the chosen point if
    /// there is one, otherwise around the pointer.
    /// </summary>
    void UpdateRadiusCircle()
    {
        _radiusLayer.Features = IsPicking && (Coordinates ?? _pointer) is { } center
            ? [new GeometryFeature(Circle(center, RadiusMeters))]
            : [];
        _radiusLayer.DataHasChanged();
    }

    /// <summary>
    /// A true ground circle as a polygon in map units. Each point is computed on the globe and then
    /// projected, so it stays accurate at any latitude (Web Mercator stretches distances away from the equator).
    /// </summary>
    static Polygon Circle(GeoPoint center, double radiusMeters)
    {
        const int segments = 72;
        const double earthRadius = 6_371_000, rad = Math.PI / 180;
        var lat1 = center.Latitude * rad;
        var lon1 = center.Longitude * rad;
        var d = radiusMeters / earthRadius; // angular distance

        var ring = new Coordinate[segments + 1];
        for (var i = 0; i < segments; i++)
        {
            var bearing = 2 * Math.PI * i / segments;
            var lat2 = Math.Asin(Math.Sin(lat1) * Math.Cos(d) + Math.Cos(lat1) * Math.Sin(d) * Math.Cos(bearing));
            var lon2 = lon1 + Math.Atan2(Math.Sin(bearing) * Math.Sin(d) * Math.Cos(lat1),
                                         Math.Cos(d) - Math.Sin(lat1) * Math.Sin(lat2));
            var world = WebMercator.ToWorld(new GeoPoint(lat2 / rad, lon2 / rad));
            ring[i] = new Coordinate(world.X, world.Y);
        }
        ring[segments] = ring[0]; // polygons must be closed
        return new GeometryFactory().CreatePolygon(ring);
    }

    /// <summary>Moves the marker to the chosen point (or removes it).</summary>
    partial void OnCoordinatesChanged(GeoPoint? value)
    {
        _selectionLayer.Features = value is { } point ? [new PointFeature(WebMercator.ToWorld(point))] : [];
        _selectionLayer.DataHasChanged();
        UpdateRadiusCircle(); // pin the circle to the new point (or let it follow the pointer again)
    }

    [RelayCommand(CanExecute = nameof(CanConfirm))]
    private void Confirm()
    {
        if (Coordinates is { } point) _confirm?.Invoke(point, RadiusMeters);
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
    public void OnPointerMoved(GeoPoint point)
    {
        PointerReadout = point.ToString();
        _pointer = point;
        if (IsPicking && Coordinates is null) UpdateRadiusCircle(); // once pinned, the circle stays put
    }

    void UpdateReadout() => Readout = $"{Camera.Center}  ·  zoom {Camera.Zoom:0.0}";

    public void Dispose()
    {
        Camera.Changed -= UpdateReadout;
        Camera.Dispose();
        Map.Dispose();
    }
}
