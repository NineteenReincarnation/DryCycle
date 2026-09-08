using System;
using RWCustom;
using UnityEngine;

namespace DryCycle.Creatures.DesertBatfly;

// Keep the complete vanilla Batfly animation/pose pipeline. Desert Batfly only
// changes scale, palette and adds lightweight markings/spikes on top of it.
internal sealed class DB_Graphics : FlyGraphics
{
    private readonly struct PatternMark
    {
        internal readonly bool Wing;
        internal readonly int Side;
        internal readonly float Along;
        internal readonly float Offset;
        internal readonly float Scale;
        internal readonly float Shade;
        internal readonly int Shape;

        internal PatternMark(bool wing, int side, float along, float offset, float scale, float shade, int shape)
        {
            Wing = wing;
            Side = side;
            Along = along;
            Offset = offset;
            Scale = scale;
            Shade = shade;
            Shape = shape;
        }
    }

    private const int VanillaSpriteCount = 4;
    private readonly DB_Creature desert;
    private readonly PatternMark[] patterns;
    private readonly float[] spikeLengths;
    private Color bodyColor, wingColor, darkMark, warmMark;
    private int PatternStart => VanillaSpriteCount;
    private int SpikeStart => PatternStart + patterns.Length;

    internal DB_Graphics(DB_Creature owner) : base(owner)
    {
        desert = owner;

        var random = new System.Random(owner.Personality.PatternSeed);
        patterns = new PatternMark[owner.Personality.PatternCount];
        for (int i = 0; i < patterns.Length; i++)
        {
            bool wing = i % 3 != 0;
            int side = random.NextDouble() < 0.5 ? -1 : 1;
            float along = Mathf.Lerp(0.18f, 0.88f, (float)random.NextDouble());
            float offset = Mathf.Lerp(-0.75f, 0.75f, (float)random.NextDouble());
            float scale = Mathf.Lerp(0.75f, 1.35f, (float)random.NextDouble());
            float shade = (float)random.NextDouble();
            patterns[i] = new PatternMark(wing, side, along, offset, scale, shade, i % 4);
        }

        random = new System.Random(owner.Personality.SpikeSeed);
        spikeLengths = new float[owner.Personality.SpikeCount];
        for (int i = 0; i < spikeLengths.Length; i++)
            spikeLengths[i] = Mathf.Lerp(2.2f, 4.8f, owner.Personality.Temperament) *
                Mathf.Lerp(0.8f, 1.15f, (float)random.NextDouble());
    }

    public override void Update()
    {
        base.Update();
        if (!desert.dead && desert.Injury.WingMean > 0f)
        {
            for (int sideIndex = 0; sideIndex < 2; sideIndex++)
                wings[sideIndex, 0] = 0.5f + (wings[sideIndex, 0] - 0.5f) * desert.Injury.WingAmplitude(sideIndex);
            lowerBody.vel.y += Mathf.Sin(desert.Injury.MotionTick * 0.11f) * desert.Injury.WingMean * 0.05f;
        }
        if (!desert.SandSpitWindingUp || desert.dead || desert.grabbedBy.Count == 0) return;

        int phase = desert.SandSpitWindupRemaining;
        for (int i = 0; i < 2; i++)
        {
            wings[i, 1] = wings[i, 0];
            wings[i, 0] = ((phase + i) & 1) == 0 ? 0f : 1f;
        }

        float side = (phase & 1) == 0 ? 1f : -1f;
        lowerBody.vel += new Vector2(side * 0.34f, 0.10f);
    }

    public override void InitiateSprites(RoomCamera.SpriteLeaser sLeaser, RoomCamera rCam)
    {
        base.InitiateSprites(sLeaser, rCam);

        FSprite[] vanilla = sLeaser.sprites;
        var expanded = new FSprite[VanillaSpriteCount + patterns.Length + spikeLengths.Length];
        Array.Copy(vanilla, expanded, Mathf.Min(VanillaSpriteCount, vanilla.Length));

        for (int i = 0; i < patterns.Length; i++)
        {
            expanded[PatternStart + i] = patterns[i].Shape == 0
                ? new FSprite("Circle20")
                : new FSprite("pixel");
        }

        for (int i = 0; i < spikeLengths.Length; i++)
            expanded[SpikeStart + i] = new TriangleMesh(
                "Futile_White",
                new[] { new TriangleMesh.Triangle(0, 1, 2) },
                false);

        sLeaser.sprites = expanded;
        ApplyPalette(sLeaser, rCam, rCam.currentPalette);
        AddToContainer(sLeaser, rCam, null);
    }

    public override void ApplyPalette(RoomCamera.SpriteLeaser sLeaser, RoomCamera rCam, RoomPalette palette)
    {
        base.ApplyPalette(sLeaser, rCam, palette);

        float darkness = Mathf.Clamp01(palette.darkness * 0.72f);
        bodyColor = Color.Lerp(desert.Personality.BaseColor, palette.blackColor, darkness);
        wingColor = Color.Lerp(desert.Personality.WingColor, palette.blackColor, darkness * 0.88f);
        darkMark = Color.Lerp(desert.Personality.SecondaryColor, palette.blackColor, darkness * 0.82f);
        warmMark = Color.Lerp(
            Color.Lerp(desert.Personality.BaseColor, new Color(0.60f, 0.30f, 0.18f), 0.42f + desert.Personality.Temperament * 0.28f),
            palette.blackColor,
            darkness * 0.75f);

        if (sLeaser.sprites.Length < VanillaSpriteCount) return;
        sLeaser.sprites[0].color = bodyColor;
        sLeaser.sprites[1].color = wingColor;
        sLeaser.sprites[2].color = wingColor;
        sLeaser.sprites[3].color = Color.Lerp(darkMark, bodyColor, 0.18f);

        for (int i = 0; i < patterns.Length; i++)
        {
            Color mark = Color.Lerp(darkMark, warmMark, patterns[i].Shade);
            sLeaser.sprites[PatternStart + i].color = Color.Lerp(
                mark,
                patterns[i].Wing ? wingColor : bodyColor,
                0.10f + (i % 3) * 0.06f);
        }

        for (int i = 0; i < spikeLengths.Length; i++)
            sLeaser.sprites[SpikeStart + i].color = Color.Lerp(darkMark, bodyColor, 0.18f);
    }

    public override void DrawSprites(RoomCamera.SpriteLeaser sLeaser, RoomCamera rCam, float timeStacker, Vector2 camPos)
    {
        base.DrawSprites(sLeaser, rCam, timeStacker, camPos);
        if (culled || desert.slatedForDeletetion || desert.room != rCam.room || sLeaser.sprites.Length < VanillaSpriteCount) return;

        float size = desert.Personality.Size;
        float bodySize = size * desert.Personality.BodyVisualScale;
        float emerge = desert.Emergence.Progress;
        float alpha = Mathf.SmoothStep(0f, 1f, emerge);

        sLeaser.sprites[0].scaleX = bodySize;
        sLeaser.sprites[0].scaleY = bodySize;
        sLeaser.sprites[1].scaleX *= size * desert.Personality.WingWidthScale;
        sLeaser.sprites[1].scaleY = size * desert.Personality.WingLengthScale;
        sLeaser.sprites[2].scaleX *= size * desert.Personality.WingWidthScale;
        sLeaser.sprites[2].scaleY = size * desert.Personality.WingLengthScale;
        sLeaser.sprites[3].scaleX = bodySize;
        sLeaser.sprites[3].scaleY = bodySize;

        ApplySignalDisplay(sLeaser, timeStacker);

        Vector2 head = Vector2.Lerp(desert.mainBodyChunk.lastPos, desert.mainBodyChunk.pos, timeStacker) - camPos;
        Vector2 tail = Vector2.Lerp(lowerBody.lastPos, lowerBody.pos, timeStacker) - camPos;
        Vector2 forward = Custom.DirVec(tail, head);
        if (forward.sqrMagnitude < 0.01f) forward = Vector2.up;
        Vector2 right = Custom.PerpendicularVector(forward);
        sLeaser.sprites[0].rotation += desert.Injury.BodyTilt;
        float bodyRotation = sLeaser.sprites[0].rotation;

        for (int i = 0; i < patterns.Length; i++)
        {
            PatternMark mark = patterns[i];
            FSprite sprite = sLeaser.sprites[PatternStart + i];
            bool visible;

            if (mark.Wing)
            {
                int wingIndex = mark.Side < 0 ? 1 : 2;
                FSprite baseWing = sLeaser.sprites[wingIndex];
                Vector2 wingDirection = Custom.DegToVec(baseWing.rotation);
                float distance = Mathf.Lerp(4.2f, 11.2f, mark.Along) * size * desert.Personality.WingLengthScale;
                sprite.x = head.x + wingDirection.x * distance;
                sprite.y = head.y + wingDirection.y * distance;
                sprite.rotation = baseWing.rotation + mark.Offset * 14f;

                if (mark.Shape == 0)
                {
                    sprite.scaleX = 0.10f * mark.Scale * size;
                    sprite.scaleY = 0.065f * mark.Scale * size;
                }
                else
                {
                    sprite.scaleX = Mathf.Lerp(1.5f, 3.2f, mark.Along) * mark.Scale * size;
                    sprite.scaleY = (0.75f + Mathf.Abs(mark.Offset) * 0.75f) * size;
                }
                visible = baseWing.isVisible;
            }
            else
            {
                Vector2 position = Vector2.Lerp(tail, head, mark.Along) + right * mark.Offset * 3f * size;
                sprite.x = position.x;
                sprite.y = position.y;
                sprite.rotation = bodyRotation + mark.Offset * 28f;

                if (mark.Shape == 0)
                {
                    sprite.scaleX = 0.09f * mark.Scale * size;
                    sprite.scaleY = 0.055f * mark.Scale * size;
                }
                else
                {
                    sprite.scaleX = (1.4f + mark.Along * 1.6f) * mark.Scale * size;
                    sprite.scaleY = (0.65f + Mathf.Abs(mark.Offset) * 0.8f) * size;
                }
                visible = sLeaser.sprites[0].isVisible;
            }

            sprite.isVisible = visible && alpha > 0.01f;
            sprite.alpha = alpha * Mathf.Clamp01(Mathf.Lerp(0.68f, 0.94f, desert.Personality.Contrast) * desert.Personality.MarkProminence);
        }

        for (int i = 0; i < spikeLengths.Length; i++)
        {
            float t = (i + 1f) / (spikeLengths.Length + 1f);
            float sign = i % 2 == 0 ? -1f : 1f;
            Vector2 root = Vector2.Lerp(head, tail, Mathf.Lerp(0.28f, 0.82f, t));
            root += right * sign * 2.3f * size;
            Vector2 spikeSide = (right * sign - forward * 0.35f).normalized;
            var mesh = (TriangleMesh)sLeaser.sprites[SpikeStart + i];
            mesh.MoveVertice(0, root + forward * 1.25f * size);
            mesh.MoveVertice(1, root - forward * 1.25f * size);
            mesh.MoveVertice(2, root + spikeSide * spikeLengths[i] * size * desert.Personality.MarkProminence);
            mesh.isVisible = sLeaser.sprites[0].isVisible && alpha > 0.01f;
            mesh.alpha = alpha;
        }

        for (int i = 0; i < VanillaSpriteCount; i++)
            sLeaser.sprites[i].alpha = alpha;
        sLeaser.sprites[1].alpha *= 1f - desert.DesertState.LeftWingInjury * 0.15f;
        sLeaser.sprites[2].alpha *= 1f - desert.DesertState.RightWingInjury * 0.15f;
    }

    private void ApplySignalDisplay(RoomCamera.SpriteLeaser sLeaser, float timeStacker)
    {
        if (!DB_SignalRuntime.TryGetDisplay(desert, out DB_SignalDisplayState display))
            return;

        float clock = (desert.room?.game?.clock ?? 0) + timeStacker;
        float intensity = display.Intensity;
        float pulse = 0.5f + 0.5f * Mathf.Sin(clock * 0.75f);
        float left = 0f;
        float right = 0f;
        float spread = 0f;

        switch (display.Kind)
        {
            case DB_SignalKind.AlarmFlutter:
                left = Mathf.Sin(clock * 1.75f) * (7f + 12f * intensity);
                right = -left;
                spread = 0.08f + pulse * 0.12f * intensity;
                break;
            case DB_SignalKind.DistressCall:
                left = Mathf.Sin(clock * 2.25f) * (11f + 15f * intensity);
                right = Mathf.Sin(clock * 2.25f + Mathf.PI * 0.72f) * (11f + 15f * intensity);
                spread = 0.14f + pulse * 0.18f * intensity;
                break;
            case DB_SignalKind.RallySignal:
                left = -8f * intensity;
                right = 8f * intensity;
                spread = 0.10f + pulse * 0.08f;
                break;
            case DB_SignalKind.RoostCall:
                left = -4f * intensity;
                right = 4f * intensity;
                spread = 0.05f;
                break;
            case DB_SignalKind.SafeSignal:
                left = -Mathf.Sin(clock * 0.18f) * 2.5f * intensity;
                right = -left;
                spread = pulse * 0.025f;
                break;
            case DB_SignalKind.HarassSignal:
                left = 3f * intensity;
                right = -3f * intensity;
                spread = pulse * 0.035f;
                break;
        }

        // Visual-only wing posture. No BodyChunk/lowerBody velocity is touched here.
        sLeaser.sprites[1].rotation += left;
        sLeaser.sprites[2].rotation += right;
        sLeaser.sprites[1].scaleX *= 1f + spread;
        sLeaser.sprites[2].scaleX *= 1f + spread;
    }
}
