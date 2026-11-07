namespace PathFinder.Models;

public class Intersection
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
}