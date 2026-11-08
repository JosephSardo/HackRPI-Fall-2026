using System;
using System.Collections.Generic;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using PathFinder.Services;

namespace PathFinder.Views;

/// <summary>
/// Cloud Navigator's dashboard compass for the driver: the way you're facing is always up, so the rose
/// (N/E/S/W) turns as you turn, smoothly. On top: the roads out of this intersection as numbered spokes
/// (relative angles, the same numbers as the exit buttons), and a red pointer towards the destination.
/// Deliberately not a map: no streets beyond the one you're standing on.
/// </summary>
public sealed class CompassRose : Control
{
    /// <summary>Which way the driver faces, in compass degrees.</summary>
    public static readonly StyledProperty<double> HeadingProperty =
        AvaloniaProperty.Register<CompassRose, double>(nameof(Heading));

    /// <summary>Compass bearing from the driver to the destination, or null to hide the pointer.</summary>
    public static readonly StyledProperty<double?> DestinationBearingProperty =
        AvaloniaProperty.Register<CompassRose, double?>(nameof(DestinationBearing));

    /// <summary>The exits as relative angles (negative = left), in button order: spoke 1 is the first.</summary>
    public static readonly StyledProperty<IReadOnlyList<double>?> ExitAnglesProperty =
        AvaloniaProperty.Register<CompassRose, IReadOnlyList<double>?>(nameof(ExitAngles));

    static CompassRose() => AffectsRender<CompassRose>(DestinationBearingProperty, ExitAnglesProperty);

    readonly DispatcherTimer _animation = new() { Interval = TimeSpan.FromMilliseconds(16) };
    double _shownHeading;
    bool _hasHeading;

    public CompassRose()
    {
        _animation.Tick += (_, _) =>
        {
            // Ease towards the real heading the short way round; snap when close.
            var gap = Compass.Relative(_shownHeading, Heading);
            if (Math.Abs(gap) < 0.5)
            {
                _shownHeading = Heading;
                _animation.Stop();
            }
            else _shownHeading = Compass.Normalize(_shownHeading + gap * 0.18);
            InvalidateVisual();
        };
        DetachedFromVisualTree += (_, _) => _animation.Stop();
    }

    public double Heading
    {
        get => GetValue(HeadingProperty);
        set => SetValue(HeadingProperty, value);
    }

    public double? DestinationBearing
    {
        get => GetValue(DestinationBearingProperty);
        set => SetValue(DestinationBearingProperty, value);
    }

    public IReadOnlyList<double>? ExitAngles
    {
        get => GetValue(ExitAnglesProperty);
        set => SetValue(ExitAnglesProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property != HeadingProperty) return;
        if (!_hasHeading) // first heading of the screen: no spin-up from north
        {
            _hasHeading = true;
            _shownHeading = Heading;
            InvalidateVisual();
        }
        else _animation.Start();
    }

    static readonly IBrush Face = new SolidColorBrush(Color.Parse("#2a2560"));
    static readonly IPen Rim = new Pen(new SolidColorBrush(Color.Parse("#8FA3FF")), 3);
    static readonly IPen Tick = new Pen(new SolidColorBrush(Color.Parse("#5A6BB0")), 2);
    static readonly IPen Road = new Pen(new SolidColorBrush(Color.Parse("#5A6BB0")), 14, lineCap: PenLineCap.Round);
    static readonly IBrush Label = new SolidColorBrush(Color.Parse("#d3eaff"));
    static readonly IBrush North = new SolidColorBrush(Color.Parse("#FF5252"));
    static readonly IBrush You = new SolidColorBrush(Color.Parse("#4FC3F7"));
    static readonly IBrush Destination = new SolidColorBrush(Color.Parse("#FF5252"));
    static readonly IPen Outline = new Pen(new SolidColorBrush(Color.Parse("#1d1945")), 2);
    static readonly Typeface Bold = new(FontFamily.Default, weight: FontWeight.Bold);

    public override void Render(DrawingContext context)
    {
        var size = Math.Min(Bounds.Width, Bounds.Height);
        if (size < 40) return;
        var centre = new Point(Bounds.Width / 2, Bounds.Height / 2);
        var radius = size / 2 - 4;

        // Screen point at a relative angle (0 = up, clockwise) and distance from the centre.
        Point At(double relative, double distance)
        {
            var rad = relative * Math.PI / 180;
            return centre + new Point(Math.Sin(rad), -Math.Cos(rad)) * distance;
        }

        context.DrawEllipse(Face, Rim, centre, radius, radius);
        for (var bearing = 0; bearing < 360; bearing += 30)
        {
            var rel = bearing - _shownHeading;
            context.DrawLine(Tick, At(rel, radius - 10), At(rel, radius - 2));
        }

        // The roads out of here, numbered like the buttons.
        var exits = ExitAngles ?? [];
        for (var i = 0; i < exits.Count; i++)
            context.DrawLine(Road, centre, At(exits[i], radius * 0.62));
        for (var i = 0; i < exits.Count; i++)
            DrawCentred(context, (i + 1).ToString(CultureInfo.InvariantCulture), At(exits[i], radius * 0.62), 15, Label);

        // Compass letters, turning with the heading.
        (string Text, double Bearing)[] points = [("N", 0), ("E", 90), ("S", 180), ("W", 270)];
        foreach (var (text, bearing) in points)
            DrawCentred(context, text, At(bearing - _shownHeading, radius - 26), 22, text == "N" ? North : Label);

        // Where the destination is, as the crow flies.
        if (DestinationBearing is { } destination)
        {
            var rel = destination - _shownHeading;
            Triangle(context, At(rel, radius - 2), At(rel - 6, radius - 34), At(rel + 6, radius - 34), Destination);
        }

        // You, always facing up.
        Triangle(context, centre + new Point(0, -18), centre + new Point(-12, 12), centre + new Point(12, 12), You);
    }

    static void Triangle(DrawingContext context, Point a, Point b, Point c, IBrush fill)
    {
        var geometry = new StreamGeometry();
        using (var g = geometry.Open())
        {
            g.BeginFigure(a, isFilled: true);
            g.LineTo(b);
            g.LineTo(c);
            g.EndFigure(isClosed: true);
        }
        context.DrawGeometry(fill, Outline, geometry);
    }

    static void DrawCentred(DrawingContext context, string text, Point at, double fontSize, IBrush brush)
    {
        var formatted = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Bold, fontSize, brush);
        context.DrawText(formatted, at - new Point(formatted.Width / 2, formatted.Height / 2));
    }
}
