using System.Drawing;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using PathFinder.Models;
using PathFinder.ViewModels;
using System;
using System.Linq;
using Avalonia;
using NetTopologySuite.Triangulate;
using Avalonia.Media;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace PathFinder.Views;

public partial class PlayingView : UserControl
{
    private readonly Bitmap _nodeImage;
    private readonly Bitmap _streetImage;

    private const double NodeSize = 48;
    private const double StreetSegmentSize = 48;

    public PlayingView()
    {
        InitializeComponent();

        _nodeImage = new Bitmap(
            AssetLoader.Open(
                new System.Uri("avares://PathFinder/Assets/large-node.png")
            )
        );

        _streetImage = new Bitmap(
            AssetLoader.Open(
                new System.Uri("avares://PathFinder/Assets/road-segment.png")
            )
        );

        Loaded += (_, _) => DrawMap();
        MapCanvas.SizeChanged += (_, _) => DrawMap();

        // Street selection: click a street to extend the route (or click the last one to undo).
        MapCanvas.PointerPressed += OnMapPointerPressed;
        MapCanvas.PointerMoved += OnMapPointerMoved;
        MapCanvas.PointerExited += (_, _) => SetHovered(null);
    }

    private Avalonia.Point MapToScreen(
        Intersection intersection,
        double minX,
        double minY,
        double scale,
        double padding)
    {
        double x = padding + (intersection.X - minX) * scale;
        double y = MapCanvas.Bounds.Height - padding - (intersection.Y - minY) * scale;

        return new Avalonia.Point(x, y);
    }

    private void DrawIntersection(
        Intersection intersection,
        double minX,
        double minY,
        double scale,
        double padding)
    {
        Avalonia.Point position = MapToScreen(
            intersection,
            minX,
            minY,
            scale,
            padding
        );

        Image image = new()
        {
            Source = _nodeImage,
            Width = NodeSize,
            Height = NodeSize
        };

        Canvas.SetLeft(image, position.X - NodeSize / 2);
        Canvas.SetTop(image, position.Y - NodeSize / 2);

        MapCanvas.Children.Add(image);
    }

    private void DrawStreet(
        Intersection start,
        Intersection end,
        double minX,
        double minY,
        double scale,
        double padding)
    {
        Avalonia.Point startPoint = MapToScreen(start, minX, minY, scale, padding);
        Avalonia.Point endPoint = MapToScreen(end, minX, minY, scale, padding);

        double dx = endPoint.X - startPoint.X;
        double dy = endPoint.Y - startPoint.Y;

        double length = Math.Sqrt(dx * dx + dy * dy);
        double angleRadians = Math.Atan2(dy, dx);
        double angleDegrees = angleRadians * 180 / Math.PI;

        int segmentCount = (int)Math.Ceiling(length / StreetSegmentSize);

        for(int i = 0; i < segmentCount; i++)
        {
            double segmentStart = i * StreetSegmentSize;
            double segmentLength = Math.Min(
                StreetSegmentSize,
                length - segmentStart
            );
            double distance = segmentStart + segmentLength / 2;

            double x = startPoint.X + Math.Cos(angleRadians) * distance;
            double y = startPoint.Y + Math.Sin(angleRadians) * distance;

            Canvas segmentCanvas = new()
            {
                Width = segmentLength,
                Height = StreetSegmentSize,
                ClipToBounds = true,
                RenderTransform = new RotateTransform(angleDegrees),
                RenderTransformOrigin = new RelativePoint(0.5, 0.5, RelativeUnit.Relative)
            };

            Image image = new()
            {
                Source = _streetImage,
                Width = StreetSegmentSize,
                Height = StreetSegmentSize
            };

            // Position the full image inside the smaller canvas.
            Canvas.SetLeft(image, 0);
            Canvas.SetTop(image, 0);

            segmentCanvas.Children.Add(image);

            Canvas.SetLeft(segmentCanvas, x - segmentLength / 2);
            Canvas.SetTop(segmentCanvas, y - StreetSegmentSize / 2);

            MapCanvas.Children.Add(segmentCanvas);
        }
    }

    private void DrawMap()
    {
        StopPulses();
        MapCanvas.Children.Clear();

        if(MapCanvas.Bounds.Width <= 0 || MapCanvas.Bounds.Height <= 0)
        {
            return;
        }
        
        if (DataContext is not PlayingViewModel vm)
            return;

        CityMap map = vm.Game.CityMap;

        var intersections = map.Graph.Vertices.ToList();

        if (intersections.Count == 0)
            return;

        double minX = intersections.Min(i => i.X);
        double maxX = intersections.Max(i => i.X);
        double minY = intersections.Min(i => i.Y);
        double maxY = intersections.Max(i => i.Y);

        double padding = 40;

        double availableWidth = MapCanvas.Bounds.Width - 2 * padding;
        double availableHeight = MapCanvas.Bounds.Height - 2 * padding;

        double rangeX = maxX - minX;
        double rangeY = maxY - minY;

        double scaleX = rangeX > 0 ? availableWidth / rangeX : double.PositiveInfinity;
        double scaleY = rangeY > 0 ? availableHeight / rangeY : double.PositiveInfinity;

        double scale = Math.Min(scaleX, scaleY);

        // Remembered for street selection, so clicks can be matched to streets on screen.
        _minX = minX;
        _minY = minY;
        _scale = scale;
        _padding = padding;
        _hasLayout = true;

        foreach (var edge in map.Graph.Edges)
        {
            DrawStreet(
                edge.Source,
                edge.Target,
                minX,
                minY,
                scale,
                padding
            );
        }

        foreach (var intersection in intersections)
        {
            DrawIntersection(
                intersection,
                minX,
                minY,
                scale,
                padding
            );
        }

        AddPulses(vm);
        if (!_introPlayed)
        {
            // First time the map appears this round: it settles into place instead of popping in.
            _introPlayed = true;
            _ = Motion.Enter(MapCanvas, delayMs: 150, fromY: 0, fromScale: 0.94, durationMs: 600);
        }
        DrawOverlay();
    }

    // ------------------------------------------------------------ animation

    /// <summary>How long a newly added street takes to draw itself, in milliseconds.</summary>
    private const double GrowMs = 220;

    private bool _introPlayed;
    private CancellationTokenSource? _pulses;
    private int _shownRouteCount = -1;           // route length last drawn; -1 = nothing drawn yet
    private readonly Stopwatch _grow = new();    // running while the newest street is drawing itself

    /// <summary>Sonar-style pings on the start and destination, so they're easy to spot on a busy map.</summary>
    private void AddPulses(PlayingViewModel vm)
    {
        _pulses = new CancellationTokenSource();
        var map = vm.Game.CityMap;
        AddPulse(ToScreen(map.GetIntersection(vm.StartId)), "#3DDC84", delayMs: 0);
        AddPulse(ToScreen(map.GetIntersection(vm.EndId)), "#FF5252", delayMs: 800); // alternate with the start
    }

    private void AddPulse(Avalonia.Point at, string color, double delayMs)
    {
        var halo = Ring(at, color, 34, 3);
        MapCanvas.Children.Add(halo);
        _ = Motion.Ripple(halo, durationMs: 1600, delayMs: delayMs, toScale: 2.1, ct: _pulses!.Token);
    }

    private void StopPulses()
    {
        _pulses?.Cancel();
        _pulses?.Dispose();
        _pulses = null;
    }

    /// <summary>Rings bursting out of the destination when the route reaches it.</summary>
    private async void Celebrate(Avalonia.Point at)
    {
        _ = Motion.Pop(RouteHintText); // "Destination reached! Submit your route."
        var rings = new[] { 0d, 140, 280 }.Select(delay => (Ring: Ring(at, "#FFC83D", 30, 4), Delay: delay)).ToList();
        foreach (var (ring, _) in rings) MapCanvas.Children.Add(ring);
        try
        {
            await Task.WhenAll(rings.Select(r => Motion.Ripple(r.Ring, durationMs: 700, delayMs: r.Delay,
                                                                toScale: 3.4, repeat: false)));
        }
        finally
        {
            foreach (var (ring, _) in rings) MapCanvas.Children.Remove(ring);
        }
    }

    private static Avalonia.Controls.Shapes.Ellipse Ring(Avalonia.Point at, string color, double size, double thickness)
    {
        var ring = new Avalonia.Controls.Shapes.Ellipse
        {
            Width = size,
            Height = size,
            Stroke = new SolidColorBrush(Avalonia.Media.Color.Parse(color)),
            StrokeThickness = thickness,
            IsHitTestVisible = false,
            Opacity = 0, // shown by its animation
        };
        Canvas.SetLeft(ring, at.X - size / 2);
        Canvas.SetTop(ring, at.Y - size / 2);
        return ring;
    }

    /// <summary>Redraws the overlay every frame until the newest street has finished drawing itself.</summary>
    private void RequestGrowFrame() =>
        TopLevel.GetTopLevel(this)?.RequestAnimationFrame(_ =>
        {
            DrawOverlay();
            if (_grow.IsRunning) RequestGrowFrame();
        });

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        StopPulses(); // the pings repeat forever otherwise
        _grow.Reset();
        base.OnDetachedFromVisualTree(e);
    }

    // ------------------------------------------------------------ street selection

    /// <summary>How close (in pixels) a click must be to a street to select it.</summary>
    private const double HitTolerance = 18;

    private double _minX, _minY, _scale, _padding;
    private bool _hasLayout;
    private Street? _hovered;
    private PlayingViewModel? _vm;
    private readonly System.Collections.Generic.List<Control> _overlay = [];

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_vm is not null) _vm.RouteUpdated -= DrawOverlay;
        _vm = DataContext as PlayingViewModel;
        if (_vm is not null) _vm.RouteUpdated += DrawOverlay;
        _introPlayed = false;
        _shownRouteCount = -1;
        _grow.Reset();
        DrawMap();
    }

    private Avalonia.Point ToScreen(Intersection intersection) =>
        MapToScreen(intersection, _minX, _minY, _scale, _padding);

    private void OnMapPointerPressed(object? sender, Avalonia.Input.PointerPressedEventArgs e)
    {
        if (_vm is null || !_hasLayout) return;
        var point = e.GetCurrentPoint(MapCanvas);
        if (point.Properties.IsRightButtonPressed)
        {
            _vm.UndoStreetCommand.Execute(null); // right-click: undo
            return;
        }
        if (StreetAt(point.Position) is { } street)
        {
            var before = _vm.RouteNodeIds.Count;
            _vm.ClickStreet(street);
            // The click was refused (the hint says why): shake the hint so the player notices it.
            if (_vm.RouteNodeIds.Count == before && _vm.RouteHint is not null) _ = Motion.Shake(RouteHintText);
        }
    }

    private void OnMapPointerMoved(object? sender, Avalonia.Input.PointerEventArgs e)
    {
        if (_vm is null || !_hasLayout) return;
        var street = StreetAt(e.GetPosition(MapCanvas));
        SetHovered(street is not null && _vm.CanClick(street) ? street : null);
    }

    private void SetHovered(Street? street)
    {
        if (street == _hovered) return;
        _hovered = street;
        Cursor = street is null ? Avalonia.Input.Cursor.Default : new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand);
        DrawOverlay();
    }

    /// <summary>The street closest to a point on screen, if any is within <see cref="HitTolerance"/>.</summary>
    private Street? StreetAt(Avalonia.Point p)
    {
        if (_vm is null) return null;
        Street? best = null;
        var bestDistance = HitTolerance;
        foreach (var edge in _vm.Game.CityMap.Graph.Edges)
        {
            var d = DistanceToSegment(p, ToScreen(edge.Source), ToScreen(edge.Target));
            if (d < bestDistance) (best, bestDistance) = (edge.Tag, d);
        }
        return best;
    }

    private static double DistanceToSegment(Avalonia.Point p, Avalonia.Point a, Avalonia.Point b)
    {
        double dx = b.X - a.X, dy = b.Y - a.Y;
        double lengthSquared = dx * dx + dy * dy;
        double t = lengthSquared == 0 ? 0 : Math.Clamp(((p.X - a.X) * dx + (p.Y - a.Y) * dy) / lengthSquared, 0, 1);
        double x = a.X + t * dx - p.X, y = a.Y + t * dy - p.Y;
        return Math.Sqrt(x * x + y * y);
    }

    /// <summary>Route highlight, hovered street and start/end markers, drawn on top of the map.</summary>
    private void DrawOverlay()
    {
        foreach (var control in _overlay) MapCanvas.Children.Remove(control);
        _overlay.Clear();
        if (_vm is null || !_hasLayout) return;

        var map = _vm.Game.CityMap;
        var route = _vm.RouteNodeIds;

        // A street was just added: draw it growing out from the previous end of the route.
        if (_shownRouteCount >= 0 && route.Count == _shownRouteCount + 1 && route.Count >= 2)
        {
            _grow.Restart();
            RequestGrowFrame();
        }
        else if (route.Count != _shownRouteCount)
        {
            _grow.Reset(); // undo / clear / first draw: no animation
        }
        _shownRouteCount = route.Count;

        var progress = _grow.IsRunning ? Math.Min(1, _grow.Elapsed.TotalMilliseconds / GrowMs) : 1;
        var eased = 1 - Math.Pow(1 - progress, 3); // ease out: fast start, gentle landing
        if (_grow.IsRunning && progress >= 1)
        {
            _grow.Reset();
            if (_vm.Game.PathIsComplete()) Celebrate(ToScreen(map.GetIntersection(_vm.EndId)));
        }

        if (_hovered is { } hovered)
            AddLine(ToScreen(hovered.Start), ToScreen(hovered.End), "#80FFFFFF", 12);

        var tip = route.Count > 0 ? ToScreen(map.GetIntersection(route[^1])) : default;
        for (var i = 1; i < route.Count; i++)
        {
            var from = ToScreen(map.GetIntersection(route[i - 1]));
            var to = ToScreen(map.GetIntersection(route[i]));
            if (i == route.Count - 1 && eased < 1)
                tip = to = new Avalonia.Point(from.X + (to.X - from.X) * eased, from.Y + (to.Y - from.Y) * eased);
            AddLine(from, to, "#E0FFC83D", 10);
        }

        AddRing(ToScreen(map.GetIntersection(_vm.StartId)), "#3DDC84", 34);           // start: green
        AddRing(ToScreen(map.GetIntersection(_vm.EndId)), "#FF5252", 34);             // destination: red
        if (route.Count > 1)
            AddRing(tip, "#FFC83D", 24);                                              // end of route so far: yellow
    }

    private void AddLine(Avalonia.Point from, Avalonia.Point to, string color, double thickness) =>
        AddOverlay(new Avalonia.Controls.Shapes.Line
        {
            StartPoint = from,
            EndPoint = to,
            Stroke = new SolidColorBrush(Avalonia.Media.Color.Parse(color)),
            StrokeThickness = thickness,
            StrokeLineCap = PenLineCap.Round,
        });

    private void AddRing(Avalonia.Point at, string color, double size)
    {
        var ring = new Avalonia.Controls.Shapes.Ellipse
        {
            Width = size,
            Height = size,
            Stroke = new SolidColorBrush(Avalonia.Media.Color.Parse(color)),
            StrokeThickness = 5,
        };
        Canvas.SetLeft(ring, at.X - size / 2);
        Canvas.SetTop(ring, at.Y - size / 2);
        AddOverlay(ring);
    }

    private void AddOverlay(Control control)
    {
        control.IsHitTestVisible = false; // clicks go through to the canvas
        _overlay.Add(control);
        MapCanvas.Children.Add(control);
    }
}
