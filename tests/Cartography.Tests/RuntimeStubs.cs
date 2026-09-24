// Only external game/editor boundaries are faked. The region loader, runtime state machine,
// authoring commands, persistence, preview rendering and undo history are production code.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using DryCycle.DevUI.DevTool.Core;

public sealed class World { public string name; public TestGame game = new(); }
public sealed class TestGame { public SlugcatStats.Name StoryCharacter = new("Yellow", false), TimelinePoint = new("Yellow", false); public TestRainWorld rainWorld = new(); }
public sealed class TestRainWorld { public TestTranslator inGameTranslator = new(); }
public sealed class TestTranslator
{
    public readonly Dictionary<string, string> Translations = new();
    public bool TryTranslate(string text, out string translated) => Translations.TryGetValue(text, out translated);
}
public static class SlugcatStats
{
    public sealed class Name { public string value; public Name(string value, bool register) { this.value = value; } }
}
public static class Region
{
    public static string GetRegionFullName(string code, SlugcatStats.Name campaign) =>
        File.ReadAllText(AssetManager.ResolveFilePath("world/" + code + "/displayname.txt")).Trim();
}
public static class AssetManager
{
    public static string ResolveFilePath(string relative) => Path.Combine(RWCustom.Custom.Root, relative.ToLowerInvariant().Replace('/', Path.DirectorySeparatorChar));
    public static string[] ListDirectory(string relative, bool directories = false)
    {
        string path = ResolveFilePath(relative);
        if (!Directory.Exists(path)) return Array.Empty<string>();
        return directories ? Directory.GetDirectories(path) : Directory.GetFiles(path);
    }
}
namespace RWCustom { public static class Custom { public static string Root; public static string RootFolderDirectory() => Root; } }
namespace BepInEx { public static class Paths { public static string ConfigPath; } }
namespace UnityEngine { public static class Time { public static int frameCount => Environment.TickCount; } }
namespace DryCycle
{
    public static class Plugin { public static TestLog Logger = new(); }
    public sealed class TestLog
    {
        public readonly List<string> Errors = new();
        public void LogError(object value) { Errors.Add(value.ToString()); }
        public void LogWarning(object value) { }
    }
}
namespace DryCycle.DevUI.DevTool.Map.Cartography
{
    internal static class CartographyGameAssets { internal static void Load(CartographySource source) { } }
}
namespace DryCycle.DevUI.DevTool.Core
{
    public enum EditorToolMode { Map }
    public enum EditorDocumentKind { RegionMap }
    public readonly struct EditorDocumentKey
    {
        public readonly EditorDocumentKind Kind; public readonly string Identity;
        public EditorDocumentKey(EditorDocumentKind kind, string identity) { Kind = kind; Identity = identity; }
    }
    public sealed class EditorSession
    {
        public global::World World; public EditorToolMode ToolMode = EditorToolMode.Map;
        public History.EditorHistoryService History = new(64);
    }
    public static class EditorUiModeState { public static bool UseVanilla => false; public static bool OverlayHidden => false; }
    public enum EditorRevisionKind { Shell }
    public static class EditorRevisionHub { public static void Mark(EditorSession session, EditorRevisionKind kind) { } public static void MarkWorkspace(EditorSession session) { } }
    public static class DevToolSessionHub { public static EditorSession Current => null; }
    public static class EditorPresentationHub { public static void ObservePublishedHistoryRevision(EditorSession session, long revision) { } }
    public static class DevToolSubsystemCoordinator { public static void ReconcileRuntimeAfterHistoryRestore(EditorSession session) { } }
}
namespace DryCycle.DevUI.DevTool.Compatibility
{
    public static class LegacyDevUiQuiescenceController { public static bool TryDeferRefresh(EditorSession session) => true; }
    public static class NativeLegacyPresentationInvalidation { public static void RefreshCurrentSoundOrTriggerFallback(EditorSession session) { } }
}
