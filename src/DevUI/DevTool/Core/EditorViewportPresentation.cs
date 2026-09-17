namespace DryCycle.DevUI.DevTool.Core;

/// <summary>
/// Detached camera transform consumed by native scene-space editor tooling. The RWImGui assembly can
/// map Rain World coordinates without dereferencing RoomCamera/DevUI, preserving the one-way frontend
/// boundary while allowing native gizmos to replace DevInterface.Handle.
/// </summary>
public sealed class EditorViewportSnapshot
{
    public static readonly EditorViewportSnapshot Empty = new();

    public bool Available { get; init; }
    public float CameraX { get; init; }
    public float CameraY { get; init; }
    public float Width { get; init; }
    public float Height { get; init; }
}

public static class EditorViewportPresentationHub
{
    private static volatile EditorViewportSnapshot current = EditorViewportSnapshot.Empty;
    private static RoomCamera observedCamera;
    private static float observedX;
    private static float observedY;
    private static float observedWidth;
    private static float observedHeight;

    public static EditorViewportSnapshot Current => current;

    internal static void Publish(EditorSession session)
    {
        RoomCamera camera = session?.Owner?.game?.cameras != null && session.Owner.game.cameras.Length > 0
            ? session.Owner.game.cameras[0]
            : null;
        if (camera == null)
        {
            Clear();
            return;
        }

        UnityEngine.Vector2 size = camera.sSize;
        float x = camera.pos.x;
        float y = camera.pos.y;
        float width = size.x;
        float height = size.y;

        if (ReferenceEquals(observedCamera, camera) &&
            observedX == x && observedY == y &&
            observedWidth == width && observedHeight == height &&
            current.Available)
            return;

        current = new EditorViewportSnapshot
        {
            Available = width > 0f && height > 0f,
            CameraX = x,
            CameraY = y,
            Width = width,
            Height = height
        };
        observedCamera = camera;
        observedX = x;
        observedY = y;
        observedWidth = width;
        observedHeight = height;
    }

    internal static void Clear()
    {
        current = EditorViewportSnapshot.Empty;
        observedCamera = null;
        observedX = 0f;
        observedY = 0f;
        observedWidth = 0f;
        observedHeight = 0f;
    }
}
