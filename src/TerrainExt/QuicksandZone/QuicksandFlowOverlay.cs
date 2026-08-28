using System.Collections.Generic;
using RWCustom;
using UnityEngine;

namespace DryCycle.TerrainExt.QuicksandZone;

internal sealed class QuicksandFlowOverlay : UpdatableAndDeletable, IDrawable
{
    private const int StripeCount = 24;

    private readonly float[] _phase = new float[StripeCount];
    private readonly float[] _depth = new float[StripeCount];
    private readonly float[] _length = new float[StripeCount];
    private readonly float[] _speed = new float[StripeCount];
    private readonly List<Vector2> _intervals = new();
    private float _time;

    internal QuicksandZone Zone { get; }

    internal QuicksandFlowOverlay(QuicksandZone zone)
    {
        Zone = zone;
        for (int i = 0; i < StripeCount; i++)
        {
            float hash = Mathf.Repeat((i + 1) * 0.6180339887f, 1f);
            _phase[i] = hash;
            _depth[i] = Mathf.Lerp(0.04f, 0.44f, Mathf.Repeat(hash * 2.41f, 1f));
            _length[i] = Mathf.Lerp(11f, 30f, Mathf.Repeat(hash * 5.07f, 1f));
            _speed[i] = Mathf.Lerp(0.62f, 1.22f, Mathf.Repeat(hash * 7.73f, 1f));
        }
    }

    public override void Update(bool eu)
    {
        base.Update(eu);

        if (Zone == null ||
            Zone.slatedForDeletetion ||
            Zone.PlacedObject == null ||
            !Zone.PlacedObject.active)
        {
            Destroy();
            return;
        }

        _time += Mathf.Max(0.08f, Mathf.Abs(Zone.Data?.FlowSpeed ?? 0f));
    }

    public void InitiateSprites(RoomCamera.SpriteLeaser sLeaser, RoomCamera rCam)
    {
        sLeaser.sprites = new FSprite[StripeCount];
        for (int i = 0; i < StripeCount; i++)
        {
            sLeaser.sprites[i] = new FSprite("pixel")
            {
                anchorY = 0.5f,
                alpha = 0.32f
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
        if (slatedForDeletetion ||
            Zone == null ||
            Zone.slatedForDeletetion ||
            room != rCam.room ||
            Zone.Data == null)
        {
            sLeaser.CleanSpritesAndRemove();
            return;
        }

        Zone.Data.FillQuicksandIntervals(_intervals);
        if (_intervals.Count == 0)
        {
            HideAll(sLeaser);
            return;
        }

        float signedSpeed = Zone.Data.FlowSpeed;
        float direction = signedSpeed >= 0f ? 1f : -1f;
        float speed = Mathf.Max(0.05f, Mathf.Abs(signedSpeed));
        float grain = room.roomSettings.TerrainGrain;
        Color light = rCam.terrainPalette != null
            ? Color.Lerp(rCam.terrainPalette.LightDustColor, rCam.terrainPalette.LightTint, 0.22f)
            : new Color(0.92f, 0.76f, 0.43f);
        Color dark = rCam.terrainPalette != null
            ? Color.Lerp(rCam.terrainPalette.DarkDustColor, rCam.currentPalette.blackColor, 0.12f)
            : new Color(0.50f, 0.32f, 0.17f);

        for (int i = 0; i < StripeCount; i++)
        {
            Vector2 interval = _intervals[i % _intervals.Count];
            float localPhase = Mathf.Repeat(
                _phase[i] +
                direction * (_time + timeStacker) * 0.0021f * speed * _speed[i],
                1f);
            float u = Mathf.Lerp(interval.x, interval.y, localPhase);

            if (!Zone.TrySampleSurfaceFrame(
                    u,
                    out Vector2 surfacePoint,
                    out Vector2 tangent,
                    out _,
                    out _))
            {
                sLeaser.sprites[i].isVisible = false;
                continue;
            }

            float visualDepth = _depth[i];
            float depth = Mathf.Lerp(Zone.minDepth, Zone.maxDepth, visualDepth);
            Vector2 raw = surfacePoint + Vector2.up * 50f * visualDepth - camPos;
            Vector2 center = TerrainCurve.ApplyDepth(rCam, raw, depth);

            float aheadU = Mathf.Clamp(
                u + direction * Mathf.Max(0.002f, (interval.y - interval.x) * 0.012f),
                interval.x,
                interval.y);
            Vector2 aheadPoint = surfacePoint + tangent * direction * 5f;
            if (Zone.TrySampleSurfaceFrame(
                    aheadU,
                    out Vector2 sampledAhead,
                    out _,
                    out _,
                    out _))
            {
                aheadPoint = sampledAhead;
            }

            Vector2 aheadRaw = aheadPoint + Vector2.up * 50f * visualDepth - camPos;
            Vector2 ahead = TerrainCurve.ApplyDepth(rCam, aheadRaw, depth);
            Vector2 screenTangent = SafeNormal(ahead - center, Vector2.right * direction);

            float edgeFade = Mathf.SmoothStep(
                0f,
                1f,
                Mathf.Clamp01(Mathf.Min(localPhase, 1f - localPhase) / 0.10f));

            FSprite stripe = sLeaser.sprites[i];
            stripe.SetPosition(center);
            stripe.scaleY = _length[i] * Mathf.Lerp(0.82f, 1.22f, grain);
            stripe.scaleX = (i % 3 == 0 ? 1.55f : 0.95f) * Mathf.Lerp(0.82f, 1.22f, grain);
            stripe.rotation = Custom.AimFromOneVectorToAnother(center, center + screenTangent);
            stripe.color = i % 5 == 4 ? dark : light;
            stripe.alpha = (i % 5 == 4 ? 0.16f : 0.30f) *
                           Mathf.Lerp(1f, 0.72f, visualDepth) *
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
        FContainer sand = newContainer ?? rCam.ReturnFContainer("Sand");
        for (int i = 0; i < sLeaser.sprites.Length; i++)
        {
            sand.AddChild(sLeaser.sprites[i]);
        }
    }

    private static void HideAll(RoomCamera.SpriteLeaser sLeaser)
    {
        for (int i = 0; i < sLeaser.sprites.Length; i++)
        {
            sLeaser.sprites[i].isVisible = false;
        }
    }

    private static Vector2 SafeNormal(Vector2 value, Vector2 fallback)
    {
        return value.sqrMagnitude > 0.0001f ? value.normalized : fallback;
    }
}
