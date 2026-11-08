using Avalonia.Controls;

namespace PathFinder.Views;

public partial class NavigatorView : UserControl
{
    bool? _wide;

    public NavigatorView()
    {
        InitializeComponent();
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        var wide = e.NewSize.Width >= ResponsiveLayout.WideWidth;
        if (wide != _wide) _wide = ResponsiveLayout.Apply(Root, Panel, e.NewSize.Width, panelWidth: 340, narrowMaxHeight: 300);
    }
}
