using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

[FilePath("SpriteOptimizer/Settings", FilePathAttribute.Location.ProjectFolder)]
class SpriteOptimizerSettings : ScriptableSingleton<SpriteOptimizerSettings>
{
    [SerializeField]
    private SpriteOptimizer m_Optimizer = new()
    {
        MinimumAngle = 20,
        Holes = false,
        CleanSteps = 100,
        ConformingDelaunay = true,
        AreaIncreaseTolerance = 0.6f,
        AreaDecreaseTolerance = 0,
        SteinerPoints = 1000,
    };

    public static bool IsAllowed(string path)
    {
        if (string.IsNullOrEmpty(BlackList))
        {
            return true;
        }

        return !path.Equals(BlackList, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Allows reimporting a texture to get the normal sprite mesh back.
    /// </summary>
    private static string s_BlackList;

    public SpriteOptimizer Optimizer => m_Optimizer;

    internal static string BlackList
    {
        get => s_BlackList;
        set
        {
            if (s_BlackList == value)
            {
                return;
            }

            List<string> toReimport = new();
            if (!string.IsNullOrEmpty(s_BlackList))
            {
                toReimport.Add(s_BlackList);
            }

            if (!string.IsNullOrEmpty(value))
            {
                toReimport.Add(value);
            }

            s_BlackList = value;
            Selection.objects = toReimport
                               .Select(AssetDatabase.LoadAssetAtPath<Texture2D>)
                               .OfType<Object>()
                               .ToArray();
            EditorApplication.ExecuteMenuItem("Assets/Reimport");
        }
    }
}

class SpriteOptimizerSettingsProvider : SettingsProvider
{
    private SerializedObject m_Serialized;
    private Sprite m_PreviewSprite;

    public SpriteOptimizerSettingsProvider() : base("Project/Sprite Optimizer", SettingsScope.Project)
    {
        keywords = new HashSet<string>
            { "SpriteOptimizer", "Sprite", "Mesh", "Optimizer", "Renderer", "Tight", "Rect", "Triangulation" };
    }

    public override void OnGUI(string searchContext)
    {
        m_Serialized ??= new SerializedObject(SpriteOptimizerSettings.instance);

        foreach (SerializedProperty props in m_Serialized.FindProperty("m_Optimizer"))
        {
            EditorGUILayout.PropertyField(props, true);
        }

        m_Serialized.ApplyModifiedPropertiesWithoutUndo();
        {
            using EditorGUI.DisabledScope _ = new(m_ReimportTask is { IsCompleted: false });

            if (GUILayout.Button("Reimport All Sprites", GUILayout.ExpandWidth(false)))
            {
                m_ReimportTask = ReimportAllSpritesAsync();
            }
        }

        SpriteOptimizer opt = SpriteOptimizerSettings.instance.Optimizer;

        foreach (string warning in opt.GetWarnings())
        {
            EditorGUILayout.HelpBox(warning, MessageType.Warning);
        }

        DrawPreview();
    }

    private Task m_ReimportTask;

    private void DrawPreview()
    {
        m_PreviewSprite = (Sprite)EditorGUILayout.ObjectField(
            "Preview Sprite",
            m_PreviewSprite,
            typeof(Sprite),
            false,
            GUILayout.ExpandWidth(true),
            GUILayout.ExpandHeight(false));

        SpriteOptimizerSettings.BlackList = AssetDatabase.GetAssetPath(m_PreviewSprite);

        if (m_PreviewSprite == null && SpriteOptimizerSettings.BlackList != null)
        {
            return;
        }

        {
            using EditorGUILayout.HorizontalScope _ = new();
            EditorGUILayout.Space();
            DrawSpriteMesh(m_PreviewSprite, false);
            EditorGUILayout.Space();
            DrawSpriteMesh(m_PreviewSprite, true);
            EditorGUILayout.Space();
        }
    }

    public static async Task ReimportAllSpritesAsync()
    {
        // Prevent skipping preview sprite during full reimport
        SpriteOptimizerSettings.BlackList = null;

        // Find all sprites in the project
        string[] guids = AssetDatabase.FindAssets("t:Sprite");
        string[] paths = guids.Select(AssetDatabase.GUIDToAssetPath).Distinct().ToArray();

        int total = paths.Length;
        int current = 0;
        // Reimport all sprites one by one
        foreach (string path in paths)
        {
            current++;
            // Start import
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate | ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ImportRecursive);
            // Display progress bar
            if (EditorUtility.DisplayCancelableProgressBar("Reimporting Sprites", path, (float)current / total))
            {
                Debug.Log($"Reimport cancelled, processed {current}/{total} sprites.");
                break;
            }
            // Force wait for import to finish
            AssetDatabase.StartAssetEditing();
            AssetDatabase.StopAssetEditing();
            // Await minimum time to prevent AssetDatabase overriding progress bar
            await Task.Delay(0);
        }
        // Leave
        EditorUtility.ClearProgressBar();
        AssetDatabase.Refresh();
    }

    /// <summary>
    /// Returns a mesh B that is to param Rect what spr.textureRect is to spr.rect
    /// </summary>
    private static Rect GetSubRect(Rect rect, Sprite spr)
    {
        Rect fullRect = spr.textureRect;
        fullRect.size = spr.rect.size;
        fullRect.position -= spr.textureRectOffset;

        // The part of the texture we actually want to draw
        Rect drawRect = spr.textureRect;

        // THe part of the UI we draw drawRect so that fullRect matches rect
        Rect subRect = new(
            rect.xMin + rect.width * (drawRect.xMin - fullRect.xMin) / fullRect.width,
            rect.yMin + rect.height * (drawRect.yMin - fullRect.yMin) / fullRect.height,
            rect.width * drawRect.width / fullRect.width,
            rect.height * drawRect.height / fullRect.height);

        return subRect;
    }

    private static void DrawSpriteMesh(Sprite sprite, bool optimized)
    {
        float desiredWidth = EditorGUIUtility.currentViewWidth / 3;
        float desiredHeight = desiredWidth / sprite.rect.width * sprite.rect.height;

        float maxHeight = 600;
        if (desiredHeight > maxHeight)
        {
            desiredWidth /= desiredHeight / maxHeight;
            desiredHeight = maxHeight;
        }

        Rect rect = EditorGUILayout.GetControlRect(
            false,
            GUILayout.Height(desiredHeight),
            GUILayout.Width(desiredWidth));

        if (Event.current.type != EventType.Repaint)
        {
            return;
        }

        ushort[] triangles;
        Vector2[] vertices;

        if (optimized)
        {
            try
            {
                (vertices, triangles) = SpriteOptimizerSettings.instance.Optimizer.GetUnityMesh(sprite);
            }
            catch (Exception e)
            {
                Debug.LogException(e);
                return;
            }
        }
        else
        {
            vertices = sprite.vertices;
            triangles = sprite.triangles;
            for (int i = 0; i < vertices.Length; ++i)
            {
                vertices[i] = vertices[i] * sprite.pixelsPerUnit + sprite.pivot;
            }
        }

        EditorGUI.DrawRect(rect, Color.gray);
        using GUI.ClipScope clipScope = new(rect);
        rect.position = Vector2.zero;
        Rect subRect = GetSubRect(rect, sprite);

        DrawSprite(subRect, sprite);
        DrawMesh(subRect, sprite, vertices, triangles);
    }

    private static void DrawMesh(Rect rect, Sprite spr, Vector2[] vertices, ushort[] triangles)
    {
        Handles.color = Color.green;
        vertices = vertices.ToArray();

        Rect textureRect = spr.textureRect;
        textureRect.position = spr.textureRectOffset;

        Vector2 offset1 = -textureRect.center;
        Vector2 mult1 = Vector2.one / textureRect.size;
        Vector2 mult2 = new(rect.size.x, -rect.size.y);
        Vector2 offset2 = rect.center;

        for (int i = 0; i < triangles.Length; i += 3)
        {
            Vector2 v0 = vertices[triangles[i + 0]];
            Vector2 v1 = vertices[triangles[i + 1]];
            Vector2 v2 = vertices[triangles[i + 2]];

            v0 = (v0 + offset1) * mult1;
            v1 = (v1 + offset1) * mult1;
            v2 = (v2 + offset1) * mult1;

            v0 = v0 * mult2 + offset2;
            v1 = v1 * mult2 + offset2;
            v2 = v2 * mult2 + offset2;

            Handles.DrawLine(v0, v1);
            Handles.DrawLine(v1, v2);
            Handles.DrawLine(v2, v0);
        }
    }

    // Draw a sprite given a rect
    // Note: will not use the full original rect, only the part that contains the sprite
    private static void DrawSprite(Rect subRect, Sprite spr)
    {
        Vector2 textureSize = new(spr.texture.width, spr.texture.height);
        // The part of the texture we actually want to draw
        Rect drawRect = spr.textureRect;
        drawRect.position /= textureSize;
        drawRect.size /= textureSize;

        GUI.DrawTextureWithTexCoords(subRect, spr.texture, drawRect, true);
    }

    // Register the SettingsProvider
    [SettingsProvider]
    public static SettingsProvider CreateMyCustomSettingsProvider()
    {
        return new SpriteOptimizerSettingsProvider();
    }
}