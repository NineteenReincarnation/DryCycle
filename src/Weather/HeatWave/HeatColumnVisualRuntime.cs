using System;
using System.Collections.Generic;
using UnityEngine;

namespace DryCycle.Weather.HeatWave;

/// <summary>
/// Local high-temperature optics for mapper-authored HeatColumn objects. This follows
/// Rain World's own separation of responsibilities: global HeatWave uses LevelHeat,
/// while localized heat sources use HeatDistortion sprites in GrabShaders.
/// </summary>
internal static class HeatColumnVisualRuntime
{
    internal static void AttachToRoom(Room room)
    {
        if (room?.roomSettings?.placedObjects == null || HeatColumnHooks.PlacedType == null)
        {
            return;
        }

        for (int i = 0; i < room.roomSettings.placedObjects.Count; i++)
        {
            PlacedObject placed = room.roomSettings.placedObjects[i];
            if (placed == null ||
                !placed.active ||
                placed.type != HeatColumnHooks.PlacedType ||
                placed.data is not HeatColumnData)
            {
                continue;
            }

            room.AddObject(new HeatColumnVisual(placed));
        }
    }

    private sealed class HeatColumnVisual : CosmeticSprite, INotifyWhenRoomUnloaded
    {
        private const int SegmentCount = 4;
        private readonly PlacedObject _placed;
        private float _time;

        internal HeatColumnVisual(PlacedObject placed)
        {
            _placed = placed ?? throw new ArgumentNullException(nameof(placed));
        }

        public override void Update(bool eu)
        {
            base.Update(eu);
            _time += 1f / 40f;

            if (_placed == null || !_placed.active || _placed.data is not HeatColumnData)
            {
                Destroy();
            }
        }

        public override void InitiateSprites(RoomCamera.SpriteLeaser sLeaser, RoomCamera rCam)
        {
            sLeaser.sprites = new FSprite[SegmentCount];
            bool hasShader = rCam?.room?.game?.rainWorld?.Shaders != null &&
                             rCam.room.game.rainWorld.Shaders.TryGetValue("HeatDistortion", out FShader heatShader) &&
                             heatShader != null;

            for (int i = 0; i < SegmentCount; i++)
            {
                FSprite sprite = new("Futile_White")
                {
                    anchorX = 0.5f,
                    anchorY = 0.5f,
                    isVisible = false
                };
                if (hasShader)
                {
                    sprite.shader = heatShader;
                }
                sLeaser.sprites[i] = sprite;
            }

            AddToContainer(sLeaser, rCam, null);
        }

        public override void DrawSprites(
            RoomCamera.SpriteLeaser sLeaser,
            RoomCamera rCam,
            float timeStacker,
            Vector2 camPos)
        {
            if (room == null || room != rCam.room || _placed?.data is not HeatColumnData data)
            {
                sLeaser.CleanSpritesAndRemove();
                return;
            }

            if (!HeatWaveWeatherRuntime.TryEvaluate(room, out float intensity) || intensity <= 0.0001f)
            {
                SetVisible(sLeaser.sprites, false);
                base.DrawSprites(sLeaser, rCam, timeStacker, camPos);
                return;
            }

            Vector2 flow = data.FlowVector;
            float length = Mathf.Max(1f, flow.magnitude);
            Vector2 direction = flow / length;
            Vector2 perpendicular = new(-direction.y, direction.x);
            float angle = Custom.VecToDeg(direction) - 90f;
            float heatStrength = Mathf.Clamp01(intensity * data.Strength);

            for (int i = 0; i < SegmentCount; i++)
            {
                float t = (i + 0.5f) / SegmentCount;
                float expansion = Mathf.Lerp(0.72f, data.Expansion, t);
                float radius = data.Radius * expansion;
                float segmentLength = length / SegmentCount * 1.45f;
                float phase = _time * Mathf.Lerp(0.55f, 1.15f, data.FlowSpeed) + i * 1.73f;
                float sway = Mathf.Sin(phase * 1.37f) * data.Turbulence * radius * 0.07f;
                sway += Mathf.Sin(phase * 0.61f + 2.4f) * data.Turbulence * radius * 0.035f;
                float pulse = 1f + Mathf.Sin(phase * 1.91f) * data.Pulse * 0.10f;

                Vector2 worldPos = _placed.pos + flow * t + perpendicular * sway;
                FSprite sprite = sLeaser.sprites[i];
                sprite.x = worldPos.x - camPos.x;
                sprite.y = worldPos.y - camPos.y;
                sprite.rotation = angle;
                sprite.scaleX = Mathf.Max(0.5f, radius * 2f * pulse / 16f);
                sprite.scaleY = Mathf.Max(0.5f, segmentLength * pulse / 16f);
                sprite.alpha = Mathf.Clamp01(
                    heatStrength *
                    Mathf.Lerp(0.34f, 0.58f, t) *
                    Mathf.Lerp(0.82f, 1.08f, pulse - 0.9f));
                sprite.isVisible = sprite.alpha > 0.005f;
            }

            base.DrawSprites(sLeaser, rCam, timeStacker, camPos);
        }

        public override void AddToContainer(
            RoomCamera.SpriteLeaser sLeaser,
            RoomCamera rCam,
            FContainer newContatiner)
        {
            if (sLeaser?.sprites == null || rCam == null)
            {
                return;
            }

            FContainer container = rCam.ReturnFContainer("GrabShaders");
            for (int i = 0; i < sLeaser.sprites.Length; i++)
            {
                FSprite sprite = sLeaser.sprites[i];
                if (sprite == null)
                {
                    continue;
                }
                sprite.RemoveFromContainer();
                container.AddChild(sprite);
            }
        }

        public void RoomUnloaded()
        {
            Destroy();
        }

        private static void SetVisible(IReadOnlyList<FSprite> sprites, bool visible)
        {
            if (sprites == null)
            {
                return;
            }

            for (int i = 0; i < sprites.Count; i++)
            {
                if (sprites[i] != null)
                {
                    sprites[i].isVisible = visible;
                }
            }
        }
    }
}
