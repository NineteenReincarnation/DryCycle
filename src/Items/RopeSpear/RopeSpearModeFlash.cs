using RWCustom;
using UnityEngine;

namespace DryCycle.Items.RopeSpear;

/// <summary>
/// Brief world-space mode indicator shown above the slugcat when RopeSpear
/// switches between long-payout and fixed-short modes.
/// </summary>
internal sealed class RopeSpearModeFlash : UpdatableAndDeletable, IDrawable
{
    private const int FadeInFrames = 6;
    private const int HoldFrames = 30;
    private const int FadeOutFrames = 34;
    private const int TotalFrames = FadeInFrames + HoldFrames + FadeOutFrames;
    private const float HeightAboveBody = 33f;

    private readonly Player _player;
    private readonly bool _longMode;
    private int _age;
    private float _alpha;
    private float _lastAlpha;

    internal RopeSpearModeFlash(Player player, bool longMode)
    {
        _player = player;
        _longMode = longMode;
    }

    public override void Update(bool eu)
    {
        base.Update(eu);
        _lastAlpha = _alpha;
        _age++;

        if (_player == null ||
            _player.slatedForDeletetion ||
            _player.room == null ||
            room != _player.room ||
            _age >= TotalFrames)
        {
            Destroy();
            return;
        }

        if (_age <= FadeInFrames)
        {
            _alpha = Mathf.InverseLerp(0f, FadeInFrames, _age);
        }
        else if (_age <= FadeInFrames + HoldFrames)
        {
            _alpha = 1f;
        }
        else
        {
            _alpha = 1f - Mathf.InverseLerp(
                FadeInFrames + HoldFrames,
                TotalFrames,
                _age);
        }
    }

    public void InitiateSprites(RoomCamera.SpriteLeaser sLeaser, RoomCamera rCam)
    {
        // 0: rope line, 1/2: rope end caps, 3..7: compact L/S mode glyph.
        sLeaser.sprites = new FSprite[8];
        for (int i = 0; i < sLeaser.sprites.Length; i++)
        {
            sLeaser.sprites[i] = new FSprite("pixel")
            {
                anchorX = 0.5f,
                anchorY = 0.5f,
                alpha = 0f
            };
        }

        ConfigureGlyph(sLeaser);
        AddToContainer(sLeaser, rCam, null);
    }

    public void DrawSprites(
        RoomCamera.SpriteLeaser sLeaser,
        RoomCamera rCam,
        float timeStacker,
        Vector2 camPos)
    {
        if (slatedForDeletetion ||
            _player == null ||
            _player.room != rCam.room)
        {
            sLeaser.CleanSpritesAndRemove();
            return;
        }

        Vector2 body = Vector2.Lerp(
            _player.mainBodyChunk.lastPos,
            _player.mainBodyChunk.pos,
            timeStacker);
        float fade = Mathf.Lerp(_lastAlpha, _alpha, timeStacker);
        float drift = Mathf.InverseLerp(0f, TotalFrames, _age) * 3.5f;
        Vector2 center = body + new Vector2(0f, HeightAboveBody + drift) - camPos;

        Color color = _longMode
            ? new Color(0.62f, 0.91f, 1f)
            : new Color(1f, 0.78f, 0.34f);

        float ropeWidth = _longMode ? 28f : 13f;
        FSprite rope = sLeaser.sprites[0];
        rope.SetPosition(center + new Vector2(0f, -7.5f));
        rope.scaleX = ropeWidth;
        rope.scaleY = 1.35f;

        FSprite leftCap = sLeaser.sprites[1];
        FSprite rightCap = sLeaser.sprites[2];
        leftCap.SetPosition(center + new Vector2(-ropeWidth * 0.5f, -7.5f));
        rightCap.SetPosition(center + new Vector2(ropeWidth * 0.5f, -7.5f));
        leftCap.scaleX = rightCap.scaleX = 2.6f;
        leftCap.scaleY = rightCap.scaleY = 3.8f;

        DrawGlyph(sLeaser, center);

        for (int i = 0; i < sLeaser.sprites.Length; i++)
        {
            sLeaser.sprites[i].color = color;
            sLeaser.sprites[i].alpha = fade;
            sLeaser.sprites[i].isVisible = fade > 0.01f && sLeaser.sprites[i].scaleX > 0f;
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
        FContainer container = newContainer ?? rCam.ReturnFContainer("Foreground");
        for (int i = 0; i < sLeaser.sprites.Length; i++)
        {
            sLeaser.sprites[i].RemoveFromContainer();
            container.AddChild(sLeaser.sprites[i]);
        }
    }

    private void ConfigureGlyph(RoomCamera.SpriteLeaser sLeaser)
    {
        for (int i = 3; i < 8; i++)
        {
            sLeaser.sprites[i].scaleX = 0f;
            sLeaser.sprites[i].scaleY = 0f;
        }

        if (_longMode)
        {
            // L: one vertical and one bottom segment.
            sLeaser.sprites[3].scaleX = 1.7f;
            sLeaser.sprites[3].scaleY = 8f;
            sLeaser.sprites[4].scaleX = 6f;
            sLeaser.sprites[4].scaleY = 1.7f;
        }
        else
        {
            // S: top, upper-left, middle, lower-right, bottom.
            sLeaser.sprites[3].scaleX = 6f;
            sLeaser.sprites[3].scaleY = 1.7f;
            sLeaser.sprites[4].scaleX = 1.7f;
            sLeaser.sprites[4].scaleY = 4f;
            sLeaser.sprites[5].scaleX = 6f;
            sLeaser.sprites[5].scaleY = 1.7f;
            sLeaser.sprites[6].scaleX = 1.7f;
            sLeaser.sprites[6].scaleY = 4f;
            sLeaser.sprites[7].scaleX = 6f;
            sLeaser.sprites[7].scaleY = 1.7f;
        }
    }

    private void DrawGlyph(RoomCamera.SpriteLeaser sLeaser, Vector2 center)
    {
        if (_longMode)
        {
            sLeaser.sprites[3].SetPosition(center + new Vector2(-2.2f, 1.5f));
            sLeaser.sprites[4].SetPosition(center + new Vector2(0.1f, -2.1f));
            return;
        }

        sLeaser.sprites[3].SetPosition(center + new Vector2(0f, 5f));
        sLeaser.sprites[4].SetPosition(center + new Vector2(-2.2f, 3f));
        sLeaser.sprites[5].SetPosition(center + new Vector2(0f, 1f));
        sLeaser.sprites[6].SetPosition(center + new Vector2(2.2f, -1f));
        sLeaser.sprites[7].SetPosition(center + new Vector2(0f, -3f));
    }
}
