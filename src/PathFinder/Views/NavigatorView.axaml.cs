using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;

namespace PathFinder.Views;

public partial class NavigatorView : UserControl
{
    /// <summary>Below this width the panel moves under the map, so the map keeps the full width.</summary>
    const double WideLayoutWidth = 860;

    bool? _wide;

    public NavigatorView()
    {
        InitializeComponent();
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        var wide = e.NewSize.Width >= WideLayoutWidth;
        if (wide == _wide) return;
        _wide = wide;

        Root.ColumnDefinitions = wide ? new ColumnDefinitions("*,340") : new ColumnDefinitions("*");
        Root.RowDefinitions = wide ? new RowDefinitions("*") : new RowDefinitions("*,Auto");
        Grid.SetColumn(Panel, wide ? 1 : 0);
        Grid.SetRow(Panel, wide ? 0 : 1);
        Panel.Margin = wide ? new Thickness(16, 0, 0, 0) : new Thickness(0, 12, 0, 0);
        Panel.VerticalAlignment = wide ? VerticalAlignment.Center : VerticalAlignment.Stretch;
        Panel.MaxHeight = wide ? double.PositiveInfinity : 300;
    }
}
