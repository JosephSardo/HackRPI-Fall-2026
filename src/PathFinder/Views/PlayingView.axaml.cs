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
    }
}
