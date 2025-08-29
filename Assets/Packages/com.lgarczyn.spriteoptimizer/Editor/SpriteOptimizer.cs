using System;
using System.Collections.Generic;
using System.Linq;
using TriangleNet.Geometry;
using TriangleNet.Meshing;
using TriangleNet.Topology;
using UnityEngine;

[Serializable]
public struct SpriteOptimizer
{
    [Header("Quality")]
    [Range(0, 180)]
    public double MaximumAngle;

    [Range(0, 180)]
    public double MinimumAngle;

    [Min(0)]
    public int SteinerPoints;

    [Header("Constraints")]
    public bool Holes;

    public bool ConformingDelaunay;

    [Header("Cleaning")]
    [Range(0, 2)] public float AreaIncreaseTolerance;
    [Range(0, 2)] public float AreaDecreaseTolerance;
    [Range(0, 100)] public int CleanSteps;

    public IEnumerable<string> GetWarnings()
    {
        if (MinimumAngle > MaximumAngle && MaximumAngle > 0)
        {
            yield return "Minimum angle is greater than maximum angle.";
        }
        else if (MinimumAngle > 120)
        {
            yield return "Minimum angle above 120 is not supported.";
        }
        else if (MinimumAngle > 40)
        {
            yield return "Minimum angle above 40 degrees may cause excessive triangles.";
        }
        if (MaximumAngle is > 0 and < 90)
        {
            yield return "Maximum angle below 100 may cause excessive triangles.";
        }

        if (Holes)
        {
            yield return "If encountering invalid meshes, try disabling holes.";
        }

        if (CleanSteps > 0 && AreaDecreaseTolerance > 0)
        {
            yield return "Removing surface area may cause some parts of the sprite to be cut off.";
        }
    }

    /// <summary>
    /// Simple struct to represent an directed edge
    /// </summary>
    private struct Edge : IEquatable<Edge>
    {
        public readonly int A;
        public readonly int B;

        public Edge(int a, int b)
        {
            A = a;
            B = b;
        }

        public bool Equals(Edge other)
        {
            return A == other.A
                && B == other.B;
        }

        public override          bool Equals(object obj)                 => obj is Edge other && Equals(other);
        public readonly override int  GetHashCode()                      => HashCode.Combine(A, B);
        public static            bool operator ==(Edge left, Edge right) => left.Equals(right);
        public static            bool operator !=(Edge left, Edge right) => !left.Equals(right);

        public readonly override string ToString() => $"({A}->{B})";
    }

    private IMesh GetOptimizedMesh(Sprite sprite)
    {
        ushort[] triangles = sprite.triangles;
        Vector2[] vertices = sprite.vertices;

        ILookup<int, Edge> edges = GetEdgeMap(triangles);
        List<List<int>> contours = GetContours(edges);

        for (int i = 0; i < contours.Count; i++)
        {
            contours[i] = CleanLoop(contours[i], vertices);
        }

        Polygon poly = GetPolygon(vertices, contours, Holes);

        // Since we want to do CVT smoothing, ensure that the mesh
        // is conforming Delaunay.
        ConstraintOptions options = new()
        {
            ConformingDelaunay = ConformingDelaunay,
        };

        // Set maximum area quality option (we don't need to set a minimum
        // angle, since smoothing will improve the triangle shapes).
        QualityOptions quality = new()
        {
            MaximumAngle = MaximumAngle,
            MinimumAngle = MinimumAngle,
            SteinerPoints = SteinerPoints,
        };

        // Generate mesh using the polygons Triangulate extension method.
        return poly.Triangulate(options, quality);
    }

    public (Vector2[] Vertices, ushort[] Triangles) GetUnityMesh(Sprite sprite)
    {
        IMesh mesh = GetOptimizedMesh(sprite);
        Vector2[] unityVerts = GetUnityVerts(sprite, mesh, out Dictionary<int, int> indexMap);
        ushort[] unityTris = GetUnityTriangles(mesh, indexMap);
        return (unityVerts, unityTris);
    }

    /// <summary>
    /// Replace the sprite's geometry with an optimized mesh according to the current settings.
    /// Must be run in an asset processor, Start or OnGUI due to Unity limitations to OverrideGeometry.
    /// </summary>
    /// <param name="sprite"></param>
    public void Optimize(Sprite sprite)
    {
        (Vector2[] verts, ushort[] tris) = GetUnityMesh(sprite);
        sprite.OverrideGeometry(verts, tris);
    }

    /// <summary>
    /// Remove vertices from contour if they don't contribute much to area.
    /// Use different tolerances for adding and removing area, since removing is far more dangerous.
    /// Increase tolerances over multiple steps to start with low hanging fruits
    /// </summary>
    private List<int> CleanLoop(List<int> contour, Vector2[] vertices)
    {
        contour = contour.ToList();
        for (int s = 0; s < CleanSteps; s++)
        {
            float permissiveRatio = (float)(s + 1) / CleanSteps;
            float maxRemove = permissiveRatio * AreaDecreaseTolerance;
            float maxAdd = permissiveRatio * AreaIncreaseTolerance;
            for (int i = 0; i < contour.Count; i++)
            {
                int prevI = (i - 1 + contour.Count) % contour.Count;
                int nextI = (i + 1) % contour.Count;

                Vector2 prev = vertices[contour[prevI]];
                Vector2 current = vertices[contour[i]];
                Vector2 next = vertices[contour[nextI]];

                float area = (prev.x * (current.y - next.y)
                            + current.x * (next.y - prev.y)
                            + next.x * (prev.y - current.y));

                if ((area < 0 && -area < maxRemove) || (area > 0 && area < maxAdd))
                {
                    contour.RemoveAt(i);
                    i++;
                }
            }
        }

        return contour;
    }

    private static Vector2[] GetUnityVerts(Sprite sprite, IMesh mesh, out Dictionary<int, int> indexMap)
    {
        Vector2[] unityVerts = new Vector2[mesh.Vertices.Count];
        indexMap = new(mesh.Vertices.Count);

        int vertexIndex = 0;
        foreach (Vertex vertex in mesh.Vertices)
        {
            Vector2 unityVert = new((float)vertex.X, (float)vertex.Y);
            unityVerts[vertexIndex] = unityVert;
            indexMap[vertex.ID] = vertexIndex;
            vertexIndex++;
        }

        Rect bounds = new Rect(0, 0, sprite.texture.width, sprite.texture.height);
        Vector2 pivot = sprite.pivot;
        float ppu = sprite.pixelsPerUnit;
        for (int i = 0; i < unityVerts.Length; i++)
        {
            Vector2 objectSpace = unityVerts[i];
            Vector2 textureSpace = objectSpace * ppu + pivot;
            unityVerts[i] = textureSpace;
        }

        for (int i = 0; i < unityVerts.Length; i++)
        {
            var textureSpace = unityVerts[i];
            // mini margin to shut up unity warnings
            Vector2 bounded = new(
                Mathf.Clamp(textureSpace.x, bounds.xMin + 0.0001f, bounds.xMax - 0.0001f),
                Mathf.Clamp(textureSpace.y, bounds.yMin + 0.0001f, bounds.yMax - 0.0001f)
            );
            unityVerts[i] = bounded;
        }

        return unityVerts;
    }

    private static ushort[] GetUnityTriangles(IMesh mesh, Dictionary<int, int> indexMap)
    {
        ushort[] unityTris = new ushort[mesh.Triangles.Count * 3];
        int ti = 0;

        foreach (Triangle tri in mesh.Triangles)
        {
            unityTris[ti++] = (ushort)indexMap[tri.GetVertex(0).ID];
            unityTris[ti++] = (ushort)indexMap[tri.GetVertex(1).ID];
            unityTris[ti++] = (ushort)indexMap[tri.GetVertex(2).ID];
        }

        return unityTris;
    }

    /// <summary>
    /// Create a Triangle.Net polygon from the given vertices and contours
    /// </summary>
    private static Polygon GetPolygon(Vector2[] vertices, List<List<int>> contours, bool withHoles)
    {
        Polygon poly = new();

        // Negative if loop is a hole
        float SignedArea(List<int> loop)
        {
            float area = 0;
            for (int i = 0; i < loop.Count; i++)
            {
                Vector2 p1 = vertices[loop[i]];
                Vector2 p2 = vertices[loop[(i + 1) % loop.Count]];
                area += (p2.x - p1.x) * (p2.y + p1.y);
            }

            return area;
        }

        foreach (List<int> loop in contours)
        {
            if (loop.Count < 3)
            {
                continue;
            }

            bool isHole = SignedArea(loop) < 0;
            List<Vertex> verts = new(loop.Count);

            foreach (int t in loop)
            {
                Vector2 v = vertices[t];
                verts.Add(new Vertex(v.x, v.y));
            }

            Contour contour = new(verts, 0, true);
            if (!isHole || withHoles)
            {
                poly.Add(contour, isHole);
            }
        }

        return poly;
    }

    /// <summary>
    /// Get oriented contours from the edge map
    /// </summary>
    private static List<List<int>> GetContours(ILookup<int, Edge> edges)
    {
        List<List<int>> contours = new();
        HashSet<Edge> used = new();

        foreach (Edge start in edges.SelectMany(g => g))
        {
            if (used.Contains(start))
            {
                continue;
            }

            List<int> loop = new();
            Edge current = start;

            do
            {
                loop.Add(current.A);
                if (!used.Add(current))
                {
                    throw new Exception("Edge already used, throwing to avoid infinite loop");
                }

                current = edges[current.B].FirstOrDefault(e => !used.Contains(e));
            } while (current != new Edge());

            contours.Add(loop);
        }

        return contours;
    }

    /// <summary>
    /// Calculate a list of unique external edges indexed by their parent edge end index
    /// Allows following edges to create contours
    /// </summary>
    private static ILookup<int, Edge> GetEdgeMap(ushort[] triangles)
    {
        HashSet<Edge> edges = new();

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
            Edge edge = new(i1, i2);
            Edge revEdge = new(i2, i1);
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