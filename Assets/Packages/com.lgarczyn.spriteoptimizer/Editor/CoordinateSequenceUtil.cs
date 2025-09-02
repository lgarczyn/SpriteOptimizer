using NetTopologySuite.Geometries;
using NetTopologySuite.Geometries.Implementation;
using UnityEngine;

public static class CoordinateSequenceUtil
{
    public static Vector2 Get(this PackedFloatCoordinateSequence sequence, int n)
    {
        return new Vector2(
            (float)sequence.GetX(n),
            (float)sequence.GetY(n)
        );
    }

    public static void Set(this PackedFloatCoordinateSequence sequence, int n, Vector2 value)
    {
        sequence.SetX(n, value.x);
        sequence.SetY(n, value.y);
    }
}
