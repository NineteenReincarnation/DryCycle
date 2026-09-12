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
/// RegionKit reference. Every persisted AdvancedShader.Data field is exposed through the rebuilt
/// inspector while RegionKit remains an optional dependency.
/// </summary>
public sealed class RegionKitAdvancedShaderInspectorAdapter : IObjectInspectorAdapter
{
    public const string DataTypeName = "RegionKit.Modules.Objects.AdvancedShaderController.AdvancedShader+Data";
    public const string RepresentationTypeName = "RegionKit.Modules.Objects.AdvancedShaderController.AdvancedShaderRepresentation";
    private const string RuntimeTypeName = "RegionKit.Modules.Objects.AdvancedShaderController.AdvancedShader";
    private const int PngScanDepthLimit = 32;
    private const int PngScanEntryLimit = 8192;

    public static readonly RegionKitAdvancedShaderInspectorAdapter Instance = new();
    private static bool registered;

    private static readonly object CacheGate = new();
    private static string[] cachedShaders = Array.Empty<string>();
    private static int cachedShaderCount = -1;
    private static string[] cachedSprites = Array.Empty<string>();
    private static int cachedSpriteCount = -1;
    private static string[] cachedPngAssets = Array.Empty<string>();
    private static bool pngAssetsScanned;

    static RegionKitAdvancedShaderInspectorAdapter() => EnsureRegistered();

    private RegionKitAdvancedShaderInspectorAdapter() { }

    public static void EnsureRegistered()
    {
        if (registered) return;
        registered = true;
        ObjectInspectorRegistry.Register(Instance, 5000);
    }

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
            result.Add(StringProperty("rk.as.filePath", "Manual PNG Path", "Material", filePath,
                "AssetManager-relative PNG path. The searchable PNG Asset field above is preferred when available."));
        }
        else
        {
            string spriteName = ReadString(data, "spriteName", "Futile_White");
            result.Add(EnumProperty("rk.as.spriteName", "Sprite", "Material", spriteName, SpriteNames()));
        }

        object container = ReadMember(data, "container");
        Type containerType = container?.GetType();
        if (containerType?.IsEnum == true)
        {
            string[] options = Enum.GetNames(containerType);
            int selected = FindOption(options, container.ToString());
            result.Add(new EditorPropertySnapshot
            {
                Key = "rk.as.container", DisplayName = "Container", Group = "Material",
                Source = "RegionKit AdvancedShader.Data.container", Kind = EditorPropertyKind.Enum,
                IntegerValue = selected, StringValue = container.ToString(), Options = options
            });
        }

        object shapeLock = ReadMember(data, "shapeLock");
        Type shapeType = shapeLock?.GetType();
        if (shapeType?.IsEnum == true)
        {
            string[] options = Enum.GetNames(shapeType);
            result.Add(new EditorPropertySnapshot
            {
                Key = "rk.as.shapeLock", DisplayName = "Shape Lock", Group = "Geometry",
                Source = "RegionKit AdvancedShader.Data.shapeLock (runtime editing state; not serialized by RK)",
                Kind = EditorPropertyKind.Enum, IntegerValue = FindOption(options, shapeLock.ToString()),
                StringValue = shapeLock.ToString(), Options = options
            });
        }

        if (ReadMember(data, "panelPos") is Vector2 panelPos)
            result.Add(VectorProperty("rk.as.panelPos", "Legacy Panel Position", "Layout", panelPos,
                "Preserved because RegionKit serializes panelPos."));

        if (ReadMember(data, "vertices") is Vector2[] vertices)
        {
            for (int i = 0; i < vertices.Length; i++)
                result.Add(VectorProperty("rk.as.vertex." + i, "Vertex " + i, "Geometry", vertices[i], "RegionKit AdvancedShader.Data.vertices"));
        }

        result.Add(BooleanProperty("rk.as.restrictColors", "Restrict Colors", "Colors", ReadBool(data, "restrictColors")));
        result.Add(BooleanProperty("rk.as.lockColors", "Lock Colors", "Colors", ReadBool(data, "lockColors")));
        result.Add(ActionProperty("rk.as.resetColors", "Reset Colors", "Colors", "RegionKit AdvancedShader.Data.ResetColors"));
        if (ReadMember(data, "colors") is Color[] colors)
        {
            for (int i = 0; i < colors.Length; i++)
                result.Add(ColorProperty("rk.as.color." + i, "Vertex " + i + " Color", "Colors", colors[i]));
        }

        result.Add(BooleanProperty("rk.as.restrictUVs", "Restrict UVs", "UVs", ReadBool(data, "restrictUVs")));
        result.Add(BooleanProperty("rk.as.lockUVs", "Lock UVs", "UVs", ReadBool(data, "lockUVs")));
        if (ReadMember(data, "uvs") is Vector2[][] uvs)
        {
            for (int channel = 0; channel < uvs.Length; channel++)
            {
                result.Add(ActionProperty("rk.as.resetUv." + channel, "Reset UV Channel " + channel, "UV Channel " + channel,
                    "RegionKit AdvancedShader.Data.ResetUVs(" + channel + ")"));
                Vector2[] channelValues = uvs[channel] ?? Array.Empty<Vector2>();
                for (int vertex = 0; vertex < channelValues.Length; vertex++)
                    result.Add(VectorProperty("rk.as.uv." + channel + "." + vertex, "Vertex " + vertex, "UV Channel " + channel,
                        channelValues[vertex], "RegionKit AdvancedShader.Data.uvs"));
            }
        }

        return result;
    }

    public bool TrySetValue(PlacedObject target, string key, EditorPropertyValue value)
    {
        if (!CanInspect(target) || string.IsNullOrEmpty(key)) return false;
        object data = target.data;
        bool refreshSprite = false;
        bool success = false;

        switch (key)
        {
            case "rk.as.shader":
                success = SetEnumString(data, "shader", value, ShaderNames()); refreshSprite = success; break;
            case "rk.as.useFile":
                success = value.Kind == EditorPropertyKind.Boolean && WriteMember(data, "useFile", value.Boolean); refreshSprite = success; break;
            case "rk.as.fileAsset":
            {
                string[] paths = PngAssetPaths(ReadString(data, "filePath", string.Empty));
                success = SetEnumString(data, "filePath", value, paths); refreshSprite = success; break;
            }
            case "rk.as.filePath":
                success = value.Kind == EditorPropertyKind.String && WriteMember(data, "filePath", value.Text ?? string.Empty); refreshSprite = success; break;
            case "rk.as.spriteName":
                success = SetEnumString(data, "spriteName", value, SpriteNames());
                if (success) { Invoke(data, "ResetUVs", 0); refreshSprite = true; }
                break;
            case "rk.as.container":
                success = SetEnumMember(data, "container", value); refreshSprite = success; break;
            case "rk.as.shapeLock":
                success = SetEnumMember(data, "shapeLock", value); break;
            case "rk.as.panelPos":
                success = value.Kind == EditorPropertyKind.Vector2 && WriteMember(data, "panelPos", new Vector2(value.X, value.Y)); break;
            case "rk.as.restrictColors":
                success = value.Kind == EditorPropertyKind.Boolean && WriteMember(data, "restrictColors", value.Boolean); break;
            case "rk.as.lockColors":
                success = value.Kind == EditorPropertyKind.Boolean && WriteMember(data, "lockColors", value.Boolean); break;
            case "rk.as.resetColors":
                success = value.Kind == EditorPropertyKind.Action && Invoke(data, "ResetColors"); break;
            case "rk.as.restrictUVs":
                success = value.Kind == EditorPropertyKind.Boolean && WriteMember(data, "restrictUVs", value.Boolean); break;
            case "rk.as.lockUVs":
                success = value.Kind == EditorPropertyKind.Boolean && WriteMember(data, "lockUVs", value.Boolean); break;
            default:
                if (TryParseIndexedKey(key, "rk.as.vertex.", 1, out int[] vertexIndex))
                    success = SetVectorArray(data, "vertices", vertexIndex[0], value);
                else if (TryParseIndexedKey(key, "rk.as.color.", 1, out int[] colorIndex))
                    success = SetColorArray(data, "colors", colorIndex[0], value);
                else if (TryParseIndexedKey(key, "rk.as.resetUv.", 1, out int[] resetUv) && value.Kind == EditorPropertyKind.Action)
                    success = Invoke(data, "ResetUVs", resetUv[0]);
                else if (TryParseIndexedKey(key, "rk.as.uv.", 2, out int[] uvIndex))
                    success = SetUv(data, uvIndex[0], uvIndex[1], value);
                break;
        }

        if (success && refreshSprite) RefreshRuntime(target);
        return success;
    }

    private static EditorPropertySnapshot StringProperty(string key, string name, string group, string value, string source) => new()
    { Key = key, DisplayName = name, Group = group, Source = source, Kind = EditorPropertyKind.String, StringValue = value ?? string.Empty };
    private static EditorPropertySnapshot BooleanProperty(string key, string name, string group, bool value) => new()
    { Key = key, DisplayName = name, Group = group, Source = "RegionKit AdvancedShader", Kind = EditorPropertyKind.Boolean, BooleanValue = value };
    private static EditorPropertySnapshot VectorProperty(string key, string name, string group, Vector2 value, string source) => new()
    { Key = key, DisplayName = name, Group = group, Source = source, Kind = EditorPropertyKind.Vector2, X = value.x, Y = value.y };
    private static EditorPropertySnapshot ColorProperty(string key, string name, string group, Color value) => new()
    { Key = key, DisplayName = name, Group = group, Source = "RegionKit AdvancedShader.Data.colors", Kind = EditorPropertyKind.Color, X = value.r, Y = value.g, Z = value.b, W = value.a };
    private static EditorPropertySnapshot ActionProperty(string key, string name, string group, string source) => new()
    { Key = key, DisplayName = name, Group = group, Source = source, Kind = EditorPropertyKind.Action };

    private static EditorPropertySnapshot EnumProperty(string key, string name, string group, string current, string[] options) => new()
    { Key = key, DisplayName = name, Group = group, Source = "RegionKit AdvancedShader", Kind = EditorPropertyKind.Enum,
      IntegerValue = FindOption(options, current), StringValue = current ?? string.Empty, Options = options ?? Array.Empty<string>() };

    private static string[] ShaderNames()
    {
        try
        {
            int count = Custom.rainWorld?.Shaders?.Count ?? 0;
            lock (CacheGate)
            {
                if (cachedShaderCount == count && cachedShaders.Length > 0) return Clone(cachedShaders);
                List<string> names = new(count);
                if (Custom.rainWorld?.Shaders != null) foreach (string name in Custom.rainWorld.Shaders.Keys) names.Add(name);
                names.Sort(StringComparer.OrdinalIgnoreCase); cachedShaders = names.ToArray(); cachedShaderCount = count; return Clone(cachedShaders);
            }
        }
        catch { return Array.Empty<string>(); }
    }

    private static string[] SpriteNames()
    {
        try
        {
            IDictionary dictionary = Futile.atlasManager?._allElementsByName;
            int count = dictionary?.Count ?? 0;
            lock (CacheGate)
            {
                if (cachedSpriteCount == count && cachedSprites.Length > 0) return Clone(cachedSprites);
                List<string> names = new(count);
                if (dictionary != null) foreach (object key in dictionary.Keys) if (key is string text) names.Add(text);
                names.Sort(StringComparer.OrdinalIgnoreCase); cachedSprites = names.ToArray(); cachedSpriteCount = count; return Clone(cachedSprites);
            }
        }
        catch { return Array.Empty<string>(); }
    }

    private static string[] PngAssetPaths(string current)
    {
        lock (CacheGate)
        {
            if (!pngAssetsScanned)
            {
                pngAssetsScanned = true;
                try
                {
                    string root = AssetManager.ResolveFilePath(string.Empty);
                    List<string> files = new();
                    ScanPng(root, root, 0, files);
                    files.Sort(StringComparer.OrdinalIgnoreCase);
                    cachedPngAssets = files.ToArray();
                }
                catch (Exception error) { Plugin.Logger?.LogWarning("DevTool AdvancedShader PNG scan failed: " + error.Message); }
            }
            if (string.IsNullOrEmpty(current) || Array.IndexOf(cachedPngAssets, current) >= 0) return Clone(cachedPngAssets);
            string[] extended = new string[cachedPngAssets.Length + 1]; extended[0] = current;
            Array.Copy(cachedPngAssets, 0, extended, 1, cachedPngAssets.Length); return extended;
        }
    }

    private static void ScanPng(string root, string directory, int depth, List<string> output)
    {
        if (depth > PngScanDepthLimit || output.Count >= PngScanEntryLimit || !Directory.Exists(directory)) return;
        string[] files;
        try { files = Directory.GetFiles(directory, "*.png", SearchOption.TopDirectoryOnly); } catch { return; }
        for (int i = 0; i < files.Length && output.Count < PngScanEntryLimit; i++)
        {
            string relative = files[i].Substring(root.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Replace('\\', '/');
            output.Add(relative);
        }
        string[] directories;
        try { directories = Directory.GetDirectories(directory); } catch { return; }
        for (int i = 0; i < directories.Length && output.Count < PngScanEntryLimit; i++) ScanPng(root, directories[i], depth + 1, output);
    }

    private static bool SetEnumString(object data, string member, EditorPropertyValue value, string[] options)
    {
        if (value.Kind != EditorPropertyKind.Enum || value.Integer < 0 || value.Integer >= (options?.Length ?? 0)) return false;
        return WriteMember(data, member, options[value.Integer]);
    }

    private static bool SetEnumMember(object data, string member, EditorPropertyValue value)
    {
        object current = ReadMember(data, member); Type type = current?.GetType();
        if (type?.IsEnum != true || value.Kind != EditorPropertyKind.Enum) return false;
        Array values = Enum.GetValues(type); if (value.Integer < 0 || value.Integer >= values.Length) return false;
        return WriteMember(data, member, values.GetValue(value.Integer));
    }

    private static bool SetVectorArray(object data, string member, int index, EditorPropertyValue value)
    {
        if (value.Kind != EditorPropertyKind.Vector2 || ReadMember(data, member) is not Vector2[] array || index < 0 || index >= array.Length) return false;
        array[index] = new Vector2(value.X, value.Y); return true;
    }
    private static bool SetColorArray(object data, string member, int index, EditorPropertyValue value)
    {
        if (value.Kind != EditorPropertyKind.Color || ReadMember(data, member) is not Color[] array || index < 0 || index >= array.Length) return false;
        array[index] = new Color(value.X, value.Y, value.Z, value.W); return true;
    }
    private static bool SetUv(object data, int channel, int vertex, EditorPropertyValue value)
    {
        if (value.Kind != EditorPropertyKind.Vector2 || ReadMember(data, "uvs") is not Vector2[][] uvs || channel < 0 || channel >= uvs.Length ||
            uvs[channel] == null || vertex < 0 || vertex >= uvs[channel].Length) return false;
        uvs[channel][vertex] = new Vector2(value.X, value.Y); return true;
    }

    private static void RefreshRuntime(PlacedObject target)
    {
        try
        {
            global::Room room = DevTool.Core.DevToolRuntime.ActiveSession?.Owner?.room;
            if (room?.updateList == null) return;
            for (int i = 0; i < room.updateList.Count; i++)
            {
                object candidate = room.updateList[i];
                if (candidate == null || !string.Equals(candidate.GetType().FullName, RuntimeTypeName, StringComparison.Ordinal)) continue;
                if (ReadMember(candidate, "pObj") is PlacedObject owner && ReferenceEquals(owner, target)) { Invoke(candidate, "CompletelyRefreshSprite"); return; }
            }
        }
        catch (Exception error) { Plugin.Logger?.LogWarning("DevTool AdvancedShader runtime refresh failed: " + error.Message); }
    }

    private static bool Invoke(object target, string name, params object[] args)
    {
        if (target == null) return false;
        try
        {
            MethodInfo[] methods = target.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            for (int i = 0; i < methods.Length; i++)
            {
                MethodInfo method = methods[i]; if (method.Name != name || method.GetParameters().Length != args.Length) continue;
                method.Invoke(target, args); return true;
            }
        }
        catch (Exception error) { Plugin.Logger?.LogWarning("DevTool AdvancedShader invoke failed: " + error.Message); }
        return false;
    }

    private static object ReadMember(object target, string name)
    {
        if (target == null) return null;
        for (Type type = target.GetType(); type != null; type = type.BaseType)
        {
            FieldInfo field = type.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
            if (field != null) return field.GetValue(target);
            PropertyInfo property = type.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
            if (property?.CanRead == true) return property.GetValue(target, null);
        }
        return null;
    }
    private static bool WriteMember(object target, string name, object value)
    {
        if (target == null) return false;
        for (Type type = target.GetType(); type != null; type = type.BaseType)
        {
            FieldInfo field = type.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
            if (field != null && !field.IsInitOnly) { field.SetValue(target, value); return true; }
            PropertyInfo property = type.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
            if (property?.CanWrite == true) { property.SetValue(target, value, null); return true; }
        }
        return false;
    }

    private static bool ReadBool(object target, string name) => ReadMember(target, name) is bool value && value;
    private static string ReadString(object target, string name, string fallback) => ReadMember(target, name) as string ?? fallback;
    private static int FindOption(string[] options, string value)
    { for (int i = 0; i < (options?.Length ?? 0); i++) if (string.Equals(options[i], value, StringComparison.Ordinal)) return i; return -1; }
    private static bool TryParseIndexedKey(string key, string prefix, int count, out int[] values)
    {
        values = Array.Empty<int>(); if (!key.StartsWith(prefix, StringComparison.Ordinal)) return false;
        string[] parts = key.Substring(prefix.Length).Split('.'); if (parts.Length != count) return false;
        values = new int[count]; for (int i = 0; i < count; i++) if (!int.TryParse(parts[i], out values[i])) return false; return true;
    }
    private static string[] Clone(string[] values)
    { if (values == null || values.Length == 0) return Array.Empty<string>(); string[] copy = new string[values.Length]; Array.Copy(values, copy, values.Length); return copy; }
}
