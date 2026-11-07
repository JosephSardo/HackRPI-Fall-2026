namespace PathFinder.Models;

using System;
using System.Collections.Generic;
using QuikGraph;
using QuikGraph.Algorithms.ShortestPath;

public class CityMap
{
    public string Name { get; set; }
    public int startId { get; set; }
    public int endId { get; set; }

    public UndirectedGraph<Intersection, TaggedUndirectedEdge<Intersection, Street>> Graph { get; set; }

    public CityMap(int start, int end)
    {
        startId = start;
        endId = end;
        Graph = new UndirectedGraph<Intersection, TaggedUndirectedEdge<Intersection, Street>>();
    }

    public void AddIntersection(Intersection intersection)
    {
        Graph.AddVertex(intersection);
    }

    public void AddStreet(Street street)
    {
        //verify both endpoints exist
        if(!(Graph.ContainsVertex(street.Start) && Graph.ContainsVertex(street.End)))
        {
            return;
        }

        //create an edge using the street's endpoints
        TaggedUndirectedEdge<Intersection, Street> newEdge = new TaggedUndirectedEdge<Intersection, Street>(
            street.Start,
            street.End,
            street
        );

        //add the edge to the graph
        Graph.AddEdge(newEdge);
    }
}