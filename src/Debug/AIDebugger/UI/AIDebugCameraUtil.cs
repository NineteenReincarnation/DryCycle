using System;
using UnityEngine;
using Num = System.Numerics;

namespace DryCycle.Debugging.AI;

// Rain World can render two RoomCamera instances into the top/bottom halves of the
// same Futile render target. All Observatory world overlays and picking go through
// this adapter so camera selection and split-screen offsets stay consistent.
internal static class AIDebugCameraUtil
{
    internal static RoomCamera Primary(RainWorldGame game)
    {
        if (game?.cameras == null) return null;
        for (int i = 0; i < game.cameras.Length; i++)
            if (game.cameras[i]?.room != null) return game.cameras[i];
        return null;
    }

    internal static RoomCamera ForCreature(RainWorldGame game, AbstractCreature creature)
    {
        if (game?.cameras == null || creature == null) return Primary(game);
        Room realizedRoom = creature.realizedCreature?.room;
        AbstractRoom abstractRoom = creature.Room;
        RoomCamera fallback = null;
        for (int i = 0; i < game.cameras.Length; i++)
        {
            RoomCamera camera = game.cameras[i];
            if (camera?.room == null) continue;
            bool sameRoom = realizedRoom != null
                ? ReferenceEquals(camera.room, realizedRoom)
                : ReferenceEquals(camera.room.abstractRoom, abstractRoom);
            if (!sameRoom) continue;
            fallback ??= camera;
            if (ReferenceEquals(camera.followAbstractCreature, creature)) return camera;
        }
        return fallback ?? Primary(game);
    }

    internal static RoomCamera ForRoomName(RainWorldGame game, string roomName)
    {
        if (game?.cameras == null || string.IsNullOrEmpty(roomName)) return Primary(game);
        for (int i = 0; i < game.cameras.Length; i++)
        {
            RoomCamera camera = game.cameras[i];
            if (camera?.room?.abstractRoom?.name == roomName) return camera;
        }
        return null;
    }

    internal static RoomCamera ForMouse(RainWorldGame game)
    {
        if (game?.cameras == null || game.cameras.Length == 0) return null;
        RoomCamera first = Primary(game);
        if (first == null) return null;

        bool split = false;
        for (int i = 0; i < game.cameras.Length; i++)
            if (game.cameras[i]?.splitScreenMode == true) { split = true; break; }
        if (!split) return first;

        // Futile uses camera 0 for the upper half and camera 1 for the lower half.
        bool upper = Input.mousePosition.y >= Screen.height * 0.5f;
        int wantedNumber = upper ? 0 : 1;
        for (int i = 0; i < game.cameras.Length; i++)
            if (game.cameras[i]?.room != null && game.cameras[i].cameraNumber == wantedNumber)
                return game.cameras[i];
        return first;
    }

    internal static Num.Vector2 WorldToImGui(RoomCamera camera, Vector2 world)
    {
        if (camera == null) return Num.Vector2.Zero;
        Vector2 local = world - camera.pos;
        float sx = Screen.width / Mathf.Max(1f, camera.sSize.x);
        float sy = Screen.height / Mathf.Max(1f, camera.sSize.y);
        float x = local.x * sx;
        float y = Screen.height - local.y * sy;
        if (camera.splitScreenMode)
            y += camera.cameraNumber == 0 ? -Screen.height * 0.25f : Screen.height * 0.25f;
        return new Num.Vector2(x, y);
    }

    internal static Vector2 MouseWorld(RoomCamera camera)
    {
        if (camera == null) return Vector2.zero;
        float sx = Screen.width / Mathf.Max(1f, camera.sSize.x);
        float sy = Screen.height / Mathf.Max(1f, camera.sSize.y);
        Vector3 mouse = Input.mousePosition;
        float localX = mouse.x / Mathf.Max(0.0001f, sx);
        float localY;
        if (!camera.splitScreenMode)
            localY = mouse.y / Mathf.Max(0.0001f, sy);
        else if (camera.cameraNumber == 0)
            localY = (mouse.y - Screen.height * 0.25f) / Mathf.Max(0.0001f, sy);
        else
            localY = (mouse.y + Screen.height * 0.25f) / Mathf.Max(0.0001f, sy);
        return camera.pos + new Vector2(localX, localY);
    }

    internal static float ScreenScale(RoomCamera camera)
    {
        if (camera == null) return 1f;
        return 0.5f * (Screen.width / Mathf.Max(1f, camera.sSize.x) +
                       Screen.height / Mathf.Max(1f, camera.sSize.y));
    }
}
