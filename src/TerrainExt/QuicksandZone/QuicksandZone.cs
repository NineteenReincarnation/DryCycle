using System;
using RWCustom;
using UnityEngine;

namespace DryCycle.TerrainExt.QuicksandZone;

internal sealed class QuicksandZone : UpdatableAndDeletable, IDrawable
{
    private const int SampleCount = 64;
    private const int FlowStripeCount = 14;
    private const float MaxStainHeight = 60f;
    private const float TerrainBackOffset = 50f;
    private const float TerrainMaxDepth = 35f;
    private const float TerrainJoinDistance = 30f;
    private const float TerrainJoinFraction = 0.13f;

    private static readonly Color FallbackSurfaceColor = new(0.79f, 0.61f, 0.32f);
    private static readonly Color FallbackDeepColor = new(0.43f, 0.28f, 0.15f);
    private static readonly Color FallbackLightFlowColor = new(0.92f, 0.76f, 0.43f);
    private static readonly Color FallbackDarkFlowColor = new(0.50f, 0.32f, 0.17f);

    private static readonly Vector4[] EmptyTerrainLightColors = new Vector4[16];
    private static readonly Vector4[] EmptyTerrainLightParams = new Vector4[16];

    private readonly PlacedObject _placedObject;
    private readonly Vector2[] _surface = new Vector2[SampleCount];
    private readonly Vector2[] _bottom = new Vector2[SampleCount];
    private readonly Vector2[] _animatedFront = new Vector2[SampleCount];
    private readonly Vector2[] _visualBack = new Vector2[SampleCount];
    private readonly Vector2[] _screenFront = new Vector2[SampleCount];
    private readonly Vector2[] _screenBack = new Vector2[SampleCount];
    private readonly Vector2[] _screenBottom = new Vector2[SampleCount];
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

            if (contact.SignedDepth > -chunk.rad * 1.05f &&
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
                if (physicalObject?.bodyChunks == null || physicalObject is Player)
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
        bool terrain = CanUseTerrainShader(rCam);

        sLeaser.sprites = new FSprite[3 + FlowStripeCount];
        sLeaser.sprites[0] = new TriangleMesh("Futile_White", triangles, customColor: true)
        {
            shader = rCam.game.rainWorld.Shaders[terrain ? "SlopedTerrainStain" : "Basic"]
        };
        sLeaser.sprites[1] = new TriangleMesh("Futile_White", triangles, customColor: true)
        {
            shader = rCam.game.rainWorld.Shaders[terrain ? "SlopedTerrainSurface" : "Basic"]
        };
        sLeaser.sprites[2] = new TriangleMesh("Futile_White", triangles, customColor: true)
        {
            shader = rCam.game.rainWorld.Shaders[terrain ? "SlopedTerrainSurface" : "Basic"]
        };

        for (int i = 0; i < FlowStripeCount; i++)
        {
            sLeaser.sprites[i + 3] = new FSprite("pixel")
            {
                anchorY = 0.5f,
                scaleX = i % 3 == 0 ? 1.8f : 1.1f,
                alpha = i % 4 == 0 ? 0.28f : 0.42f
            };
        }

        sLeaser.containers = new FContainer[1]
        {
            new FContainer()
        };

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
        ResetTerrainLightInfo();

        TriangleMesh stain = sLeaser.sprites[0] as TriangleMesh;
        TriangleMesh surfaceMesh = sLeaser.sprites[1] as TriangleMesh;
        TriangleMesh underfill = sLeaser.sprites[2] as TriangleMesh;
        bool useTerrainPalette = CanUseTerrainShader(rCam);

        stain.shader = rCam.game.rainWorld.Shaders[useTerrainPalette ? "SlopedTerrainStain" : "Basic"];
        surfaceMesh.shader = rCam.game.rainWorld.Shaders[useTerrainPalette ? "SlopedTerrainSurface" : "Basic"];
        underfill.shader = rCam.game.rainWorld.Shaders[useTerrainPalette ? "SlopedTerrainSurface" : "Basic"];

        float minDepth = room.roomSettings.TerrainDepth;
        float maxDepth = TerrainMaxDepth;
        float terrainWaves = room.roomSettings.TerrainWaves;

        BuildVisualCurves(timeStacker, camPos, terrainWaves);
        DrawTerrainSurface(surfaceMesh, underfill, useTerrainPalette, rCam, minDepth, maxDepth);
        DrawTerrainStain(stain, useTerrainPalette, rCam, minDepth, maxDepth);
        DrawFlowStripes(sLeaser, timeStacker, camPos, rCam, minDepth, maxDepth);
    }

    private void BuildVisualCurves(float timeStacker, Vector2 camPos, float terrainWaves)
    {
        for (int i = 0; i < SampleCount; i++)
        {
            float u = (float)i / (SampleCount - 1);

            float edgeMotion = Mathf.SmoothStep(
                0f,
                1f,
                Mathf.Clamp01(Mathf.Min(u, 1f - u) / TerrainJoinFraction));

            float movingWaveStrength = 0.22f + terrainWaves * 0.55f;
            float movingWave =
                Mathf.Sin(u * Mathf.PI * 6f - _flowTime * 0.050f * Data.FlowSpeed) * movingWaveStrength +
                Mathf.Sin(u * Mathf.PI * 14f - _flowTime * 0.083f * Data.FlowSpeed + 1.7f) *
                (0.08f + terrainWaves * 0.18f);
            float localWave = Mathf.Lerp(_lastWave[i], _wave[i], timeStacker);

            Vector2 front = _surface[i] +
                            Vector2.up * (movingWave + localWave) * edgeMotion;
            Vector2 back = front + Vector2.up * TerrainBackOffset;

            float joinStrength = GetTerrainJoinStrength(u);
            if (joinStrength > 0f &&
                TrySampleAdjacentTerrain(_surface[i], out Vector2 terrainFront, out Vector2 terrainBack))
            {
                front = Vector2.Lerp(front, terrainFront, joinStrength);
                back = Vector2.Lerp(back, terrainBack, joinStrength);
            }

            _animatedFront[i] = front;
            _visualBack[i] = back;
            _screenFront[i] = front - camPos;
            _screenBack[i] = back - camPos;
            _screenBottom[i] = _bottom[i] - camPos;
        }
    }

    private static float GetTerrainJoinStrength(float u)
    {
        float edgeDistance = Mathf.Min(u, 1f - u);
        if (edgeDistance >= TerrainJoinFraction)
        {
            return 0f;
        }

        return 1f - Mathf.SmoothStep(0f, 1f, edgeDistance / TerrainJoinFraction);
    }

    private bool TrySampleAdjacentTerrain(
        Vector2 reference,
        out Vector2 front,
        out Vector2 back)
    {
        front = default;
        back = default;

        if (!ModManager.Watcher || room?.terrain?.terrainList == null)
        {
            return false;
        }

        float bestDistance = TerrainJoinDistance;
        bool found = false;

        for (int i = 0; i < room.terrain.terrainList.Count; i++)
        {
            if (room.terrain.terrainList[i] is not TerrainCurve curve ||
                curve.frontPoints == null ||
                curve.backPoints == null ||
                curve.frontPoints.Length < 2 ||
                curve.backPoints.Length < 2 ||
                Mathf.Abs(curve.segmentWidth) < 0.0001f)
            {
                continue;
            }

            if (reference.x < curve.startX - TerrainJoinDistance ||
                reference.x > curve.endX + TerrainJoinDistance)
            {
                continue;
            }

            float raw = (reference.x - curve.startX) / curve.segmentWidth;
            int segment = Mathf.Clamp(
                Mathf.FloorToInt(raw),
                0,
                Mathf.Min(curve.frontPoints.Length, curve.backPoints.Length) - 2);
            float t = Mathf.Clamp01(raw - segment);
            Vector2 candidateFront = Vector2.Lerp(
                curve.frontPoints[segment],
                curve.frontPoints[segment + 1],
                t);
            Vector2 candidateBack = Vector2.Lerp(
                curve.backPoints[segment],
                curve.backPoints[segment + 1],
                t);
            float distance = Vector2.Distance(reference, candidateFront);

            if (distance < bestDistance)
            {
                bestDistance = distance;
                front = candidateFront;
                back = candidateBack;
                found = true;
            }
        }

        return found;
    }

    private void DrawTerrainSurface(
        TriangleMesh surfaceMesh,
        TriangleMesh underfill,
        bool useTerrainPalette,
        RoomCamera rCam,
        float minDepth,
        float maxDepth)
    {
        surfaceMesh.isVisible = true;
        underfill.isVisible = true;

        Color surfaceFallback = GetSurfaceFallback(rCam);
        Color deepFallback = GetDeepFallback(rCam);

        for (int i = 0; i < SampleCount; i++)
        {
            Vector2 frontDelta = GetSampleDelta(_screenFront, i);
            Vector2 backDelta = GetSampleDelta(_screenBack, i);
            Vector3 depthVector = new(
                _screenBack[i].x - _screenFront[i].x,
                _screenBack[i].y - _screenFront[i].y,
                maxDepth - minDepth);
            Vector3 frontTangent = new(frontDelta.x, frontDelta.y, 0f);
            Vector3 backTangent = new(backDelta.x, backDelta.y, 0f);
            Vector3 frontNormal3 = Vector3.Cross(depthVector, frontTangent).normalized;
            Vector3 backNormal3 = Vector3.Cross(depthVector, backTangent).normalized;
            Vector2 frontNormal = new(frontNormal3.x, frontNormal3.y);
            Vector2 backNormal = new(backNormal3.x, backNormal3.y);

            int nearIndex = i * 2;
            int farIndex = nearIndex + 1;

            surfaceMesh.MoveVertice(nearIndex, _screenFront[i]);
            surfaceMesh.MoveVertice(farIndex, _screenBack[i]);
            underfill.MoveVertice(nearIndex, _screenBottom[i]);
            underfill.MoveVertice(farIndex, _screenFront[i]);

            if (useTerrainPalette)
            {
                surfaceMesh.verticeColors[nearIndex] = new Color(
                    0f,
                    frontDelta.x,
                    frontDelta.y,
                    minDepth / 30f);
                surfaceMesh.verticeColors[farIndex] = new Color(
                    1f,
                    backDelta.x,
                    backDelta.y,
                    maxDepth / 30f);
                surfaceMesh.UVvertices[nearIndex] = frontNormal;
                surfaceMesh.UVvertices[farIndex] = backNormal;

                underfill.verticeColors[nearIndex] = new Color(
                    _screenBottom[i].y - _screenFront[i].y,
                    frontDelta.x,
                    frontDelta.y,
                    minDepth / 30f);
                underfill.verticeColors[farIndex] = new Color(
                    0f,
                    frontDelta.x,
                    frontDelta.y,
                    minDepth / 30f);
                underfill.UVvertices[nearIndex] = frontNormal;
                underfill.UVvertices[farIndex] = frontNormal;
            }
            else
            {
                surfaceMesh.verticeColors[nearIndex] = surfaceFallback;
                surfaceMesh.verticeColors[farIndex] = deepFallback;
                underfill.verticeColors[nearIndex] = deepFallback;
                underfill.verticeColors[farIndex] = surfaceFallback;
                surfaceMesh.UVvertices[nearIndex] = Vector2.zero;
                surfaceMesh.UVvertices[farIndex] = Vector2.zero;
                underfill.UVvertices[nearIndex] = Vector2.zero;
                underfill.UVvertices[farIndex] = Vector2.zero;
            }
        }
    }

    private void DrawTerrainStain(
        TriangleMesh stain,
        bool useTerrainPalette,
        RoomCamera rCam,
        float minDepth,
        float maxDepth)
    {
        float stainHeight = room.roomSettings.TerrainStainHeight * MaxStainHeight;
        bool show = room.roomSettings.TerrainStainAmount > 0.0001f && stainHeight > 0.01f;
        stain.isVisible = show;
        if (!show)
        {
            return;
        }

        if (!useTerrainPalette)
        {
            Color baseColor = Color.Lerp(
                GetSurfaceFallback(rCam),
                rCam.currentPalette.blackColor,
                0.18f);
            baseColor.a = room.roomSettings.TerrainStainAmount * 0.40f;
            Color fadeColor = baseColor;
            fadeColor.a = 0f;

            for (int i = 0; i < SampleCount; i++)
            {
                int nearIndex = i * 2;
                int farIndex = nearIndex + 1;
                stain.MoveVertice(nearIndex, _screenFront[i]);
                stain.MoveVertice(farIndex, _screenFront[i] + Vector2.up * stainHeight);
                stain.verticeColors[nearIndex] = baseColor;
                stain.verticeColors[farIndex] = fadeColor;
                stain.UVvertices[nearIndex] = Vector2.zero;
                stain.UVvertices[farIndex] = Vector2.one;
            }

            return;
        }

        int backCursor = 0;
        for (int frontIndex = 0; frontIndex < SampleCount; frontIndex++)
        {
            Vector2 projectedFront = TerrainCurve.ApplyDepth(rCam, _screenFront[frontIndex], minDepth);

            while (backCursor < SampleCount - 2 &&
                   TerrainCurve.ApplyDepth(rCam, _screenBack[backCursor + 1], maxDepth).x < projectedFront.x)
            {
                backCursor++;
            }

            Vector2 backA = TerrainCurve.ApplyDepth(rCam, _screenBack[backCursor], maxDepth);
            Vector2 backB = TerrainCurve.ApplyDepth(rCam, _screenBack[backCursor + 1], maxDepth);
            float t = Mathf.Abs(backB.x - backA.x) > 0.0001f
                ? Custom.InverseLerpUnclamped(backA.x, backB.x, projectedFront.x)
                : 0f;
            Vector2 projectedBack = Vector2.Lerp(backA, backB, t);

            Vector2 depthZero = Vector2.LerpUnclamped(
                projectedFront,
                projectedBack,
                Custom.InverseLerpUnclamped(minDepth, maxDepth, 0f));
            Vector2 depthThirty = Vector2.LerpUnclamped(
                projectedFront,
                projectedBack,
                Custom.InverseLerpUnclamped(minDepth, maxDepth, 30f));
            Vector2 stainTopAtZero = depthZero + Vector2.up * stainHeight;
            Vector2 stainTopAtThirty = depthThirty + Vector2.up * stainHeight;
            Vector2 stainBottom = projectedFront;
            Vector2 stainTop = new(
                stainTopAtZero.x,
                Mathf.Max(stainTopAtZero.y, stainTopAtThirty.y));

            int nearIndex = frontIndex * 2;
            int farIndex = nearIndex + 1;
            stain.MoveVertice(nearIndex, stainBottom);
            stain.MoveVertice(farIndex, stainTop);
            stain.verticeColors[nearIndex] = new Color(0f, 0f, 0f, minDepth / 30f);
            stain.verticeColors[farIndex] = new Color(0f, 0f, 0f, minDepth / 30f);
            stain.UVvertices[nearIndex] = new Vector2(
                Custom.InverseLerpUnclamped(depthZero.y, stainTopAtZero.y, stainBottom.y),
                Custom.InverseLerpUnclamped(depthThirty.y, stainTopAtThirty.y, stainBottom.y));
            stain.UVvertices[farIndex] = new Vector2(
                Custom.InverseLerpUnclamped(depthZero.y, stainTopAtZero.y, stainTop.y),
                Custom.InverseLerpUnclamped(depthThirty.y, stainTopAtThirty.y, stainTop.y));
        }
    }

    private void DrawFlowStripes(
        RoomCamera.SpriteLeaser sLeaser,
        float timeStacker,
        Vector2 camPos,
        RoomCamera rCam,
        float minDepth,
        float maxDepth)
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

            float edgeFade = Mathf.SmoothStep(
                0f,
                1f,
                Mathf.Clamp01(Mathf.Min(phase, 1f - phase) / TerrainJoinFraction));

            float visualDepth = Mathf.Clamp01(_flowDepth[i]);
            Vector2 front = _placedObject.pos +
                            QuicksandSurface.EvaluateByApproximateLength(Data.SurfaceSpline, phase);
            Vector2 rawPoint = front + Vector2.up * TerrainBackOffset * visualDepth;
            float depth = Mathf.Lerp(minDepth, maxDepth, visualDepth);
            Vector2 center = TerrainCurve.ApplyDepth(rCam, rawPoint - camPos, depth);

            float aheadU = Mathf.Clamp01(phase + 0.008f * stripeDirection);
            Vector2 aheadFront = _placedObject.pos +
                                 QuicksandSurface.EvaluateByApproximateLength(Data.SurfaceSpline, aheadU);
            Vector2 aheadRaw = aheadFront + Vector2.up * TerrainBackOffset * visualDepth;
            Vector2 ahead = TerrainCurve.ApplyDepth(rCam, aheadRaw - camPos, depth);
            Vector2 tangent = SafeNormal(ahead - center, Vector2.right * stripeDirection);

            FSprite stripe = sLeaser.sprites[i + 3];
            stripe.SetPosition(center);
            stripe.scaleY = _flowLength[i] *
                            Mathf.Lerp(0.75f, 1.15f, speed / 2f) *
                            Mathf.Lerp(0.85f, 1.25f, grain);
            stripe.scaleX = (i % 3 == 0 ? 1.6f : 1f) * Mathf.Lerp(0.8f, 1.25f, grain);
            stripe.rotation = Custom.AimFromOneVectorToAnother(center, center + tangent);
            stripe.color = i % 4 == 0 ? darkFlow : lightFlow;
            stripe.alpha = (i % 4 == 0 ? 0.18f : 0.32f) *
                           Mathf.Lerp(1f, 0.70f, visualDepth) *
                           Mathf.Lerp(0.65f, 1f, grain) *
                           edgeFade;
            stripe.isVisible = stripe.alpha > 0.002f;
        }
    }

    public void ApplyPalette(
        RoomCamera.SpriteLeaser sLeaser,
        RoomCamera rCam,
        RoomPalette palette)
    {
    }

    public void AddToContainer(
        RoomCamera.SpriteLeaser sLeaser,
        RoomCamera rCam,
        FContainer newContainer)
    {
        // Match Watcher's TerrainCurve hierarchy exactly. The terrain sprites are
        // direct children of Sand; sLeaser.containers[0] is only the final internal
        // container, not a wrapper around the entire terrain drawable.
        FContainer sand = newContainer ?? rCam.ReturnFContainer("Sand");
        FContainer foreground = rCam.ReturnFContainer("Foreground");

        foreground.AddChild(sLeaser.sprites[0]);
        sand.AddChild(sLeaser.sprites[1]);
        for (int i = 0; i < FlowStripeCount; i++)
        {
            sand.AddChild(sLeaser.sprites[i + 3]);
        }
        sand.AddChild(sLeaser.sprites[2]);

        if (sLeaser.containers != null &&
            sLeaser.containers.Length > 0 &&
            sLeaser.containers[0] != null)
        {
            sand.AddChild(sLeaser.containers[0]);
        }
    }

    private static TriangleMesh.Triangle[] BuildStripTriangles()
    {
        TriangleMesh.Triangle[] triangles = new TriangleMesh.Triangle[(SampleCount - 1) * 2];
        for (int i = 0; i < SampleCount - 1; i++)
        {
            int near = i * 2;
            int far = near + 1;
            int nextNear = near + 2;
            int nextFar = near + 3;
            triangles[i * 2] = new TriangleMesh.Triangle(near, far, nextNear);
            triangles[i * 2 + 1] = new TriangleMesh.Triangle(nextNear, far, nextFar);
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

        Vector2 sunOffset = Vector2.zero;
        Vector2 sunlightTextureSize = rCam.sunlightTexture != null
            ? new Vector2(rCam.sunlightTexture.width, rCam.sunlightTexture.height)
            : Vector2.zero;

        if (room.roomSettings?.placedObjects != null)
        {
            for (int i = 0; i < room.roomSettings.placedObjects.Count; i++)
            {
                PlacedObject placed = room.roomSettings.placedObjects[i];
                if (placed != null &&
                    placed.type == PlacedObject.Type.TerrainSunOffset &&
                    placed.data is PlacedObject.ResizableObjectData resize)
                {
                    sunOffset = resize.handlePos;
                    break;
                }
            }
        }

        Vector2 shaderSunOffset =
            (sunlightTextureSize - new Vector2(room.PixelWidth, room.PixelHeight)) / 2f +
            new Vector2(140f, -120f) +
            sunOffset;
        Shader.SetGlobalVector(
            "_sunlightOffset",
            new Vector4(shaderSunOffset.x, shaderSunOffset.y, 0f, 0f));

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

    private static void ResetTerrainLightInfo()
    {
        Array.Clear(EmptyTerrainLightColors, 0, EmptyTerrainLightColors.Length);
        Array.Clear(EmptyTerrainLightParams, 0, EmptyTerrainLightParams.Length);
        Shader.SetGlobalVectorArray("_lightSourceColors", EmptyTerrainLightColors);
        Shader.SetGlobalVectorArray("_lightSourceParams", EmptyTerrainLightParams);
    }

    private static bool CanUseTerrainShader(RoomCamera rCam)
    {
        return ModManager.Watcher &&
               rCam?.terrainPalette != null &&
               rCam.game?.rainWorld?.Shaders != null &&
               rCam.game.rainWorld.Shaders.ContainsKey("SlopedTerrainSurface") &&
               rCam.game.rainWorld.Shaders.ContainsKey("SlopedTerrainStain");
    }

    private static Vector2 GetSampleDelta(Vector2[] points, int index)
    {
        if (points == null || points.Length < 2)
        {
            return Vector2.right;
        }

        int previous = Mathf.Max(0, index - 1);
        int next = Mathf.Min(points.Length - 1, index + 1);
        Vector2 delta = points[next] - points[previous];
        return delta.sqrMagnitude > 0.0001f ? delta : Vector2.right;
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
