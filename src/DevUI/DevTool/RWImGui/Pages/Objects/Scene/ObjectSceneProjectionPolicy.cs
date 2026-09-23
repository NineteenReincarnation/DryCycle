using System;
using DryCycle.DevUI.DevTool.Core;
using Num = System.Numerics;

namespace DryCycle.DevUI.DevTool.RWImGui;

internal enum ObjectSceneSemanticZoomBand
{
    Near,
    Mid,
    Far
}

/// <summary>
/// Shared scene-projection policy layered on top of logical visibility.
///
/// Visibility answers whether an object is Hidden/Ghost/Normal/Hovered/Selected. Projection answers
/// whether that logical object should currently have a scene-space presentation at this zoom. Keeping
/// this decision outside the label renderer prevents invisible semantic-zoom objects from remaining
/// active in marquee/hit-test paths.
/// </summary>
internal static class ObjectSceneProjectionPolicy
{
    private const float MidZoomEnter = 1.55f;
    private const float MidZoomExit = 1.30f;
    private const float FarZoomEnter = 2.50f;
    private const float FarZoomExit = 2.20f;

    private static ObjectSceneSemanticZoomBand band;
    private static bool initialized;

    internal static ObjectSceneSemanticZoomBand Band => band;

    internal static void Update(EditorViewportSnapshot viewport, Num.Vector2 display)
    {
        if (viewport?.Available != true || display.X <= 1f || display.Y <= 1f)
            return;

        float unitsPerPixelX = viewport.Width / Math.Max(1f, display.X);
        float unitsPerPixelY = viewport.Height / Math.Max(1f, display.Y);
        float unitsPerPixel = Math.Max(unitsPerPixelX, unitsPerPixelY);

        if (!initialized)
        {
            band = unitsPerPixel >= FarZoomEnter
                ? ObjectSceneSemanticZoomBand.Far
                : unitsPerPixel >= MidZoomEnter
                    ? ObjectSceneSemanticZoomBand.Mid
                    : ObjectSceneSemanticZoomBand.Near;
            initialized = true;
            return;
        }

        band = band switch
        {
            ObjectSceneSemanticZoomBand.Near when unitsPerPixel >= FarZoomEnter => ObjectSceneSemanticZoomBand.Far,
            ObjectSceneSemanticZoomBand.Near when unitsPerPixel >= MidZoomEnter => ObjectSceneSemanticZoomBand.Mid,
            ObjectSceneSemanticZoomBand.Mid when unitsPerPixel >= FarZoomEnter => ObjectSceneSemanticZoomBand.Far,
            ObjectSceneSemanticZoomBand.Mid when unitsPerPixel <= MidZoomExit => ObjectSceneSemanticZoomBand.Near,
            ObjectSceneSemanticZoomBand.Far when unitsPerPixel <= MidZoomExit => ObjectSceneSemanticZoomBand.Near,
            ObjectSceneSemanticZoomBand.Far when unitsPerPixel <= FarZoomExit => ObjectSceneSemanticZoomBand.Mid,
            _ => band
        };
    }

    internal static bool ShouldPresent(EditorObjectSnapshot item)
    {
        if (item == null) return false;
        return ShouldPresent(item, ObjectSceneVisibilityState.Resolve(item));
    }

    internal static bool ShouldPresent(EditorObjectSnapshot item, ObjectSceneVisibility visibility)
    {
        if (item == null || visibility == ObjectSceneVisibility.Hidden)
            return false;

        // Direct user intent must beat density reduction. Search results, the focused category,
        // selection and hover remain discoverable at every zoom level.
        if (visibility is ObjectSceneVisibility.Selected or ObjectSceneVisibility.Hovered ||
            ObjectSceneVisibilityState.IsExplicitlyEmphasized(item))
            return true;

        return band switch
        {
            ObjectSceneSemanticZoomBand.Far => item.Importance >= 2,
            ObjectSceneSemanticZoomBand.Mid => item.Importance >= 1,
            _ => true
        };
    }

    internal static bool IsInteractive(EditorObjectSnapshot item)
    {
        if (item == null) return false;
        ObjectSceneVisibility visibility = ObjectSceneVisibilityState.Resolve(item);
        if (!ShouldPresent(item, visibility)) return false;
        return visibility is ObjectSceneVisibility.Normal or
            ObjectSceneVisibility.Hovered or
            ObjectSceneVisibility.Selected;
    }

    internal static void Reset()
    {
        band = ObjectSceneSemanticZoomBand.Near;
        initialized = false;
    }
}
