using RWCustom;
using UnityEngine;

namespace DryCycle.Items.ScavengerLance;

internal sealed partial class ScavengerLance
{
    public override void InitiateSprites(RoomCamera.SpriteLeaser sLeaser, RoomCamera rCam)
    {
        sLeaser.sprites = new FSprite[7];
        sLeaser.sprites[0] = TriangleMesh.MakeLongMesh(8, false, false);
        sLeaser.sprites[1] = TriangleMesh.MakeLongMesh(2, true, false);
        for (int i = 2; i < 7; i++) sLeaser.sprites[i] = new FSprite("pixel");
        ApplyPalette(sLeaser, rCam, rCam.currentPalette);
        AddToContainer(sLeaser, rCam, null);
    }

    public override void DrawSprites(RoomCamera.SpriteLeaser sLeaser, RoomCamera rCam, float t, Vector2 camPos)
    {
        Vector2 direction = Vector2.Lerp(lastRotation, rotation, t).normalized;
        Vector2 grip = Vector2.Lerp(firstChunk.lastPos, firstChunk.pos, t);
        Vector2 tail = grip - direction * Length * LanceCombatMath.GripFraction;
        Vector2 perp = Custom.PerpendicularVector(direction);
        float bend = Mathf.Lerp(_lastBend, _bend, t);
        TriangleMesh shaft = (TriangleMesh)sLeaser.sprites[0];
        for (int i = 0; i < 8; i++)
        {
            float a = i / 8f, b = (i + 1) / 8f;
            Vector2 start = tail + direction * (Length - 12f) * a + perp * Mathf.Sin(a * Mathf.PI) * bend;
            Vector2 end = tail + direction * (Length - 12f) * b + perp * Mathf.Sin(b * Mathf.PI) * bend;
            shaft.MoveVertice(i * 4, start - perp * 1.3f - camPos);
            shaft.MoveVertice(i * 4 + 1, start + perp * 1.3f - camPos);
            shaft.MoveVertice(i * 4 + 2, end - perp * 1.1f - camPos);
            shaft.MoveVertice(i * 4 + 3, end + perp * 1.1f - camPos);
        }
        TriangleMesh tip = (TriangleMesh)sLeaser.sprites[1];
        Vector2 root = tail + direction * (Length - 14f);
        for (int i = 0; i < 2; i++)
        {
            Vector2 a = root + direction * (i * 7f);
            Vector2 b = root + direction * ((i + 1) * 7f);
            tip.MoveVertice(i * 4, a - perp * (i == 0 ? 1.8f : 1.3f) - camPos);
            tip.MoveVertice(i * 4 + 1, a + perp * (i == 0 ? 2.3f : 1.4f) - camPos);
            if (i == 1) tip.MoveVertice(i * 4 + 2, b - camPos);
            else
            { tip.MoveVertice(i * 4 + 2, b - perp * 1.3f - camPos); tip.MoveVertice(i * 4 + 3, b + perp * 1.4f - camPos); }
        }
        for (int i = 2; i < 7; i++)
        {
            FSprite wrap = sLeaser.sprites[i];
            Vector2 position = grip + direction * (i == 6 ? -Length * 0.29f : (i - 3) * 5f);
            wrap.SetPosition(position - camPos);
            wrap.rotation = Custom.VecToDeg(direction);
            wrap.scaleX = i == 6 ? 3.6f : 3.1f;
            wrap.scaleY = i == 6 ? 5f : 3.5f;
        }
        ApplyPalette(sLeaser, rCam, rCam.currentPalette);
        if (slatedForDeletetion || room != rCam.room) sLeaser.CleanSpritesAndRemove();
    }

    public override void ApplyPalette(RoomCamera.SpriteLeaser sLeaser, RoomCamera rCam, RoomPalette palette)
    {
        float darkness = room == null ? 0f : room.Darkness(firstChunk.pos) * (1f - room.LightSourceExposure(firstChunk.pos));
        sLeaser.sprites[0].color = Color.Lerp(new Color(0.27f, 0.23f, 0.17f), palette.blackColor, darkness);
        sLeaser.sprites[1].color = Color.Lerp(new Color(0.79f, 0.77f, 0.64f), palette.blackColor, darkness);
        for (int i = 2; i < 7; i++)
            sLeaser.sprites[i].color = Color.Lerp(i % 2 == 0 ? new Color(0.58f, 0.41f, 0.16f) : new Color(0.24f, 0.18f, 0.11f), palette.blackColor, darkness);
    }

    public override void AddToContainer(RoomCamera.SpriteLeaser sLeaser, RoomCamera rCam, FContainer container)
    {
        container ??= rCam.ReturnFContainer("Items");
        foreach (FSprite sprite in sLeaser.sprites) { sprite.RemoveFromContainer(); container.AddChild(sprite); }
    }
}
