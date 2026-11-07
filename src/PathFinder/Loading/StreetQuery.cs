using System.Globalization;

namespace PathFinder.Loading;

/// <summary>The Overpass query for the streets in a <see cref="SearchArea"/>.</summary>
internal static class StreetQuery
{
    // Drivable public streets. "service" (driveways, parking aisles) is excluded so
    // every driveway doesn't register as an intersection.
    private const string StreetFilter =
        "^(motorway|trunk|primary|secondary|tertiary|unclassified|residential|living_street)(_link)?$";

    /// <summary>
    /// Streets (ways) touching the area, each with its ordered node refs, then the coordinates of
    /// every node they reference. Ways that only partly overlap the circle come back whole.
    /// </summary>
    public static string For(SearchArea area) =>
        // InvariantCulture so coordinates always use '.' as the decimal separator.
        string.Create(CultureInfo.InvariantCulture, $"""
            [out:json][timeout:25];
            way(around:{area.RadiusMeters},{area.Center.Lat},{area.Center.Lon})["highway"~"{StreetFilter}"];
            out body;
            >;
            out skel qt;
            """);
}
