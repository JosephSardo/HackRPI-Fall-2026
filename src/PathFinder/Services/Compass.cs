using System;
using System.Collections.Generic;
using System.Linq;
using PathFinder.Models;

namespace PathFinder.Services;

/// <summary>
/// Pure angle maths for Cloud Navigator's "ground view". No UI, no networking: the host uses it to
/// work out which way the driver is facing, and the driver's screen uses it to label each exit.
///
/// Conventions (match the map: intersection X is metres east, Y is metres north):
/// <list type="bullet">
/// <item>A <b>heading</b> or <b>bearing</b> is a compass angle in degrees, 0 = north, 90 = east,
///       clockwise, always in [0, 360).</item>
/// <item>A <b>relative</b> angle is how far an exit turns from the way you're facing, in (-180, 180]:
///       0 = straight on, negative = to the left, positive = to the right, 180 = a U-turn.</item>
/// </list>
/// Streets are straight lines between intersections in this model, so the bearing of a street is
/// just the bearing from one end to the other, and after driving it you face that same bearing.
/// </summary>
public static class Compass
{
    /// <summary>Exits within this many degrees of straight on count as "straight" (shown as ↑).</summary>
    public const double StraightTolerance = 10;

    /// <summary>Exits within this many degrees of straight back count as a U-turn (shown as ↓).</summary>
    public const double UTurnTolerance = 10;

    /// <summary>
    /// Compass bearing from <paramref name="from"/> to <paramref name="to"/>.
    /// atan2(dx, dy) (note: x first) measures the angle from north towards east, which is exactly a
    /// compass bearing; Math.Atan2 returns (-180, 180], so it's normalised into [0, 360).
    /// Two intersections at the same spot have no direction; that returns 0 (north).
    /// </summary>
    public static double Bearing(double fromX, double fromY, double toX, double toY)
    {
        double dx = toX - fromX, dy = toY - fromY;
        if (dx == 0 && dy == 0) return 0;
        return Normalize(Math.Atan2(dx, dy) * 180 / Math.PI);
    }

    public static double Bearing(Intersection from, Intersection to) => Bearing(from.X, from.Y, to.X, to.Y);

    /// <summary>
    /// How far <paramref name="bearing"/> turns from <paramref name="heading"/>, in (-180, 180]:
    /// negative = left, positive = right. E.g. facing 350 (just west of north), an exit at bearing 20
    /// is +30 (a bit to the right), not -330.
    /// </summary>
    public static double Relative(double heading, double bearing)
    {
        var turn = Normalize(bearing - heading);    // [0, 360): how far clockwise
        return turn > 180 ? turn - 360 : turn;       // fold the left half over to negative
    }

    /// <summary>Any angle into [0, 360).</summary>
    public static double Normalize(double degrees)
    {
        var d = degrees % 360;
        if (d < 0) d += 360;
        return d >= 360 ? 0 : d; // guards -1e-15 % 360 + 360 == 360 from floating-point rounding
    }

    /// <summary>
    /// An arrow for a relative angle, in eight 45° slices except that "straight" and "U-turn" are
    /// narrower (see the tolerances), so a gentle 20° bend still shows as ↖/↗ rather than ↑.
    /// </summary>
    public static string Arrow(double relative)
    {
        var a = Math.Abs(relative);
        if (a <= StraightTolerance) return "↑";
        if (a >= 180 - UTurnTolerance) return "↓";
        var left = relative < 0;
        return a switch
        {
            < 67.5 => left ? "↖" : "↗",
            <= 112.5 => left ? "←" : "→",
            _ => left ? "↙" : "↘",
        };
    }

    /// <summary>
    /// Button text for an exit, e.g. "↑ straight", "↖ 20° left", "→ 90° right", "↓ U-turn".
    /// </summary>
    public static string Describe(double relative)
    {
        var arrow = Arrow(relative);
        var a = Math.Abs(relative);
        return arrow switch
        {
            "↑" => "↑ straight",
            "↓" => "↓ U-turn",
            _ => $"{arrow} {a:0}° {(relative < 0 ? "left" : "right")}",
        };
    }

    /// <summary>Compass point for a heading, e.g. 0 -> "N", 135 -> "SE". For the navigator's readout.</summary>
    public static string CompassPoint(double heading)
    {
        string[] points = ["N", "NE", "E", "SE", "S", "SW", "W", "NW"];
        return points[(int)Math.Round(Normalize(heading) / 45) % 8];
    }

    /// <summary>
    /// Which way the driver faces at the start of a round, before they've driven anywhere: along one of
    /// the start's streets, picked with <paramref name="random"/> (or the lowest-ID street without one).
    /// Only the host calls this with a Random; it sends the result in NavigatorRound and the round's first
    /// DriverMoved, so every client shows the same heading. 0 (north) if the start has no streets.
    /// </summary>
    public static double InitialHeading(CityMap map, int startId, Random? random = null)
    {
        var start = map.GetIntersection(startId);
        var streets = map.Graph.AdjacentEdges(start).OrderBy(e => e.Tag.Id).ToList();
        if (streets.Count == 0) return 0;
        var street = streets[random?.Next(streets.Count) ?? 0];
        var other = street.Source.Id == startId ? street.Target : street.Source;
        return Bearing(start, other);
    }

    /// <summary>
    /// A rough direction in words for a relative angle, in eight 45° slices:
    /// "ahead", "ahead-right", "right", "behind-right", "behind", "behind-left", "left", "ahead-left".
    /// </summary>
    public static string RoughDirection(double relative)
    {
        var a = Math.Abs(relative);
        var side = relative < 0 ? "left" : "right";
        return a switch
        {
            <= 22.5 => "ahead",
            < 67.5 => $"ahead-{side}",
            <= 112.5 => side,
            < 157.5 => $"behind-{side}",
            _ => "behind",
        };
    }

    /// <summary>A distance rounded for talking about, e.g. 37 -> "~40 m", 412 -> "~400 m", 1460 -> "~1.5 km".</summary>
    public static string RoughDistance(double metres) => metres switch
    {
        < 15 => "right here",
        < 100 => $"~{Math.Round(metres / 10) * 10:0} m",
        < 1000 => $"~{Math.Round(metres / 50) * 50:0} m",
        _ => $"~{metres / 1000:0.0} km",
    };

    /// <summary>Straight-line distance in metres between two intersections (map X/Y are metres).</summary>
    public static double Distance(Intersection from, Intersection to) =>
        Math.Sqrt((to.X - from.X) * (to.X - from.X) + (to.Y - from.Y) * (to.Y - from.Y));

    /// <summary>One way out of an intersection, as the driver sees it.</summary>
    public sealed record Exit(int NodeId, double Relative, double Length, string Label);

    /// <summary>
    /// Every street out of <paramref name="nodeId"/>, labelled relative to <paramref name="heading"/>,
    /// sorted left to right (the order they'd appear through a windscreen; the U-turn ends up at an end).
    /// </summary>
    public static IReadOnlyList<Exit> Exits(CityMap map, int nodeId, double heading)
    {
        var here = map.GetIntersection(nodeId);
        return map.Graph.AdjacentEdges(here)
            .Select(e =>
            {
                var other = e.Source.Id == nodeId ? e.Target : e.Source;
                var relative = Relative(heading, Bearing(here, other));
                return new Exit(other.Id, relative, e.Tag.Distance, Describe(relative));
            })
            .OrderBy(x => x.Relative)
            .ToList();
    }
}
