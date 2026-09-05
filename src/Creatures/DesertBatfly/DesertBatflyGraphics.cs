using System;
using RWCustom;
using UnityEngine;

namespace DryCycle.Creatures.DesertBatfly;

// Keep the complete vanilla Batfly animation/pose pipeline. Desert Batfly only
// changes scale, palette and adds lightweight markings/spikes on top of it.
internal sealed class DesertBatflyGraphics : FlyGraphics
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
    private readonly DesertBatfly desert;
    private readonly PatternMark[] patterns;
    private readonly float[] spikeLengths;
    private Color bodyColor, wingColor, darkMark, warmMark;
    private int PatternStart => VanillaSpriteCount;
    private int SpikeStart => PatternStart + patterns.Length;

    internal DesertBatflyGraphics(DesertBatfly owner) : base(owner)
    {
        desert = owner;

        var random = new System.Random(owner.Personality.PatternSeed);
        patterns = new PatternMark[owner.Personality.PatternCount];
        for (int i = 0; i < patterns.Length; i++)
        {
            // Roughly two thirds of the markings sit on the moving wing membranes;
            // the rest break up the body silhouette. Everything is deterministic.
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

    public override void InitiateSprites(RoomCamera.SpriteLeaser sLeaser, RoomCamera rCam)
    {
        // This creates the exact vanilla FlyBody / FlyWing / FlyWing / FlyEyes set
        // and keeps all vanilla grabbed/dead/flight animation behavior intact.
        base.InitiateSprites(sLeaser, rCam);

        FSprite[] vanilla = sLeaser.sprites;
        var expanded = new FSprite[VanillaSpriteCount + patterns.Length + spikeLengths.Length];
        Array.Copy(vanilla, expanded, Mathf.Min(VanillaSpriteCount, vanilla.Length));

        for (int i = 0; i < patterns.Length; i++)
        {
            // Mix dots and broken bars instead of painting the animal one flat color.
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
        // Let vanilla perform its normal setup first, then replace only the colors.
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
            // A little local mixing keeps adjacent markings from becoming a single
            // solid patch when many aggressive markings overlap.
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
        // Vanilla owns lowerBody, wing phases, grabbed struggle, death pose and the
        // base four sprite positions. We deliberately do not reimplement any of it.
        base.DrawSprites(sLeaser, rCam, timeStacker, camPos);
        if (culled || desert.slatedForDeletetion || desert.room != rCam.room || sLeaser.sprites.Length < VanillaSpriteCount) return;

        float size = desert.Personality.Size;
        float emerge = desert.Emergence.Progress;
        float alpha = Mathf.SmoothStep(0f, 1f, emerge);

        // Vanilla geometry, personality-controlled 1.00x-1.25x scaling.
        sLeaser.sprites[0].scaleX = size;
        sLeaser.sprites[0].scaleY = size;
        sLeaser.sprites[1].scaleX *= size;
        sLeaser.sprites[1].scaleY = size;
        sLeaser.sprites[2].scaleX *= size;
        sLeaser.sprites[2].scaleY = size;
        sLeaser.sprites[3].scaleX = size;
        sLeaser.sprites[3].scaleY = size;

        Vector2 head = Vector2.Lerp(desert.mainBodyChunk.lastPos, desert.mainBodyChunk.pos, timeStacker) - camPos;
        Vector2 tail = Vector2.Lerp(lowerBody.lastPos, lowerBody.pos, timeStacker) - camPos;
        Vector2 forward = Custom.DirVec(tail, head);
        if (forward.sqrMagnitude < 0.01f) forward = Vector2.up;
        Vector2 right = Custom.PerpendicularVector(forward);
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
                float distance = Mathf.Lerp(4.2f, 11.2f, mark.Along) * size;
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
            sprite.alpha = alpha * Mathf.Lerp(0.68f, 0.94f, desert.Personality.Contrast);
        }

        for (int i = 0; i < spikeLengths.Length; i++)
        {
            float t = (i + 1f) / (spikeLengths.Length + 1f);
            float sign = i % 2 == 0 ? -1f : 1f;
            Vector2 root = Vector2.Lerp(head, tail, Mathf.Lerp(0.28f, 0.82f, t));
            root += right * sign * 2.3f * size;
            Vector2 side = (right * sign - forward * 0.35f).normalized;
            var mesh = (TriangleMesh)sLeaser.sprites[SpikeStart + i];
            mesh.MoveVertice(0, root + forward * 1.25f * size);
            mesh.MoveVertice(1, root - forward * 1.25f * size);
            mesh.MoveVertice(2, root + side * spikeLengths[i] * size);
            mesh.isVisible = sLeaser.sprites[0].isVisible && alpha > 0.01f;
            mesh.alpha = alpha;
        }

        // Curve emergence fades the entire vanilla silhouette in while its body is
        // physically moved through the surface. Normal hive emergence stays at 1.
        for (int i = 0; i < VanillaSpriteCount; i++)
            sLeaser.sprites[i].alpha = alpha;
    }
}
