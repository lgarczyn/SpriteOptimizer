using System;
using System.Collections.Generic;
using System.Linq;
using NetTopologySuite.Geometries;
using NetTopologySuite.Geometries.Implementation;
using NetTopologySuite.Operation.Polygonize;
using NetTopologySuite.Simplify;
using NetTopologySuite.Triangulate.Polygon;
using NetTopologySuite.Triangulate.Tri;
using UnityEngine;
// Float array to store coordinates compactly and with less GC pressure
using Loop = NetTopologySuite.Geometries.Implementation.PackedFloatCoordinateSequence;

[Serializable]
public struct SpriteOptimizer
{
    public bool ExtractOnlyPolygonal;
    public bool IsCheckingRingsValid;

    [Header("Cleaning")]
    [Min(0)]
    public float AreaIncreaseTolerance;

    [Range(0, 1)]
    public float Tightness;

    [Range(0, 1000)]
    public int CleanSteps;

    /// <summary>
    /// Ensure geometries are build as packed float for best performance
    /// </summary>
    private static readonly GeometryFactory GeomFact = new(
        PrecisionModel.FloatingSingle.Value,
        0,
        new PackedCoordinateSequenceFactory());

    public (Vector2[] Vertices, ushort[] Triangles) GetOptimizedMesh(Sprite sprite)
    {
        // Get mesh data from sprite
        ushort[] triangles = sprite.triangles;
        Vector2[] vertices = sprite.GetVerticesFinalCoordinates();

        // Build a directed edge graph from the triangles
        ILookup<int, DirectedEdge> edges = GetEdgeMap(triangles);
        List<List<int>> contours = GetContours(edges);
        Loop[] loops = GetLoops(contours, vertices);

        // Calculate the maximum bounds out of which we can create new vertices
        Rect bounds = new(0, 0, sprite.texture.width, sprite.texture.height);
        bounds.min -= Vector2.one * 0.001f;
        bounds.max += Vector2.one * 0.001f;

        // Extend clipped corners
        for (int i = 0; i < loops.Length; i++)
        {
            Loop collapsed = CustomCollapse(loops[i], bounds);
            // If loop collapsed to nothing, ignore it
            if (collapsed.Count > 3)
            {
                loops[i] = collapsed;
            }
        }
        // Polygonize the loops to get valid polygons
        Polygonizer polygonizer = new(ExtractOnlyPolygonal);
        polygonizer.IsCheckingRingsValid = IsCheckingRingsValid;
        foreach (Loop loop in loops.ToArray())
        {
            polygonizer.Add(GeomFact.CreateLinearRing(loop));
        }
        Geometry geom = polygonizer.GetGeometry();
        // Simplify the hull and holes
        Geometry simplified = SimplifyHull(geom);
        // Turn the geometry into a triangulated mesh
        ConstrainedDelaunayTriangulator triangulator = new(simplified);
        IList<Tri> mesh = triangulator.GetTriangles();
        // Convert to Unity format
        return GetUnityVerts(sprite, mesh);
    }

    /// <summary>
    /// Replace the sprite's geometry with an optimized mesh according to the current settings.
    /// Must be run in an asset processor, Start or OnGUI due to Unity limitations to OverrideGeometry.
    /// </summary>
    /// <param name="sprite"></param>
    public void Optimize(Sprite sprite)
    {
        (Vector2[] verts, ushort[] tris) = GetOptimizedMesh(sprite);
        sprite.OverrideGeometry(verts, tris);
    }

    /// <summary>
    /// The signed area of the triangle formed by points a, b, and c.
    /// Positive if a->b->c is counter-clockwise, negative if clockwise, 0 if degenerate.
    /// </summary>
    public static float SignedArea(Vector2 a, Vector2 b, Vector2 c)
    {
        float ax = a.x, ay = a.y;
        float bx = b.x, by = b.y;
        float cx = c.x, cy = c.y;

        float cross = (by - ay) * (cx - ax) - (bx - ax) * (cy - ay);
        return 0.5f * cross;
    }

    /// <summary>
    /// Apply hull simplification to the geometry
    /// </summary>
    private Geometry SimplifyHull(Geometry geom)
    {
        PolygonHullSimplifier simp = new(geom, true);
        simp.AreaDeltaRatio = 0.01f;
        simp.VertexNumFraction = Tightness;
        return simp.GetResult();
    }

    /// <summary>
    /// Remove vertices from contour if they don't contribute much to area.
    /// Use different tolerances for adding and removing area, since removing is far more dangerous.
    /// Increase tolerances over multiple steps to start with low hanging fruits
    /// </summary>
    private Loop CustomCollapse(Loop loop, Rect bounds)
    {
        int realCount = loop.Count - 1;
        for (int s = 0; s < CleanSteps; s++)
        {
            float permissiveRatio = (float)(s + 1) / CleanSteps;
            float maxAdd = permissiveRatio * AreaIncreaseTolerance;

            for (int i = 0; i < realCount; i++)
            {
                int a = i;
                int b = (i + 1) % realCount;
                int c = (i + 2) % realCount;
                int d = (i + 3) % realCount;

                Vector2 la1 = loop.Get(a);
                Vector2 la2 = loop.Get(b);
                Vector2 lb1 = loop.Get(c);
                Vector2 lb2 = loop.Get(d);

                Vector2 intersection = Vector2.zero;
                bool intersects = false;
                {
                    float denominator = (la1.x - la2.x) * (lb1.y - lb2.y) - (la1.y - la2.y) * (lb1.x - lb2.x);
                    if (Mathf.Abs(denominator) > 0.0001f)
                    {
                        float t = ((la1.x - lb1.x) * (lb1.y - lb2.y) - (la1.y - lb1.y) * (lb1.x - lb2.x)) / denominator;
                        float u = -((la1.x - la2.x) * (la1.y - lb1.y) - (la1.y - la2.y) * (la1.x - lb1.x)) / denominator;
                        intersection = new Vector2(la1.x + t * (la2.x - la1.x), la1.y + t * (la2.y - la1.y));
                        intersects = t >= 0 && u <= 0 && bounds.Contains(intersection);
                    }
                }
                if (!intersects)
                {
                    continue;
                }

                float area = SignedArea(la2, intersection, lb1);
                bool yes = (area >= 0 && area < maxAdd);
                if (yes)
                {
                    loop.Set(b, intersection);
                    loop.Set(c, intersection);
                }
            }
        }

        loop.Set(realCount, loop.Get(0));
        return loop;
    }

    /// <summary>
    /// Convert a NetTopologySuite triangulated mesh to Unity format
    /// Super annoying since NTS doesn't give us vertices, only triangles with coordinates
    /// We have to deduplicate vertices ourselves and then reindex triangles
    /// </summary>
    private static (Vector2[] Vertices, ushort[] Triangles) GetUnityVerts(Sprite sprite, IList<Tri> mesh)
    {
        ushort[] unityTris = new ushort[mesh.Count * 3];
        Dictionary<Coordinate, int> visited = new();

        int vertexIndex = 0;
        for (int i = 0; i < mesh.Count; i++)
        {
            Tri tri = mesh[i];
            for (int j = 0; j < 3; j++)
            {
                Coordinate coord = tri.GetCoordinate(j);
                if (!visited.TryGetValue(coord, out int index))
                {
                    index = vertexIndex++;
                    visited[coord] = index;
                }

                unityTris[i * 3 + j] = (ushort)index;
            }
        }

        Vector2[] unityVerts = new Vector2[visited.Count];
        foreach ((Coordinate coord, int index) in visited)
        {
            unityVerts[index] = new Vector2((float)coord.X, (float)coord.Y);
        }

        Rect bounds = new(0, 0, sprite.texture.width, sprite.texture.height);

        for (int i = 0; i < unityVerts.Length; i++)
        {
            Vector2 textureSpace = unityVerts[i];
            // mini margin to shut up unity warnings
            Vector2 bounded = new(
                Mathf.Clamp(textureSpace.x, bounds.xMin + 0.0001f, bounds.xMax - 0.0001f),
                Mathf.Clamp(textureSpace.y, bounds.yMin + 0.0001f, bounds.yMax - 0.0001f)
            );
            unityVerts[i] = bounded;
        }

        return (unityVerts, unityTris);
    }

    /// <summary>
    /// Get oriented contours from the edge map
    /// </summary>
    private static List<List<int>> GetContours(ILookup<int, DirectedEdge> edges)
    {
        List<List<int>> contours = new();
        HashSet<DirectedEdge> used = new();

        foreach (DirectedEdge start in edges.SelectMany(g => g))
        {
            if (used.Contains(start))
            {
                continue;
            }

            List<int> loop = new();
            DirectedEdge current = start;

            do
            {
                loop.Add(current.A);
                if (!used.Add(current))
                {
                    throw new Exception("Edge already used, throwing to avoid infinite loop");
                }

                current = edges[current.B].FirstOrDefault(e => !used.Contains(e));
            } while (current != new DirectedEdge());

            contours.Add(loop);
        }

        return contours;
    }

    /// <summary>
    /// Get NTS coordinate sequences from contours
    /// Last vertex is a duplicate of the first to close the loop
    /// </summary>
    private static Loop[] GetLoops(List<List<int>> contours, Vector2[] vertices)
    {
        Loop[] loops = new Loop[contours.Count];
        for (int i = 0; i < contours.Count; i++)
        {
            List<int> contour = contours[i];
            Loop loop = new(contour.Count + 1, 2, 0);

            for (int j = 0; j < contour.Count; j++)
            {
                int index = contour[j];
                Vector2 vec = vertices[index];
                loop.SetX(j, vec.x);
                loop.SetY(j, vec.y);
            }
            loop.SetX(contour.Count, loop.GetX(0));
            loop.SetY(contour.Count, loop.GetY(0));
            loops[i] = loop;
        }

        return loops;
    }

    /// <summary>
    /// Calculate a list of unique external edges indexed by their parent edge end index
    /// Allows following edges to create contours
    /// </summary>
    private static ILookup<int, DirectedEdge> GetEdgeMap(ushort[] triangles)
    {
        HashSet<DirectedEdge> edges = new();

        for (int i = 0; i < triangles.Length; i += 3)
        {
            int a = triangles[i];
            int b = triangles[i + 1];
            int c = triangles[i + 2];

            AddEdge(a, b);
            AddEdge(b, c);
            AddEdge(c, a);
        }

        void AddEdge(int i1, int i2)
        {
            DirectedEdge edge = new(i1, i2);
            DirectedEdge revEdge = new(i2, i1);
            // If reverse edge exists, remove it (we only want boundary edges)
            if (edges.Remove(revEdge))
            {
                return;
            }

            edges.Add(edge);
        }

        return edges.ToLookup(e => e.A, e => e);
    }
}