using System;
using RWCustom;
using UnityEngine;

namespace DryCycle.TerrainExt.QuicksandZone;

internal sealed class QuicksandZone : UpdatableAndDeletable, IDrawable
{
    private const int SampleCount = 64;
    private const int FlowStripeCount = 14;

    private static readonly Color SurfaceColor = new(0.79f, 0.61f, 0.32f);
    private static readonly Color MidColor = new(0.64f, 0.45f, 0.23f);
    private static readonly Color DeepColor = new(0.43f, 0.28f, 0.15f);
    private static readonly Color LightFlowColor = new(0.92f, 0.76f, 0.43f);
    private static readonly Color DarkFlowColor = new(0.50f, 0.32f, 0.17f);

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
    private Color _surfaceColor = SurfaceColor;
    private Color _midColor = MidColor;
    private Color _deepColor = DeepColor;
    private Color _lightFlowColor = LightFlowColor;
    private Color _darkFlowColor = DarkFlowColor;

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

        sLeaser.sprites = new FSprite[1 + FlowStripeCount];
        sLeaser.sprites[0] = new TriangleMesh("Futile_White", triangles, customColor: true);

        for (int i = 0; i < FlowStripeCount; i++)
        {
            sLeaser.sprites[i + 1] = new FSprite("pixel")
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

        TriangleMesh mesh = sLeaser.sprites[0] as TriangleMesh;
        for (int i = 0; i < SampleCount; i++)
        {
            float u = (float)i / (SampleCount - 1);
            Vector2 surface = _surface[i];
            Vector2 bottom = _bottom[i];
            Vector2 inward = SafeNormal(bottom - surface, Vector2.down);
            Vector2 outward = -inward;

            float movingWave =
                Mathf.Sin(u * Mathf.PI * 6f - _flowTime * 0.050f * Data.FlowSpeed) * 1.45f +
                Mathf.Sin(u * Mathf.PI * 14f - _flowTime * 0.083f * Data.FlowSpeed + 1.7f) * 0.48f;
            float localWave = Mathf.Lerp(_lastWave[i], _wave[i], timeStacker);
            Vector2 animatedSurface = surface + outward * (movingWave + localWave);

            int topIndex = i * 2;
            int bottomIndex = topIndex + 1;
            mesh.MoveVertice(topIndex, animatedSurface - camPos);
            mesh.MoveVertice(bottomIndex, bottom - camPos);
            mesh.verticeColors[topIndex] = Color.Lerp(_surfaceColor, _midColor, 0.10f + u * 0.03f);
            mesh.verticeColors[bottomIndex] = _deepColor;
        }

        DrawFlowStripes(sLeaser, timeStacker, camPos);
    }

    private void DrawFlowStripes(
        RoomCamera.SpriteLeaser sLeaser,
        float timeStacker,
        Vector2 camPos)
    {
        float signedSpeed = Data.FlowSpeed;
        float direction = signedSpeed >= 0f ? 1f : -1f;
        float speed = Mathf.Max(0.08f, Mathf.Abs(signedSpeed));

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

            FSprite stripe = sLeaser.sprites[i + 1];
            stripe.SetPosition(center - camPos);
            stripe.scaleY = _flowLength[i] * Mathf.Lerp(0.75f, 1.15f, speed / 2f);
            stripe.rotation = Custom.AimFromOneVectorToAnother(center, center + tangent);
            stripe.color = i % 4 == 0 ? _darkFlowColor : _lightFlowColor;
            stripe.alpha = (i % 4 == 0 ? 0.30f : 0.48f) *
                           Mathf.Lerp(1f, 0.68f, _flowDepth[i]);
            stripe.isVisible = true;
        }
    }

    public void ApplyPalette(
        RoomCamera.SpriteLeaser sLeaser,
        RoomCamera rCam,
        RoomPalette palette)
    {
        Color black = palette.blackColor;
        _surfaceColor = Color.Lerp(SurfaceColor, black, 0.10f);
        _midColor = Color.Lerp(MidColor, black, 0.14f);
        _deepColor = Color.Lerp(DeepColor, black, 0.18f);
        _lightFlowColor = Color.Lerp(LightFlowColor, black, 0.08f);
        _darkFlowColor = Color.Lerp(DarkFlowColor, black, 0.16f);
    }

    public void AddToContainer(
        RoomCamera.SpriteLeaser sLeaser,
        RoomCamera rCam,
        FContainer newContainer)
    {
        newContainer ??= rCam.ReturnFContainer("Sand");
        for (int i = 0; i < sLeaser.sprites.Length; i++)
        {
            newContainer.AddChild(sLeaser.sprites[i]);
        }
    }

    private static Vector2 SafeNormal(Vector2 value, Vector2 fallback)
    {
        return value.sqrMagnitude > 0.0001f ? value.normalized : fallback;
    }
}
