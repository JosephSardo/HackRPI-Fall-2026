using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;

namespace PathFinder.Views;

/// <summary>
/// "Map + side panel" screens (results, Cloud Navigator): side by side when the window is wide,
/// panel under the map when it's narrow (the default window is only 600 wide), so the map always
/// gets the full width or height it can.
/// </summary>
public static class ResponsiveLayout
{
    /// <summary>Below this width the panel moves under the map.</summary>
    public const double WideWidth = 860;

    /// <summary>Lays out <paramref name="root"/> (map in its first cell) for this width. Returns whether it's the wide layout.</summary>
    public static bool Apply(Grid root, Control panel, double width, double panelWidth, double narrowMaxHeight)
    {
        var wide = width >= WideWidth;
        root.ColumnDefinitions = wide ? new ColumnDefinitions($"*,{panelWidth}") : new ColumnDefinitions("*");
        root.RowDefinitions = wide ? new RowDefinitions("*") : new RowDefinitions("*,Auto");
        Grid.SetColumn(panel, wide ? 1 : 0);
        Grid.SetRow(panel, wide ? 0 : 1);
        panel.Margin = wide ? new Thickness(16, 0, 0, 0) : new Thickness(0, 12, 0, 0);
        panel.VerticalAlignment = wide ? VerticalAlignment.Center : VerticalAlignment.Stretch;
        panel.MaxHeight = wide ? double.PositiveInfinity : narrowMaxHeight;
        return wide;
    }
}
