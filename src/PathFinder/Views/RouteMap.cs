using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using PathFinder.Models;

namespace PathFinder.Views;

/// <summary>A route to draw on a <see cref="RouteMap"/>: intersection IDs in order, plus how it looks.</summary>
public sealed record RouteLine(IReadOnlyList<int> NodeIds, string Color, double Thickness, bool Dashed = false);

/// <summary>
/// A ring (or, if Filled, an outlined dot) drawn on one intersection of a <see cref="RouteMap"/>.
/// With <paramref name="HeadingDegrees"/> (compass degrees), an arrowhead shows which way it faces.
/// </summary>
public sealed record MapMarker(int NodeId, string Color, double Radius = 11, bool Filled = false, double? HeadingDegrees = null);

/// <summary>
/// A simple, read-only drawing of a <see cref="CityMap"/> with routes on top: streets as thin lines,
/// each route in its own colour, start (green ring) and end (red ring). Used on the Results screen.
/// North is up (map Y grows north, screen Y grows down).
/// </summary>
public sealed class RouteMap : Control
{
    public static readonly StyledProperty<CityMap?> MapProperty =
        AvaloniaProperty.Register<RouteMap, CityMap?>(nameof(Map));

    public static readonly StyledProperty<IReadOnlyList<RouteLine>?> RoutesProperty =
        AvaloniaProperty.Register<RouteMap, IReadOnlyList<RouteLine>?>(nameof(Routes));

    /// <summary>
    /// Optional rings/dots to draw on top. When set (even empty), they replace the default start/end
    /// rings taken from the first route (Cloud Navigator uses this for the destination and the driver).
    /// </summary>
    public static readonly StyledProperty<IReadOnlyList<MapMarker>?> MarkersProperty =
        AvaloniaProperty.Register<RouteMap, IReadOnlyList<MapMarker>?>(nameof(Markers));

    public IReadOnlyList<MapMarker>? Markers
    {
        get => GetValue(MarkersProperty);
        set => SetValue(MarkersProperty, value);
    }

    /// <summary>
    /// How far the reveal has got, 0 to 1. Players' routes race out from the start during the first
    /// part; the best route is revealed underneath them after. Animated whenever the map appears.
    /// </summary>
    public static readonly StyledProperty<double> ProgressProperty =
        AvaloniaProperty.Register<RouteMap, double>(nameof(Progress), 1);

    static RouteMap() => AffectsRender<RouteMap>(MapProperty, RoutesProperty, MarkersProperty, ProgressProperty);

    public double Progress
    {
        get => GetValue(ProgressProperty);
        set => SetValue(ProgressProperty, value);
    }

    /// <summary>Whether to play the reveal when the map appears (off for live maps, e.g. the navigator's).</summary>
    public bool Reveal { get; set; } = true;

    /// <summary>Length of the reveal animation.</summary>
    public static readonly TimeSpan RevealDuration = TimeSpan.FromSeconds(2.6);

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (!Reveal) return;
        var reveal = new Avalonia.Animation.Animation
        {
            Duration = RevealDuration,
            Delay = TimeSpan.FromMilliseconds(300), // let the screen fade in first
            Easing = new Avalonia.Animation.Easings.LinearEasing(), // each phase eases itself below
            FillMode = Avalonia.Animation.FillMode.Both,
            Children =
            {
                new Avalonia.Animation.KeyFrame { Cue = new Avalonia.Animation.Cue(0), Setters = { new Avalonia.Styling.Setter(ProgressProperty, 0d) } },
                new Avalonia.Animation.KeyFrame { Cue = new Avalonia.Animation.Cue(1), Setters = { new Avalonia.Styling.Setter(ProgressProperty, 1d) } },
            },
        };
        _ = reveal.RunAsync(this);
    }

    /// <summary>Progress within one phase of the reveal (0 before it starts, 1 after it ends), eased.</summary>
    static double Phase(double progress, double start, double end)
    {
        var t = Math.Clamp((progress - start) / (end - start), 0, 1);
        return t < 0.5 ? 4 * t * t * t : 1 - Math.Pow(-2 * t + 2, 3) / 2; // ease in-out
    }

    public CityMap? Map
    {
        get => GetValue(MapProperty);
        set => SetValue(MapProperty, value);
    }

    public IReadOnlyList<RouteLine>? Routes
    {
        get => GetValue(RoutesProperty);
        set => SetValue(RoutesProperty, value);
    }

    const double Padding = 24;
    static readonly IPen StreetPen = new Pen(new SolidColorBrush(Color.Parse("#5A6BB0")), 3, lineCap: PenLineCap.Round);
    static readonly IBrush NodeBrush = new SolidColorBrush(Color.Parse("#8FA3FF"));

    public override void Render(DrawingContext context)
    {
        if (Map is not { } map || Bounds.Width <= 2 * Padding || Bounds.Height <= 2 * Padding) return;
        var nodes = map.Graph.Vertices.ToList();
        if (nodes.Count == 0) return;

        // Fit the map into the control, keeping its proportions, centred.
        double minX = nodes.Min(n => n.X), maxX = nodes.Max(n => n.X);
        double minY = nodes.Min(n => n.Y), maxY = nodes.Max(n => n.Y);
        double width = Bounds.Width - 2 * Padding, height = Bounds.Height - 2 * Padding;
        double scale = Math.Min(maxX > minX ? width / (maxX - minX) : double.PositiveInfinity,
                                maxY > minY ? height / (maxY - minY) : double.PositiveInfinity);
        if (double.IsInfinity(scale)) scale = 1;
        double offsetX = Padding + (width - (maxX - minX) * scale) / 2;
        double offsetY = Padding + (height - (maxY - minY) * scale) / 2;
        Point ToScreen(Intersection i) => new(offsetX + (i.X - minX) * scale, offsetY + (maxY - i.Y) * scale);

        foreach (var edge in map.Graph.Edges)
            context.DrawLine(StreetPen, ToScreen(edge.Source), ToScreen(edge.Target));
        foreach (var node in nodes)
            context.DrawEllipse(NodeBrush, null, ToScreen(node), 3, 3);

        var routes = Routes ?? [];
        for (var r = 0; r < routes.Count; r++)
        {
            var route = routes[r];
            var pen = new Pen(new SolidColorBrush(Color.Parse(route.Color)), route.Thickness,
                              dashStyle: route.Dashed ? new DashStyle([2, 1.5], 0) : null, lineCap: PenLineCap.Round);
            // The first route (the best-route band) stays centred; players' routes are nudged sideways
            // by a few pixels each, so routes along the same streets show side by side instead of hiding each other.
            var shift = r == 0 ? 0 : (r - (routes.Count) / 2.0) * 5;

            // Reveal: players race out first, then the best route sweeps in underneath.
            var shown = r == 0 ? Phase(Progress, 0.55, 1) : Phase(Progress, 0, 0.62);
            if (shown <= 0) continue;

            var segments = new List<(Point From, Point To)>();
            for (var i = 1; i < route.NodeIds.Count; i++)
                if (TryGet(map, route.NodeIds[i - 1], out var a) && TryGet(map, route.NodeIds[i], out var b))
                {
                    Point p1 = ToScreen(a), p2 = ToScreen(b);
                    var length = Distance(p1, p2);
                    var normal = length == 0 ? new Point(0, 0) : new Point(-(p2.Y - p1.Y) / length * shift, (p2.X - p1.X) / length * shift);
                    segments.Add((p1 + normal, p2 + normal));
                }

            // Draw the first `shown` fraction of the route's on-screen length.
            var remaining = shown * segments.Sum(s => Distance(s.From, s.To));
            Point? head = null;
            foreach (var (from, to) in segments)
            {
                var length = Distance(from, to);
                if (remaining >= length) { context.DrawLine(pen, from, to); remaining -= length; continue; }
                head = from + (to - from) * (length == 0 ? 0 : remaining / length);
                context.DrawLine(pen, from, head.Value);
                break;
            }
            // A dot leads each player's line while it's still travelling.
            if (head is { } dot && r > 0)
                context.DrawEllipse(pen.Brush, null, dot, route.Thickness + 2, route.Thickness + 2);
        }

        if (Markers is { } markers)
        {
            foreach (var m in markers)
                if (TryGet(map, m.NodeId, out var at))
                {
                    var brush = new SolidColorBrush(Color.Parse(m.Color));
                    if (m.HeadingDegrees is { } heading) Arrow(context, ToScreen(at), heading, m.Radius, brush);
                    context.DrawEllipse(m.Filled ? brush : null, m.Filled ? OutlinePen : new Pen(brush, 4), ToScreen(at), m.Radius, m.Radius);
                }
        }
        // Start and end of the round: the ends of the first route (the best route).
        else if (routes.FirstOrDefault(r => r.NodeIds.Count >= 2) is { } first)
        {
            if (TryGet(map, first.NodeIds[0], out var start)) Ring(context, ToScreen(start), "#3DDC84");
            if (TryGet(map, first.NodeIds[^1], out var end)) Ring(context, ToScreen(end), "#FF5252");
        }
    }

    static readonly IPen OutlinePen = new Pen(new SolidColorBrush(Color.Parse("#1d1945")), 2);

    /// <summary>
    /// A filled arrowhead just in front of a marker, pointing along a compass heading (0 = up/north,
    /// 90 = right/east), outlined so it stands out on the streets.
    /// </summary>
    static void Arrow(DrawingContext context, Point at, double heading, double radius, IBrush brush)
    {
        var rad = heading * Math.PI / 180;
        var dir = new Point(Math.Sin(rad), -Math.Cos(rad));     // screen Y grows down
        var side = new Point(-dir.Y, dir.X);
        var tip = at + dir * (radius * 2.6 + 6);
        var baseCentre = at + dir * (radius * 0.4);
        var halfWidth = radius * 1.3 + 2;
        var geometry = new StreamGeometry();
        using (var g = geometry.Open())
        {
            g.BeginFigure(tip, isFilled: true);
            g.LineTo(baseCentre + side * halfWidth);
            g.LineTo(baseCentre - side * halfWidth);
            g.EndFigure(isClosed: true);
        }
        context.DrawGeometry(brush, OutlinePen, geometry);
    }

    static double Distance(Point a, Point b) => Math.Sqrt((b.X - a.X) * (b.X - a.X) + (b.Y - a.Y) * (b.Y - a.Y));

    static void Ring(DrawingContext context, Point at, string color) =>
        context.DrawEllipse(null, new Pen(new SolidColorBrush(Color.Parse(color)), 4), at, 11, 11);

    static bool TryGet(CityMap map, int id, out Intersection intersection)
    {
        try { intersection = map.GetIntersection(id); return true; }
        catch (KeyNotFoundException) { intersection = null!; return false; }
    }
}
