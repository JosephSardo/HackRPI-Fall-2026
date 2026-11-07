namespace PathFinder.Services;

using System;
using System.Collections.Generic;
using PathFinder.Models;

public class GameService
{
    public CityMap CityMap { get; private set; }
    public PlayerRoute Route { get; private set; }

    public GameService(
        List<(double X, double Y)> intersections,
        List<(
            double Distance,
            (double X, double Y) Start,
            (double X, double Y) End
        )> streets
    )
    {
        CityMap = MapService.CreateMap(intersections, streets);
        Route = new(1);
    }

    //sets the starting node for the map. Also resets the PlayerRoute object
    public void SetStart(int id)
    {
        CityMap.StartId = id;
        Route = new(CityMap.StartId);
    }

    //sets the ending node for the map. Also resets the PlayerRoute object
    public void SetEnd(int id)
    {
        CityMap.EndId = id;
        Route = new(CityMap.StartId);
    }

    //adds a node to the player's path when they select a street
    //street must be attached to the end of the existing path
    public bool AddStreetToRoute(Street street)
    {
        return Route.AddStreet(street, CityMap.EndId);
    }

    //removes a node from the player's path when they deselect a street
    //street must be the final street in the existing path
    public bool RemoveStreetFromRoute(Street street)
    {
        return Route.RemoveStreet(street, CityMap.EndId);
    }

    //returns the player's selected path of nodes (each int is a node's id)
    public List<int> GetPlayerPath()
    {
        return Route.Path;
    }

    //returns whether the route is complete (ends exactly at the final node)
    public bool PathIsComplete()
    {
        return Route.IsComplete;
    }

    //returns the route's length
    public double PathLength()
    {
        return Route.TotalDistance;
    }

    //returns the best route's length. If there is no route, returns -1
    public double BestPathLength()
    {
        double? OptimalDistance = PathfindingService.FindOptimalDistance(CityMap);
        if(OptimalDistance != null)
        {
            return (double)OptimalDistance;
        }
        return -1;
    }

    //returns player's score, 0 to 100. The closer the route is to the optimal route, the closer the score is to 100
    //If there is no possible route, returns -1
    //If the player's route is incomplete, returns -2
    public int CalculatePlayerScore()
    {
        if (!PathIsComplete())
        {
            return -2;
        }

        double OptimalDistance = BestPathLength();
        if(OptimalDistance == -1)
        {
            return -1;
        }
        return int.Max(0, (int)(100 * (1 - (Math.Abs(PathLength() - OptimalDistance) / OptimalDistance))));
    }
}