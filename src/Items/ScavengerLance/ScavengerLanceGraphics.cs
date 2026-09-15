using RWCustom;
using UnityEngine;

namespace DryCycle.Items.ScavengerLance;

internal sealed partial class ScavengerLance
{
    private static readonly float[] BladeSections = { 0f, 0.22f, 0.50f, 0.74f, 0.90f, 1f };

    // Small cloth strip tied to the leather wrap. The motion model deliberately follows the
    // vanilla ExplosiveSpear rag idea: a short constrained point chain with gravity, drag and
    // inertial lag, rendered as a jagged long mesh. It is cosmetic only.
    private const int FabricSegments = 4;
    private const float FabricSegmentLength = 4.8f;
    private readonly Vector2[,] _fabric = new Vector2[FabricSegments, 3]; // pos / lastPos / vel
    private bool _fabricInitialized;
    private int _fabricUpdateClock = int.MinValue;

    public override void InitiateSprites(RoomCamera.SpriteLeaser sLeaser, RoomCamera rCam)
    {
        // 0/1: rear metal handle base / inner highlight
        // 2/3: asymmetric wedge blade base / inner worn-metal highlight
        // 4-6: subtle forge / sand-wear marks
        // 7-9: leather wrap at the blade-handle junction
        // 10 : short desert cloth streamer tied into that wrap
        //
        // No black exterior outline is used anywhere on the weapon.
        sLeaser.sprites = new FSprite[11];
        sLeaser.sprites[0] = TriangleMesh.MakeLongMesh(5, false, false);
        sLeaser.sprites[1] = TriangleMesh.MakeLongMesh(5, false, false);
        sLeaser.sprites[2] = TriangleMesh.MakeLongMesh(5, true, false);
        sLeaser.sprites[3] = TriangleMesh.MakeLongMesh(5, true, false);
        for (int i = 4; i < 10; i++) sLeaser.sprites[i] = new FSprite("pixel");
        sLeaser.sprites[10] = TriangleMesh.MakeLongMesh(FabricSegments, false, false);
        sLeaser.sprites[10].shader = rCam.game.rainWorld.Shaders["JaggedSquare"];
        sLeaser.sprites[10].alpha = Mathf.Lerp(0.42f, 0.72f,
            rCam.game.SeededRandom(abstractPhysicalObject.ID.RandomSeed));

        _fabricInitialized = false;
        _fabricUpdateClock = int.MinValue;
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
        float bend = Mathf.Lerp(_lastBend, _bend, t) * 0.32f;

        // Sand-worn metal, not bone. The base mesh owns the silhouette and the inner mesh is a
        // broad worn face, leaving no dark rim around the outside.
        DrawHandle((TriangleMesh)sLeaser.sprites[0], tail, bladeRoot, direction, perp, bend, 1.72f, 0f, camPos);
        DrawHandle((TriangleMesh)sLeaser.sprites[1], tail, bladeRoot, direction, perp, bend, 0.58f, 0.42f, camPos);
        DrawBlade((TriangleMesh)sLeaser.sprites[2], bladeRoot, tip, direction, perp, camPos);
        DrawBladeHighlight((TriangleMesh)sLeaser.sprites[3], bladeRoot, tip, direction, perp, camPos);

        // A few subdued forging / abrasion marks break up the large flat steel face without
        // turning it back into a cracked-bone visual.
        float[] markT = { 0.31f, 0.56f, 0.76f };
        float[] markTilt = { 20f, -14f, 24f };
        float[] markLength = { 0.34f, 0.43f, 0.30f };
        for (int i = 0; i < 3; i++)
        {
            float bladeT = markT[i];
            FSprite mark = sLeaser.sprites[4 + i];
            Vector2 position = Vector2.Lerp(bladeRoot, tip, bladeT);
            GetBladeWidths(bladeT, out float left, out float right);
            position += perp * (i == 1 ? -0.55f : 0.38f);
            mark.SetPosition(position - camPos);
            mark.rotation = Custom.VecToDeg(direction) + 90f + markTilt[i];
            mark.scaleX = Mathf.Max(1.8f, (left + right) * markLength[i]);
            mark.scaleY = i == 1 ? 0.46f : 0.38f;
        }

        // Leather wrap sits exactly at the requested junction: in front of the hand, immediately
        // before the blade shoulder. It spans the narrow metal neck rather than the rear handle.
        Vector2 wrapCenter = WrapCenter(direction, grip);
        for (int i = 0; i < 3; i++)
        {
            FSprite band = sLeaser.sprites[7 + i];
            Vector2 position = wrapCenter + direction * ((i - 1) * 2.45f);
            band.SetPosition(position - camPos);
            band.rotation = Custom.VecToDeg(direction) + 90f + (i == 1 ? -8f : 6f);
            band.scaleX = i == 1 ? 5.6f : 5.1f;
            band.scaleY = i == 1 ? 1.05f : 0.90f;
        }

        EnsureFabricPhysics();
        DrawFabric((TriangleMesh)sLeaser.sprites[10], direction, grip, t, camPos);

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
        // so the silhouette remains the requested long wedge rather than a diamond spearhead.
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
        // Broad internal plane = worn steel face catching desert light. It stays well inside the
        // silhouette, so there is still no visible exterior stroke.
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
        left = Mathf.Lerp(4.20f, 0.10f, shaped);
        right = Mathf.Lerp(6.55f, 0.10f, shaped);
    }

    private Vector2 WrapCenter(Vector2 direction, Vector2 grip)
    {
        Vector2 dir = direction.sqrMagnitude > 0.001f ? direction.normalized : Vector2.right;
        Vector2 bladeRoot = grip + dir * LanceCombatMath.BladeRootDistance(Length);
        return Vector2.Lerp(grip, bladeRoot, 0.72f);
    }

    private Vector2 FabricAttachPos(Vector2 direction, Vector2 grip)
    {
        Vector2 dir = direction.sqrMagnitude > 0.001f ? direction.normalized : Vector2.right;
        Vector2 perp = Custom.PerpendicularVector(dir);
        // Attach to the lower side of the leather knot so the strip visibly comes out of the wrap.
        return WrapCenter(dir, grip) - perp * 1.55f;
    }

    private void ResetFabric(Vector2 attach)
    {
        for (int i = 0; i < FabricSegments; i++)
        {
            Vector2 position = attach + Vector2.down * FabricSegmentLength * (i + 1);
            _fabric[i, 0] = position;
            _fabric[i, 1] = position;
            _fabric[i, 2] = Vector2.zero;
        }
        _fabricInitialized = true;
    }

    private void EnsureFabricPhysics()
    {
        if (room == null || _fabricUpdateClock == _clock) return;
        _fabricUpdateClock = _clock;

        Vector2 dir = rotation.sqrMagnitude > 0.001f ? rotation.normalized : Vector2.right;
        Vector2 attach = FabricAttachPos(dir, firstChunk.pos);
        if (!_fabricInitialized || Vector2.Distance(_fabric[0, 0], attach) > 70f)
            ResetFabric(attach);

        Vector2 weaponMotion = firstChunk.pos - firstChunk.lastPos;
        Vector2 side = Custom.PerpendicularVector(dir);
        for (int i = 0; i < FabricSegments; i++)
        {
            float progress = (i + 1f) / FabricSegments;
            _fabric[i, 1] = _fabric[i, 0];
            _fabric[i, 0] += _fabric[i, 2];

            bool submerged = room.PointSubmerged(_fabric[i, 0]);
            _fabric[i, 2] *= submerged ? 0.74f : Mathf.Lerp(0.91f, 0.87f, progress);
            _fabric[i, 2].y -= room.gravity * (submerged ? 0.025f : Mathf.Lerp(0.16f, 0.28f, progress));

            // The moving weapon drags air past the loose end. This is intentionally small: the
            // constraint chain provides most of the trailing motion, while this term gives a
            // readable flutter during backstep / brace / charge without becoming a flag pole.
            _fabric[i, 2] -= weaponMotion * (0.010f + 0.018f * progress);
            float flutter = Mathf.Sin((_clock + i * 6.7f) * 0.22f) *
                Mathf.Min(0.16f, weaponMotion.magnitude * 0.018f) * progress;
            _fabric[i, 2] += side * flutter;
        }

        // Three short constraint passes are enough for a four-segment decorative strip. This is
        // the same basic idea as the explosive spear rag: fixed root, chained distance constraints,
        // velocities corrected together with positions so the cloth does not numerically explode.
        for (int iteration = 0; iteration < 3; iteration++)
        {
            ConstrainFabricToAnchor(attach);
            for (int i = 1; i < FabricSegments; i++)
                ConstrainFabricPair(i - 1, i);
        }
    }

    private void ConstrainFabricToAnchor(Vector2 attach)
    {
        Vector2 delta = _fabric[0, 0] - attach;
        float distance = delta.magnitude;
        if (distance < 0.001f) return;
        Vector2 correction = delta / distance * (distance - FabricSegmentLength);
        _fabric[0, 0] -= correction;
        _fabric[0, 2] -= correction * 0.55f;
    }

    private void ConstrainFabricPair(int previous, int current)
    {
        Vector2 delta = _fabric[current, 0] - _fabric[previous, 0];
        float distance = delta.magnitude;
        if (distance < 0.001f) return;
        Vector2 correction = delta / distance * (distance - FabricSegmentLength) * 0.5f;
        _fabric[current, 0] -= correction;
        _fabric[current, 2] -= correction * 0.45f;
        _fabric[previous, 0] += correction;
        _fabric[previous, 2] += correction * 0.45f;
    }

    private void DrawFabric(TriangleMesh mesh, Vector2 direction, Vector2 grip, float timeStacker, Vector2 camPos)
    {
        Vector2 previous = FabricAttachPos(direction, grip);
        float previousWidth = 2.25f;

        for (int i = 0; i < FabricSegments; i++)
        {
            float progress = (i + 1f) / FabricSegments;
            Vector2 current = Vector2.Lerp(_fabric[i, 1], _fabric[i, 0], timeStacker);
            Vector2 tangent = current - previous;
            if (tangent.sqrMagnitude < 0.001f) tangent = Vector2.down;
            tangent.Normalize();
            Vector2 clothPerp = Custom.PerpendicularVector(tangent);

            // Broad at the leather knot, slightly full in the middle, torn down to a narrow end.
            float endWidth = Mathf.Lerp(2.05f, 0.52f, progress) +
                Mathf.Sin(progress * Mathf.PI) * 0.28f;
            int vertex = i * 4;
            mesh.MoveVertice(vertex, previous - clothPerp * previousWidth - camPos);
            mesh.MoveVertice(vertex + 1, previous + clothPerp * previousWidth - camPos);
            mesh.MoveVertice(vertex + 2, current - clothPerp * endWidth - camPos);
            mesh.MoveVertice(vertex + 3, current + clothPerp * endWidth - camPos);

            previous = current;
            previousWidth = endWidth;
        }
    }

    public override void ApplyPalette(RoomCamera.SpriteLeaser sLeaser, RoomCamera rCam, RoomPalette palette)
    {
        float darkness = room == null ? 0f : room.Darkness(firstChunk.pos) * (1f - room.LightSourceExposure(firstChunk.pos));

        // Desert-industrial material language: sand-scoured steel + old leather + faded rust cloth.
        // The metal stays light enough to read against dark rooms but no longer looks like white bone.
        Color handleBase = Color.Lerp(new Color(0.35f, 0.34f, 0.31f), palette.blackColor, darkness * 0.91f);
        Color handleLight = Color.Lerp(new Color(0.50f, 0.48f, 0.42f), palette.blackColor, darkness * 0.86f);
        Color bladeBase = Color.Lerp(new Color(0.57f, 0.56f, 0.51f), palette.blackColor, darkness * 0.90f);
        Color bladeLight = Color.Lerp(new Color(0.72f, 0.69f, 0.61f), palette.blackColor, darkness * 0.84f);
        Color wear = Color.Lerp(new Color(0.39f, 0.37f, 0.33f), palette.blackColor, darkness * 0.96f);
        Color leather = Color.Lerp(new Color(0.31f, 0.205f, 0.125f), palette.blackColor, darkness * 0.93f);
        Color leatherLight = Color.Lerp(new Color(0.43f, 0.29f, 0.17f), palette.blackColor, darkness * 0.91f);
        Color fabric = Color.Lerp(new Color(0.56f, 0.285f, 0.145f), palette.blackColor, darkness * 0.90f);

        sLeaser.sprites[0].color = handleBase;
        sLeaser.sprites[1].color = handleLight;
        sLeaser.sprites[2].color = bladeBase;
        sLeaser.sprites[3].color = bladeLight;
        for (int i = 4; i < 7; i++) sLeaser.sprites[i].color = wear;
        sLeaser.sprites[7].color = leather;
        sLeaser.sprites[8].color = leatherLight;
        sLeaser.sprites[9].color = leather;
        sLeaser.sprites[10].color = fabric;
    }

    public override void AddToContainer(RoomCamera.SpriteLeaser sLeaser, RoomCamera rCam, FContainer container)
    {
        container ??= rCam.ReturnFContainer("Items");
        foreach (FSprite sprite in sLeaser.sprites) sprite.RemoveFromContainer();

        // Cloth first, then weapon, then leather knot. This makes the fabric visibly emerge from
        // behind the wrap instead of looking pasted over the top of it.
        container.AddChild(sLeaser.sprites[10]);
        for (int i = 0; i < 7; i++) container.AddChild(sLeaser.sprites[i]);
        for (int i = 7; i < 10; i++) container.AddChild(sLeaser.sprites[i]);
    }
}
