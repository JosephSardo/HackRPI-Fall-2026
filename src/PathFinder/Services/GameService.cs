namespace PathFinder.Services;

using System.Collections.Generic;
using Avalonia.Input.TextInput;
using PathFinder.Models;

public class GameService
{
    private CityMap _cityMap;

    public GameService(
        List<(double X, double Y)> intersections,
        List<(
            double Distance,
            (double X, double Y) Start,
            (double X, double Y) End
        )> streets
    )
    {
        _cityMap = MapService.CreateMap(intersections, streets);
    }

    //returns the player's selected path
    public List<int> GetPlayerPath()
    {
        return new List<int>();
    }

    //calc player's score
    public int CalculatePlayerScore()
    {
        return 0;
    }

    //return CityMap object
    public CityMap GetCityMap()
    {
        return _cityMap; //TODO: fix this rep exposure
    }
}