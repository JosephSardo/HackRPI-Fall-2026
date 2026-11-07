using System;
using System.Diagnostics;
using Avalonia.Controls;
using PathFinder.Loading;

namespace PathFinder.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();

#if DEBUG
        // Debug-only: exercise StreetLoader once the window is open.
        Opened += async (_, _) =>
        {
            try
            {
                using var loader = new StreetLoader(42.7284, -73.6918, radiusMeters: 200);
                var intersections = await loader.LoadIntersections();
                Debug.WriteLine($"[DEBUG] LoadIntersections returned {intersections.Count} intersections:");
                foreach (var i in intersections)
                    Debug.WriteLine($"[DEBUG]   {i.X}, {i.Y}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[DEBUG] LoadIntersections failed: {ex}");
            }
        };
#endif
    }
}
