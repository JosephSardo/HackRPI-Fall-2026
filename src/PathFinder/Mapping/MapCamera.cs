using System;
using Mapsui;

namespace PathFinder.Mapping;

/// <summary>
/// Moves and zooms the map from code: buttons, keyboard shortcuts, and game logic such as
/// "fly to this round's start". Talks in <see cref="GeoPoint"/>s and zoom levels so callers never
/// deal with Mapsui units. Movement here still goes through <see cref="MapNavigationRules"/>.
/// </summary>
public sealed class MapCamera : IDisposable
{
    /// <summary>Animation length for camera moves, in milliseconds. 0 jumps instantly.</summary>
    public const long AnimationMs = 300;

    readonly Navigator _navigator;

    public MapCamera(Navigator navigator)
    {
        _navigator = navigator;
        _navigator.ViewportChanged += OnViewportChanged;
    }

    /// <summary>Raised whenever the view moves or zooms, by the player or from code.</summary>
    public event Action? Changed;

    public GeoPoint Center => WebMercator.ToGeo(_navigator.Viewport.CenterX, _navigator.Viewport.CenterY);
    public double Zoom => WebMercator.ZoomFromResolution(_navigator.Viewport.Resolution);

    /// <summary>When true, the player can't drag the map (code can still move it).</summary>
    public bool PanLocked { get => _navigator.PanLock; set => _navigator.PanLock = value; }

    /// <summary>When true, the player can't zoom (code can still zoom).</summary>
    public bool ZoomLocked { get => _navigator.ZoomLock; set => _navigator.ZoomLock = value; }

    public void CenterOn(GeoPoint point, bool animate = true) =>
        _navigator.CenterOn(WebMercator.ToWorld(point), Duration(animate));

    public void CenterOn(GeoPoint point, double zoom, bool animate = true) =>
        _navigator.CenterOnAndZoomTo(WebMercator.ToWorld(point), WebMercator.ResolutionFromZoom(zoom), Duration(animate));

    /// <summary>Zooms out partway, then in to the target, like a flight. Good for jumping between cities.</summary>
    public void FlyTo(GeoPoint point, double zoom, long durationMs = 1500) =>
        _navigator.FlyTo(WebMercator.ToWorld(point), WebMercator.ResolutionFromZoom(zoom), durationMs);

    public void ZoomIn() => _navigator.ZoomIn(AnimationMs);
    public void ZoomOut() => _navigator.ZoomOut(AnimationMs);
    public void ZoomTo(double zoom, bool animate = true) =>
        _navigator.ZoomTo(WebMercator.ResolutionFromZoom(zoom), Duration(animate));

    /// <summary>Moves the view by a fraction of its size, e.g. (0.25, 0) pans a quarter screen east.</summary>
    public void PanBy(double fractionX, double fractionY, bool animate = true)
    {
        var v = _navigator.Viewport;
        var dx = fractionX * v.Width * v.Resolution;
        var dy = fractionY * v.Height * v.Resolution;
        _navigator.CenterOn(new MPoint(v.CenterX + dx, v.CenterY + dy), Duration(animate));
    }

    /// <summary>Zooms out to the whole world.</summary>
    public void ShowWholeWorld(bool animate = true) => CenterOn(new GeoPoint(20, 0), zoom: 2, animate);

    static long Duration(bool animate) => animate ? AnimationMs : 0;

    void OnViewportChanged(object? sender, ViewportChangedEventArgs e) => Changed?.Invoke();

    public void Dispose() => _navigator.ViewportChanged -= OnViewportChanged;
}
