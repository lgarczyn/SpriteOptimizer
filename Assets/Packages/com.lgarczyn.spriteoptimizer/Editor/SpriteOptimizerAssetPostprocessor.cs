using System.Collections.Generic;
using UnityEngine;

namespace UnityEditor.U2D.Animation
{
    internal class SpriteOptimizerAssetPostprocessor : AssetPostprocessor
    {
        private static Dictionary<(string Path, string SpriteName), (Vector2[] Vertices, ushort[] Indices)> s_OriginalMeshes = new();

        private void OnPostprocessSprites(Texture2D texture, Sprite[] sprites)
        {
            foreach (Sprite sprite in sprites)
            {
                if (!SpriteOptimizerSettings.IsAllowed(assetPath))
                {
                    continue;
                }
                ProcessSprite(sprite);
                s_OriginalMeshes[(assetPath, sprite.name)] = (sprite.vertices, sprite.triangles);
            }
        }

        private void ProcessSprite(Sprite sprite)
        {
            // Only process if the sprite if it's a tight mesh
            if (sprite.packingMode == SpritePackingMode.Rectangle)
            {
                return;
            }

            SpriteOptimizerSettings.instance.Optimizer.Optimize(sprite);
        }
    }
}
