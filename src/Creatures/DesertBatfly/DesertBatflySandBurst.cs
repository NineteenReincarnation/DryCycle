using System;
using RWCustom;
using UnityEngine;

namespace DryCycle.Creatures.DesertBatfly;

// One short-lived object owns both the world-space burst and the near-camera marks.
// This avoids spawning a manager or one UpdatableAndDeletable per grain. The HUD2
// marks are intentionally sparse and capped per room so the effect obstructs rather
// than blinds the player.
internal sealed class DesertBatflySandBurst : CosmeticSprite
{
    private sealed class WorldGrain
    {
        internal Vector2 Pos, LastPos, Vel;
        internal readonly float Scale, RotationSpeed;
        internal float Rotation;
        internal readonly int Life;
        internal readonly bool Round;

        internal WorldGrain(Vector2 pos, Vector2 vel, float scale, float rotation, float rotationSpeed, int life, bool round)
        {
            Pos = LastPos = pos;
            Vel = vel;
            Scale = scale;
            Rotation = rotation;
            RotationSpeed = rotationSpeed;
            Life = life;
            Round = round;
        }
    }

    private sealed class ScreenMark
    {
        internal readonly Vector2 Offset, Drift;
        internal readonly float ScaleX, ScaleY, Rotation, Alpha;
        internal readonly int Life;
        internal readonly bool Round;

        internal ScreenMark(Vector2 offset, Vector2 drift, float scaleX, float scaleY,
            float rotation, float alpha, int life, bool round)
        {
            Offset = offset;
            Drift = drift;
            ScaleX = scaleX;
            ScaleY = scaleY;
            Rotation = rotation;
            Alpha = alpha;
            Life = life;
            Round = round;
        }
    }

    private readonly Player holder;
    private readonly Vector2 originWorld;
    private readonly WorldGrain[] grains;
    private readonly ScreenMark[] marks;
    private readonly Vector2[] cameraAnchors;
    private readonly bool[] cameraAnchorReady;
    private readonly Color sandColor, darkSandColor;
    private readonly bool screenOverlayEnabled;
    private int age, maxLife;

    private int GrainCount => grains.Length;

    private DesertBatflySandBurst(Room room, DesertBatfly bat, Player holder,
        float intensity, int seed, bool screenOverlayEnabled)
    {
        this.room = room;
        this.holder = holder;
        originWorld = bat.mainBodyChunk.pos;
        pos = lastPos = originWorld;
        this.screenOverlayEnabled = screenOverlayEnabled;

        var random = new System.Random(seed ^ 0x4B1D5EED);
        sandColor = Color.Lerp(
            new Color(0.72f, 0.59f, 0.36f),
            bat.Personality.BaseColor,
            0.42f);
        darkSandColor = Color.Lerp(sandColor, new Color(0.24f, 0.17f, 0.10f), 0.38f);

        int grainCount = Mathf.RoundToInt(Mathf.Lerp(
            DesertBatflyTuning.SandWorldParticleMin,
            DesertBatflyTuning.SandWorldParticleMax,
            intensity));
        grains = new WorldGrain[grainCount];

        Vector2 away = Custom.DirVec(holder.mainBodyChunk.pos, bat.mainBodyChunk.pos);
        if (away.sqrMagnitude < 0.01f) away = Vector2.up;
        float baseAngle = Custom.VecToDeg(away);
        for (int i = 0; i < grains.Length; i++)
        {
            float angle = baseAngle + Lerp(random, -72f, 72f) + Lerp(random, 8f, 28f);
            Vector2 direction = Custom.DegToVec(angle);
            float speed = Lerp(random, 2.2f, 5.2f) * Mathf.Lerp(0.85f, 1.12f, intensity);
            Vector2 velocity = direction * speed + holder.mainBodyChunk.vel * 0.12f;
            int life = Mathf.RoundToInt(Lerp(random, 20f, 38f));
            grains[i] = new WorldGrain(
                originWorld + direction * Lerp(random, 0f, 4f),
                velocity,
                Lerp(random, 0.7f, 1.55f),
                Lerp(random, 0f, 360f),
                Lerp(random, -7f, 7f),
                life,
                random.NextDouble() < 0.42);
            maxLife = Mathf.Max(maxLife, life);
        }

        int markCount = screenOverlayEnabled
            ? Mathf.RoundToInt(Mathf.Lerp(
                DesertBatflyTuning.SandScreenMarkMin,
                DesertBatflyTuning.SandScreenMarkMax,
                intensity))
            : 0;
        marks = new ScreenMark[markCount];
        for (int i = 0; i < marks.Length; i++)
        {
            float angle = Lerp(random, 0f, 360f);
            float radius = Lerp(random, 18f, 92f) * Mathf.Lerp(0.82f, 1.12f, intensity);
            Vector2 offset = Custom.DegToVec(angle) * radius;
            bool round = i % 2 == 0;
            int life = Mathf.RoundToInt(Lerp(
                random,
                DesertBatflyTuning.SandScreenLifeMin,
                DesertBatflyTuning.SandScreenLifeMax));
            float baseScale = Lerp(random, 0.72f, 1.18f) * Mathf.Lerp(0.86f, 1.08f, intensity);
            marks[i] = new ScreenMark(
                offset,
                new Vector2(Lerp(random, -0.14f, 0.14f), Lerp(random, -0.52f, -0.18f)),
                round ? baseScale : Lerp(random, 3.5f, 8.5f),
                round ? baseScale * Lerp(random, 0.55f, 0.95f) : Lerp(random, 1.2f, 3.5f),
                Lerp(random, 0f, 360f),
                Lerp(random, 0.18f, 0.36f) * Mathf.Lerp(0.9f, 1.08f, intensity),
                life,
                round);
            maxLife = Mathf.Max(maxLife, life);
        }

        int cameras = Mathf.Max(1, room?.game?.cameras?.Length ?? 1);
        cameraAnchors = new Vector2[cameras];
        cameraAnchorReady = new bool[cameras];
    }

    internal static void Emit(Room room, DesertBatfly bat, Player holder, float intensity, int seed)
    {
        if (room == null || bat == null || holder == null) return;

        int activeScreenBursts = 0;
        if (room.updateList != null)
        {
            for (int i = 0; i < room.updateList.Count; i++)
            {
                if (room.updateList[i] is DesertBatflySandBurst burst &&
                    !burst.slatedForDeletetion && burst.screenOverlayEnabled)
                    activeScreenBursts++;
            }
        }

        bool allowScreen = activeScreenBursts < DesertBatflyTuning.SandScreenMaxConcurrentBursts;
        room.AddObject(new DesertBatflySandBurst(
            room,
            bat,
            holder,
            Mathf.Clamp01(intensity),
            seed,
            allowScreen));
    }

    public override void Update(bool eu)
    {
        age++;
        lastPos = pos;
        pos = originWorld;

        for (int i = 0; i < grains.Length; i++)
        {
            WorldGrain grain = grains[i];
            grain.LastPos = grain.Pos;
            grain.Pos += grain.Vel;
            grain.Vel.x *= 0.94f;
            grain.Vel.y = grain.Vel.y * 0.94f - 0.18f;
            grain.Rotation += grain.RotationSpeed;
        }

        if (age > maxLife + 2) Destroy();
        base.Update(eu);
    }

    public override void InitiateSprites(RoomCamera.SpriteLeaser sLeaser, RoomCamera rCam)
    {
        sLeaser.sprites = new FSprite[grains.Length + marks.Length];
        for (int i = 0; i < grains.Length; i++)
        {
            FSprite sprite = new FSprite(grains[i].Round ? "Circle20" : "pixel");
            sprite.color = Color.Lerp(sandColor, darkSandColor, i / Mathf.Max(1f, grains.Length - 1f));
            sLeaser.sprites[i] = sprite;
        }

        for (int i = 0; i < marks.Length; i++)
        {
            FSprite sprite = new FSprite(marks[i].Round ? "Circle20" : "pixel");
            sprite.color = Color.Lerp(darkSandColor, sandColor, 0.25f + 0.5f * (i % 2));
            sLeaser.sprites[GrainCount + i] = sprite;
        }

        AddToContainer(sLeaser, rCam, null);
    }

    public override void AddToContainer(RoomCamera.SpriteLeaser sLeaser, RoomCamera rCam, FContainer newContainer)
    {
        FContainer world = rCam.ReturnFContainer("Foreground");
        FContainer screen = rCam.ReturnFContainer("HUD2");

        for (int i = 0; i < sLeaser.sprites.Length; i++)
        {
            FSprite sprite = sLeaser.sprites[i];
            sprite.RemoveFromContainer();
            if (i < GrainCount) world.AddChild(sprite);
            else screen.AddChild(sprite);
        }
    }

    public override void DrawSprites(RoomCamera.SpriteLeaser sLeaser, RoomCamera rCam,
        float timeStacker, Vector2 camPos)
    {
        for (int i = 0; i < grains.Length; i++)
        {
            WorldGrain grain = grains[i];
            FSprite sprite = sLeaser.sprites[i];
            Vector2 draw = Vector2.Lerp(grain.LastPos, grain.Pos, timeStacker) - camPos;
            sprite.x = draw.x;
            sprite.y = draw.y;
            sprite.rotation = grain.Rotation;
            if (grain.Round)
            {
                sprite.scaleX = grain.Scale * 0.075f;
                sprite.scaleY = grain.Scale * 0.05f;
            }
            else
            {
                sprite.scaleX = grain.Scale * 2.1f;
                sprite.scaleY = grain.Scale * 0.75f;
            }
            sprite.alpha = Mathf.Clamp01((grain.Life - age) / 12f) * 0.88f;
            sprite.isVisible = age <= grain.Life;
        }

        bool relevantCamera = screenOverlayEnabled &&
            (!rCam.splitScreenMode || rCam.followAbstractCreature == holder?.abstractCreature);
        int cameraIndex = Mathf.Clamp(rCam.cameraNumber, 0, cameraAnchors.Length - 1);

        if (relevantCamera && !cameraAnchorReady[cameraIndex])
        {
            Vector2 worldAnchor = holder != null && holder.room == rCam.room
                ? holder.mainBodyChunk.pos
                : originWorld;
            cameraAnchors[cameraIndex] = worldAnchor - camPos;
            cameraAnchorReady[cameraIndex] = true;
        }

        Vector2 anchor = cameraAnchors[cameraIndex];
        bool anchorOnScreen = relevantCamera && cameraAnchorReady[cameraIndex] &&
            anchor.x > -120f && anchor.y > -120f &&
            anchor.x < Futile.screen.pixelWidth + 120f &&
            anchor.y < Futile.screen.pixelHeight + 120f;

        for (int i = 0; i < marks.Length; i++)
        {
            ScreenMark mark = marks[i];
            FSprite sprite = sLeaser.sprites[GrainCount + i];
            Vector2 draw = anchor + mark.Offset + mark.Drift * age;
            sprite.x = draw.x;
            sprite.y = draw.y;
            sprite.rotation = mark.Rotation + mark.Drift.x * age * 8f;
            sprite.scaleX = mark.ScaleX;
            sprite.scaleY = mark.ScaleY;
            float fadeIn = Mathf.Clamp01(age / 4f);
            float fadeOut = Mathf.Clamp01((mark.Life - age) / 18f);
            sprite.alpha = mark.Alpha * fadeIn * fadeOut;
            sprite.isVisible = anchorOnScreen && age <= mark.Life;
        }

        base.DrawSprites(sLeaser, rCam, timeStacker, camPos);
    }

    private static float Lerp(System.Random random, float min, float max)
    {
        return Mathf.Lerp(min, max, (float)random.NextDouble());
    }
}
