using RWCustom;
using UnityEngine;

namespace DryCycle.Items.ScavengerLance;

internal sealed partial class ScavengerLance
{
    private static readonly float[] BladeSections = { 0f, 0.22f, 0.50f, 0.74f, 0.90f, 1f };

    // A clearly readable torn cloth strip tied to the leather wrap. The motion model follows the
    // vanilla ExplosiveSpear rag idea: a short constrained point chain with gravity, drag and
    // inertial lag, rendered as a jagged long mesh. It is cosmetic only.
    private const int FabricSegments = 5;
    private const float FabricSegmentLength = 5.6f;
    private readonly Vector2[,] _fabric = new Vector2[FabricSegments, 3]; // pos / lastPos / vel
    private bool _fabricInitialized;
    private int _fabricUpdateClock = int.MinValue;

    public override void InitiateSprites(RoomCamera.SpriteLeaser sLeaser, RoomCamera rCam)
    {
        // 0/1 : matte desert handle / weak dusty edge light
        // 2/3 : asymmetric ochre wedge / very narrow sand-polished facet
        // 4-6 : short abrasion / dirt marks
        // 7-9 : broad leather turns at the blade-handle junction
        // 10  : leather knot protruding from the wrap
        // 11  : torn desert cloth streamer tied into that knot
        //
        // The weapon deliberately uses no black exterior outline. Its visual language is now
        // primarily dry ochre / brown-yellow rather than bright grey metal.
        sLeaser.sprites = new FSprite[12];
        sLeaser.sprites[0] = TriangleMesh.MakeLongMesh(5, false, false);
        sLeaser.sprites[1] = TriangleMesh.MakeLongMesh(5, false, false);
        sLeaser.sprites[2] = TriangleMesh.MakeLongMesh(5, true, false);
        sLeaser.sprites[3] = TriangleMesh.MakeLongMesh(5, true, false);
        for (int i = 4; i < 11; i++) sLeaser.sprites[i] = new FSprite("pixel");
        sLeaser.sprites[11] = TriangleMesh.MakeLongMesh(FabricSegments, false, false);
        sLeaser.sprites[11].shader = rCam.game.rainWorld.Shaders["JaggedSquare"];
        sLeaser.sprites[11].alpha = Mathf.Lerp(0.90f, 0.98f,
            rCam.game.SeededRandom(abstractPhysicalObject.ID.RandomSeed));

        // Keep the secondary surfaces subdued. The previous implementation let these layers read as
        // specular steel; lower alpha makes them feel like dusty wear instead of polished highlights.
        sLeaser.sprites[1].alpha = 0.72f;
        sLeaser.sprites[3].alpha = 0.62f;
        for (int i = 4; i < 7; i++) sLeaser.sprites[i].alpha = 0.72f;

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

        // Most of the weapon is a dry, matte brown-yellow mass. Secondary layers are deliberately
        // narrow and low-contrast, so the lance reads as something weathered by the desert rather
        // than as a clean silver weapon imported from another visual style.
        DrawHandle((TriangleMesh)sLeaser.sprites[0], tail, bladeRoot, direction, perp, bend, 1.82f, 0f, camPos);
        DrawHandle((TriangleMesh)sLeaser.sprites[1], tail, bladeRoot, direction, perp, bend, 0.30f, 0.28f, camPos);
        DrawBlade((TriangleMesh)sLeaser.sprites[2], bladeRoot, tip, direction, perp, camPos);
        DrawBladeHighlight((TriangleMesh)sLeaser.sprites[3], bladeRoot, tip, direction, perp, camPos);

        // Short, low-contrast marks break up the face without making it glossy or bone-like.
        float[] markT = { 0.29f, 0.53f, 0.73f };
        float[] markTilt = { 13f, -10f, 16f };
        float[] markLength = { 0.22f, 0.27f, 0.20f };
        for (int i = 0; i < 3; i++)
        {
            float bladeT = markT[i];
            FSprite mark = sLeaser.sprites[4 + i];
            Vector2 position = Vector2.Lerp(bladeRoot, tip, bladeT);
            GetBladeWidths(bladeT, out float left, out float right);
            position += perp * (i == 1 ? -0.35f : 0.30f);
            mark.SetPosition(position - camPos);
            mark.rotation = Custom.VecToDeg(direction) + 90f + markTilt[i];
            mark.scaleX = Mathf.Max(1.45f, (left + right) * markLength[i]);
            mark.scaleY = i == 1 ? 0.34f : 0.30f;
        }

        // Make the leather junction a real visual feature. It sits immediately behind the blade
        // shoulder, extends far enough outside the scavenger hand to remain visible, and is broad
        // enough to read at Rain World's native pixel scale.
        Vector2 wrapCenter = WrapCenter(direction, grip);
        for (int i = 0; i < 3; i++)
        {
            FSprite band = sLeaser.sprites[7 + i];
            Vector2 position = wrapCenter + direction * ((i - 1) * 2.75f);
            band.SetPosition(position - camPos);
            band.rotation = Custom.VecToDeg(direction) + 90f + (i == 1 ? -6f : 5f);
            band.scaleX = i == 1 ? 8.35f : 7.65f;
            band.scaleY = i == 1 ? 1.70f : 1.48f;
        }

        FSprite knot = sLeaser.sprites[10];
        Vector2 knotPos = wrapCenter - perp * 3.15f + direction * 0.95f;
        knot.SetPosition(knotPos - camPos);
        knot.rotation = Custom.VecToDeg(direction) + 58f;
        knot.scaleX = 4.25f;
        knot.scaleY = 2.35f;

        EnsureFabricPhysics();
        DrawFabric((TriangleMesh)sLeaser.sprites[11], direction, grip, t, camPos);

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
        // This is no longer a metallic sheen strip. It is a very narrow, dusty facet where repeated
        // sand abrasion has made the surface slightly lighter. Keeping it thin is what removes most
        // of the polished-metal read at native resolution.
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
        mesh.MoveVertice(index, basePoint + perp * right * 0.03f - camPos);
        mesh.MoveVertice(index + 1, basePoint + perp * right * 0.17f - camPos);
        mesh.MoveVertice(index + 2, Vector2.Lerp(root, tip, 0.945f) + perp * 0.03f - camPos);
    }

    private static void MoveHighlightPair(TriangleMesh mesh, int index, Vector2 root, Vector2 tip,
        Vector2 perp, float t, Vector2 camPos)
    {
        Vector2 center = Vector2.Lerp(root, tip, t);
        GetBladeWidths(t, out _, out float right);
        float fade = Mathf.Lerp(1f, 0.45f, t);
        float innerNear = right * 0.025f * fade;
        float innerFar = right * 0.18f * fade;
        mesh.MoveVertice(index, center + perp * innerNear - camPos);
        mesh.MoveVertice(index + 1, center + perp * innerFar - camPos);
    }

    private static void GetBladeWidths(float t, out float left, out float right)
    {
        float shaped = Mathf.Pow(Mathf.Clamp01(t), 0.82f);
        left = Mathf.Lerp(4.55f, 0.10f, shaped);
        right = Mathf.Lerp(6.85f, 0.10f, shaped);
    }

    private Vector2 WrapCenter(Vector2 direction, Vector2 grip)
    {
        Vector2 dir = direction.sqrMagnitude > 0.001f ? direction.normalized : Vector2.right;
        Vector2 bladeRoot = grip + dir * LanceCombatMath.BladeRootDistance(Length);
        // Put the binding at the actual blade/handle transition instead of burying it close to the
        // hand. A small rear offset keeps the leather from covering the blade shoulder itself.
        return bladeRoot - dir * 0.85f;
    }

    private Vector2 FabricAttachPos(Vector2 direction, Vector2 grip)
    {
        Vector2 dir = direction.sqrMagnitude > 0.001f ? direction.normalized : Vector2.right;
        Vector2 perp = Custom.PerpendicularVector(dir);
        // The rag visibly exits from the lower side of the leather knot rather than from the shaft.
        return WrapCenter(dir, grip) - perp * 3.30f + dir * 1.05f;
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
        if (!_fabricInitialized || Vector2.Distance(_fabric[0, 0], attach) > 80f)
            ResetFabric(attach);

        Vector2 weaponMotion = firstChunk.pos - firstChunk.lastPos;
        Vector2 side = Custom.PerpendicularVector(dir);
        for (int i = 0; i < FabricSegments; i++)
        {
            float progress = (i + 1f) / FabricSegments;
            _fabric[i, 1] = _fabric[i, 0];
            _fabric[i, 0] += _fabric[i, 2];

            bool submerged = room.PointSubmerged(_fabric[i, 0]);
            _fabric[i, 2] *= submerged ? 0.74f : Mathf.Lerp(0.92f, 0.875f, progress);
            _fabric[i, 2].y -= room.gravity * (submerged ? 0.025f : Mathf.Lerp(0.14f, 0.27f, progress));

            // More readable than the previous thread-like implementation: the rag trails clearly
            // during backstep/charge and has a small irregular flutter while the weapon is held.
            _fabric[i, 2] -= weaponMotion * (0.017f + 0.026f * progress);
            float flutter = Mathf.Sin((_clock + i * 6.7f) * 0.22f) *
                Mathf.Min(0.30f, 0.035f + weaponMotion.magnitude * 0.026f) * progress;
            _fabric[i, 2] += side * flutter;
        }

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
        float previousWidth = 3.35f;

        for (int i = 0; i < FabricSegments; i++)
        {
            float progress = (i + 1f) / FabricSegments;
            Vector2 current = Vector2.Lerp(_fabric[i, 1], _fabric[i, 0], timeStacker);
            Vector2 tangent = current - previous;
            if (tangent.sqrMagnitude < 0.001f) tangent = Vector2.down;
            tangent.Normalize();
            Vector2 clothPerp = Custom.PerpendicularVector(tangent);

            // A compact torn strip: broad enough to read as cloth, but short enough not to become a
            // flag. JaggedSquare supplies the broken edge while the width falls toward a torn tip.
            float endWidth = Mathf.Lerp(3.05f, 0.72f, progress) +
                Mathf.Sin(progress * Mathf.PI) * 0.42f;
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

        // Matte desert palette. Brown-yellow / ochre is now the dominant read; contrast stays low so
        // the weapon sits inside Rain World's dusty environments instead of presenting as polished
        // silver metal. The lighter colors represent sand abrasion, not specular shine.
        Color handleBase = Color.Lerp(new Color(0.285f, 0.225f, 0.120f), palette.blackColor, darkness * 0.92f);
        Color handleLight = Color.Lerp(new Color(0.420f, 0.335f, 0.175f), palette.blackColor, darkness * 0.89f);
        Color bladeBase = Color.Lerp(new Color(0.465f, 0.365f, 0.190f), palette.blackColor, darkness * 0.91f);
        Color bladeLight = Color.Lerp(new Color(0.585f, 0.475f, 0.265f), palette.blackColor, darkness * 0.89f);
        Color wear = Color.Lerp(new Color(0.335f, 0.265f, 0.135f), palette.blackColor, darkness * 0.94f);
        Color leather = Color.Lerp(new Color(0.235f, 0.135f, 0.060f), palette.blackColor, darkness * 0.93f);
        Color leatherLight = Color.Lerp(new Color(0.405f, 0.255f, 0.105f), palette.blackColor, darkness * 0.91f);
        Color fabric = Color.Lerp(new Color(0.565f, 0.355f, 0.105f), palette.blackColor, darkness * 0.90f);

        sLeaser.sprites[0].color = handleBase;
        sLeaser.sprites[1].color = handleLight;
        sLeaser.sprites[2].color = bladeBase;
        sLeaser.sprites[3].color = bladeLight;
        for (int i = 4; i < 7; i++) sLeaser.sprites[i].color = wear;
        sLeaser.sprites[7].color = leather;
        sLeaser.sprites[8].color = leatherLight;
        sLeaser.sprites[9].color = leather;
        sLeaser.sprites[10].color = leatherLight;
        sLeaser.sprites[11].color = fabric;
    }

    public override void AddToContainer(RoomCamera.SpriteLeaser sLeaser, RoomCamera rCam, FContainer container)
    {
        container ??= rCam.ReturnFContainer("Items");
        foreach (FSprite sprite in sLeaser.sprites) sprite.RemoveFromContainer();

        // Rag behind the weapon, then the matte ochre body, then the full leather wrap/knot on top.
        // The wrap remains the foreground part so the hand-built material change stays readable.
        container.AddChild(sLeaser.sprites[11]);
        for (int i = 0; i < 7; i++) container.AddChild(sLeaser.sprites[i]);
        for (int i = 7; i < 11; i++) container.AddChild(sLeaser.sprites[i]);
    }
}
