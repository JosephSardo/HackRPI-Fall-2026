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
            double distance = Math.Min(
                i * StreetSegmentSize + StreetSegmentSize / 2, 
                length
            );

            double x = startPoint.X + Math.Cos(angleRadians) * distance;
            double y = startPoint.Y + Math.Sin(angleRadians) * distance;

            Image image = new()
            {
                Source = _streetImage,
                Width = StreetSegmentSize,
                Height = StreetSegmentSize,
                RenderTransform = new RotateTransform(angleDegrees),
                RenderTransformOrigin = new RelativePoint(0.5, 0.5, RelativeUnit.Relative)
            };

            Canvas.SetLeft(image, x - StreetSegmentSize / 2);
            Canvas.SetTop(image, y - StreetSegmentSize / 2);

            MapCanvas.Children.Add(image);
        }
    }

    private void DrawMap()
    {
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
        DrawOverlay();
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
        if (StreetAt(point.Position) is { } street) _vm.ClickStreet(street);
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

        if (_hovered is { } hovered)
            AddLine(ToScreen(hovered.Start), ToScreen(hovered.End), "#80FFFFFF", 12);

        for (var i = 1; i < route.Count; i++)
            AddLine(ToScreen(map.GetIntersection(route[i - 1])), ToScreen(map.GetIntersection(route[i])), "#E0FFC83D", 10);

        AddRing(ToScreen(map.GetIntersection(_vm.StartId)), "#3DDC84", 34);           // start: green
        AddRing(ToScreen(map.GetIntersection(_vm.EndId)), "#FF5252", 34);             // destination: red
        if (route.Count > 1)
            AddRing(ToScreen(map.GetIntersection(route[^1])), "#FFC83D", 24);         // end of route so far: yellow
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
