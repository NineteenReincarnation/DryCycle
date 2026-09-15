using RWCustom;
using UnityEngine;

namespace DryCycle.Items.ScavengerLance;

internal sealed partial class ScavengerLance
{
    private static readonly float[] BladeSections = { 0f, 0.22f, 0.50f, 0.74f, 0.90f, 1f };

    public override void InitiateSprites(RoomCamera.SpriteLeaser sLeaser, RoomCamera rCam)
    {
        // 0/1: rear bone handle base / inner highlight
        // 2/3: asymmetric bone-nail blade base / inner bone highlight
        // 4-6: restrained bone fractures
        // 7-9: grip/binding marks around the rear section
        //
        // There is deliberately no black silhouette/outline layer. The nail is separated from
        // the background through warm bone values and internal shading instead of an ink border.
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

        // The base shape carries the silhouette. A narrower, slightly offset inner strip gives the
        // handle thickness without producing an outline around it.
        DrawHandle((TriangleMesh)sLeaser.sprites[0], tail, bladeRoot, direction, perp, bend, 1.72f, 0f, camPos);
        DrawHandle((TriangleMesh)sLeaser.sprites[1], tail, bladeRoot, direction, perp, bend, 0.58f, 0.42f, camPos);

        // The blade is one continuous asymmetric wedge. The second mesh is entirely inside the
        // first one and acts as a soft bone highlight, never as an edge stroke.
        DrawBlade((TriangleMesh)sLeaser.sprites[2], bladeRoot, tip, direction, perp, camPos);
        DrawBladeHighlight((TriangleMesh)sLeaser.sprites[3], bladeRoot, tip, direction, perp, camPos);

        // Bone fractures: short, broken-looking cuts rather than thick bands across the whole blade.
        float[] crackT = { 0.28f, 0.52f, 0.72f };
        float[] crackTilt = { 23f, -17f, 27f };
        float[] crackLength = { 0.46f, 0.58f, 0.40f };
        for (int i = 0; i < 3; i++)
        {
            float bladeT = crackT[i];
            FSprite crack = sLeaser.sprites[4 + i];
            Vector2 position = Vector2.Lerp(bladeRoot, tip, bladeT);
            GetBladeWidths(bladeT, out float left, out float right);
            // Offset each cut away from the exact centre so the cracks feel naturally irregular.
            float sideOffset = i == 1 ? -0.18f : 0.12f;
            position += perp * (right - left) * sideOffset;
            crack.SetPosition(position - camPos);
            crack.rotation = Custom.VecToDeg(direction) + 90f + crackTilt[i];
            crack.scaleX = Mathf.Max(2.2f, (left + right) * crackLength[i]);
            crack.scaleY = i == 1 ? 0.58f : 0.50f;
        }

        // Grip bands stay subdued; they identify the hand position without reading as black rings.
        for (int i = 0; i < 3; i++)
        {
            FSprite band = sLeaser.sprites[7 + i];
            Vector2 position = grip + direction * ((i - 1) * 4.0f);
            band.SetPosition(position - camPos);
            band.rotation = Custom.VecToDeg(direction) + 90f + (i == 1 ? -7f : 6f);
            band.scaleX = 3.7f;
            band.scaleY = 0.62f;
        }

        ApplyPalette(sLeaser, rCam, rCam.currentPalette);
        if (slatedForDeletetion || room != rCam.room) sLeaser.CleanSpritesAndRemove();
    }

    private static void DrawHandle(TriangleMesh mesh, Vector2 tail, Vector2 bladeRoot, Vector2 direction,
        Vector2 perp, float bend, float halfWidth, float sideOffset, Vector2 camPos)
    {
        for (int i = 0; i < 5; i++)
        {
            float a = i / 5f;
            float b = (i + 1) / 5f;
            Vector2 start = Vector2.Lerp(tail, bladeRoot, a) + perp * (Mathf.Sin(a * Mathf.PI) * bend + sideOffset);
            Vector2 end = Vector2.Lerp(tail, bladeRoot, b) + perp * (Mathf.Sin(b * Mathf.PI) * bend + sideOffset);
            float startWidth = halfWidth * Mathf.Lerp(0.80f, 1f, Mathf.Sin(a * Mathf.PI));
            float endWidth = halfWidth * Mathf.Lerp(0.80f, 1f, Mathf.Sin(b * Mathf.PI));
            mesh.MoveVertice(i * 4, start - perp * startWidth - camPos);
            mesh.MoveVertice(i * 4 + 1, start + perp * startWidth - camPos);
            mesh.MoveVertice(i * 4 + 2, end - perp * endWidth - camPos);
            mesh.MoveVertice(i * 4 + 3, end + perp * endWidth - camPos);
        }
    }

    private static void DrawBlade(TriangleMesh mesh, Vector2 root, Vector2 tip, Vector2 direction,
        Vector2 perp, Vector2 camPos)
    {
        // Broad asymmetric shoulder -> one uninterrupted taper -> point. Width never grows again,
        // so this cannot collapse back into a diamond/spearhead silhouette.
        for (int i = 0; i < 4; i++)
        {
            float a = BladeSections[i];
            float b = BladeSections[i + 1];
            Vector2 start = Vector2.Lerp(root, tip, a);
            Vector2 end = Vector2.Lerp(root, tip, b);
            GetBladeWidths(a, out float startLeft, out float startRight);
            GetBladeWidths(b, out float endLeft, out float endRight);
            mesh.MoveVertice(i * 4, start - perp * startLeft - camPos);
            mesh.MoveVertice(i * 4 + 1, start + perp * startRight - camPos);
            mesh.MoveVertice(i * 4 + 2, end - perp * endLeft - camPos);
            mesh.MoveVertice(i * 4 + 3, end + perp * endRight - camPos);
        }

        float finalBase = BladeSections[4];
        Vector2 basePoint = Vector2.Lerp(root, tip, finalBase);
        GetBladeWidths(finalBase, out float baseLeft, out float baseRight);
        int index = 16;
        mesh.MoveVertice(index, basePoint - perp * baseLeft - camPos);
        mesh.MoveVertice(index + 1, basePoint + perp * baseRight - camPos);
        mesh.MoveVertice(index + 2, tip - camPos);
    }

    private static void DrawBladeHighlight(TriangleMesh mesh, Vector2 root, Vector2 tip, Vector2 direction,
        Vector2 perp, Vector2 camPos)
    {
        // A slim internal plane on the brighter face gives the bone volume. It deliberately leaves
        // clear margins on both sides, so no part of this mesh can read as an exterior outline.
        for (int i = 0; i < 4; i++)
        {
            float a = BladeSections[i];
            float b = BladeSections[i + 1];
            MoveHighlightPair(mesh, i * 4, root, tip, perp, a, camPos);
            MoveHighlightPair(mesh, i * 4 + 2, root, tip, perp, b, camPos);
        }

        float finalBase = BladeSections[4];
        Vector2 basePoint = Vector2.Lerp(root, tip, finalBase);
        GetBladeWidths(finalBase, out float left, out float right);
        int index = 16;
        mesh.MoveVertice(index, basePoint - perp * left * 0.05f - camPos);
        mesh.MoveVertice(index + 1, basePoint + perp * right * 0.50f - camPos);
        mesh.MoveVertice(index + 2, Vector2.Lerp(root, tip, 0.965f) + perp * 0.08f - camPos);
    }

    private static void MoveHighlightPair(TriangleMesh mesh, int index, Vector2 root, Vector2 tip,
        Vector2 perp, float t, Vector2 camPos)
    {
        Vector2 center = Vector2.Lerp(root, tip, t);
        GetBladeWidths(t, out float left, out float right);
        float fade = Mathf.Lerp(1f, 0.62f, t);
        float innerLeft = left * 0.10f * fade;
        float innerRight = right * 0.56f * fade;
        mesh.MoveVertice(index, center - perp * innerLeft - camPos);
        mesh.MoveVertice(index + 1, center + perp * innerRight - camPos);
    }

    private static void GetBladeWidths(float t, out float left, out float right)
    {
        float shaped = Mathf.Pow(Mathf.Clamp01(t), 0.82f);
        // One shoulder is visibly stronger while the opposite side stays straighter. Both sides
        // monotonically converge toward the same point; there is no middle bulge.
        left = Mathf.Lerp(4.20f, 0.10f, shaped);
        right = Mathf.Lerp(6.55f, 0.10f, shaped);
    }

    public override void ApplyPalette(RoomCamera.SpriteLeaser sLeaser, RoomCamera rCam, RoomPalette palette)
    {
        float darkness = room == null ? 0f : room.Darkness(firstChunk.pos) * (1f - room.LightSourceExposure(firstChunk.pos));

        // Warm bone values replace the old black-outline treatment. The highlight is intentionally
        // low-contrast so the weapon keeps Rain World's flat graphic language while gaining volume.
        Color handleBase = Color.Lerp(new Color(0.64f, 0.61f, 0.54f), palette.blackColor, darkness * 0.88f);
        Color handleLight = Color.Lerp(new Color(0.82f, 0.79f, 0.70f), palette.blackColor, darkness * 0.82f);
        Color bladeBase = Color.Lerp(new Color(0.76f, 0.74f, 0.68f), palette.blackColor, darkness * 0.88f);
        Color bladeLight = Color.Lerp(new Color(0.90f, 0.87f, 0.78f), palette.blackColor, darkness * 0.80f);
        Color crack = Color.Lerp(new Color(0.31f, 0.285f, 0.245f), palette.blackColor, darkness * 0.95f);
        Color binding = Color.Lerp(new Color(0.39f, 0.35f, 0.29f), palette.blackColor, darkness * 0.94f);

        sLeaser.sprites[0].color = handleBase;
        sLeaser.sprites[1].color = handleLight;
        sLeaser.sprites[2].color = bladeBase;
        sLeaser.sprites[3].color = bladeLight;
        for (int i = 4; i < 7; i++) sLeaser.sprites[i].color = crack;
        for (int i = 7; i < 10; i++) sLeaser.sprites[i].color = binding;
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
