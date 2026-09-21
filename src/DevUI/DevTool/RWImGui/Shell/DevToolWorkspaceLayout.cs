using System;
using Num = System.Numerics;

namespace DryCycle.DevUI.DevTool.RWImGui;

/// <summary>
/// Shared geometry helpers for page-owned central workspaces.
///
/// This contains layout only; individual pages remain responsible for rendering their own canvas,
/// preview or matrix. Keeping the rectangle calculation here prevents those pages from depending on
/// DevToolOverlay merely to obtain a common centered workspace size.
/// </summary>
internal static class DevToolWorkspaceLayout
{
    internal static void GetCentralRect(Num.Vector2 display, out Num.Vector2 position, out Num.Vector2 size)
    {
        float width = Math.Min(900f, Math.Max(520f, display.X * 0.58f));
        float height = Math.Min(620f, Math.Max(360f, display.Y * 0.64f));
        position = new Num.Vector2(
            Math.Max(180f, (display.X - width) * 0.5f),
            Math.Max(88f, (display.Y - height) * 0.5f));
        size = new Num.Vector2(width, height);
    }
}
