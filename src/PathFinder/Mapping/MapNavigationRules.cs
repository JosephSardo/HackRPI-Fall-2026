using System;
using Mapsui;
using Mapsui.Limiting;

namespace PathFinder.Mapping;

/// <summary>
/// Where the zoom and pan rules live. Mapsui passes every proposed viewport through
/// <see cref="Limit"/> before showing it: mouse drags, the scroll wheel, pinch zoom, the on-screen
/// buttons and <see cref="MapCamera"/> calls. Rules added here apply to all of them, so there's
/// no need to intercept input to restrict movement.
/// </summary>
public sealed class MapNavigationRules : IViewportLimiter
{
    // Mapsui's default behavior: keeps the view within the tile layer's extent.
    readonly ViewportLimiter _default = new();

    /// <summary>How far out the player can zoom. 2 shows the whole world on a typical window.</summary>
    public double MinZoom { get; set; } = 2;

    /// <summary>How far in the player can zoom. Past the imagery's limit, tiles just get blurry.</summary>
    public double MaxZoom { get; set; } = SatelliteLayer.MaxZoom;

    /// <summary>
    /// Optional play area: when set, the map center can't leave this circle.
    /// For example, set it to the round's StreetLoader center and radius.
    /// </summary>
    public (GeoPoint Center, double RadiusMeters)? PlayArea { get; set; }

    public Viewport Limit(Viewport viewport, MRect? panBounds, MMinMax? zoomBounds)
    {
        viewport = _default.Limit(viewport, panBounds, zoomBounds);
        viewport = LimitZoom(viewport);
        viewport = LimitPan(viewport);
        return viewport;
    }

    /// <summary>Zoom rules. The resolution is in map meters per pixel: bigger = more zoomed out.</summary>
    Viewport LimitZoom(Viewport viewport)
    {
        var zoom = WebMercator.ZoomFromResolution(viewport.Resolution);
        var clamped = Math.Clamp(zoom, MinZoom, MaxZoom);
        if (clamped == zoom) return viewport;

        // Add more zoom rules here, e.g. a minimum zoom that depends on the play area's size.
        return With(viewport, resolution: WebMercator.ResolutionFromZoom(clamped));
    }

    /// <summary>Pan rules. Center coordinates are in world units; use <see cref="WebMercator"/> to convert.</summary>
    Viewport LimitPan(Viewport viewport)
    {
        if (PlayArea is not { } area) return viewport;

        // Keep the center inside the play area. Mercator meters stretch with latitude, so the
        // radius is scaled by 1/cos(latitude) to stay roughly in true meters.
        var center = WebMercator.ToWorld(area.Center);
        var radius = area.RadiusMeters / Math.Cos(area.Center.Latitude * Math.PI / 180);
        var dx = viewport.CenterX - center.X;
        var dy = viewport.CenterY - center.Y;
        var distance = Math.Sqrt(dx * dx + dy * dy);
        if (distance <= radius) return viewport;

        var scale = radius / distance;
        return With(viewport, centerX: center.X + dx * scale, centerY: center.Y + dy * scale);
    }

    static Viewport With(Viewport v, double? centerX = null, double? centerY = null, double? resolution = null) =>
        new(centerX ?? v.CenterX, centerY ?? v.CenterY, resolution ?? v.Resolution, v.Rotation, v.Width, v.Height);
}
