namespace PathFinder.Services;

using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Data;
using Avalonia.Input.TextInput;
using PathFinder.Models;

public class GameService
{
    public CityMap CityMap { get; private set; }
    public PlayerRoute Route { get; private set; }

    private const double MinOptimalDistance = 1; //should be around 5x the average street length

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

        List<int> intersectionIds = CityMap.Graph.Vertices.Select(i => i.Id).ToList();

        if (intersectionIds.Count < 2)
        {
            throw new InvalidOperationException(
                "Cannot select a starting and ending intersection: the map has fewer than two intersections."
            );
        }

        CityMap.StartId = intersectionIds[Random.Shared.Next(intersectionIds.Count)];
        CityMap.EndId = intersectionIds[Random.Shared.Next(intersectionIds.Count)];

        int attempts = 0;
        while(attempts < 100 && 
              (CityMap.StartId == CityMap.EndId ||
              PathfindingService.FindOptimalDistance(CityMap) < MinOptimalDistance))
        {
            CityMap.EndId = intersectionIds[Random.Shared.Next(intersectionIds.Count)];
            attempts++;
        }

        if(attempts == 100)
        {
            throw new InvalidOperationException(
                "Could not find a valid starting/ending intersection combination. Select a larger map region."
            );
        }

        double? optimalDistance = PathfindingService.FindOptimalDistance(CityMap);

        Route = new(CityMap.StartId);
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