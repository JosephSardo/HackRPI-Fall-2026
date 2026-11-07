namespace PathFinder.Services;

using System.Collections.Generic;
using Avalonia.Input.TextInput;
using PathFinder.Models;

public class GameService
{
    private CityMap _cityMap;

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