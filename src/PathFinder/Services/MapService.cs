using System.Collections.Generic;
using PathFinder.Models;

namespace PathFinder.Services;

public class MapService
{
    private static int _nextIntersectionId = 1;
    private static int _nextStreetId = 1;

    public CityMap CreateMap(
        List<(double X, double Y)> intersections,
        List<(
            double Distance,
            (double X, double Y) Start,
            (double X, double Y) End
        )> streets
    )
    {
        
    }
}