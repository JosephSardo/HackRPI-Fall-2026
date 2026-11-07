namespace PathFinder.Models;

public class Intersection : System.IComparable<Intersection>
{
    public int Id { get; set; }

    //coords for rendering
    public double X { get; set; }
    public double Y { get; set; }

    public Intersection(int id, double x, double y)
    {
        Id = id;
        X = x;
        Y = y;
    }

    // QuikGraph's undirected edges need comparable vertices (and the lower one first), so order by Id.
    public int CompareTo(Intersection? other) => other is null ? 1 : Id.CompareTo(other.Id);
}