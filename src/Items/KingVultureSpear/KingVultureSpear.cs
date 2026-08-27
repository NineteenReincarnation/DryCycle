using RWCustom;
using UnityEngine;

namespace DryCycle.Items.KingVultureSpear;

internal sealed class KingVultureSpear : Spear
{
    private const int TuskSegments = 15;

    public KingVultureSpear(AbstractPhysicalObject abstractPhysicalObject, World world)
        : base(abstractPhysicalObject, world)
    {
    }

    private AbstractKingVultureSpear Data => abstractPhysicalObject as AbstractKingVultureSpear;

    public override void InitiateSprites(RoomCamera.SpriteLeaser sLeaser, RoomCamera rCam)
    {
        // This intentionally mirrors KingTusks.Tusk.InitiateSprites instead of
        // reusing Spear's sprite. The body and detail meshes must have the same
        // topology/UV layout as the original tusk for the KingTusk shader pattern
        // to line up 1:1.
        sLeaser.sprites = new FSprite[2];
        sLeaser.sprites[0] = TriangleMesh.MakeLongMesh(TuskSegments, pointyTip: true, customColor: true);
        sLeaser.sprites[1] = TriangleMesh.MakeLongMesh(TuskSegments, pointyTip: true, customColor: true);
        sLeaser.sprites[1].shader = rCam.game.rainWorld.Shaders["KingTusk"];

        ApplyPalette(sLeaser, rCam, rCam.currentPalette);
        AddToContainer(sLeaser, rCam, null);
    }

    public override void AddToContainer(
        RoomCamera.SpriteLeaser sLeaser,
        RoomCamera rCam,
        FContainer newContainer)
    {
        // Vanilla KingTusks.Tusk places both the body and KingTusk detail layer
        // in Midground. Do exactly the same for the detached item.
        FContainer container = newContainer ?? rCam.ReturnFContainer("Midground");

        for (int i = 0; i < sLeaser.sprites.Length; i++)
        {
            sLeaser.sprites[i].RemoveFromContainer();
            container.AddChild(sLeaser.sprites[i]);
        }
    }

    public override void DrawSprites(
        RoomCamera.SpriteLeaser sLeaser,
        RoomCamera rCam,
        float timeStacker,
        Vector2 camPos)
    {
        if (reinitiateSpritesOnDraw)
        {
            reinitiateSpritesOnDraw = false;
            sLeaser.RemoveAllSpritesFromContainer();
            InitiateSprites(sLeaser, rCam);
        }

        if (sLeaser.sprites.Length < 2 ||
            sLeaser.sprites[0] is not TriangleMesh body ||
            sLeaser.sprites[1] is not TriangleMesh detail)
        {
            return;
        }

        Vector2 center = Vector2.Lerp(firstChunk.lastPos, firstChunk.pos, timeStacker);
        if (vibrate > 0)
        {
            center += Custom.DegToVec(Random.value * 360f) * (2f * Random.value);
        }

        Vector2 direction = Vector2.Lerp(lastRotation, rotation, timeStacker);
        if (direction.sqrMagnitude < 0.0001f)
        {
            direction = rotation.sqrMagnitude > 0.0001f ? rotation : Vector2.right;
        }
        direction.Normalize();

        DrawVanillaTuskMesh(body, detail, center, direction, camPos);

        // Vanilla only reapplies per-vertex darkness every draw under MMF. The
        // detached item follows the same rule, but samples lighting at its own
        // position now that it no longer belongs to VultureGraphics.
        if (ModManager.MMF)
        {
            UpdateVanillaTuskColors(body, detail, rCam.currentPalette, center);
        }

        // Match vanilla PlayerCarryableItem/Spear pickup feedback. Because these
        // meshes use per-vertex colors, blink them by tinting the vertex arrays
        // instead of replacing the original KingTusk sprite color/state.
        if (blink > 0 && Random.value < 0.5f)
        {
            TintMesh(body, blinkColor, 0.9f);
            TintMesh(detail, blinkColor, 0.9f);
        }

        body.isVisible = true;
        detail.isVisible = true;

        if (slatedForDeletetion || room != rCam.room)
        {
            sLeaser.CleanSpritesAndRemove();
        }
    }

    public override void ApplyPalette(
        RoomCamera.SpriteLeaser sLeaser,
        RoomCamera rCam,
        RoomPalette palette)
    {
        color = palette.blackColor;

        if (sLeaser.sprites.Length < 2 ||
            sLeaser.sprites[0] is not TriangleMesh body ||
            sLeaser.sprites[1] is not TriangleMesh detail)
        {
            return;
        }

        // Important: VultureGraphics.ApplyPalette first assigns its common sprite
        // tint to every sprite, then KingTusks.Tusk.ApplyPalette writes the custom
        // vertex colors and detail alpha. The KingTusk shader uses that complete
        // sprite state. Skipping this tint is why the detached pattern did not
        // match the corpse-side tusk even though the mesh/formulas were the same.
        body.color = palette.blackColor;
        detail.color = palette.blackColor;

        UpdateVanillaTuskColors(body, detail, palette, firstChunk.pos);
    }

    private void DrawVanillaTuskMesh(
        TriangleMesh body,
        TriangleMesh detail,
        Vector2 center,
        Vector2 direction,
        Vector2 camPos)
    {
        // The following is the same geometry calculation used by
        // KingTusks.Tusk.DrawSprites in Rain World v1.11.8. Only the coordinate
        // source changes from the Vulture's two tusk chunkPoints to this Spear's
        // position/rotation.
        AbstractKingVultureSpear data = Data;
        int side = data?.SourceSide ?? 0;
        Vector2 zRot = data?.Profile ?? new Vector2(0.35f, 0.25f);
        Vector2 perpendicular = Custom.PerpendicularVector(direction);
        float sideSign = side == 0 ? -1f : 1f;

        Vector2 previous = center +
                           direction * -35f +
                           perpendicular * (zRot.y * sideSign * -15f);
        float previousRadius = 0f;

        for (int i = 0; i < TuskSegments; i++)
        {
            float f = Mathf.InverseLerp(0f, TuskSegments - 1f, i);
            Vector2 point = center +
                            direction * Mathf.Lerp(-30f, 60f, f) +
                            perpendicular * (TuskBend(f) * 20f * zRot.x) +
                            perpendicular * (TuskProfBend(f) * zRot.y * sideSign * 10f);

            Vector2 segment = point - previous;
            Vector2 segmentDirection = segment.sqrMagnitude > 0.0001f
                ? segment.normalized
                : direction;
            Vector2 segmentPerpendicular = Custom.PerpendicularVector(segmentDirection);
            float lengthStep = Vector2.Distance(point, previous) / 5f;
            float radius = TuskRad(f, Mathf.Abs(zRot.y));
            Vector2 averageRadius = segmentPerpendicular * ((radius + previousRadius) * 0.5f);

            body.MoveVertice(i * 4, previous - averageRadius + segmentDirection * lengthStep - camPos);
            body.MoveVertice(i * 4 + 1, previous + averageRadius + segmentDirection * lengthStep - camPos);

            if (i == TuskSegments - 1)
            {
                body.MoveVertice(i * 4 + 2, point + segmentDirection * lengthStep - camPos);
            }
            else
            {
                Vector2 currentRadius = segmentPerpendicular * radius;
                body.MoveVertice(i * 4 + 2, point - currentRadius - segmentDirection * lengthStep - camPos);
                body.MoveVertice(i * 4 + 3, point + currentRadius - segmentDirection * lengthStep - camPos);
            }

            previousRadius = radius;
            previous = point;
        }

        for (int i = 0; i < body.vertices.Length; i++)
        {
            detail.MoveVertice(i, body.vertices[i]);
        }
    }

    private void UpdateVanillaTuskColors(
        TriangleMesh body,
        TriangleMesh detail,
        RoomPalette palette,
        Vector2 worldPos)
    {
        // This mirrors KingTusks.Tusk.ApplyPalette / UpdateTuskColors from
        // Rain World v1.11.8 using the visual parameters captured from the source
        // tusk when it was extracted.
        AbstractKingVultureSpear data = Data;
        Color armor = data?.ArmorColor ?? Color.Lerp(Color.gray, Color.white, 0.35f);
        HSLColor colorA = data?.ColorA ?? new HSLColor(0f, 0.4f, 0.55f);
        HSLColor colorB = data?.ColorB ?? new HSLColor(0f, 0.8f, 0.45f);
        float patternDisplace = data?.PatternDisplace ?? 1f;

        float darkness = 0f;
        if (ModManager.MMF && room != null)
        {
            // VultureGraphics uses Darkness * (1 - 0.5 * LightSourceExposure).
            // Use the detached tusk's own position in place of the old Vulture
            // main body position so the same vanilla lighting model follows it.
            darkness = room.Darkness(worldPos);
            darkness *= 1f - 0.5f * room.LightSourceExposure(worldPos);
        }

        int count = Mathf.Min(body.verticeColors.Length, detail.verticeColors.Length);
        for (int i = 0; i < count; i++)
        {
            float f = Mathf.InverseLerp(0f, body.verticeColors.Length - 1f, i);

            body.verticeColors[i] = Color.Lerp(
                Color.Lerp(armor, Color.white, Mathf.Pow(f, 2f)),
                palette.blackColor,
                darkness);

            detail.verticeColors[i] = Color.Lerp(
                Color.Lerp(
                    Color.Lerp(
                        HSLColor.Lerp(colorA, colorB, f).rgb,
                        palette.blackColor,
                        0.65f - 0.4f * f),
                    armor,
                    Mathf.Pow(f, 2f)),
                palette.blackColor,
                darkness);
        }

        // Vanilla KingTusks.Tusk.ApplyPalette drives the KingTusk shader pattern
        // with owner.patternDisplace through the detail mesh alpha.
        detail.alpha = patternDisplace;
    }

    private static void TintMesh(TriangleMesh mesh, Color target, float amount)
    {
        if (mesh?.verticeColors == null)
        {
            return;
        }

        float t = Mathf.Clamp01(amount);
        for (int i = 0; i < mesh.verticeColors.Length; i++)
        {
            mesh.verticeColors[i] = Color.Lerp(mesh.verticeColors[i], target, t);
        }
    }

    // Exact KingTusks.Tusk geometry formulas from Rain World v1.11.8.
    private static float TuskBend(float f)
    {
        return Mathf.Sin(Mathf.Pow(f, 0.85f) * Mathf.PI * 2f) * Mathf.Pow(1f - f, 2f);
    }

    private static float TuskProfBend(float f)
    {
        return -Mathf.Cos(Mathf.Pow(f, 0.85f) * Mathf.PI * 2.5f) * Mathf.Pow(1f - f, 3f);
    }

    private static float TuskRad(float f, float profileFac)
    {
        return 0.5f +
               2f * Mathf.Pow(
                   Mathf.Clamp01(
                       Mathf.Sin(
                           Mathf.Pow(f, Mathf.Lerp(0.65f, 0.5f, profileFac)) * Mathf.PI)),
                   1.2f - 0.3f * profileFac);
    }
}
