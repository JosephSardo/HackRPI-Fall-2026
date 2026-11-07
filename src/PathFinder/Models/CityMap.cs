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

    public UndirectedGraph<Intersection, TaggedEdge<Intersection, Street>> Graph { get; set; }

    public CityMap(int start, int end)
    {
        startId = start;
        endId = end;
        Graph = new UndirectedGraph<Intersection, TaggedEdge<Intersection, Street>>();
    }
}