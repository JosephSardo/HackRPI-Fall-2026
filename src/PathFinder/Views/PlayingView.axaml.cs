using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace PathFinder.Views;

public partial class PlayingView : UserControl
{
    private readonly Bitmap _nodeImage;
    private readonly Bitmap _streetImage;

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
    }
}
