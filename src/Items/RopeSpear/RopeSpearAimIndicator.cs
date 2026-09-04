using RWCustom;
using UnityEngine;

namespace DryCycle.Items.RopeSpear;

/// <summary>
/// World-space aiming guide shown only while a RopeSpear is in long-press aim mode.
/// The guide is centered on the slugcat, draws the facing-side 180 degree arc, and
/// extends a ray along the exact direction that will be used on release.
/// </summary>
internal sealed class RopeSpearAimIndicator : CosmeticSprite
{
    private const int ArcSegments = 32;
    private const int DirectionLineSprite = ArcSegments;
    private const int SpriteCount = ArcSegments + 1;

    private const float ArcRadius = 42f;
    private const float ArcThickness = 1.6f;
    private const float DirectionThickness = 1.35f;
    private const float DirectionStartRadius = 7f;
    private const float DirectionEndRadius = 68f;

    private static readonly Color ArcColor = new(1f, 0.16f, 0.13f);
    private static readonly Color DirectionColor = new(1f, 0.72f, 0.56f);

    private readonly Player player;
    private int age;

    internal RopeSpearAimIndicator(Player player)
    {
        this.player = player;
    }

    public override void Update(bool eu)
    {
        base.Update(eu);
        age++;

        if (player == null ||
            player.room != room ||
            !RopeSpearAimController.TryGetAimVisualState(
                player,
                out _,
                out _))
        {
            Destroy();
        }
    }

    public override void InitiateSprites(
        RoomCamera.SpriteLeaser sLeaser,
        RoomCamera rCam)
    {
        sLeaser.sprites = new FSprite[SpriteCount];

        for (int i = 0; i < ArcSegments; i++)
        {
            sLeaser.sprites[i] = new FSprite("pixel")
            {
                anchorY = 0f,
                scaleX = ArcThickness,
                color = ArcColor
            };
        }

        sLeaser.sprites[DirectionLineSprite] = new FSprite("pixel")
        {
            anchorY = 0f,
            scaleX = DirectionThickness,
            color = DirectionColor
        };

        AddToContainer(sLeaser, rCam, null);
    }

    public override void AddToContainer(
        RoomCamera.SpriteLeaser sLeaser,
        RoomCamera rCam,
        FContainer newContainer)
    {
        base.AddToContainer(
            sLeaser,
            rCam,
            newContainer ?? rCam.ReturnFContainer("Foreground"));
    }

    public override void ApplyPalette(
        RoomCamera.SpriteLeaser sLeaser,
        RoomCamera rCam,
        RoomPalette palette)
    {
        for (int i = 0; i < ArcSegments; i++)
        {
            sLeaser.sprites[i].color = ArcColor;
        }

        sLeaser.sprites[DirectionLineSprite].color = DirectionColor;
    }

    public override void DrawSprites(
        RoomCamera.SpriteLeaser sLeaser,
        RoomCamera rCam,
        float timeStacker,
        Vector2 camPos)
    {
        base.DrawSprites(sLeaser, rCam, timeStacker, camPos);

        if (slatedForDeletetion ||
            room != rCam.room ||
            player == null ||
            !RopeSpearAimController.TryGetAimVisualState(
                player,
                out int facing,
                out float aimAngle))
        {
            return;
        }

        Vector2 center = InterpolatedPlayerCenter(timeStacker);
        float fade = Mathf.Clamp01(age / 5f);

        for (int i = 0; i < ArcSegments; i++)
        {
            float t0 = i / (float)ArcSegments;
            float t1 = (i + 1) / (float)ArcSegments;
            float angle0 = Mathf.Lerp(-90f, 90f, t0);
            float angle1 = Mathf.Lerp(-90f, 90f, t1);

            Vector2 a = center + ArcDirection(facing, angle0) * ArcRadius;
            Vector2 b = center + ArcDirection(facing, angle1) * ArcRadius;
            SetLine(sLeaser.sprites[i], a, b, camPos);
            sLeaser.sprites[i].alpha = 0.68f * fade;
        }

        Vector2 aimDirection = ArcDirection(facing, aimAngle);
        Vector2 lineStart = center + aimDirection * DirectionStartRadius;
        Vector2 lineEnd = center + aimDirection * DirectionEndRadius;
        SetLine(
            sLeaser.sprites[DirectionLineSprite],
            lineStart,
            lineEnd,
            camPos);
        sLeaser.sprites[DirectionLineSprite].alpha = 0.96f * fade;
    }

    private Vector2 InterpolatedPlayerCenter(float timeStacker)
    {
        if (player.bodyChunks == null || player.bodyChunks.Length == 0)
        {
            return player.firstChunk.pos;
        }

        BodyChunk front = player.bodyChunks[0];
        Vector2 frontPos = Vector2.Lerp(
            front.lastPos,
            front.pos,
            timeStacker);

        if (player.bodyChunks.Length < 2)
        {
            return frontPos;
        }

        BodyChunk rear = player.bodyChunks[1];
        Vector2 rearPos = Vector2.Lerp(
            rear.lastPos,
            rear.pos,
            timeStacker);

        return (frontPos + rearPos) * 0.5f;
    }

    private static Vector2 ArcDirection(int facing, float angleDegrees)
    {
        float radians = angleDegrees * Mathf.Deg2Rad;
        Vector2 direction = new(
            Mathf.Cos(radians) * (facing < 0 ? -1f : 1f),
            Mathf.Sin(radians));
        return direction.normalized;
    }

    private static void SetLine(
        FSprite sprite,
        Vector2 a,
        Vector2 b,
        Vector2 camPos)
    {
        sprite.SetPosition(a - camPos);
        sprite.scaleY = Vector2.Distance(a, b);
        sprite.rotation = Custom.AimFromOneVectorToAnother(a, b);
    }
}
