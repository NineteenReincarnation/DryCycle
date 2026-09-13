using System;
using DryCycle.Framework.KarmicManipulation;
using RWCustom;
using UnityEngine;
using Watcher;

namespace DryCycle.Items.KarmaSpear;

internal sealed class KarmaSpear : Spear
{
    private const int ExtraSpriteCount = 12;
    private const int HaloMeshOffset = 0;
    private const int BodyMeshOffset = 1;
    private const int CoreMeshOffset = 2;
    private const int CrownMeshOffset = 3;
    private const int SealOuterOffset = 4;
    private const int SealInnerOffset = 5;
    private const int GlyphOffset = 6;
    private const int TailSealOffset = 7;
    private const int RuneStartOffset = 8;
    private const float TipAxial = 31f;

    // The visible weapon is not the vanilla SmallSpear with effects layered on top.
    // This profile defines a complete karmic relic silhouette: sealed tail, narrow shaft,
    // expanded ritual collar, broad crown and long faceted spearhead.
    private static readonly float[] BodyAxial =
    {
        -29f, -25f, -21f, -15f, -10f, -6f, 5f, 10f, 14f, 18f, 24f, TipAxial
    };

    private static readonly float[] BodyHalfWidth =
    {
        0.25f, 2.8f, 1.15f, 1.30f, 3.05f, 1.35f, 1.45f, 2.75f, 5.15f, 3.75f, 2.35f, 0.10f
    };

    private static readonly float[] CoreHalfWidth =
    {
        0.05f, 0.75f, 0.42f, 0.48f, 0.88f, 0.48f, 0.52f, 0.82f, 1.28f, 1.02f, 0.62f, 0.04f
    };

    private KarmaSpearField _wallField;
    private bool _bindingStarted;
    private int _trailCounter;
    private int _chargeGeneration;

    internal KarmaSpear(AbstractPhysicalObject abstractPhysicalObject, World world)
        : base(abstractPhysicalObject, world)
    {
    }

    private AbstractKarmaSpear KarmaAbstract => abstractPhysicalObject as AbstractKarmaSpear;

    internal int KarmaLevel => KarmaAbstract?.KarmaLevel ?? 1;
    internal bool IsSpent => KarmaAbstract?.Spent != false;
    internal int ChargeGeneration => _chargeGeneration;

    public override void Update(bool eu)
    {
        base.Update(eu);

        if (room == null)
        {
            StopWallField();
            return;
        }

        if (IsSpent)
        {
            StopWallField();
            return;
        }

        if (mode == Mode.Thrown && firstChunk.vel.sqrMagnitude > 36f)
        {
            _trailCounter++;
            if (_trailCounter % 3 == 0)
            {
                Vector2 direction = rotation.sqrMagnitude > 0.001f
                    ? rotation.normalized
                    : firstChunk.vel.normalized;
                KarmicVisualEffects.SpawnSpearAfterimage(
                    room,
                    firstChunk.pos - direction * 5f,
                    direction,
                    KarmaLevel);
            }
        }
        else
        {
            _trailCounter = 0;
        }

        if (mode == Mode.StuckInWall)
        {
            StartWallField();
        }
        else if (_wallField != null)
        {
            StopWallField();
        }
    }

    public override void Thrown(
        Creature thrownBy,
        Vector2 thrownPos,
        Vector2? firstFrameTraceFromPos,
        IntVector2 throwDir,
        float frc,
        bool eu)
    {
        base.Thrown(thrownBy, thrownPos, firstFrameTraceFromPos, throwDir, frc, eu);

        if (!IsSpent && room != null)
        {
            room.PlaySound(WatcherEnums.WatcherSoundID.Templar_Shield_Tick_7, firstChunk);
            KarmicVisualEffects.SpawnChargePulse(this, 1f);
            KarmicVisualEffects.SpawnSparks(room, firstChunk.pos, 5, 3.4f);
        }
    }

    public override bool HitSomething(SharedPhysics.CollisionResult result, bool eu)
    {
        bool hit = base.HitSomething(result, eu);

        if (!hit || IsSpent || _bindingStarted || result.obj is not Creature creature)
        {
            return hit;
        }

        ActivateCreatureBinding(creature, result.chunk?.pos ?? firstChunk.pos);
        return hit;
    }

    public override void ChangeMode(Mode newMode)
    {
        Mode oldMode = mode;
        base.ChangeMode(newMode);

        if (IsSpent)
        {
            StopWallField();
            return;
        }

        if (newMode == Mode.StuckInWall)
        {
            StartWallField();
        }
        else if (oldMode == Mode.StuckInWall && newMode != Mode.StuckInWall)
        {
            StopWallField();
        }
    }

    private void ActivateCreatureBinding(Creature creature, Vector2 hitPosition)
    {
        _bindingStarted = true;
        StopWallField();
        KarmicVisualEffects.SpawnImpactPulse(this, hitPosition, KarmaLevel, 82f + KarmaLevel * 3f);
        room?.PlaySound(WatcherEnums.WatcherSoundID.Templar_Shield_Deflect, firstChunk);

        bool small = IsSmallTarget(creature);
        bool large = IsLargeTarget(creature);

        if (small)
        {
            creature.Die();
            MarkSpent();
            return;
        }

        bool lodged = mode == Mode.StuckInCreature && ReferenceEquals(stuckInObject, creature);
        KarmicBindingEffect binding = new(this, creature, creature.mainBodyChunk.pos, large, lodged);
        room.AddObject(binding);

        if (!lodged)
        {
            MarkSpent();
        }
    }

    private static bool IsSmallTarget(Creature creature)
    {
        return creature.TotalMass <= 0.34f;
    }

    private static bool IsLargeTarget(Creature creature)
    {
        return creature.TotalMass >= 3.4f ||
               creature is Vulture ||
               creature is MirosBird ||
               creature is DaddyLongLegs ||
               creature is BigEel;
    }

    private void StartWallField()
    {
        if (IsSpent || room == null || mode != Mode.StuckInWall)
        {
            return;
        }

        if (_wallField != null && !_wallField.slatedForDeletetion)
        {
            return;
        }

        room.PlaySound(WatcherEnums.WatcherSoundID.Templar_Shield_Deflect, firstChunk);
        KarmicVisualEffects.SpawnImpactPulse(this, firstChunk.pos, KarmaLevel, 96f);
        KarmicVisualEffects.SpawnSparks(room, firstChunk.pos, 12, 4.2f);

        _wallField = new KarmaSpearField(this);
        room.AddObject(_wallField);
    }

    private void StopWallField()
    {
        if (_wallField == null)
        {
            return;
        }

        if (!_wallField.slatedForDeletetion)
        {
            _wallField.Destroy();
        }
        _wallField = null;
    }

    internal void MarkSpent()
    {
        if (KarmaAbstract == null || KarmaAbstract.Spent)
        {
            return;
        }

        StopWallField();
        KarmaAbstract.Spent = true;
        KarmicVisualEffects.SpawnImpactPulse(this, firstChunk.pos, KarmaLevel, 54f);
        KarmicVisualEffects.SpawnSparks(room, firstChunk.pos, 8, 4f);
    }

    internal void MarkSpent(int expectedChargeGeneration)
    {
        if (expectedChargeGeneration != _chargeGeneration)
        {
            return;
        }

        MarkSpent();
    }

    internal bool Recharge(int karmaLevel)
    {
        if (KarmaAbstract == null || !KarmaAbstract.Spent)
        {
            return false;
        }

        StopWallField();
        KarmaAbstract.KarmaLevel = Mathf.Clamp(karmaLevel, 1, 10);
        KarmaAbstract.Spent = false;
        _bindingStarted = false;
        _trailCounter = 0;
        _chargeGeneration++;
        return true;
    }

    public override void InitiateSprites(RoomCamera.SpriteLeaser sLeaser, RoomCamera rCam)
    {
        base.InitiateSprites(sLeaser, rCam);

        int baseCount = sLeaser.sprites.Length;
        FSprite[] sprites = new FSprite[baseCount + ExtraSpriteCount];
        Array.Copy(sLeaser.sprites, sprites, baseCount);
        sLeaser.sprites = sprites;

        // Three complete geometry layers form the actual weapon. The vanilla spear sprite
        // stays only as a hidden compatibility sprite for inherited rendering code.
        sLeaser.sprites[baseCount + HaloMeshOffset] = MakeStripMesh(BodyAxial.Length);
        sLeaser.sprites[baseCount + BodyMeshOffset] = MakeStripMesh(BodyAxial.Length);
        sLeaser.sprites[baseCount + CoreMeshOffset] = MakeStripMesh(BodyAxial.Length);
        sLeaser.sprites[baseCount + CrownMeshOffset] = MakeCrownMesh();

        sLeaser.sprites[baseCount + SealOuterOffset] = MakeVectorCircle(rCam);
        sLeaser.sprites[baseCount + SealInnerOffset] = MakeVectorCircle(rCam);
        sLeaser.sprites[baseCount + GlyphOffset] = new FSprite(CurrentKarmaGlyphName());
        sLeaser.sprites[baseCount + TailSealOffset] = MakeVectorCircle(rCam);

        for (int i = 0; i < 4; i++)
        {
            sLeaser.sprites[baseCount + RuneStartOffset + i] = new FSprite("pixel");
        }

        AddToContainer(sLeaser, rCam, null);
    }

    private static TriangleMesh MakeStripMesh(int sections)
    {
        TriangleMesh.Triangle[] triangles = new TriangleMesh.Triangle[(sections - 1) * 2];
        for (int i = 0; i < sections - 1; i++)
        {
            int vertex = i * 2;
            triangles[i * 2] = new TriangleMesh.Triangle(vertex, vertex + 1, vertex + 2);
            triangles[i * 2 + 1] = new TriangleMesh.Triangle(vertex + 1, vertex + 3, vertex + 2);
        }

        return new TriangleMesh("Futile_White", triangles, customColor: true);
    }

    private static TriangleMesh MakeCrownMesh()
    {
        TriangleMesh.Triangle[] triangles =
        {
            new(0, 1, 2),
            new(3, 4, 5),
            new(6, 7, 8),
            new(9, 10, 11)
        };
        return new TriangleMesh("Futile_White", triangles, customColor: true);
    }

    private static FSprite MakeVectorCircle(RoomCamera rCam)
    {
        return new FSprite("Futile_White")
        {
            shader = rCam.room.game.rainWorld.Shaders["VectorCircle"]
        };
    }

    public override void DrawSprites(
        RoomCamera.SpriteLeaser sLeaser,
        RoomCamera rCam,
        float timeStacker,
        Vector2 camPos)
    {
        base.DrawSprites(sLeaser, rCam, timeStacker, camPos);

        int baseCount = sLeaser.sprites.Length - ExtraSpriteCount;
        if (baseCount <= 0)
        {
            return;
        }

        Vector2 chunkCenter = Vector2.Lerp(firstChunk.lastPos, firstChunk.pos, timeStacker);
        if (vibrate > 0)
        {
            chunkCenter += Custom.DegToVec(UnityEngine.Random.value * 360f) * (2f * UnityEngine.Random.value);
        }

        Vector3 slerped = Vector3.Slerp(lastRotation, rotation, timeStacker);
        Vector2 direction = new(slerped.x, slerped.y);
        if (direction.sqrMagnitude < 0.001f)
        {
            direction = Vector2.right;
        }
        direction.Normalize();
        Vector2 perpendicular = new(-direction.y, direction.x);

        float tipDistance = Mathf.Lerp(lastPivotAtTip ? 7f : 26f, pivotAtTip ? 7f : 26f, timeStacker);
        Vector2 visualCenter = chunkCenter + direction * (tipDistance - TipAxial);
        float clock = room?.game?.clock ?? 0;
        float pulse = 0.5f + 0.5f * Mathf.Sin(clock * 0.12f + KarmaLevel * 0.71f);
        float slowPulse = 0.5f + 0.5f * Mathf.Sin(clock * 0.055f + 1.35f);
        bool active = !IsSpent;
        bool wallAnchored = active && mode == Mode.StuckInWall;

        // The inherited SmallSpear is deliberately invisible. All visible mass below is
        // custom geometry, so the design still reads as Karma Spear with every effect off.
        sLeaser.sprites[0].alpha = 0f;

        TriangleMesh halo = (TriangleMesh)sLeaser.sprites[baseCount + HaloMeshOffset];
        TriangleMesh body = (TriangleMesh)sLeaser.sprites[baseCount + BodyMeshOffset];
        TriangleMesh core = (TriangleMesh)sLeaser.sprites[baseCount + CoreMeshOffset];
        TriangleMesh crown = (TriangleMesh)sLeaser.sprites[baseCount + CrownMeshOffset];

        SetStripGeometry(halo, visualCenter, direction, perpendicular, camPos, BodyHalfWidth, 1.18f, 0.65f);
        SetStripGeometry(body, visualCenter, direction, perpendicular, camPos, BodyHalfWidth, 1f, 0f);
        SetStripGeometry(core, visualCenter, direction, perpendicular, camPos, CoreHalfWidth, 1f, 0f);
        SetCrownGeometry(crown, visualCenter, direction, perpendicular, camPos);

        Color gold = KarmicVisualEffects.Gold;
        Color paleGold = Color.Lerp(gold, Color.white, 0.72f);
        Color deepGold = Color.Lerp(new Color(0.12f, 0.09f, 0.035f), gold, 0.38f);
        Color deadMetal = Color.Lerp(new Color(0.055f, 0.052f, 0.045f), gold, 0.12f);
        Color deadEdge = Color.Lerp(new Color(0.12f, 0.10f, 0.07f), gold, 0.20f);

        for (int i = 0; i < BodyAxial.Length; i++)
        {
            float along = (float)i / (BodyAxial.Length - 1);
            float blade = Mathf.InverseLerp(0.48f, 1f, along);
            float seal = 1f - Mathf.Clamp01(Mathf.Abs(BodyAxial[i] - 14f) / 18f);

            Color bodyColor = active
                ? Color.Lerp(deepGold, Color.Lerp(gold, paleGold, blade * 0.62f), 0.30f + seal * 0.28f)
                : Color.Lerp(deadMetal, deadEdge, blade * 0.45f + seal * 0.18f);
            SetSectionColor(body, i, bodyColor, 1f);

            Color coreColor = active
                ? Color.Lerp(gold, Color.white, 0.42f + blade * 0.38f)
                : Color.Lerp(deadMetal, deadEdge, 0.28f);
            SetSectionColor(core, i, coreColor, active ? Mathf.Lerp(0.52f, 0.92f, pulse) : 0.22f);

            SetSectionColor(
                halo,
                i,
                active ? paleGold : deadMetal,
                active ? Mathf.Lerp(0.035f, wallAnchored ? 0.16f : 0.10f, pulse) : 0f);
        }

        SetCrownColors(crown, active, wallAnchored, pulse, gold, paleGold, deadEdge);

        Vector2 sealCenter = visualCenter + direction * 13.5f;
        FSprite outerSeal = sLeaser.sprites[baseCount + SealOuterOffset];
        FSprite innerSeal = sLeaser.sprites[baseCount + SealInnerOffset];
        FSprite glyph = sLeaser.sprites[baseCount + GlyphOffset];
        FSprite tailSeal = sLeaser.sprites[baseCount + TailSealOffset];

        SetSpritePosition(outerSeal, sealCenter, camPos);
        outerSeal.scale = Mathf.Lerp(1.35f, wallAnchored ? 2.20f : 1.78f, pulse);
        outerSeal.alpha = active ? Mathf.Lerp(0.14f, wallAnchored ? 0.35f : 0.24f, pulse) : 0.06f;
        outerSeal.color = active ? gold : deadEdge;

        SetSpritePosition(innerSeal, sealCenter, camPos);
        innerSeal.scale = Mathf.Lerp(0.58f, wallAnchored ? 0.98f : 0.82f, slowPulse);
        innerSeal.alpha = active ? Mathf.Lerp(0.28f, wallAnchored ? 0.58f : 0.47f, slowPulse) : 0.08f;
        innerSeal.color = active ? paleGold : deadEdge;

        string karmaGlyphName = CurrentKarmaGlyphName();
        if (glyph.element == null || glyph.element.name != karmaGlyphName)
        {
            glyph.SetElementByName(karmaGlyphName);
        }
        SetSpritePosition(glyph, sealCenter, camPos);
        glyph.rotation = -clock * (wallAnchored ? 0.18f : 0.30f);
        glyph.scale = Mathf.Lerp(0.20f, wallAnchored ? 0.32f : 0.27f, pulse);
        glyph.alpha = active ? Mathf.Lerp(0.58f, wallAnchored ? 0.98f : 0.88f, pulse) : 0.13f;
        glyph.color = active ? Color.Lerp(Color.white, gold, 0.48f) : deadEdge;

        Vector2 tailCenter = visualCenter + direction * -25f;
        SetSpritePosition(tailSeal, tailCenter, camPos);
        tailSeal.scale = Mathf.Lerp(0.42f, active ? 0.68f : 0.52f, slowPulse);
        tailSeal.alpha = active ? Mathf.Lerp(0.12f, 0.26f, slowPulse) : 0.07f;
        tailSeal.color = active ? gold : deadEdge;

        // These are engraved cross-strokes in the shaft, not free particles. They remain
        // visible on a spent spear as dark ritual cuts, preserving the relic identity.
        float[] runeAxial = { -16.5f, -8.8f, -0.5f, 7.2f };
        float rotationDeg = Custom.AimFromOneVectorToAnother(Vector2.zero, direction);
        for (int i = 0; i < 4; i++)
        {
            FSprite rune = sLeaser.sprites[baseCount + RuneStartOffset + i];
            Vector2 runePos = visualCenter + direction * runeAxial[i];
            SetSpritePosition(rune, runePos, camPos);
            rune.rotation = rotationDeg + (i % 2 == 0 ? 52f : -52f);
            rune.scaleX = 1.05f;
            rune.scaleY = 4.8f + i * 0.45f;
            rune.color = active ? Color.Lerp(gold, Color.white, 0.24f + i * 0.06f) : deadEdge;
            rune.alpha = active
                ? Mathf.Lerp(0.50f, wallAnchored ? 0.90f : 0.72f, 0.5f + 0.5f * Mathf.Sin(clock * 0.10f + i))
                : 0.42f;
        }
    }

    private string CurrentKarmaGlyphName()
    {
        int value = Mathf.Clamp(KarmaLevel - 1, 0, 9);
        return global::HUD.KarmaMeter.KarmaSymbolSprite(
            small: false,
            new IntVector2(value, value));
    }

    private static void SetStripGeometry(
        TriangleMesh mesh,
        Vector2 center,
        Vector2 direction,
        Vector2 perpendicular,
        Vector2 camPos,
        float[] halfWidths,
        float widthScale,
        float widthExtra)
    {
        for (int i = 0; i < BodyAxial.Length; i++)
        {
            float width = halfWidths[i] * widthScale;
            if (halfWidths[i] > 0.2f)
            {
                width += widthExtra;
            }

            Vector2 sectionCenter = center + direction * BodyAxial[i];
            mesh.MoveVertice(i * 2, sectionCenter - perpendicular * width - camPos);
            mesh.MoveVertice(i * 2 + 1, sectionCenter + perpendicular * width - camPos);
        }
    }

    private static void SetSectionColor(TriangleMesh mesh, int section, Color color, float alpha)
    {
        color.a = alpha;
        mesh.verticeColors[section * 2] = color;
        mesh.verticeColors[section * 2 + 1] = color;
    }

    private static void SetCrownGeometry(
        TriangleMesh mesh,
        Vector2 center,
        Vector2 direction,
        Vector2 perpendicular,
        Vector2 camPos)
    {
        // Four angular plates wrap the central Karma seal. Their mirrored shape echoes
        // the rotational symmetry of Karma glyphs instead of conventional spear guards.
        SetCrownTriangle(mesh, 0, center, direction, perpendicular, camPos,
            8.5f, 1.6f, 9.4f, 7.4f, 14.0f, 4.7f);
        SetCrownTriangle(mesh, 3, center, direction, perpendicular, camPos,
            8.5f, -1.6f, 9.4f, -7.4f, 14.0f, -4.7f);
        SetCrownTriangle(mesh, 6, center, direction, perpendicular, camPos,
            14.4f, 4.2f, 19.2f, 6.2f, 17.3f, 1.8f);
        SetCrownTriangle(mesh, 9, center, direction, perpendicular, camPos,
            14.4f, -4.2f, 19.2f, -6.2f, 17.3f, -1.8f);
    }

    private static void SetCrownTriangle(
        TriangleMesh mesh,
        int startVertex,
        Vector2 center,
        Vector2 direction,
        Vector2 perpendicular,
        Vector2 camPos,
        float aAxial,
        float aLateral,
        float bAxial,
        float bLateral,
        float cAxial,
        float cLateral)
    {
        mesh.MoveVertice(startVertex, CrownPoint(center, direction, perpendicular, camPos, aAxial, aLateral));
        mesh.MoveVertice(startVertex + 1, CrownPoint(center, direction, perpendicular, camPos, bAxial, bLateral));
        mesh.MoveVertice(startVertex + 2, CrownPoint(center, direction, perpendicular, camPos, cAxial, cLateral));
    }

    private static Vector2 CrownPoint(
        Vector2 center,
        Vector2 direction,
        Vector2 perpendicular,
        Vector2 camPos,
        float axial,
        float lateral)
    {
        return center + direction * axial + perpendicular * lateral - camPos;
    }

    private static void SetCrownColors(
        TriangleMesh mesh,
        bool active,
        bool wallAnchored,
        float pulse,
        Color gold,
        Color paleGold,
        Color deadEdge)
    {
        for (int triangle = 0; triangle < 4; triangle++)
        {
            int start = triangle * 3;
            Color root = active ? Color.Lerp(gold, paleGold, 0.16f) : deadEdge;
            Color edge = active ? Color.Lerp(gold, paleGold, 0.62f) : deadEdge;
            float alpha = active ? Mathf.Lerp(0.78f, wallAnchored ? 1f : 0.94f, pulse) : 0.52f;
            root.a = alpha;
            edge.a = alpha;
            mesh.verticeColors[start] = root;
            mesh.verticeColors[start + 1] = edge;
            mesh.verticeColors[start + 2] = edge;
        }
    }

    private static void SetSpritePosition(FSprite sprite, Vector2 worldPos, Vector2 camPos)
    {
        sprite.x = worldPos.x - camPos.x;
        sprite.y = worldPos.y - camPos.y;
    }
}
