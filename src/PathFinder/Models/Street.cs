using System;

namespace PathFinder.Models;

public class Street
{
    public int Id { get; set; }

    public Intersection Start { get; set; }
    public Intersection End { get; set; }

    public double Distance { get; set; }

    public string? ImagePath { get; set; }

    /// <summary>The street's name from OpenStreetMap (e.g. "Congress Street"), or null if it has none.</summary>
    public string? Name { get; set; }

    public Street(int id, double d, Intersection s, Intersection e)
    {
        if(d < 0 || !double.IsFinite(d))
        {
            throw new ArgumentException("Street distance cannot be negative, NaN, or infinity");
        }
        
        Id = id;
        Distance = d;
        Start = s;
        End = e;
    }

    
}