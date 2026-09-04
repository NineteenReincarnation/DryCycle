using DryCycle.DayNight;
using RWCustom;
using UnityEngine;

namespace DryCycle.Items.RopeSpear;

/// <summary>
/// World-space eight-direction guide shown while RopeSpear hold-to-aim is active.
/// Eight short radial marks show the legal directions and one longer ray shows the
/// exact direction that will be used when Throw is released.
/// </summary>
internal sealed class RopeSpearAimIndicator : CosmeticSprite
{
    private const int DirectionCount = 8;
    private const int SelectedLineSprite = DirectionCount;
    private const int SpriteCount = DirectionCount + 1;

    private const float TickStartRadius = 35f;
    private const float TickEndRadius = 44f;
    private const float TickThickness = 1.55f;
    private const float DirectionThickness = 1.45f;
    private const float DirectionStartRadius = 7f;
    private const float DirectionEndRadius = 68f;

    private static readonly Color TickColor = new(1f, 0.16f, 0.13f);
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

        if (!RegionDayNightOptions.RopeSpearAimIndicatorEnabled ||
            player == null ||
            player.room != room ||
            !RopeSpearAimController.TryGetAimVisualState(
                player,
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

        for (int i = 0; i < DirectionCount; i++)
        {
            sLeaser.sprites[i] = new FSprite("pixel")
            {
                anchorY = 0f,
                scaleX = TickThickness,
                color = TickColor
            };
        }

        sLeaser.sprites[SelectedLineSprite] = new FSprite("pixel")
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
        for (int i = 0; i < DirectionCount; i++)
        {
            sLeaser.sprites[i].color = TickColor;
        }

        sLeaser.sprites[SelectedLineSprite].color = DirectionColor;
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
                out Vector2 aimDirection))
        {
            return;
        }

        Vector2 center = InterpolatedPlayerCenter(timeStacker);
        float fade = Mathf.Clamp01(age / 5f);

        for (int i = 0; i < DirectionCount; i++)
        {
            float angle = i * 45f * Mathf.Deg2Rad;
            Vector2 direction = new(Mathf.Cos(angle), Mathf.Sin(angle));
            Vector2 a = center + direction * TickStartRadius;
            Vector2 b = center + direction * TickEndRadius;
            SetLine(sLeaser.sprites[i], a, b, camPos);

            float selected = Vector2.Dot(direction, aimDirection.normalized);
            sLeaser.sprites[i].alpha =
                (selected > 0.995f ? 0.95f : 0.45f) * fade;
        }

        if (aimDirection.sqrMagnitude < 0.0001f)
        {
            aimDirection = Vector2.right;
        }
        else
        {
            aimDirection.Normalize();
        }

        Vector2 lineStart = center + aimDirection * DirectionStartRadius;
        Vector2 lineEnd = center + aimDirection * DirectionEndRadius;
        SetLine(
            sLeaser.sprites[SelectedLineSprite],
            lineStart,
            lineEnd,
            camPos);
        sLeaser.sprites[SelectedLineSprite].alpha = 0.96f * fade;
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
