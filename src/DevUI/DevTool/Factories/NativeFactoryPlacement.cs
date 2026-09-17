using DryCycle.DevUI.DevTool.Core;
using UnityEngine;

namespace DryCycle.DevUI.DevTool.Factories;

/// <summary>
/// Backend-only placement policy shared by native authoring factories. It deliberately knows
/// nothing about DevInterface panels or RWImGui layout: world placement comes from the current
/// camera/cursor, while the legacy panel coordinate is only retained as serialized compatibility
/// metadata for the day a document is opened in vanilla DevUI.
/// </summary>
internal static class NativeFactoryPlacement
{
    internal static Vector2 WorldCursor(EditorSession session)
    {
        if (session?.Owner == null)
            return Vector2.zero;

        RoomCamera[] cameras = session.Owner.game?.cameras;
        RoomCamera camera = cameras != null && cameras.Length > 0 ? cameras[0] : null;
        if (camera != null)
            return camera.pos + session.Owner.mousePos;

        global::Room room = session.Room;
        return room == null
            ? Vector2.zero
            : new Vector2(room.PixelWidth * 0.5f, room.PixelHeight * 0.5f);
    }

    internal static Vector2 LegacyPanelSlot(int ordinal)
    {
        int safe = Mathf.Max(0, ordinal);
        const int columns = 10;
        return new Vector2(
            40f + (safe % columns) * 14f,
            40f + (safe / columns) * 14f);
    }
}
