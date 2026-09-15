using RWCustom;
using UnityEngine;

namespace DryCycle.Items.ScavengerLance;

internal sealed partial class ScavengerLance
{
    private static readonly float[] BladeSections = { 0f, 0.22f, 0.50f, 0.74f, 0.90f, 1f };

    public override void InitiateSprites(RoomCamera.SpriteLeaser sLeaser, RoomCamera rCam)
    {
        // 0/1: rear bone handle outline/fill
        // 2/3: asymmetric bone-nail blade outline/fill
        // 4-6: dark fractures across the blade
        // 7-9: narrow grip/binding marks around the rear section
        sLeaser.sprites = new FSprite[10];
        sLeaser.sprites[0] = TriangleMesh.MakeLongMesh(5, false, false);
        sLeaser.sprites[1] = TriangleMesh.MakeLongMesh(5, false, false);
        sLeaser.sprites[2] = TriangleMesh.MakeLongMesh(5, true, false);
        sLeaser.sprites[3] = TriangleMesh.MakeLongMesh(5, true, false);
        for (int i = 4; i < 10; i++) sLeaser.sprites[i] = new FSprite("pixel");
        ApplyPalette(sLeaser, rCam, rCam.currentPalette);
        AddToContainer(sLeaser, rCam, null);
    }

    public override void DrawSprites(RoomCamera.SpriteLeaser sLeaser, RoomCamera rCam, float t, Vector2 camPos)
    {
        Vector2 direction = Vector2.Lerp(lastRotation, rotation, t).normalized;
        Vector2 grip = Vector2.Lerp(firstChunk.lastPos, firstChunk.pos, t);
        Vector2 perp = Custom.PerpendicularVector(direction);
        Vector2 tail = grip - direction * Length * LanceCombatMath.GripFraction;
        float forwardLength = LanceCombatMath.ForwardLength(Length);
        Vector2 bladeRoot = grip + direction * LanceCombatMath.BladeRootDistance(Length);
        Vector2 tip = grip + direction * forwardLength;
        float bend = Mathf.Lerp(_lastBend, _bend, t) * 0.32f;

        DrawHandle((TriangleMesh)sLeaser.sprites[0], tail, bladeRoot, direction, perp, bend, 2.35f, camPos);
        DrawHandle((TriangleMesh)sLeaser.sprites[1], tail, bladeRoot, direction, perp, bend, 1.45f, camPos);
        DrawBlade((TriangleMesh)sLeaser.sprites[2], bladeRoot, tip, direction, perp, 1.15f, camPos);
        DrawBlade((TriangleMesh)sLeaser.sprites[3], bladeRoot, tip, direction, perp, 0f, camPos);

        // Bone fractures: short diagonal dark cuts that follow the wide front wedge.
        float[] crackT = { 0.24f, 0.49f, 0.70f };
        float[] crackTilt = { 24f, -19f, 28f };
        for (int i = 0; i < 3; i++)
        {
            float bladeT = crackT[i];
            FSprite crack = sLeaser.sprites[4 + i];
            Vector2 position = Vector2.Lerp(bladeRoot, tip, bladeT);
            GetBladeWidths(bladeT, out float left, out float right);
            crack.SetPosition(position - camPos);
            crack.rotation = Custom.VecToDeg(direction) + 90f + crackTilt[i];
            crack.scaleX = Mathf.Max(2.5f, (left + right) * (i == 1 ? 0.94f : 0.78f));
            crack.scaleY = 0.72f;
        }

        // The grip remains narrow and visibly hand-held; these are not decorative body pieces.
        for (int i = 0; i < 3; i++)
        {
            FSprite band = sLeaser.sprites[7 + i];
            Vector2 position = grip + direction * ((i - 1) * 4.2f);
            band.SetPosition(position - camPos);
            band.rotation = Custom.VecToDeg(direction) + 90f + (i == 1 ? -8f : 7f);
            band.scaleX = 4.4f;
            band.scaleY = 0.85f;
        }

        ApplyPalette(sLeaser, rCam, rCam.currentPalette);
        if (slatedForDeletetion || room != rCam.room) sLeaser.CleanSpritesAndRemove();
    }

    private static void DrawHandle(TriangleMesh mesh, Vector2 tail, Vector2 bladeRoot, Vector2 direction,
        Vector2 perp, float bend, float halfWidth, Vector2 camPos)
    {
        for (int i = 0; i < 5; i++)
        {
            float a = i / 5f;
            float b = (i + 1) / 5f;
            Vector2 start = Vector2.Lerp(tail, bladeRoot, a) + perp * Mathf.Sin(a * Mathf.PI) * bend;
            Vector2 end = Vector2.Lerp(tail, bladeRoot, b) + perp * Mathf.Sin(b * Mathf.PI) * bend;
            float startWidth = halfWidth * Mathf.Lerp(0.78f, 1f, Mathf.Sin(a * Mathf.PI));
            float endWidth = halfWidth * Mathf.Lerp(0.78f, 1f, Mathf.Sin(b * Mathf.PI));
            mesh.MoveVertice(i * 4, start - perp * startWidth - camPos);
            mesh.MoveVertice(i * 4 + 1, start + perp * startWidth - camPos);
            mesh.MoveVertice(i * 4 + 2, end - perp * endWidth - camPos);
            mesh.MoveVertice(i * 4 + 3, end + perp * endWidth - camPos);
        }
    }

    private static void DrawBlade(TriangleMesh mesh, Vector2 root, Vector2 tip, Vector2 direction,
        Vector2 perp, float outline, Vector2 camPos)
    {
        // Five connected sections. The profile is deliberately monotonic: broad shoulder at
        // the root, then a continuous taper to one point. There is no diamond-shaped bulge.
        for (int i = 0; i < 4; i++)
        {
            float a = BladeSections[i];
            float b = BladeSections[i + 1];
            Vector2 start = Vector2.Lerp(root, tip, a);
            Vector2 end = Vector2.Lerp(root, tip, b);
            GetBladeWidths(a, out float startLeft, out float startRight);
            GetBladeWidths(b, out float endLeft, out float endRight);
            float startOutline = outline * (1f - a * 0.75f);
            float endOutline = outline * (1f - b * 0.75f);
            mesh.MoveVertice(i * 4, start - perp * (startLeft + startOutline) - camPos);
            mesh.MoveVertice(i * 4 + 1, start + perp * (startRight + startOutline) - camPos);
            mesh.MoveVertice(i * 4 + 2, end - perp * (endLeft + endOutline) - camPos);
            mesh.MoveVertice(i * 4 + 3, end + perp * (endRight + endOutline) - camPos);
        }

        // Pointy final section uses the mesh's final single tip vertex.
        float finalBase = BladeSections[4];
        Vector2 basePoint = Vector2.Lerp(root, tip, finalBase);
        GetBladeWidths(finalBase, out float baseLeft, out float baseRight);
        float baseOutline = outline * (1f - finalBase * 0.75f);
        int index = 16;
        mesh.MoveVertice(index, basePoint - perp * (baseLeft + baseOutline) - camPos);
        mesh.MoveVertice(index + 1, basePoint + perp * (baseRight + baseOutline) - camPos);
        mesh.MoveVertice(index + 2, tip - camPos);
    }

    private static void GetBladeWidths(float t, out float left, out float right)
    {
        float shaped = Mathf.Pow(Mathf.Clamp01(t), 0.82f);
        // Asymmetric shoulder like the reference nail: one side is straighter/narrower,
        // the other has the stronger shoulder. Both sides only taper from here to the point.
        left = Mathf.Lerp(4.15f, 0.12f, shaped);
        right = Mathf.Lerp(6.65f, 0.12f, shaped);
    }

    public override void ApplyPalette(RoomCamera.SpriteLeaser sLeaser, RoomCamera rCam, RoomPalette palette)
    {
        float darkness = room == null ? 0f : room.Darkness(firstChunk.pos) * (1f - room.LightSourceExposure(firstChunk.pos));
        Color outline = Color.Lerp(new Color(0.11f, 0.105f, 0.095f), palette.blackColor, darkness);
        Color handle = Color.Lerp(new Color(0.63f, 0.63f, 0.59f), palette.blackColor, darkness);
        Color blade = Color.Lerp(new Color(0.80f, 0.80f, 0.76f), palette.blackColor, darkness);
        Color crack = Color.Lerp(new Color(0.16f, 0.15f, 0.14f), palette.blackColor, darkness);

        sLeaser.sprites[0].color = outline;
        sLeaser.sprites[1].color = handle;
        sLeaser.sprites[2].color = outline;
        sLeaser.sprites[3].color = blade;
        for (int i = 4; i < 7; i++) sLeaser.sprites[i].color = crack;
        for (int i = 7; i < 10; i++) sLeaser.sprites[i].color = crack;
    }

    public override void AddToContainer(RoomCamera.SpriteLeaser sLeaser, RoomCamera rCam, FContainer container)
    {
        container ??= rCam.ReturnFContainer("Items");
        foreach (FSprite sprite in sLeaser.sprites)
        {
            sprite.RemoveFromContainer();
            container.AddChild(sprite);
        }
    }
}
