using System;
using RWCustom;
using UnityEngine;

namespace DryCycle.TerrainExt.QuicksandZone;

internal sealed class QuicksandZone : UpdatableAndDeletable, IDrawable
{
    private const int SampleCount = 64;
    private const int FlowStripeCount = 14;
    private const float MaxStainHeight = 60f;

    private static readonly Color FallbackSurfaceColor = new(0.79f, 0.61f, 0.32f);
    private static readonly Color FallbackDeepColor = new(0.43f, 0.28f, 0.15f);
    private static readonly Color FallbackLightFlowColor = new(0.92f, 0.76f, 0.43f);
    private static readonly Color FallbackDarkFlowColor = new(0.50f, 0.32f, 0.17f);

    private readonly PlacedObject _placedObject;
    private readonly Vector2[] _surface = new Vector2[SampleCount];
    private readonly Vector2[] _bottom = new Vector2[SampleCount];
    private readonly float[] _wave = new float[SampleCount];
    private readonly float[] _lastWave = new float[SampleCount];
    private readonly float[] _waveVelocity = new float[SampleCount];
    private readonly float[] _flowPhase = new float[FlowStripeCount];
    private readonly float[] _flowDepth = new float[FlowStripeCount];
    private readonly float[] _flowLength = new float[FlowStripeCount];
    private readonly float[] _flowSpeedMultiplier = new float[FlowStripeCount];

    private float _flowTime;

    internal PlacedObject PlacedObject => _placedObject;
    private QuicksandZoneData Data => _placedObject?.data as QuicksandZoneData;

    internal QuicksandZone(PlacedObject placedObject)
    {
        _placedObject = placedObject;

        for (int i = 0; i < FlowStripeCount; i++)
        {
            float hash = Mathf.Repeat((i + 1) * 0.6180339887f, 1f);
            _flowPhase[i] = hash;
            _flowDepth[i] = Mathf.Lerp(0.06f, 0.48f, Mathf.Repeat(hash * 2.37f, 1f));
            _flowLength[i] = Mathf.Lerp(13f, 34f, Mathf.Repeat(hash * 5.13f, 1f));
            _flowSpeedMultiplier[i] = Mathf.Lerp(0.55f, 1.18f, Mathf.Repeat(hash * 7.91f, 1f));
        }
    }

    public override void Update(bool eu)
    {
        base.Update(eu);

        if (room == null || Data == null || _placedObject == null || !_placedObject.active)
        {
            return;
        }

        QuicksandSurface.SampleZone(_placedObject, Data, _surface, _bottom);
        UpdateSurfaceWave();
        ApplyQuicksandPhysics();
        _flowTime += Mathf.Max(0.12f, Mathf.Abs(Data.FlowSpeed));
    }

    internal bool IntersectsPlayerForLayer(Player player)
    {
        if (player == null || player.room != room || player.bodyChunks == null || Data == null)
        {
            return false;
        }

        QuicksandSurface.SampleZone(_placedObject, Data, _surface, _bottom);

        for (int i = 0; i < player.bodyChunks.Length; i++)
        {
            BodyChunk chunk = player.bodyChunks[i];
            if (chunk == null ||
                !QuicksandSurface.TryGetContact(
                    chunk.pos,
                    chunk.rad,
                    _surface,
                    _bottom,
                    out QuicksandSurface.Contact contact))
            {
                continue;
            }

            if (contact.SignedDepth > -chunk.rad * 0.35f &&
                contact.SignedDepth < contact.DepthLength + chunk.rad * 0.25f)
            {
                return true;
            }
        }

        return false;
    }

    private void UpdateSurfaceWave()
    {
        Array.Copy(_wave, _lastWave, _wave.Length);

        for (int i = 0; i < _wave.Length; i++)
        {
            float left = i > 0 ? _lastWave[i - 1] : _lastWave[i];
            float right = i < _wave.Length - 1 ? _lastWave[i + 1] : _lastWave[i];
            float laplacian = left + right - _lastWave[i] * 2f;

            _waveVelocity[i] += laplacian * 0.055f;
            _waveVelocity[i] *= 0.90f;
            _wave[i] = (_lastWave[i] + _waveVelocity[i]) * 0.994f;
            _wave[i] = Mathf.Clamp(_wave[i], -7f, 5f);
        }
    }

    private void ApplyQuicksandPhysics()
    {
        if (room.physicalObjects == null)
        {
            return;
        }

        for (int layer = 0; layer < room.physicalObjects.Length; layer++)
        {
            var objects = room.physicalObjects[layer];
            if (objects == null)
            {
                continue;
            }

            for (int objectIndex = 0; objectIndex < objects.Count; objectIndex++)
            {
                PhysicalObject physicalObject = objects[objectIndex];
                if (physicalObject?.bodyChunks == null)
                {
                    continue;
                }

                for (int chunkIndex = 0; chunkIndex < physicalObject.bodyChunks.Length; chunkIndex++)
                {
                    BodyChunk chunk = physicalObject.bodyChunks[chunkIndex];
                    if (chunk == null ||
                        !QuicksandSurface.TryGetContact(
                            chunk.pos,
                            chunk.rad,
                            _surface,
                            _bottom,
                            out QuicksandSurface.Contact contact))
                    {
                        continue;
                    }

                    float surfaceImmersion = Mathf.Clamp01(
                        (contact.SignedDepth + chunk.rad) /
                        Mathf.Max(1f, chunk.rad * 2f));
                    float deepness = Mathf.Clamp01(contact.SignedDepth / contact.DepthLength);
                    float viscosity = Mathf.Clamp01(Mathf.Max(
                        surfaceImmersion,
                        Mathf.Pow(deepness, 0.8f)));

                    float drag = Mathf.Lerp(
                        0.985f,
                        Mathf.Lerp(0.72f, 0.84f, 1f - Data.HorizontalDrag),
                        Mathf.Pow(viscosity, 1.25f));
                    chunk.vel *= drag;

                    float middleBias = 1f - Mathf.Abs(deepness - 0.52f) / 0.52f;
                    middleBias = Mathf.Clamp01(middleBias);
                    float sink = Data.SinkStrength *
                                 Mathf.Lerp(0.22f, 1f, middleBias) *
                                 Mathf.Lerp(0.25f, 1f, viscosity);
                    chunk.vel += contact.Inward * sink;

                    if (deepness > 0.72f)
                    {
                        float support = Mathf.InverseLerp(0.72f, 1f, deepness);
                        chunk.vel -= contact.Inward *
                                     Data.SinkStrength *
                                     support *
                                     1.85f;
                    }

                    float signedFlow = Data.FlowSpeed;
                    if (Mathf.Abs(signedFlow) > 0.001f)
                    {
                        chunk.vel += contact.Tangent *
                                     Mathf.Sign(signedFlow) *
                                     Data.FlowStrength *
                                     Mathf.Abs(signedFlow) *
                                     viscosity *
                                     0.055f;
                    }

                    if (physicalObject is Player player &&
                        player.input != null &&
                        player.input.Length > 0 &&
                        player.input[0].jmp &&
                        surfaceImmersion > 0.25f)
                    {
                        chunk.vel -= contact.Inward * 0.075f * surfaceImmersion;
                    }

                    DisturbSurface(contact.U, chunk, viscosity);
                }
            }
        }
    }

    private void DisturbSurface(float u, BodyChunk chunk, float viscosity)
    {
        int center = Mathf.Clamp(
            Mathf.RoundToInt(u * (_wave.Length - 1)),
            0,
            _wave.Length - 1);
        float impact = Mathf.Clamp(
            chunk.vel.magnitude * 0.06f + viscosity * 0.18f,
            0.025f,
            0.55f);

        for (int offset = -2; offset <= 2; offset++)
        {
            int index = center + offset;
            if (index < 0 || index >= _wave.Length)
            {
                continue;
            }

            float falloff = 1f - Mathf.Abs(offset) / 3f;
            _waveVelocity[index] -= impact * falloff;
        }
    }

    public void InitiateSprites(RoomCamera.SpriteLeaser sLeaser, RoomCamera rCam)
    {
        TriangleMesh.Triangle[] triangles = BuildStripTriangles();

        sLeaser.sprites = new FSprite[2 + FlowStripeCount];
        sLeaser.sprites[0] = new TriangleMesh("Futile_White", triangles, customColor: true)
        {
            shader = rCam.game.rainWorld.Shaders["Basic"]
        };
        sLeaser.sprites[1] = new TriangleMesh("Futile_White", triangles, customColor: true)
        {
            shader = rCam.game.rainWorld.Shaders["Basic"]
        };

        for (int i = 0; i < FlowStripeCount; i++)
        {
            sLeaser.sprites[i + 2] = new FSprite("pixel")
            {
                anchorY = 0.5f,
                scaleX = i % 3 == 0 ? 1.8f : 1.1f,
                alpha = i % 4 == 0 ? 0.34f : 0.50f
            };
        }

        AddToContainer(sLeaser, rCam, null);
    }

    public void DrawSprites(
        RoomCamera.SpriteLeaser sLeaser,
        RoomCamera rCam,
        float timeStacker,
        Vector2 camPos)
    {
        if (slatedForDeletetion || room != rCam.room || Data == null)
        {
            sLeaser.CleanSpritesAndRemove();
            return;
        }

        QuicksandSurface.SampleZone(_placedObject, Data, _surface, _bottom);
        ApplyTerrainShaderSettings(rCam);

        TriangleMesh fill = sLeaser.sprites[0] as TriangleMesh;
        TriangleMesh stain = sLeaser.sprites[1] as TriangleMesh;
        bool useTerrainPalette = CanUseTerrainShader(rCam);

        fill.shader = rCam.game.rainWorld.Shaders[useTerrainPalette ? "SlopedTerrainSurface" : "Basic"];
        stain.shader = rCam.game.rainWorld.Shaders[useTerrainPalette ? "SlopedTerrainStain" : "Basic"];

        float terrainWaves = room.roomSettings.TerrainWaves;
        float terrainDepth = room.roomSettings.TerrainDepth;
        float bottomDepth = Mathf.Clamp(terrainDepth + 30f, terrainDepth, 35f);
        float stainHeight = room.roomSettings.TerrainStainHeight * MaxStainHeight;
        bool showStain = room.roomSettings.TerrainStainAmount > 0.0001f && stainHeight > 0.01f;
        stain.isVisible = showStain;

        Color surfaceFallback = GetSurfaceFallback(rCam);
        Color deepFallback = GetDeepFallback(rCam);

        Vector2[] animatedSurface = new Vector2[SampleCount];
        for (int i = 0; i < SampleCount; i++)
        {
            float u = (float)i / (SampleCount - 1);
            Vector2 surface = _surface[i];
            Vector2 bottom = _bottom[i];
            Vector2 inward = SafeNormal(bottom - surface, Vector2.down);
            Vector2 outward = -inward;

            float movingWaveStrength = 0.60f + terrainWaves * 2.4f;
            float movingWave =
                Mathf.Sin(u * Mathf.PI * 6f - _flowTime * 0.050f * Data.FlowSpeed) * movingWaveStrength +
                Mathf.Sin(u * Mathf.PI * 14f - _flowTime * 0.083f * Data.FlowSpeed + 1.7f) *
                (0.24f + terrainWaves * 0.70f);
            float localWave = Mathf.Lerp(_lastWave[i], _wave[i], timeStacker);
            animatedSurface[i] = surface + outward * (movingWave + localWave);
        }

        for (int i = 0; i < SampleCount; i++)
        {
            Vector2 surface = animatedSurface[i];
            Vector2 bottom = _bottom[i];
            Vector2 surfaceTangent = GetSampleTangent(animatedSurface, i);
            Vector2 bottomTangent = GetSampleTangent(_bottom, i);
            Vector3 depthVector = new(
                bottom.x - surface.x,
                bottom.y - surface.y,
                bottomDepth - terrainDepth);
            Vector3 frontTangent = new(surfaceTangent.x, surfaceTangent.y, 0f);
            Vector3 backTangent = new(bottomTangent.x, bottomTangent.y, 0f);
            Vector3 frontNormal3 = Vector3.Cross(depthVector, frontTangent).normalized;
            Vector3 backNormal3 = Vector3.Cross(depthVector, backTangent).normalized;
            Vector2 frontNormal = new(frontNormal3.x, frontNormal3.y);
            Vector2 backNormal = new(backNormal3.x, backNormal3.y);

            int topIndex = i * 2;
            int bottomIndex = topIndex + 1;
            fill.MoveVertice(topIndex, surface - camPos);
            fill.MoveVertice(bottomIndex, bottom - camPos);

            if (useTerrainPalette)
            {
                fill.verticeColors[topIndex] = new Color(
                    0f,
                    surfaceTangent.x,
                    surfaceTangent.y,
                    terrainDepth / 30f);
                fill.verticeColors[bottomIndex] = new Color(
                    1f,
                    bottomTangent.x,
                    bottomTangent.y,
                    bottomDepth / 30f);
                fill.UVvertices[topIndex] = frontNormal;
                fill.UVvertices[bottomIndex] = backNormal;
            }
            else
            {
                fill.verticeColors[topIndex] = surfaceFallback;
                fill.verticeColors[bottomIndex] = deepFallback;
                fill.UVvertices[topIndex] = Vector2.zero;
                fill.UVvertices[bottomIndex] = Vector2.zero;
            }

            if (showStain)
            {
                Vector2 stainTop = surface + Vector2.up * stainHeight;
                stain.MoveVertice(topIndex, surface - camPos);
                stain.MoveVertice(bottomIndex, stainTop - camPos);

                if (useTerrainPalette)
                {
                    stain.verticeColors[topIndex] = new Color(0f, 0f, 0f, terrainDepth / 30f);
                    stain.verticeColors[bottomIndex] = new Color(0f, 0f, 0f, terrainDepth / 30f);
                    stain.UVvertices[topIndex] = Vector2.zero;
                    stain.UVvertices[bottomIndex] = Vector2.one;
                }
                else
                {
                    Color stainColor = Color.Lerp(surfaceFallback, rCam.currentPalette.blackColor, 0.18f);
                    stainColor.a = room.roomSettings.TerrainStainAmount * 0.40f;
                    Color topColor = stainColor;
                    topColor.a = 0f;
                    stain.verticeColors[topIndex] = stainColor;
                    stain.verticeColors[bottomIndex] = topColor;
                }
            }
        }

        DrawFlowStripes(sLeaser, timeStacker, camPos, rCam);
    }

    private void DrawFlowStripes(
        RoomCamera.SpriteLeaser sLeaser,
        float timeStacker,
        Vector2 camPos,
        RoomCamera rCam)
    {
        float signedSpeed = Data.FlowSpeed;
        float direction = signedSpeed >= 0f ? 1f : -1f;
        float speed = Mathf.Max(0.08f, Mathf.Abs(signedSpeed));
        float grain = room.roomSettings.TerrainGrain;
        Color lightFlow = GetLightFlowColor(rCam);
        Color darkFlow = GetDarkFlowColor(rCam);

        for (int i = 0; i < FlowStripeCount; i++)
        {
            bool counterFlow = i % 5 == 4;
            float stripeDirection = counterFlow ? -direction : direction;
            float phase = Mathf.Repeat(
                _flowPhase[i] +
                stripeDirection *
                (_flowTime + timeStacker) *
                0.0019f *
                speed *
                _flowSpeedMultiplier[i],
                1f);

            Vector2 surface = _placedObject.pos +
                              QuicksandSurface.EvaluateByApproximateLength(Data.SurfaceSpline, phase);
            Vector2 bottom = _placedObject.pos +
                             QuicksandSurface.EvaluateByApproximateLength(Data.BottomSpline, phase);
            Vector2 center = Vector2.Lerp(surface, bottom, _flowDepth[i]);

            float aheadU = Mathf.Clamp01(phase + 0.008f * stripeDirection);
            Vector2 aheadSurface = _placedObject.pos +
                                   QuicksandSurface.EvaluateByApproximateLength(Data.SurfaceSpline, aheadU);
            Vector2 aheadBottom = _placedObject.pos +
                                  QuicksandSurface.EvaluateByApproximateLength(Data.BottomSpline, aheadU);
            Vector2 ahead = Vector2.Lerp(aheadSurface, aheadBottom, _flowDepth[i]);
            Vector2 tangent = SafeNormal(ahead - center, Vector2.right * stripeDirection);

            FSprite stripe = sLeaser.sprites[i + 2];
            stripe.SetPosition(center - camPos);
            stripe.scaleY = _flowLength[i] *
                            Mathf.Lerp(0.75f, 1.15f, speed / 2f) *
                            Mathf.Lerp(0.85f, 1.25f, grain);
            stripe.scaleX = (i % 3 == 0 ? 1.8f : 1.1f) * Mathf.Lerp(0.8f, 1.35f, grain);
            stripe.rotation = Custom.AimFromOneVectorToAnother(center, center + tangent);
            stripe.color = i % 4 == 0 ? darkFlow : lightFlow;
            stripe.alpha = (i % 4 == 0 ? 0.24f : 0.42f) *
                           Mathf.Lerp(1f, 0.66f, _flowDepth[i]) *
                           Mathf.Lerp(0.65f, 1f, grain);
            stripe.isVisible = true;
        }
    }

    public void ApplyPalette(
        RoomCamera.SpriteLeaser sLeaser,
        RoomCamera rCam,
        RoomPalette palette)
    {
        // TerrainPalette is maintained separately from RoomPalette by RoomCamera.
        // Color selection is refreshed in DrawSprites so per-screen Terrain Fade
        // changes immediately affect the quicksand as well.
    }

    public void AddToContainer(
        RoomCamera.SpriteLeaser sLeaser,
        RoomCamera rCam,
        FContainer newContainer)
    {
        sLeaser.RemoveAllSpritesFromContainer();

        FContainer sand = newContainer ?? rCam.ReturnFContainer("Sand");
        FContainer foreground = rCam.ReturnFContainer("Foreground");

        sand.AddChild(sLeaser.sprites[0]);
        for (int i = 0; i < FlowStripeCount; i++)
        {
            sand.AddChild(sLeaser.sprites[i + 2]);
        }

        // Match Watcher's TerrainCurve: stain is rendered in the foreground,
        // while the actual terrain surface occupies the Sand layer.
        foreground.AddChild(sLeaser.sprites[1]);
    }

    private static TriangleMesh.Triangle[] BuildStripTriangles()
    {
        TriangleMesh.Triangle[] triangles = new TriangleMesh.Triangle[(SampleCount - 1) * 2];
        for (int i = 0; i < SampleCount - 1; i++)
        {
            int top = i * 2;
            int bottom = top + 1;
            int nextTop = top + 2;
            int nextBottom = top + 3;
            triangles[i * 2] = new TriangleMesh.Triangle(top, bottom, nextTop);
            triangles[i * 2 + 1] = new TriangleMesh.Triangle(nextTop, bottom, nextBottom);
        }

        return triangles;
    }

    private void ApplyTerrainShaderSettings(RoomCamera rCam)
    {
        Shader.SetGlobalFloat("_terrainStainFactor", room.roomSettings.TerrainStainAmount);
        Shader.SetGlobalFloat("_terrainStainBrightness", room.roomSettings.TerrainStainBrightness);
        Shader.SetGlobalVector(
            "_terrainParams",
            new Vector4(
                room.roomSettings.TerrainLight,
                room.roomSettings.TerrainWaves,
                room.roomSettings.TerrainEdgeRadius,
                room.roomSettings.TerrainGrain));

        float gooHeight = room.roomSettings.TerrainGooHeight;
        gooHeight = gooHeight == 0f
            ? float.NegativeInfinity
            : (gooHeight == 1f
                ? float.PositiveInfinity
                : Mathf.Pow(gooHeight, 2f) * 1500f);

        Shader.SetGlobalVector(
            "_terrainParams2",
            new Vector4(
                gooHeight,
                room.roomSettings.TerrainSkyFade,
                0f,
                0f));
    }

    private static bool CanUseTerrainShader(RoomCamera rCam)
    {
        return ModManager.Watcher &&
               rCam?.terrainPalette != null &&
               rCam.game?.rainWorld?.Shaders != null &&
               rCam.game.rainWorld.Shaders.ContainsKey("SlopedTerrainSurface") &&
               rCam.game.rainWorld.Shaders.ContainsKey("SlopedTerrainStain");
    }

    private static Vector2 GetSampleTangent(Vector2[] points, int index)
    {
        if (points == null || points.Length < 2)
        {
            return Vector2.right;
        }

        int previous = Mathf.Max(0, index - 1);
        int next = Mathf.Min(points.Length - 1, index + 1);
        return SafeNormal(points[next] - points[previous], Vector2.right);
    }

    private static Color GetSurfaceFallback(RoomCamera rCam)
    {
        if (rCam?.terrainPalette != null)
        {
            return Color.Lerp(rCam.terrainPalette.LightDustColor, rCam.terrainPalette.DarkDustColor, 0.28f);
        }

        return Color.Lerp(FallbackSurfaceColor, rCam.currentPalette.blackColor, 0.10f);
    }

    private static Color GetDeepFallback(RoomCamera rCam)
    {
        if (rCam?.terrainPalette != null)
        {
            return Color.Lerp(rCam.terrainPalette.DarkDustColor, rCam.currentPalette.blackColor, 0.18f);
        }

        return Color.Lerp(FallbackDeepColor, rCam.currentPalette.blackColor, 0.18f);
    }

    private static Color GetLightFlowColor(RoomCamera rCam)
    {
        if (rCam?.terrainPalette != null)
        {
            return Color.Lerp(rCam.terrainPalette.LightDustColor, rCam.terrainPalette.LightTint, 0.22f);
        }

        return Color.Lerp(FallbackLightFlowColor, rCam.currentPalette.blackColor, 0.08f);
    }

    private static Color GetDarkFlowColor(RoomCamera rCam)
    {
        if (rCam?.terrainPalette != null)
        {
            return Color.Lerp(rCam.terrainPalette.DarkDustColor, rCam.currentPalette.blackColor, 0.12f);
        }

        return Color.Lerp(FallbackDarkFlowColor, rCam.currentPalette.blackColor, 0.16f);
    }

    private static Vector2 SafeNormal(Vector2 value, Vector2 fallback)
    {
        return value.sqrMagnitude > 0.0001f ? value.normalized : fallback;
    }
}
