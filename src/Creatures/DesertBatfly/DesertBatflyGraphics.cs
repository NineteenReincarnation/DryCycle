using RWCustom;
using UnityEngine;

namespace DryCycle.Creatures.DesertBatfly;

internal sealed class DesertBatflyGraphics : GraphicsModule
{
    private readonly DesertBatfly fly;
    private readonly Vector2[] patterns;
    private readonly float[] spikeLengths;
    private Vector2 axis = Vector2.up, lastAxis = Vector2.up;
    private float phase, lastPhase, spread = 1f, lastSpread = 1f;
    private Color bodyColor, wingColor, markingColor;
    private const int Body = 2, Abdomen = 3, Head = 4, PatternStart = 5;
    private int SpikeStart => PatternStart + patterns.Length;

    internal DesertBatflyGraphics(DesertBatfly owner) : base(owner, false)
    {
        fly = owner;
        var random = new System.Random(fly.Personality.PatternSeed);
        patterns = new Vector2[fly.Personality.PatternCount];
        for (int i = 0; i < patterns.Length; i++)
            patterns[i] = new Vector2(Mathf.Lerp(-1f, 1f, (float)random.NextDouble()), (float)random.NextDouble());
        random = new System.Random(fly.Personality.SpikeSeed);
        spikeLengths = new float[fly.Personality.SpikeCount];
        for (int i = 0; i < spikeLengths.Length; i++)
            spikeLengths[i] = Mathf.Lerp(2f, 6.5f, fly.Personality.Temperament) * Mathf.Lerp(0.8f, 1.15f, (float)random.NextDouble());
        phase = (fly.Personality.VisualSeed & 255) / 255f * Mathf.PI * 2f;
        cullRange = 180f;
    }

    public override void Update()
    {
        base.Update();
        lastAxis = axis;
        lastPhase = phase;
        lastSpread = spread;
        bool held = fly.grabbedBy.Count > 0;
        bool attached = fly.DesertAI.Mode == DesertBatflyAI.Activity.Attach;
        bool dive = fly.DesertAI.Mode is DesertBatflyAI.Activity.Dive or DesertBatflyAI.Activity.FakeDive;
        Vector2 wanted = dive ? fly.mainBodyChunk.vel.normalized : new Vector2(fly.mainBodyChunk.vel.x * 0.12f, 1f).normalized;
        if (fly.DesertAI.PullingUp) wanted = Vector2.up;
        if (attached && fly.DesertAI.Target != null)
            wanted = Custom.DirVec(fly.mainBodyChunk.pos, fly.DesertAI.Target.mainBodyChunk.pos);
        if (!fly.Consious)
        {
            axis = Custom.RotateAroundOrigo(axis, Mathf.Clamp(fly.mainBodyChunk.vel.x * 2f, -12f, 12f));
            spread = Mathf.Lerp(spread, 0.38f, 0.12f);
        }
        else
        {
            if (fly.Emergence.Active) wanted = fly.dir;
            axis = Vector2.Lerp(axis, wanted, dive ? 0.22f : 0.09f).normalized;
            phase += DesertBatflyTuning.WingRate * (held ? 1.9f : attached ? 0.35f : dive ? 0.75f : 1f);
            spread = Mathf.Lerp(spread, fly.DesertAI.PullingUp ? 1.25f : attached ? 0.3f : dive ? 0.62f : 1f, 0.16f);
        }
        if (fly.DesertAI.Mode == DesertBatflyAI.Activity.Roost) spread = Mathf.Lerp(spread, 0.18f, 0.25f);
    }

    public override void Reset()
    {
        base.Reset();
        lastAxis = axis;
        lastPhase = phase;
        lastSpread = spread;
    }

    public override void InitiateSprites(RoomCamera.SpriteLeaser leaser, RoomCamera camera)
    {
        leaser.sprites = new FSprite[SpikeStart + spikeLengths.Length];
        for (int side = 0; side < 2; side++)
            leaser.sprites[side] = new TriangleMesh("Futile_White", new[] {
                new TriangleMesh.Triangle(0, 1, 2), new TriangleMesh.Triangle(0, 2, 3),
                new TriangleMesh.Triangle(0, 3, 4), new TriangleMesh.Triangle(0, 4, 5),
                new TriangleMesh.Triangle(0, 5, 6) }, true);
        leaser.sprites[Body] = new FSprite("FlyBody");
        leaser.sprites[Abdomen] = new FSprite("Circle20");
        leaser.sprites[Head] = new FSprite("Circle20");
        for (int i = 0; i < patterns.Length; i++) leaser.sprites[PatternStart + i] = new FSprite("pixel");
        for (int i = 0; i < spikeLengths.Length; i++)
            leaser.sprites[SpikeStart + i] = new TriangleMesh("Futile_White", new[] { new TriangleMesh.Triangle(0, 1, 2) }, false);
        ApplyPalette(leaser, camera, camera.currentPalette);
        AddToContainer(leaser, camera, null);
    }

    public override void AddToContainer(RoomCamera.SpriteLeaser leaser, RoomCamera camera, FContainer container)
    {
        container ??= camera.ReturnFContainer("Midground");
        // Membranes behind body, markings and attached silhouette spikes in front.
        foreach (FSprite sprite in leaser.sprites)
        {
            sprite.RemoveFromContainer();
            container.AddChild(sprite);
        }
    }

    public override void ApplyPalette(RoomCamera.SpriteLeaser leaser, RoomCamera camera, RoomPalette palette)
    {
        float darkness = Mathf.Clamp01(palette.darkness * 0.65f);
        bodyColor = Color.Lerp(fly.Personality.BaseColor, palette.blackColor, darkness);
        wingColor = Color.Lerp(fly.Personality.WingColor, palette.blackColor, darkness);
        markingColor = Color.Lerp(fly.Personality.SecondaryColor, palette.blackColor, darkness);
        for (int i = Body; i < leaser.sprites.Length; i++)
            leaser.sprites[i].color = i >= PatternStart ? markingColor : bodyColor;
        leaser.sprites[Head].color = Color.Lerp(bodyColor, markingColor, 0.45f);
    }

    public override void DrawSprites(RoomCamera.SpriteLeaser leaser, RoomCamera camera, float timeStacker, Vector2 camPos)
    {
        if (fly.slatedForDeletetion || fly.room != camera.room) { leaser.CleanSpritesAndRemove(); return; }
        Vector2 pos = Vector2.Lerp(fly.mainBodyChunk.lastPos, fly.mainBodyChunk.pos, timeStacker) - camPos;
        Vector2 forward = Vector2.Lerp(lastAxis, axis, timeStacker).normalized;
        Vector2 right = new(forward.y, -forward.x);
        float rotation = Custom.VecToDeg(forward);
        float size = fly.Personality.Size;
        float emerge = fly.Emergence.Progress;
        float opening = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0.25f, 0.9f, emerge));
        float flap = Mathf.Sin(Mathf.Lerp(lastPhase, phase, timeStacker));
        float width = Mathf.Lerp(lastSpread, spread, timeStacker) * opening;
        float struggle = fly.grabbedBy.Count > 0 && !fly.dead ? Mathf.Sin(phase * 2.1f) * 1.2f : 0f;
        pos += right * struggle;
        foreach (FSprite sprite in leaser.sprites)
        {
            sprite.isVisible = emerge > 0.01f && !culled;
            sprite.alpha = Mathf.InverseLerp(0f, 0.28f, emerge);
        }
        for (int side = 0; side < 2; side++)
        {
            float sign = side == 0 ? -1f : 1f;
            var mesh = (TriangleMesh)leaser.sprites[side];
            Vector2 root = pos + right * sign * 3f * size;
            Vector2 lateral = right * sign * DesertBatflyTuning.WingLength * size * Mathf.Max(0.1f, width * (0.72f + 0.28f * flap));
            Vector2 lift = forward * (5f + flap * 6f) * size;
            mesh.MoveVertice(0, root);
            mesh.MoveVertice(1, root + lateral * 0.45f + lift + forward * 5f);
            mesh.MoveVertice(2, root + lateral + lift * 0.5f);
            mesh.MoveVertice(3, root + lateral * 0.73f - forward * 3f);
            mesh.MoveVertice(4, root + lateral * 0.57f - forward * 10f * size);
            mesh.MoveVertice(5, root + lateral * 0.30f - forward * 7f * size);
            mesh.MoveVertice(6, root - forward * 6f * size);
            for (int v = 0; v < mesh.verticeColors.Length; v++)
                mesh.verticeColors[v] = Color.Lerp(wingColor, markingColor, v is 2 or 4 or 6 ? 0.25f + fly.Personality.Temperament * 0.5f : 0.05f);
        }
        Place(leaser.sprites[Body], pos, rotation, 1.20f * size, 1.45f * size);
        Place(leaser.sprites[Abdomen], pos - forward * 6f * size, rotation, 0.32f * size, 0.62f * size);
        Place(leaser.sprites[Head], pos + forward * 4f * size, rotation, 0.29f * size, 0.25f * size);
        for (int i = 0; i < patterns.Length; i++)
        {
            Vector2 pattern = patterns[i];
            bool wing = i % 3 != 0;
            float sign = pattern.x < 0f ? -1f : 1f;
            Vector2 location = wing
                ? pos + right * sign * (7f + pattern.y * 8f) * width * (0.72f + 0.28f * flap) * size + forward * (flap * 2f - 1f)
                : pos + right * pattern.x * 2.5f * size - forward * pattern.y * 9f * size;
            Place(leaser.sprites[PatternStart + i], location, rotation + pattern.x * 30f,
                (wing ? 1.3f : 1.7f) * size, (1.2f + pattern.y * 1.8f) * size);
            if (wing) leaser.sprites[PatternStart + i].alpha *= opening;
        }
        for (int i = 0; i < spikeLengths.Length; i++)
        {
            float sign = i % 2 == 0 ? -1f : 1f;
            Vector2 root = pos + right * sign * 2.8f * size - forward * (i / 2 * 3f + 1f) * size;
            var mesh = (TriangleMesh)leaser.sprites[SpikeStart + i];
            mesh.MoveVertice(0, root + forward * 1.6f);
            mesh.MoveVertice(1, root - forward * 1.6f);
            mesh.MoveVertice(2, root + (right * sign - forward * 0.45f).normalized * spikeLengths[i] * size);
        }
    }

    private static void Place(FSprite sprite, Vector2 pos, float rotation, float x, float y)
    {
        sprite.x = pos.x;
        sprite.y = pos.y;
        sprite.rotation = rotation;
        sprite.scaleX = x;
        sprite.scaleY = y;
    }
}
