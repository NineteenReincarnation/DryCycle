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
        sLeaser.sprites = new FSprite[2];
        sLeaser.sprites[0] = TriangleMesh.MakeLongMesh(TuskSegments, pointyTip: true, customColor: true);
        sLeaser.sprites[1] = TriangleMesh.MakeLongMesh(TuskSegments, pointyTip: true, customColor: true);
        sLeaser.sprites[1].shader = rCam.game.rainWorld.Shaders["KingTusk"];
        AddToContainer(sLeaser, rCam, null);
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
            ApplyPalette(sLeaser, rCam, rCam.currentPalette);
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

        DrawTuskMesh(body, detail, center, direction, camPos);
        UpdateColors(body, detail, rCam.currentPalette, center);

        // Match vanilla PlayerCarryableItem/Spear pickup feedback. Because these
        // meshes use per-vertex colors, blink them by tinting the vertex arrays
        // instead of assigning FSprite.color, which would destroy the tusk pattern.
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

        if (sLeaser.sprites.Length >= 2 &&
            sLeaser.sprites[0] is TriangleMesh body &&
            sLeaser.sprites[1] is TriangleMesh detail)
        {
            UpdateColors(body, detail, palette, firstChunk.pos);
        }
    }

    private void DrawTuskMesh(
        TriangleMesh body,
        TriangleMesh detail,
        Vector2 center,
        Vector2 direction,
        Vector2 camPos)
    {
        AbstractKingVultureSpear data = Data;
        int side = data?.SourceSide ?? 0;
        Vector2 profile = data?.Profile ?? new Vector2(0.35f, 0.25f);
        Vector2 perpendicular = Custom.PerpendicularVector(direction);
        float sideSign = side == 0 ? -1f : 1f;

        Vector2 previous = center +
                           direction * -35f +
                           perpendicular * (profile.y * sideSign * -15f);
        float previousRadius = 0f;

        for (int i = 0; i < TuskSegments; i++)
        {
            float t = Mathf.InverseLerp(0f, TuskSegments - 1f, i);
            Vector2 point = center +
                            direction * Mathf.Lerp(-30f, 60f, t) +
                            perpendicular * (TuskBend(t) * 20f * profile.x) +
                            perpendicular * (TuskProfileBend(t) * profile.y * sideSign * 10f);

            Vector2 segment = point - previous;
            Vector2 segmentDir = segment.sqrMagnitude > 0.0001f
                ? segment.normalized
                : direction;
            Vector2 segmentPerp = Custom.PerpendicularVector(segmentDir);
            float lengthStep = Vector2.Distance(point, previous) / 5f;
            float radius = TuskRadius(t, Mathf.Abs(profile.y));
            Vector2 averageRadius = segmentPerp * ((radius + previousRadius) * 0.5f);

            body.MoveVertice(i * 4, previous - averageRadius + segmentDir * lengthStep - camPos);
            body.MoveVertice(i * 4 + 1, previous + averageRadius + segmentDir * lengthStep - camPos);

            if (i == TuskSegments - 1)
            {
                body.MoveVertice(i * 4 + 2, point + segmentDir * lengthStep - camPos);
            }
            else
            {
                Vector2 currentRadius = segmentPerp * radius;
                body.MoveVertice(i * 4 + 2, point - currentRadius - segmentDir * lengthStep - camPos);
                body.MoveVertice(i * 4 + 3, point + currentRadius - segmentDir * lengthStep - camPos);
            }

            previousRadius = radius;
            previous = point;
        }

        for (int i = 0; i < body.vertices.Length; i++)
        {
            detail.MoveVertice(i, body.vertices[i]);
        }
    }

    private void UpdateColors(
        TriangleMesh body,
        TriangleMesh detail,
        RoomPalette palette,
        Vector2 worldPos)
    {
        AbstractKingVultureSpear data = Data;
        Color armor = data?.ArmorColor ?? Color.Lerp(Color.gray, Color.white, 0.35f);
        HSLColor colorA = data?.ColorA ?? new HSLColor(0f, 0.4f, 0.55f);
        HSLColor colorB = data?.ColorB ?? new HSLColor(0f, 0.8f, 0.45f);
        float pattern = data?.PatternDisplace ?? 1f;
        float darkness = ModManager.MMF && room != null
            ? room.Darkness(worldPos)
            : 0f;

        int count = Mathf.Min(body.verticeColors.Length, detail.verticeColors.Length);
        for (int i = 0; i < count; i++)
        {
            float t = Mathf.InverseLerp(0f, body.verticeColors.Length - 1f, i);
            Color bodyColor = Color.Lerp(armor, Color.white, Mathf.Pow(t, 2f));
            Color detailColor = Color.Lerp(
                Color.Lerp(
                    HSLColor.Lerp(colorA, colorB, t).rgb,
                    palette.blackColor,
                    0.65f - 0.4f * t),
                armor,
                Mathf.Pow(t, 2f));

            body.verticeColors[i] = Color.Lerp(bodyColor, palette.blackColor, darkness);
            detail.verticeColors[i] = Color.Lerp(detailColor, palette.blackColor, darkness);
        }

        // Do not assign body.color/detail.color here. TriangleMesh customColor uses
        // the per-vertex arrays above, and assigning FSprite.color after them turns
        // the detached tusk into a flat white mesh and erases the KingTusk pattern.
        detail.alpha = pattern;
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

    private static float TuskBend(float f)
    {
        return Mathf.Sin(Mathf.Pow(f, 0.85f) * Mathf.PI * 2f) * Mathf.Pow(1f - f, 2f);
    }

    private static float TuskProfileBend(float f)
    {
        return -Mathf.Cos(Mathf.Pow(f, 0.85f) * Mathf.PI * 2.5f) * Mathf.Pow(1f - f, 3f);
    }

    private static float TuskRadius(float f, float profileFactor)
    {
        return 0.5f +
               2f * Mathf.Pow(
                   Mathf.Clamp01(
                       Mathf.Sin(
                           Mathf.Pow(f, Mathf.Lerp(0.65f, 0.5f, profileFactor)) * Mathf.PI)),
                   1.2f - 0.3f * profileFactor);
    }
}