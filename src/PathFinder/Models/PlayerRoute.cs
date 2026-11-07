using System.Collections.Generic;

namespace PathFinder.Models;

public class PlayerRoute
{
    //intersections traversed, in order
    public List<int> Path { get; private set; }

    //total distance travelled
    public double TotalDistance { get; private set; }

    //whether the player reached their destination
    public bool IsComplete { get; private set; }

    public PlayerRoute(int startingIntersectionID)
    {
        Path = new List<int> {startingIntersectionID};
        TotalDistance = 0;
        IsComplete = false;
    }

    //adds a street to the end of the route. returns true iff operation was successful
    public bool AddStreet(Street street, int endId)
    {
        if (Path[Path.Count - 1] == street.Start.Id)
        {
            Path.Add(street.End.Id);
            TotalDistance += street.Distance;
            IsComplete = Path[Path.Count - 1] == endId;
            return true;
        }
        return false;
    }

    //removes the last street from the route. returns true iff operation was successful
    public bool RemoveStreet(Street street, int endId)
    {
        if(Path.Count < 2)
        {
            return false;
        }

        if(street.End.Id != Path[Path.Count - 1])
        {
            return false;
        }

        Path.RemoveAt(Path.Count - 1);
        TotalDistance -= street.Distance;
        IsComplete = Path[Path.Count - 1] == endId;
        return true;
    }
}