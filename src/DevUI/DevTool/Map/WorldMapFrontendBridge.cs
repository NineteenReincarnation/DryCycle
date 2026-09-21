using System;
using DryCycle.DevUI.DevTool.Core;

namespace DryCycle.DevUI.DevTool.Map;

/// <summary>
/// Optional frontend callbacks for the core World Map model/presentation layer.
///
/// DryCycle.dll must not reference the separate DryCycle.DevTool.RWImGui assembly. The frontend
/// registers lightweight policies here at runtime; when no frontend is loaded the core keeps its
/// normal, fully functional behavior.
/// </summary>
internal static class WorldMapFrontendBridge
{
    private static Func<EditorSession, bool> shouldPublish;
    private static Func<EditorSession, bool> shouldPrimeGeometry;
    private static Func<int, EditorMapRoomVisualSnapshot, EditorMapRoomVisualSnapshot> enhanceRaster;
    private static Func<global::World, bool> shouldProcessGeometryBackground;

    internal static bool ShouldPublish(EditorSession session) =>
        shouldPublish?.Invoke(session) ?? true;

    internal static bool ShouldPrimeGeometry(EditorSession session) =>
        shouldPrimeGeometry?.Invoke(session) ?? true;

    internal static EditorMapRoomVisualSnapshot EnhanceRaster(
        int roomIndex,
        EditorMapRoomVisualSnapshot original) =>
        enhanceRaster?.Invoke(roomIndex, original) ?? original ?? EditorMapRoomVisualSnapshot.Empty;

    internal static bool ShouldProcessGeometryBackground(global::World world) =>
        shouldProcessGeometryBackground?.Invoke(world) ?? true;

    internal static void RegisterShouldPublish(Func<EditorSession, bool> callback) =>
        shouldPublish = callback;

    internal static void UnregisterShouldPublish(Func<EditorSession, bool> callback)
    {
        if (Delegate.Equals(shouldPublish, callback))
            shouldPublish = null;
    }

    internal static void RegisterShouldPrimeGeometry(Func<EditorSession, bool> callback) =>
        shouldPrimeGeometry = callback;

    internal static void UnregisterShouldPrimeGeometry(Func<EditorSession, bool> callback)
    {
        if (Delegate.Equals(shouldPrimeGeometry, callback))
            shouldPrimeGeometry = null;
    }

    internal static void RegisterRasterEnhancer(
        Func<int, EditorMapRoomVisualSnapshot, EditorMapRoomVisualSnapshot> callback) =>
        enhanceRaster = callback;

    internal static void UnregisterRasterEnhancer(
        Func<int, EditorMapRoomVisualSnapshot, EditorMapRoomVisualSnapshot> callback)
    {
        if (Delegate.Equals(enhanceRaster, callback))
            enhanceRaster = null;
    }

    internal static void RegisterGeometryBackgroundBudget(Func<global::World, bool> callback) =>
        shouldProcessGeometryBackground = callback;

    internal static void UnregisterGeometryBackgroundBudget(Func<global::World, bool> callback)
    {
        if (Delegate.Equals(shouldProcessGeometryBackground, callback))
            shouldProcessGeometryBackground = null;
    }
}
