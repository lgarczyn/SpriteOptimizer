using UnityEngine;

public static class SpriteUtil
{
    public static Vector2[] GetVerticesFinalCoordinates(this Sprite sprite)
    {
        Vector2 pivot = sprite.pivot;
        float ppu = sprite.pixelsPerUnit;
        Vector2[] vertices = sprite.vertices;
        for (int i = 0; i < vertices.Length; i++)
        {
            vertices[i] = vertices[i] * ppu + pivot;
        }
        return vertices;
    }
}
