using System.Drawing;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using PathFinder.Models;
using PathFinder.ViewModels;
using System;
using System.Linq;
using Avalonia;

namespace PathFinder.Views;

public partial class PlayingView : UserControl
{
    private readonly Bitmap _nodeImage;
    private readonly Bitmap _streetImage;

    private const double NodeSize = 48;

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

    private void DrawMap()
    {
        MapCanvas.Children.Clear();

        if(MapCanvas.Bounds.Width <= 0 || MapCanvas.Bounds.Height <= 0)
        {
            return;
        }

        Console.WriteLine($"DataContext: {DataContext?.GetType().Name}");
        Console.WriteLine($"Canvas size: {MapCanvas.Bounds.Width} x {MapCanvas.Bounds.Height}");
        
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
