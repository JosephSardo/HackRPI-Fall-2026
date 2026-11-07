using System;
using System.Collections.Generic;
using PathFinder.Models;

namespace PathFinder.Services;

public static class MapService
{
    private static int CurrentLevel = 1;

    //decimal places coordinates are rounded to before being used as lookup keys.
    //StreetLoader returns latitude/longitude in degrees, where 0.1 is ~11 km, so 1 decimal
    //made distinct intersections collide. 6 decimals is ~0.1 m in degrees (and harmless for metres)
    private const int CoordinateDecimals = 6;

    public static CityMap CreateMap(
        List<(double X, double Y)> intersections,
        List<(
            double Distance,
            (double X, double Y) Start,
            (double X, double Y) End
        )> streets
    )
    {
        //create Id counters
        int NextIntersectionId = 1;
        int NextStreetId = 1;

        //create CityMap object
        CityMap map = new("Level " + CurrentLevel);
        CurrentLevel++;

        //create dictionary for easy lookup of intersections based on their coords
        Dictionary<(double X, double Y), Intersection> intersectionLookup = new Dictionary<(double X, double Y), Intersection>();

        //construct and add intersections
        foreach (var (x, y) in intersections)
        {
            double roundedX = Math.Round(x, CoordinateDecimals);
            double roundedY = Math.Round(y, CoordinateDecimals);
            
            Intersection newIntersection = new(NextIntersectionId, roundedX, roundedY);

            map.AddIntersection(newIntersection);
            intersectionLookup.Add((roundedX, roundedY), newIntersection);

            NextIntersectionId++;
        }

        //construct and add streets
        foreach (var (distance, start, end) in streets)
        {
            //distance is a double
            //start and end are (double x, double y) tuples

            double x1 = Math.Round(start.X, CoordinateDecimals);
            double y1 = Math.Round(start.Y, CoordinateDecimals);

            double x2 = Math.Round(end.X, CoordinateDecimals);
            double y2 = Math.Round(end.Y, CoordinateDecimals);

            Intersection startInter = intersectionLookup[(x1, y1)];
            Intersection endInter = intersectionLookup[(x2, y2)];
            Street newStreet = new(NextStreetId, distance, startInter, endInter);

            map.AddStreet(newStreet);
            NextStreetId++;
        }

        return map;
    }
}