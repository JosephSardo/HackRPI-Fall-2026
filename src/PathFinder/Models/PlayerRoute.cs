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
        //the map is undirected, so the street can be walked from either end
        int last = Path[Path.Count - 1];
        int next;
        if (last == street.Start.Id)
        {
            next = street.End.Id;
        }
        else if (last == street.End.Id)
        {
            next = street.Start.Id;
        }
        else
        {
            return false;
        }

        Path.Add(next);
        TotalDistance += street.Distance;
        IsComplete = Path[Path.Count - 1] == endId;
        return true;
    }

    //removes the last street from the route. returns true iff operation was successful
    public bool RemoveStreet(Street street, int endId)
    {
        if(Path.Count < 2)
        {
            return false;
        }

        //the street must connect the last two nodes of the path, in either orientation
        int last = Path[Path.Count - 1];
        int previous = Path[Path.Count - 2];
        bool forward = street.Start.Id == previous && street.End.Id == last;
        bool backward = street.Start.Id == last && street.End.Id == previous;
        if(!forward && !backward)
        {
            return false;
        }

        Path.RemoveAt(Path.Count - 1);
        TotalDistance -= street.Distance;
        IsComplete = Path[Path.Count - 1] == endId;
        return true;
    }
}