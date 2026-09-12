using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using DryCycle.DevUI.DevTool.Compatibility;
using UnityEngine;

namespace DryCycle.DevUI.DevTool.Objects;

/// <summary>
/// Full native inspector adapter for RegionKit's AdvancedShaderController.
///
/// This adapter deliberately uses runtime type names and reflection instead of a compile-time
/// RegionKit reference. The old RegionKit DevInterface panel remains only as a hidden behavior
/// backend; every persisted AdvancedShader.Data field is exposed through the rebuilt inspector.
/// </summary>
public sealed class RegionKitAdvancedShaderInspectorAdapter : IObjectInspectorAdapter
{
    public const string DataTypeName = "RegionKit.Modules.Objects.AdvancedShaderController.AdvancedShader+Data";
    public const string RepresentationTypeName = "RegionKit.Modules.Objects.AdvancedShaderController.AdvancedShaderRepresentation";
    private const string RuntimeTypeName = "RegionKit.Modules.Objects.AdvancedShaderController.AdvancedShader";
    private const int PngScanDepthLimit = 32;
    private const int PngScanEntryLimit = 8192;

    public static readonly RegionKitAdvancedShaderInspectorAdapter Instance = new();

    private static readonly object CacheGate = new();
    private static string[] cachedShaders = Array.Empty<string>();
    private static int cachedShaderCount = -1;
    private static string[] cachedSprites = Array.Empty<string>();
    private static int cachedSpriteCount = -1;
    private static string[] cachedPngAssets = Array.Empty<string>();
    private static bool pngAssetsScanned;

    static RegionKitAdvancedShaderInspectorAdapter()
    {
        // Registration is lazy and idempotent. Coverage or the frontend touching this type is
        // enough to activate the adapter without introducing a hard RegionKit dependency in Plugin.
        ObjectInspectorRegistry.Register(Instance, 5000);
    }

    private RegionKitAdvancedShaderInspectorAdapter() { }

    public static bool IsDataTypeName(string fullName) =>
        string.Equals(fullName, DataTypeName, StringComparison.Ordinal);

    public static bool IsRepresentation(DevInterface.DevUINode node) =>
        node != null && string.Equals(node.GetType().FullName, RepresentationTypeName, StringComparison.Ordinal);

    public bool CanInspect(PlacedObject target) =>
        target?.data != null && IsDataTypeName(target.data.GetType().FullName);

    public IReadOnlyList<EditorPropertySnapshot> Capture(PlacedObject target)
    {
        if (!CanInspect(target)) return Array.Empty<EditorPropertySnapshot>();

        object data = target.data;
        List<EditorPropertySnapshot> result = new(112);

        string shader = ReadString(data, "shader", "Basic");
        result.Add(EnumProperty("rk.as.shader", "Shader", "Material", shader, ShaderNames()));

        bool useFile = ReadBool(data, "useFile");
        result.Add(BooleanProperty("rk.as.useFile", "Use File", "Material", useFile));
        if (useFile)
        {
            string filePath = ReadString(data, "filePath", "illustrations/icon0.png");
            string[] pngAssets = PngAssetPaths(filePath);
            if (pngAssets.Length > 0)
                result.Add(EnumProperty("rk.as.fileAsset", "PNG Asset", "Material", filePath, pngAssets));
            result.Add(StringProperty(
                "rk.as.filePath",
                "Manual PNG Path",
                "Material",
                filePath,
                "AssetManager-relative path. The searchable PNG Asset list replaces RegionKit's paged FilePicker; manual entry remains available for merged or late-loaded assets."));
        }
        else
        {
            string sprite = ReadString(data, "spriteName", "Futile_White");
            result.Add(EnumProperty("rk.as.sprite", "Sprite", "Material", sprite, SpriteNames()));
        }

        FieldInfo containerField = FindField(data.GetType(), "container");
        if (containerField != null && containerField.FieldType.IsEnum)
            result.Add(EnumProperty("rk.as.container", "Container", "Material", containerField.GetValue(data)?.ToString() ?? string.Empty,
                Enum.GetNames(containerField.FieldType)));

        FieldInfo shapeField = FindField(data.GetType(), "shapeLock");
        if (shapeField != null && shapeField.FieldType.IsEnum)
            result.Add(EnumProperty("rk.as.shapeLock", "Shape Lock", "Geometry", shapeField.GetValue(data)?.ToString() ?? string.Empty,
                Enum.GetNames(shapeField.FieldType)));

        Vector2 panelPos = ReadVector2(data, "panelPos", new Vector2(0f, 150f));
        result.Add(VectorProperty("rk.as.panelPos", "Legacy Panel Position", "Geometry", panelPos,
            "Preserved for RegionKit serialization compatibility. It no longer controls the ImGui inspector position."));

        Vector2[] vertices = ReadField(data, "vertices") as Vector2[] ?? Array.Empty<Vector2>();
        for (int i = 0; i < vertices.Length; i++)
            result.Add(VectorProperty("rk.as.vertex." + i, "Vertex " + i, "Geometry · Vertices", vertices[i]));

        bool restrictColors = ReadBool(data, "restrictColors");
        bool lockColors = ReadBool(data, "lockColors");
        result.Add(BooleanProperty("rk.as.restrictColors", "Clamp Colors", "Vertex Colors", restrictColors));
        result.Add(BooleanProperty("rk.as.lockColors", "Sync Colors", "Vertex Colors", lockColors));
        result.Add(ActionProperty("rk.as.resetColors", "Reset Colors", "Vertex Colors"));

        Color[] colors = ReadField(data, "colors") as Color[] ?? Array.Empty<Color>();
        for (int i = 0; i < colors.Length; i++)
        {
            Color color = colors[i];
            string group = "Vertex Colors · Vertex " + i;
            result.Add(FloatProperty("rk.as.color." + i + ".r", "Red", group, color.r, restrictColors));
            result.Add(FloatProperty("rk.as.color." + i + ".g", "Green", group, color.g, restrictColors));
            result.Add(FloatProperty("rk.as.color." + i + ".b", "Blue", group, color.b, restrictColors));
            result.Add(FloatProperty("rk.as.color." + i + ".a", "Alpha", group, color.a, restrictColors));
        }

        bool restrictUVs = ReadBool(data, "restrictUVs");
        bool lockUVs = ReadBool(data, "lockUVs");
        result.Add(BooleanProperty("rk.as.restrictUVs", "Clamp UVs", "UVs", restrictUVs));
        result.Add(BooleanProperty("rk.as.lockUVs", "Sync UVs", "UVs", lockUVs));

        Vector2[][] uvs = ReadField(data, "uvs") as Vector2[][];
        if (uvs != null)
        {
            for (int channel = 0; channel < uvs.Length; channel++)
            {
                Vector2[] channelValues = uvs[channel] ?? Array.Empty<Vector2>();
                string group = "UVs · Channel " + channel;
                result.Add(ActionProperty("rk.as.resetUv." + channel, "Reset Channel " + channel, group));
                for (int vertex = 0; vertex < channelValues.Length; vertex++)
                    result.Add(VectorProperty("rk.as.uv." + channel + "." + vertex,
                        "Vertex " + vertex, group, channelValues[vertex]));
            }
        }

        return result;
    }

    public bool TrySetValue(PlacedObject target, string key, EditorPropertyValue value)
    {
        if (!CanInspect(target) || string.IsNullOrEmpty(key)) return false;
        object data = target.data;

        if (key == "rk.as.shader")
        {
            if (!TrySetEnumString(data, "shader", ShaderNames(), value)) return false;
            RefreshRuntime(target);
            return true;
        }
        if (key == "rk.as.useFile")
        {
            if (value.Kind != EditorPropertyKind.Boolean || !WriteField(data, "useFile", value.Boolean)) return false;
            RefreshRuntime(target);
            return true;
        }
        if (key == "rk.as.fileAsset")
        {
            string current = ReadString(data, "filePath", "illustrations/icon0.png");
            if (!TrySetEnumString(data, "filePath", PngAssetPaths(current), value)) return false;
            RefreshRuntime(target);
            return true;
        }
        if (key == "rk.as.filePath")
        {
            if (value.Kind != EditorPropertyKind.String || !WriteField(data, "filePath", value.Text ?? string.Empty)) return false;
            RefreshRuntime(target);
            return true;
        }
        if (key == "rk.as.sprite")
        {
            if (!TrySetEnumString(data, "spriteName", SpriteNames(), value)) return false;
            InvokeDataMethod(data, "ResetUVs", 0);
            RefreshRuntime(target);
            return true;
        }
        if (key == "rk.as.container")
        {
            if (!TrySetEnumField(data, "container", value)) return false;
            RefreshRuntime(target);
            return true;
        }
        if (key == "rk.as.shapeLock")
            return TrySetEnumField(data, "shapeLock", value);
        if (key == "rk.as.panelPos")
            return value.Kind == EditorPropertyKind.Vector2 && WriteField(data, "panelPos", new Vector2(value.X, value.Y));
        if (key == "rk.as.restrictColors")
            return value.Kind == EditorPropertyKind.Boolean && WriteField(data, "restrictColors", value.Boolean);
        if (key == "rk.as.lockColors")
            return value.Kind == EditorPropertyKind.Boolean && WriteField(data, "lockColors", value.Boolean);
        if (key == "rk.as.resetColors")
        {
            if (value.Kind != EditorPropertyKind.Action) return false;
            return InvokeDataMethod(data, "ResetColors");
        }
        if (key == "rk.as.restrictUVs")
            return value.Kind == EditorPropertyKind.Boolean && WriteField(data, "restrictUVs", value.Boolean);
        if (key == "rk.as.lockUVs")
            return value.Kind == EditorPropertyKind.Boolean && WriteField(data, "lockUVs", value.Boolean);

        if (TryParseIndexedKey(key, "rk.as.resetUv.", 1, out int[] resetUvParts))
        {
            if (value.Kind != EditorPropertyKind.Action) return false;
            return InvokeDataMethod(data, "ResetUVs", resetUvParts[0]);
        }

        if (TryParseIndexedKey(key, "rk.as.vertex.", 1, out int[] vertexParts))
        {
            if (value.Kind != EditorPropertyKind.Vector2) return false;
            Vector2[] vertices = ReadField(data, "vertices") as Vector2[];
            int index = vertexParts[0];
            if (vertices == null || index < 0 || index >= vertices.Length) return false;
            vertices[index] = new Vector2(value.X, value.Y);
            return true;
        }

        if (TryParseColorKey(key, out int colorIndex, out int component))
        {
            if (value.Kind != EditorPropertyKind.Float) return false;
            Color[] colors = ReadField(data, "colors") as Color[];
            if (colors == null || colorIndex < 0 || colorIndex >= colors.Length) return false;

            float next = ReadBool(data, "restrictColors") ? Mathf.Clamp01(value.X) : value.X;
            Color color = colors[colorIndex];
            switch (component)
            {
                case 0: color.r = next; break;
                case 1: color.g = next; break;
                case 2: color.b = next; break;
                default: color.a = next; break;
            }

            if (ReadBool(data, "lockColors"))
            {
                for (int i = 0; i < colors.Length; i++) colors[i] = color;
            }
            else
            {
                colors[colorIndex] = color;
            }
            return true;
        }

        if (TryParseIndexedKey(key, "rk.as.uv.", 2, out int[] uvParts))
        {
            if (value.Kind != EditorPropertyKind.Vector2) return false;
            Vector2[][] uvs = ReadField(data, "uvs") as Vector2[][];
            int channel = uvParts[0];
            int vertex = uvParts[1];
            if (uvs == null || channel < 0 || channel >= uvs.Length || uvs[channel] == null ||
                vertex < 0 || vertex >= uvs[channel].Length)
                return false;

            Vector2 next = new(value.X, value.Y);
            if (ReadBool(data, "restrictUVs"))
            {
                next.x = Mathf.Clamp01(next.x);
                next.y = Mathf.Clamp01(next.y);
            }

            uvs[channel][vertex] = next;
            if (ReadBool(data, "lockUVs") && uvs[channel].Length >= 4 && vertex < 4)
                ApplyLockedUvRectangle(uvs[channel], vertex, next);
            return true;
        }

        return false;
    }

    private static void ApplyLockedUvRectangle(Vector2[] values, int changedIndex, Vector2 changed)
    {
        float left = changedIndex == 0 || changedIndex == 1 ? changed.x : values[0].x;
        float bottom = changedIndex == 0 || changedIndex == 2 ? changed.y : values[0].y;
        float right = changedIndex == 2 || changedIndex == 3 ? changed.x : values[3].x;
        float top = changedIndex == 1 || changedIndex == 3 ? changed.y : values[3].y;
        values[0] = new Vector2(left, bottom);
        values[1] = new Vector2(left, top);
        values[2] = new Vector2(right, bottom);
        values[3] = new Vector2(right, top);
    }

    private static bool TrySetEnumString(object data, string fieldName, string[] options, EditorPropertyValue value)
    {
        if (value.Kind != EditorPropertyKind.Enum || options == null ||
            value.Integer < 0 || value.Integer >= options.Length)
            return false;
        return WriteField(data, fieldName, options[value.Integer]);
    }

    private static bool TrySetEnumField(object data, string fieldName, EditorPropertyValue value)
    {
        if (value.Kind != EditorPropertyKind.Enum) return false;
        FieldInfo field = FindField(data?.GetType(), fieldName);
        if (field == null || !field.FieldType.IsEnum) return false;
        string[] names = Enum.GetNames(field.FieldType);
        if (value.Integer < 0 || value.Integer >= names.Length) return false;
        object parsed = Enum.Parse(field.FieldType, names[value.Integer], false);
        field.SetValue(data, parsed);
        return true;
    }

    private static EditorPropertySnapshot EnumProperty(string key, string label, string group, string current, string[] options)
    {
        options ??= Array.Empty<string>();
        int index = Array.IndexOf(options, current ?? string.Empty);
        return new EditorPropertySnapshot
        {
            Key = key,
            DisplayName = label,
            Group = group,
            Source = "RegionKit AdvancedShader",
            Kind = EditorPropertyKind.Enum,
            IntegerValue = index,
            StringValue = current ?? string.Empty,
            Options = options
        };
    }

    private static EditorPropertySnapshot BooleanProperty(string key, string label, string group, bool value) => new()
    {
        Key = key,
        DisplayName = label,
        Group = group,
        Source = "RegionKit AdvancedShader",
        Kind = EditorPropertyKind.Boolean,
        BooleanValue = value
    };

    private static EditorPropertySnapshot StringProperty(string key, string label, string group, string value, string source = null) => new()
    {
        Key = key,
        DisplayName = label,
        Group = group,
        Source = source ?? "RegionKit AdvancedShader",
        Kind = EditorPropertyKind.String,
        StringValue = value ?? string.Empty
    };

    private static EditorPropertySnapshot VectorProperty(string key, string label, string group, Vector2 value, string source = null) => new()
    {
        Key = key,
        DisplayName = label,
        Group = group,
        Source = source ?? "RegionKit AdvancedShader",
        Kind = EditorPropertyKind.Vector2,
        X = value.x,
        Y = value.y
    };

    private static EditorPropertySnapshot FloatProperty(string key, string label, string group, float value, bool restrict) => new()
    {
        Key = key,
        DisplayName = label,
        Group = group,
        Source = "RegionKit AdvancedShader",
        Kind = EditorPropertyKind.Float,
        X = value,
        Min = 0f,
        Max = 1f,
        Step = 0.01f,
        HasRange = restrict
    };

    private static EditorPropertySnapshot ActionProperty(string key, string label, string group) => new()
    {
        Key = key,
        DisplayName = label,
        Group = group,
        Source = "RegionKit AdvancedShader",
        Kind = EditorPropertyKind.Action
    };

    private static string[] ShaderNames()
    {
        try
        {
            IDictionary shaders = RWCustom.Custom.rainWorld?.Shaders as IDictionary;
            int count = shaders?.Count ?? 0;
            lock (CacheGate)
            {
                if (count == cachedShaderCount && cachedShaders.Length > 0) return cachedShaders;
                List<string> names = new(count);
                if (shaders != null)
                {
                    foreach (DictionaryEntry entry in shaders)
                        if (entry.Key != null) names.Add(entry.Key.ToString());
                }
                names.Sort(StringComparer.OrdinalIgnoreCase);
                cachedShaders = names.ToArray();
                cachedShaderCount = count;
                return cachedShaders;
            }
        }
        catch
        {
            return cachedShaders.Length > 0 ? cachedShaders : new[] { "Basic" };
        }
    }

    private static string[] SpriteNames()
    {
        try
        {
            object manager = Futile.atlasManager;
            FieldInfo field = FindField(manager?.GetType(), "_allElementsByName");
            IDictionary elements = field?.GetValue(manager) as IDictionary;
            int count = elements?.Count ?? 0;
            lock (CacheGate)
            {
                if (count == cachedSpriteCount && cachedSprites.Length > 0) return cachedSprites;
                List<string> names = new(count);
                if (elements != null)
                {
                    foreach (DictionaryEntry entry in elements)
                        if (entry.Key != null) names.Add(entry.Key.ToString());
                }
                names.Sort(StringComparer.OrdinalIgnoreCase);
                cachedSprites = names.ToArray();
                cachedSpriteCount = count;
                return cachedSprites;
            }
        }
        catch
        {
            return cachedSprites.Length > 0 ? cachedSprites : new[] { "Futile_White" };
        }
    }

    private static string[] PngAssetPaths(string current)
    {
        lock (CacheGate)
        {
            if (!pngAssetsScanned)
            {
                cachedPngAssets = ScanPngAssets();
                pngAssetsScanned = true;
            }

            string normalizedCurrent = NormalizeAssetPath(current);
            if (string.IsNullOrWhiteSpace(normalizedCurrent)) return (string[])cachedPngAssets.Clone();

            for (int i = 0; i < cachedPngAssets.Length; i++)
                if (string.Equals(cachedPngAssets[i], normalizedCurrent, StringComparison.OrdinalIgnoreCase))
                    return (string[])cachedPngAssets.Clone();

            string[] withCurrent = new string[cachedPngAssets.Length + 1];
            withCurrent[0] = normalizedCurrent;
            Array.Copy(cachedPngAssets, 0, withCurrent, 1, cachedPngAssets.Length);
            return withCurrent;
        }
    }

    private static string[] ScanPngAssets()
    {
        HashSet<string> visited = new(StringComparer.OrdinalIgnoreCase);
        HashSet<string> found = new(StringComparer.OrdinalIgnoreCase);
        ScanPngDirectory(string.Empty, 0, visited, found);
        List<string> sorted = new(found);
        sorted.Sort(StringComparer.OrdinalIgnoreCase);
        return sorted.ToArray();
    }

    private static void ScanPngDirectory(string relativeDirectory, int depth, HashSet<string> visited, HashSet<string> found)
    {
        if (depth > PngScanDepthLimit || found.Count >= PngScanEntryLimit) return;
        string normalizedDirectory = NormalizeAssetPath(relativeDirectory).Trim('/');
        if (!visited.Add(normalizedDirectory)) return;

        try
        {
            string[] files = AssetManager.ListDirectory(normalizedDirectory, false, false, false) ?? Array.Empty<string>();
            for (int i = 0; i < files.Length && found.Count < PngScanEntryLimit; i++)
            {
                string name = Path.GetFileName(files[i]);
                if (string.IsNullOrWhiteSpace(name) || !name.EndsWith(".png", StringComparison.OrdinalIgnoreCase)) continue;
                found.Add(CombineAssetPath(normalizedDirectory, name));
            }

            string[] folders = AssetManager.ListDirectory(normalizedDirectory, true, false, false) ?? Array.Empty<string>();
            for (int i = 0; i < folders.Length && found.Count < PngScanEntryLimit; i++)
            {
                string raw = (folders[i] ?? string.Empty).TrimEnd('/', '\\');
                string name = Path.GetFileName(raw);
                if (string.IsNullOrWhiteSpace(name)) continue;
                ScanPngDirectory(CombineAssetPath(normalizedDirectory, name), depth + 1, visited, found);
            }
        }
        catch (Exception error)
        {
            if (depth == 0)
                Plugin.Logger?.LogWarning("DevTool RegionKit AdvancedShader PNG asset scan failed: " + error.Message);
        }
    }

    private static string CombineAssetPath(string directory, string name)
    {
        if (string.IsNullOrWhiteSpace(directory)) return NormalizeAssetPath(name);
        return NormalizeAssetPath(directory.TrimEnd('/', '\\') + "/" + (name ?? string.Empty).TrimStart('/', '\\'));
    }

    private static string NormalizeAssetPath(string path) =>
        (path ?? string.Empty).Replace('\\', '/').Trim();

    private static void RefreshRuntime(PlacedObject target)
    {
        try
        {
            RainWorldGame game = RWCustom.Custom.rainWorld?.processManager?.currentMainLoop as RainWorldGame;
            if (game?.cameras == null) return;
            for (int c = 0; c < game.cameras.Length; c++)
            {
                Room room = game.cameras[c]?.room;
                if (room?.updateList == null) continue;
                for (int i = 0; i < room.updateList.Count; i++)
                {
                    object item = room.updateList[i];
                    if (item == null || !string.Equals(item.GetType().FullName, RuntimeTypeName, StringComparison.Ordinal)) continue;
                    FieldInfo pObjField = FindField(item.GetType(), "pObj");
                    if (!ReferenceEquals(pObjField?.GetValue(item), target)) continue;
                    MethodInfo refresh = item.GetType().GetMethod("CompletelyRefreshSprite",
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    refresh?.Invoke(item, null);
                    return;
                }
            }
        }
        catch (Exception error)
        {
            Plugin.Logger?.LogWarning("DevTool RegionKit AdvancedShader live refresh failed: " + error.Message);
        }
    }

    private static object ReadField(object instance, string name)
    {
        FieldInfo field = FindField(instance?.GetType(), name);
        return field?.GetValue(instance);
    }

    private static bool WriteField(object instance, string name, object value)
    {
        FieldInfo field = FindField(instance?.GetType(), name);
        if (field == null || field.IsInitOnly || (value != null && !field.FieldType.IsInstanceOfType(value))) return false;
        field.SetValue(instance, value);
        return true;
    }

    private static FieldInfo FindField(Type type, string name)
    {
        Type current = type;
        while (current != null)
        {
            FieldInfo field = current.GetField(name,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
            if (field != null) return field;
            current = current.BaseType;
        }
        return null;
    }

    private static string ReadString(object instance, string name, string fallback)
    {
        return ReadField(instance, name) as string ?? fallback;
    }

    private static bool ReadBool(object instance, string name)
    {
        return ReadField(instance, name) is bool value && value;
    }

    private static Vector2 ReadVector2(object instance, string name, Vector2 fallback)
    {
        return ReadField(instance, name) is Vector2 value ? value : fallback;
    }

    private static bool InvokeDataMethod(object data, string name, params object[] args)
    {
        try
        {
            Type[] signature = new Type[args?.Length ?? 0];
            for (int i = 0; i < signature.Length; i++) signature[i] = args[i]?.GetType() ?? typeof(object);
            MethodInfo method = data?.GetType().GetMethod(name,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                null,
                signature,
                null);
            if (method == null) return false;
            method.Invoke(data, args);
            return true;
        }
        catch (Exception error)
        {
            Plugin.Logger?.LogWarning("DevTool RegionKit AdvancedShader data method failed: " + error.Message);
            return false;
        }
    }

    private static bool TryParseColorKey(string key, out int index, out int component)
    {
        index = -1;
        component = -1;
        const string prefix = "rk.as.color.";
        if (!key.StartsWith(prefix, StringComparison.Ordinal)) return false;
        string[] parts = key.Substring(prefix.Length).Split('.');
        if (parts.Length != 2 || !int.TryParse(parts[0], out index)) return false;
        component = parts[1] switch
        {
            "r" => 0,
            "g" => 1,
            "b" => 2,
            "a" => 3,
            _ => -1
        };
        return component >= 0;
    }

    private static bool TryParseIndexedKey(string key, string prefix, int count, out int[] values)
    {
        values = Array.Empty<int>();
        if (!key.StartsWith(prefix, StringComparison.Ordinal)) return false;
        string[] parts = key.Substring(prefix.Length).Split('.');
        if (parts.Length != count) return false;
        int[] parsed = new int[count];
        for (int i = 0; i < count; i++)
            if (!int.TryParse(parts[i], out parsed[i])) return false;
        values = parsed;
        return true;
    }
}
