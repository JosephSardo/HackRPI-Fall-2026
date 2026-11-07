using BruTile.Predefined;
using BruTile.Web;
using Mapsui.Tiling.Layers;

namespace PathFinder.Mapping;

/// <summary>The satellite background. Swap the URL here to change imagery providers.</summary>
public static class SatelliteLayer
{
    /// <summary>Shown on the map; Esri's terms require it to stay visible.</summary>
    public const string Attribution = "Imagery © Esri, Maxar, Earthstar Geographics";

    /// <summary>Highest zoom level the imagery has tiles for.</summary>
    public const int MaxZoom = 19;

    // Esri World Imagery: no API key needed. For heavier or commercial use, switch to a keyed
    // provider (MapTiler, Mapbox) by changing this URL; the {z}/{x}/{y} placeholders work the same.
    const string Url = "https://server.arcgisonline.com/ArcGIS/rest/services/World_Imagery/MapServer/tile/{z}/{y}/{x}";

    // No attribution on the tile source: Mapsui would draw it as plain text that's hard to read
    // on imagery. MapView shows Attribution in its own overlay instead.
    public static TileLayer Create() =>
        new(new HttpTileSource(
            new GlobalSphericalMercator(0, MaxZoom),
            Url,
            name: "Esri World Imagery"))
        {
            Name = "Satellite",
        };
}
