using BruTile.Predefined;
using BruTile.Web;
using Mapsui.Tiling.Layers;

namespace PathFinder.Mapping;

/// <summary>
/// The map background: satellite imagery with roads and labels on top, like Google Maps'
/// satellite view. Built from three keyless Esri tile services merged into one layer by
/// <see cref="CompositeTileSource"/>. Swap the URLs here to change providers.
/// </summary>
public static class SatelliteLayer
{
    /// <summary>Shown on the map; Esri's terms require it to stay visible. Wording from each service's metadata.</summary>
    public const string Attribution =
        "Imagery © Esri, Vantor, Earthstar Geographics · Roads & labels © Esri, HERE, Garmin, OpenStreetMap contributors";

    /// <summary>Highest zoom level the imagery has tiles for (the overlays go higher).</summary>
    public const int MaxZoom = 19;

    // All keyless Esri services in the same Web Mercator tiling. For heavier or commercial use,
    // a keyed single-layer hybrid (e.g. MapTiler Hybrid) could replace all three.
    const string ImageryUrl = "https://server.arcgisonline.com/ArcGIS/rest/services/World_Imagery/MapServer/tile/{z}/{y}/{x}";
    const string RoadsUrl = "https://server.arcgisonline.com/ArcGIS/rest/services/Reference/World_Transportation/MapServer/tile/{z}/{y}/{x}";
    const string LabelsUrl = "https://server.arcgisonline.com/ArcGIS/rest/services/Reference/World_Boundaries_and_Places/MapServer/tile/{z}/{y}/{x}";

    public static TileLayer Create() =>
        new(new CompositeTileSource(
            "Esri Imagery with Labels",
            Source(ImageryUrl, "Esri World Imagery"),
            Source(RoadsUrl, "Esri World Transportation"),   // road outlines, route shields, one-way arrows
            Source(LabelsUrl, "Esri Boundaries and Places")))  // street, place and water names; drawn last so text sits on top
        {
            Name = "Satellite",
        };

    // No attribution on the sources: Mapsui would draw it as plain text that's hard to read on
    // imagery. MapView shows Attribution in its own overlay instead.
    static HttpTileSource Source(string url, string name) =>
        new(new GlobalSphericalMercator(0, MaxZoom), url, name: name);
}
