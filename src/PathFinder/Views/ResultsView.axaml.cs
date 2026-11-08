using Avalonia.Controls;

namespace PathFinder.Views;

public partial class ResultsView : UserControl
{
    bool? _wide;

    public ResultsView()
    {
        InitializeComponent();
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        var wide = e.NewSize.Width >= ResponsiveLayout.WideWidth;
        if (wide != _wide) _wide = ResponsiveLayout.Apply(Root, Panel, e.NewSize.Width, panelWidth: 380, narrowMaxHeight: 340);
    }
}
