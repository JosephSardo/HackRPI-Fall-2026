namespace PathFinder.Services;

using System;
using System.Collections.Generic;
using QuikGraph;
using QuikGraph.Algorithms;
using PathFinder.Models;

public class PathfindingService
{
    public double? FindOptimalDistance(CityMap map)
    {
        Intersection start = map.GetIntersection(map.StartId);
        Intersection end = map.GetIntersection(map.EndId);

        var tryGetPath = map.Graph.ShortestPathsDijkstra(
            edge => edge.Tag.Distance,
            start
        );

        //check if no path exists
        if(!tryGetPath(end, out var path))
        {
            return null;
        }

        double distance = 0;

        foreach(var edge in path)
        {
            distance += edge.Tag.Distance;
        }

        return distance;
    }
}