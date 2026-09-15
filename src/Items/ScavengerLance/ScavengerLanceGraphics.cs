using RWCustom;
using UnityEngine;

namespace DryCycle.Items.ScavengerLance;

internal sealed partial class ScavengerLance
{
    private static readonly float[] BladeSections = { 0f, 0.18f, 0.42f, 0.66f, 0.86f, 1f };
    private static readonly float[] CrackPositions = { 0.24f, 0.43f, 0.61f, 0.77f };
    private static readonly float[] CrackTilts = { 21f, -17f, 24f, -19f };

    public override void InitiateSprites(RoomCamera.SpriteLeaser sLeaser, RoomCamera rCam)
    {
        // 0/1: narrow bone handle + subdued facet
        // 2/3: broad tapered bone blade + inner facet
        // 4  : dark root seam where the handle disappears into the blade shoulder
        // 5-8: main fractures across the bone surface
        // 9-11: short fracture branches
        //
        // The weapon is drawn procedurally so rendering and collision can share the same dimensions.
        // No atlas outline is required and there is no decorative cloth/leather mass hiding the shape.
        sLeaser.sprites = new FSprite[12];
        sLeaser.sprites[0] = TriangleMesh.MakeLongMesh(5, false, false);
        sLeaser.sprites[1] = TriangleMesh.MakeLongMesh(5, false, false);
        sLeaser.sprites[2] = TriangleMesh.MakeLongMesh(5, true, false);
        sLeaser.sprites[3] = TriangleMesh.MakeLongMesh(5, true, false);
        for (int i = 4; i < sLeaser.sprites.Length; i++)
            sLeaser.sprites[i] = new FSprite("pixel");

        sLeaser.sprites[1].alpha = 0.42f;
        sLeaser.sprites[3].alpha = 0.46f;
        sLeaser.sprites[4].alpha = 0.90f;
        for (int i = 5; i < sLeaser.sprites.Length; i++)
            sLeaser.sprites[i].alpha = 0.86f;

        ApplyPalette(sLeaser, rCam, rCam.currentPalette);
        AddToContainer(sLeaser, rCam, null);
    }

    public override void DrawSprites(RoomCamera.SpriteLeaser sLeaser, RoomCamera rCam, float t, Vector2 camPos)
    {
        Vector2 direction = Vector2.Lerp(lastRotation, rotation, t).normalized;
        if (direction.sqrMagnitude < 0.001f) direction = Vector2.right;

        Vector2 grip = Vector2.Lerp(firstChunk.lastPos, firstChunk.pos, t);
        Vector2 perp = Custom.PerpendicularVector(direction);
        Vector2 tail = grip - direction * Length * LanceCombatMath.GripFraction;
        float forwardLength = LanceCombatMath.ForwardLength(Length);
        Vector2 bladeRoot = grip + direction * LanceCombatMath.BladeRootDistance(Length);
        Vector2 tip = grip + direction * forwardLength;
        float bend = Mathf.Lerp(_lastBend, _bend, t) * 0.28f;

        DrawHandle((TriangleMesh)sLeaser.sprites[0], tail, bladeRoot, direction, perp, bend,
            1.72f, 0f, camPos);
        DrawHandle((TriangleMesh)sLeaser.sprites[1], tail, bladeRoot, direction, perp, bend,
            0.42f, 0.34f, camPos);
        DrawBlade((TriangleMesh)sLeaser.sprites[2], bladeRoot, tip, perp, camPos);
        DrawBladeFacet((TriangleMesh)sLeaser.sprites[3], bladeRoot, tip, perp, camPos);

        // A narrow dark seam separates the harmless handle from the damaging front body. It also
        // makes the exact collision boundary visually legible during play.
        FSprite seam = sLeaser.sprites[4];
        Vector2 seamPos = bladeRoot - direction * 0.45f;
        seam.SetPosition(seamPos - camPos);
        seam.rotation = Custom.VecToDeg(direction) + 90f;
        seam.scaleX = LanceCombatMath.BladeShoulderHalfWidth * 1.62f;
        seam.scaleY = 0.82f;

        DrawFractures(sLeaser, direction, perp, bladeRoot, tip, camPos);

        ApplyPalette(sLeaser, rCam, rCam.currentPalette);
        if (slatedForDeletetion || room != rCam.room)
            sLeaser.CleanSpritesAndRemove();
    }

    private static void DrawHandle(TriangleMesh mesh, Vector2 tail, Vector2 bladeRoot, Vector2 direction,
        Vector2 perp, float bend, float halfWidth, float sideOffset, Vector2 camPos)
    {
        for (int i = 0; i < 5; i++)
        {
            float a = i / 5f;
            float b = (i + 1) / 5f;
            Vector2 start = Vector2.Lerp(tail, bladeRoot, a) +
                perp * (Mathf.Sin(a * Mathf.PI) * bend + sideOffset);
            Vector2 end = Vector2.Lerp(tail, bladeRoot, b) +
                perp * (Mathf.Sin(b * Mathf.PI) * bend + sideOffset);

            // Slight thickening near the shoulder makes the rear read as a bone tang rather than a
            // metal spear shaft, while it remains narrow enough to be clearly non-damaging.
            float startWidth = halfWidth * Mathf.Lerp(0.72f, 1.05f, a);
            float endWidth = halfWidth * Mathf.Lerp(0.72f, 1.05f, b);
            mesh.MoveVertice(i * 4, start - perp * startWidth - camPos);
            mesh.MoveVertice(i * 4 + 1, start + perp * startWidth - camPos);
            mesh.MoveVertice(i * 4 + 2, end - perp * endWidth - camPos);
            mesh.MoveVertice(i * 4 + 3, end + perp * endWidth - camPos);
        }
    }

    private static void DrawBlade(TriangleMesh mesh, Vector2 root, Vector2 tip, Vector2 perp, Vector2 camPos)
    {
        // The visual half-width is exactly LanceCombatMath.BladeHalfWidth(). This is deliberate:
        // the broad shoulder and every point of the taper are the same shape used by SweepBlade.
        for (int i = 0; i < 4; i++)
        {
            float a = BladeSections[i];
            float b = BladeSections[i + 1];
            Vector2 start = Vector2.Lerp(root, tip, a);
            Vector2 end = Vector2.Lerp(root, tip, b);
            float startWidth = LanceCombatMath.BladeHalfWidth(a);
            float endWidth = LanceCombatMath.BladeHalfWidth(b);
            mesh.MoveVertice(i * 4, start - perp * startWidth - camPos);
            mesh.MoveVertice(i * 4 + 1, start + perp * startWidth - camPos);
            mesh.MoveVertice(i * 4 + 2, end - perp * endWidth - camPos);
            mesh.MoveVertice(i * 4 + 3, end + perp * endWidth - camPos);
        }

        float finalBaseT = BladeSections[4];
        Vector2 finalBase = Vector2.Lerp(root, tip, finalBaseT);
        float finalWidth = LanceCombatMath.BladeHalfWidth(finalBaseT);
        int index = 16;
        mesh.MoveVertice(index, finalBase - perp * finalWidth - camPos);
        mesh.MoveVertice(index + 1, finalBase + perp * finalWidth - camPos);
        mesh.MoveVertice(index + 2, tip - camPos);
    }

    private static void DrawBladeFacet(TriangleMesh mesh, Vector2 root, Vector2 tip, Vector2 perp, Vector2 camPos)
    {
        // A narrow uneven light plane makes the object read as carved/broken bone rather than a
        // flat white polygon. It stays inside the collision silhouette and never changes its width.
        for (int i = 0; i < 4; i++)
        {
            float a = BladeSections[i];
            float b = BladeSections[i + 1];
            MoveFacetPair(mesh, i * 4, root, tip, perp, a, camPos);
            MoveFacetPair(mesh, i * 4 + 2, root, tip, perp, b, camPos);
        }

        float finalBaseT = BladeSections[4];
        Vector2 finalBase = Vector2.Lerp(root, tip, finalBaseT);
        float width = LanceCombatMath.BladeHalfWidth(finalBaseT);
        int index = 16;
        mesh.MoveVertice(index, finalBase + perp * width * 0.12f - camPos);
        mesh.MoveVertice(index + 1, finalBase + perp * width * 0.57f - camPos);
        mesh.MoveVertice(index + 2, Vector2.Lerp(root, tip, 0.955f) + perp * 0.03f - camPos);
    }

    private static void MoveFacetPair(TriangleMesh mesh, int index, Vector2 root, Vector2 tip,
        Vector2 perp, float bladeT, Vector2 camPos)
    {
        Vector2 center = Vector2.Lerp(root, tip, bladeT);
        float width = LanceCombatMath.BladeHalfWidth(bladeT);
        float fade = Mathf.Lerp(1f, 0.42f, bladeT);
        mesh.MoveVertice(index, center + perp * width * 0.10f * fade - camPos);
        mesh.MoveVertice(index + 1, center + perp * width * 0.58f * fade - camPos);
    }

    private static void DrawFractures(RoomCamera.SpriteLeaser sLeaser, Vector2 direction, Vector2 perp,
        Vector2 root, Vector2 tip, Vector2 camPos)
    {
        float directionAngle = Custom.VecToDeg(direction);
        for (int i = 0; i < CrackPositions.Length; i++)
        {
            float bladeT = CrackPositions[i];
            float width = LanceCombatMath.BladeHalfWidth(bladeT);
            Vector2 center = Vector2.Lerp(root, tip, bladeT) + perp * (i % 2 == 0 ? -width * 0.08f : width * 0.10f);
            FSprite crack = sLeaser.sprites[5 + i];
            crack.SetPosition(center - camPos);
            crack.rotation = directionAngle + 90f + CrackTilts[i];
            crack.scaleX = Mathf.Max(1.5f, width * (i == 1 ? 1.22f : 1.05f));
            crack.scaleY = i == 2 ? 0.48f : 0.42f;
        }

        // Small offshoots stop the marks from reading as painted stripes. They are kept short and
        // sparse so the weapon still reads cleanly at Rain World's native sprite scale.
        float[] branchT = { 0.30f, 0.55f, 0.73f };
        float[] branchTilt = { -31f, 29f, -27f };
        for (int i = 0; i < branchT.Length; i++)
        {
            float bladeT = branchT[i];
            float width = LanceCombatMath.BladeHalfWidth(bladeT);
            Vector2 center = Vector2.Lerp(root, tip, bladeT) + perp * (i == 1 ? width * 0.23f : -width * 0.20f);
            FSprite branch = sLeaser.sprites[9 + i];
            branch.SetPosition(center - camPos);
            branch.rotation = directionAngle + 90f + branchTilt[i];
            branch.scaleX = Mathf.Max(1.1f, width * 0.56f);
            branch.scaleY = 0.36f;
        }
    }

    public override void ApplyPalette(RoomCamera.SpriteLeaser sLeaser, RoomCamera rCam, RoomPalette palette)
    {
        float darkness = room == null ? 0f :
            room.Darkness(firstChunk.pos) * (1f - room.LightSourceExposure(firstChunk.pos));

        // Neutral pale bone rather than ochre metal. The facet is not a metal sheen; it is a lighter
        // fractured plane. Cracks and the root seam are intentionally close to palette black.
        Color handle = Color.Lerp(new Color(0.57f, 0.58f, 0.54f), palette.blackColor, darkness * 0.92f);
        Color handleFacet = Color.Lerp(new Color(0.73f, 0.74f, 0.69f), palette.blackColor, darkness * 0.90f);
        Color blade = Color.Lerp(new Color(0.76f, 0.77f, 0.72f), palette.blackColor, darkness * 0.91f);
        Color bladeFacet = Color.Lerp(new Color(0.92f, 0.92f, 0.86f), palette.blackColor, darkness * 0.88f);
        Color fracture = Color.Lerp(new Color(0.12f, 0.115f, 0.105f), palette.blackColor, darkness * 0.55f);
        Color seam = Color.Lerp(new Color(0.19f, 0.17f, 0.145f), palette.blackColor, darkness * 0.70f);

        sLeaser.sprites[0].color = handle;
        sLeaser.sprites[1].color = handleFacet;
        sLeaser.sprites[2].color = blade;
        sLeaser.sprites[3].color = bladeFacet;
        sLeaser.sprites[4].color = seam;
        for (int i = 5; i < sLeaser.sprites.Length; i++)
            sLeaser.sprites[i].color = fracture;
    }

    public override void AddToContainer(RoomCamera.SpriteLeaser sLeaser, RoomCamera rCam, FContainer container)
    {
        container ??= rCam.ReturnFContainer("Items");
        foreach (FSprite sprite in sLeaser.sprites)
            sprite.RemoveFromContainer();

        // Main bone body first, then light facets, then the seam/fractures on top.
        container.AddChild(sLeaser.sprites[0]);
        container.AddChild(sLeaser.sprites[2]);
        container.AddChild(sLeaser.sprites[1]);
        container.AddChild(sLeaser.sprites[3]);
        for (int i = 4; i < sLeaser.sprites.Length; i++)
            container.AddChild(sLeaser.sprites[i]);
    }
}
