namespace PathFinder.Models;

using System;
using System.Collections.Generic;
using QuikGraph;
using QuikGraph.Algorithms.ShortestPath;
using Tmds.DBus.Protocol;

public class CityMap
{
    public string Name { get; set; }
    public int StartId { get; set; }
    public int EndId { get; set; }

    public UndirectedGraph<Intersection, TaggedUndirectedEdge<Intersection, Street>> Graph { get; set; }
    private Dictionary<int, Intersection> Intersections;

    public CityMap(string name)
    {
        Name = name;
        StartId = 0;
        EndId = 0;
        Graph = new UndirectedGraph<Intersection, TaggedUndirectedEdge<Intersection, Street>>();
        Intersections = new Dictionary<int, Intersection>();
    }

    public void AddIntersection(Intersection intersection)
    {
        Intersections.Add(intersection.Id, intersection);
        Graph.AddVertex(intersection);
    }

    public void AddStreet(Street street)
    {
        //verify both endpoints exist
        if(!Intersections.ContainsKey(street.Start.Id) || !Intersections.ContainsKey(street.End.Id))
        {
            return;
        }

        Intersection start = Intersections[street.Start.Id];
        Intersection end = Intersections[street.End.Id];

        //create an edge using the street's endpoints
        TaggedUndirectedEdge<Intersection, Street> newEdge = new TaggedUndirectedEdge<Intersection, Street>(
            start,
            end,
            street
        );

        //add the edge to the graph
        Graph.AddEdge(newEdge);
    }

    public Intersection GetIntersection(int id)
    {
        return Intersections[id];
    }
}