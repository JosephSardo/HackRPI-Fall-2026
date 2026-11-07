using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using PathFinder.Models;
using PathFinder.Services;

namespace PathFinder.Views;

public partial class PlayingView : UserControl
{
    private readonly Bitmap _nodeImage;
    private readonly Bitmap _streetImage;
    private readonly GameService _gameService;

    public PlayingView(GameService gameService)
    {
        InitializeComponent();

        _gameService = gameService;

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
    }

    //converts map coords into canvas coords
    /*private (double X, double Y) MapToScreen(
        Intersection intersection,
        double minX,
        double minY,
        double scale,
        double padding
    )
    {
        
        double minX = _gameService.CityMap.Graph.Vertices.Min(i => i.X);
        double maxX = _gameService.CityMap.Graph.Vertices.Max(i => i.X);

        double minY = _gameService.CityMap.Graph.Vertices.Min(i => i.Y);
        double maxY = _gameService.CityMap.Graph.Vertices.Max(i => i.Y);
        
    }*/

    //Draws an intersection on the canvas
    private void DrawIntersection(
        Intersection intersection,
        double screenX,
        double screenY
    )
    {
        
    }
}
