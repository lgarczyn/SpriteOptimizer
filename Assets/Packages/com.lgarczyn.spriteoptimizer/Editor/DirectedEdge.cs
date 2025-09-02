using System;

/// <summary>
/// Simple struct to represent an directed edge
/// </summary>
internal readonly struct DirectedEdge : IEquatable<DirectedEdge>
{
    public readonly int A;
    public readonly int B;

    public DirectedEdge(int a, int b)
    {
        A = a;
        B = b;
    }

    public bool Equals(DirectedEdge other)
    {
        return A == other.A
            && B == other.B;
    }

    public override bool Equals(object obj) => obj is DirectedEdge other && Equals(other);
    public override int  GetHashCode() => HashCode.Combine(A, B);
    public static   bool operator ==(DirectedEdge left, DirectedEdge right) => left.Equals(right);
    public static   bool operator !=(DirectedEdge left, DirectedEdge right) => !left.Equals(right);

    public override string ToString() => $"({A}->{B})";
}