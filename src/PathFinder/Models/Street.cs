namespace PathFinder.Models;

public class Street
{
    public int Id { get; set; }

    public Intersection Start { get; set; }
    public Intersection End { get; set; }

    public double Distance { get; set; }

    public string ImagePath { get; set; }

    public Street(int id, double d, Intersection s, Intersection e)
    {
        Id = id;
        Distance = d;
        Start = s;
        End = e;
    }

    
}