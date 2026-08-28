using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using RWCustom;
using UnityEngine;

namespace DryCycle.Items.DewPod;

/// <summary>
/// Restores the original Dew Pod liquid treatment and translucent top membrane.
/// Pod liquid intentionally no longer samples room or water palettes. The mother
/// plant's root/stem terrain blending remains owned by DewPodPlant and continues
/// to use RoomCamera.PixelColorAtCoordinate.
/// </summary>
internal static class DewPodClassicVisualHooks
{
    private static readonly Color EmptyLiquidColor = new(0.14f, 0.48f, 0.50f);
    private static readonly Color FullLiquidColor = new(0.50f, 0.92f, 0.78f);
    private static readonly Color WindowColor = Color.Lerp(
        Color.white,
        new Color(0.62f, 0.96f, 0.82f),
        0.45f);

    private const int PlantRootSpriteCount = 3;
    private const int PlantSpritesPerSlot = 4;

    private static readonly float[] PlantSlotScales = { 0.94f, 1.04f, 1f, 0.92f };

    private static readonly FieldInfo PlantLiquidColorField = typeof(DewPodPlant).GetField(
        "_liquidColor",
        BindingFlags.Instance | BindingFlags.NonPublic);

    private static readonly FieldInfo PlantHasLiquidColorField = typeof(DewPodPlant).GetField(
        "_hasLiquidColor",
        BindingFlags.Instance | BindingFlags.NonPublic);

    private static readonly FieldInfo PodDrinkFramesField = typeof(DewPod).GetField(
        "_drinkPoseFrames",
        BindingFlags.Instance | BindingFlags.NonPublic);

    private static readonly FieldInfo PodDrinkTargetField = typeof(DewPod).GetField(
        "_drinkPoseTarget",
        BindingFlags.Instance | BindingFlags.NonPublic);

    private static readonly FieldInfo PodRotationField = typeof(DewPod).GetField(
        "_rotation",
        BindingFlags.Instance | BindingFlags.NonPublic);

    private static readonly FieldInfo PodLastRotationField = typeof(DewPod).GetField(
        "_lastRotation",
        BindingFlags.Instance | BindingFlags.NonPublic);

    private static readonly ConditionalWeakTable<DewPod, DewPodTopWindow> PodWindows = new();
    private static readonly ConditionalWeakTable<DewPodPlant, DewPodPlantTopWindows> PlantWindows = new();

    private static bool _enabled;

    internal static void Enable()
    {
        if (_enabled)
        {
            return;
        }

        _enabled = true;
        On.Room.Update += Room_Update;
        On.RoomCamera.DrawUpdate += RoomCamera_DrawUpdate;
    }

    internal static void Disable()
    {
        if (!_enabled)
        {
            return;
        }

        _enabled = false;
        On.Room.Update -= Room_Update;
        On.RoomCamera.DrawUpdate -= RoomCamera_DrawUpdate;
    }

    private static void Room_Update(On.Room.orig_Update orig, Room self)
    {
        orig(self);

        if (self?.updateList == null)
        {
            return;
        }

        List<DewPod> podsNeedingWindow = null;
        List<DewPodPlant> plantsNeedingWindow = null;

        for (int i = 0; i < self.updateList.Count; i++)
        {
            if (self.updateList[i] is DewPod pod &&
                pod.room == self &&
                !pod.slatedForDeletetion)
            {
                ForceClassicPodLiquid(pod);

                if (!PodWindows.TryGetValue(pod, out DewPodTopWindow overlay) ||
                    overlay == null ||
                    overlay.slatedForDeletetion)
                {
                    podsNeedingWindow ??= new List<DewPod>();
                    podsNeedingWindow.Add(pod);
                }
            }
            else if (self.updateList[i] is DewPodPlant plant &&
                     plant.room == self &&
                     !plant.slatedForDeletetion)
            {
                ForceClassicPlantLiquid(plant);

                if (!PlantWindows.TryGetValue(plant, out DewPodPlantTopWindows overlay) ||
                    overlay == null ||
                    overlay.slatedForDeletetion)
                {
                    plantsNeedingWindow ??= new List<DewPodPlant>();
                    plantsNeedingWindow.Add(plant);
                }
            }
        }

        if (podsNeedingWindow != null)
        {
            for (int i = 0; i < podsNeedingWindow.Count; i++)
            {
                DewPod pod = podsNeedingWindow[i];
                DewPodTopWindow overlay = new(pod);
                PodWindows.Remove(pod);
                PodWindows.Add(pod, overlay);
                self.AddObject(overlay);
            }
        }

        if (plantsNeedingWindow != null)
        {
            for (int i = 0; i < plantsNeedingWindow.Count; i++)
            {
                DewPodPlant plant = plantsNeedingWindow[i];
                DewPodPlantTopWindows overlay = new(plant);
                PlantWindows.Remove(plant);
                PlantWindows.Add(plant, overlay);
                self.AddObject(overlay);
            }
        }
    }

    private static void ForceClassicPodLiquid(DewPod pod)
    {
        if (pod?.AbstrPod == null)
        {
            return;
        }

        // Keep the persisted/runtime liquid source independent of room palettes as
        // well, so leak/burst droplets no longer inherit local water colors.
        pod.AbstrPod.LiquidColor = FullLiquidColor;
        pod.AbstrPod.HasLiquidColor = true;
    }

    private static void ForceClassicPlantLiquid(DewPodPlant plant)
    {
        if (plant == null)
        {
            return;
        }

        PlantLiquidColorField?.SetValue(plant, FullLiquidColor);
        PlantHasLiquidColorField?.SetValue(plant, true);
    }

    private static void RoomCamera_DrawUpdate(
        On.RoomCamera.orig_DrawUpdate orig,
        RoomCamera self,
        float timeStacker,
        float timeSpeed)
    {
        orig(self, timeStacker, timeSpeed);

        if (self?.room == null || self.spriteLeasers == null)
        {
            return;
        }

        // Apply the original fixed liquid gradient after normal DrawSprites and the
        // puncture overlay have run. This guarantees no local RoomPalette water
        // color can tint the visible chamber.
        for (int i = 0; i < self.spriteLeasers.Count; i++)
        {
            RoomCamera.SpriteLeaser leaser = self.spriteLeasers[i];
            if (leaser?.sprites == null)
            {
                continue;
            }

            if (leaser.drawableObject is DewPod pod &&
                pod.room == self.room &&
                leaser.sprites.Length > 1 &&
                leaser.sprites[1] != null)
            {
                leaser.sprites[1].color = Color.Lerp(
                    EmptyLiquidColor,
                    FullLiquidColor,
                    pod.Fill);
                continue;
            }

            if (leaser.drawableObject is not DewPodPlant plant || plant.room != self.room)
            {
                continue;
            }

            for (int slot = 0; slot < DewPodPlant.SlotCount; slot++)
            {
                if (!plant.IsMatureSlot(slot))
                {
                    continue;
                }

                int shellIndex = PlantRootSpriteCount + slot * PlantSpritesPerSlot + 1;
                int liquidIndex = PlantRootSpriteCount + slot * PlantSpritesPerSlot + 2;
                if (shellIndex >= leaser.sprites.Length || liquidIndex >= leaser.sprites.Length)
                {
                    continue;
                }

                FSprite shell = leaser.sprites[shellIndex];
                FSprite liquid = leaser.sprites[liquidIndex];
                if (shell == null || liquid == null)
                {
                    continue;
                }

                float ratio = shell.scaleY > 0.0001f
                    ? liquid.scaleY / shell.scaleY
                    : 0.86f;
                float estimatedFill = Mathf.Clamp01(Mathf.InverseLerp(0.09f, 0.86f, ratio));
                liquid.color = Color.Lerp(
                    EmptyLiquidColor,
                    FullLiquidColor,
                    estimatedFill);
            }
        }
    }

    private sealed class DewPodTopWindow : UpdatableAndDeletable, IDrawable
    {
        private readonly DewPod _pod;

        internal DewPodTopWindow(DewPod pod)
        {
            _pod = pod;
            room = pod?.room;
        }

        public override void Update(bool eu)
        {
            base.Update(eu);

            if (_pod == null ||
                _pod.slatedForDeletetion ||
                _pod.room == null ||
                room != _pod.room)
            {
                Destroy();
            }
        }

        public void InitiateSprites(RoomCamera.SpriteLeaser sLeaser, RoomCamera rCam)
        {
            sLeaser.sprites = new[] { new FSprite("Circle20") };
            AddToContainer(sLeaser, rCam, null);
        }

        public void DrawSprites(
            RoomCamera.SpriteLeaser sLeaser,
            RoomCamera rCam,
            float timeStacker,
            Vector2 camPos)
        {
            if (_pod?.firstChunk == null ||
                _pod.slatedForDeletetion ||
                _pod.room != rCam.room)
            {
                sLeaser.CleanSpritesAndRemove();
                return;
            }

            Vector2 drawPos = Vector2.Lerp(
                _pod.firstChunk.lastPos,
                _pod.firstChunk.pos,
                timeStacker);

            int drinkFrames = PodDrinkFramesField?.GetValue(_pod) is int frames ? frames : 0;
            if (drinkFrames > 0 && PodDrinkTargetField?.GetValue(_pod) is Vector2 drinkTarget)
            {
                drawPos = Vector2.Lerp(drawPos, drinkTarget, 0.72f);
            }

            float fill = _pod.Fill;
            float fullness = Mathf.Lerp(0.62f, 1f, Mathf.Sqrt(fill));
            float height = Mathf.Lerp(0.76f, 1.24f, fullness);
            float width = Mathf.Lerp(0.57f, 0.82f, fullness);

            float rotation = 0f;
            if (PodRotationField?.GetValue(_pod) is float currentRotation &&
                PodLastRotationField?.GetValue(_pod) is float lastRotation)
            {
                rotation = Mathf.LerpAngle(lastRotation, currentRotation, timeStacker);
            }

            Vector2 offset = RotateLocal(new Vector2(0f, height * 5.15f), rotation);
            FSprite window = sLeaser.sprites[0];
            window.x = drawPos.x + offset.x - camPos.x;
            window.y = drawPos.y + offset.y - camPos.y;
            window.scaleX = width * 0.42f;
            window.scaleY = Mathf.Lerp(0.12f, 0.27f, fullness);
            window.rotation = rotation;
            window.alpha = Mathf.Lerp(0.22f, 0.72f, fill);
            window.color = WindowColor;
        }

        public void ApplyPalette(
            RoomCamera.SpriteLeaser sLeaser,
            RoomCamera rCam,
            RoomPalette palette)
        {
            sLeaser.sprites[0].color = WindowColor;
        }

        public void AddToContainer(
            RoomCamera.SpriteLeaser sLeaser,
            RoomCamera rCam,
            FContainer newContainer)
        {
            newContainer ??= rCam.ReturnFContainer("Items");
            sLeaser.sprites[0].RemoveFromContainer();
            newContainer.AddChild(sLeaser.sprites[0]);
        }
    }

    private sealed class DewPodPlantTopWindows : UpdatableAndDeletable, IDrawable
    {
        private readonly DewPodPlant _plant;

        internal DewPodPlantTopWindows(DewPodPlant plant)
        {
            _plant = plant;
            room = plant?.room;
        }

        public override void Update(bool eu)
        {
            base.Update(eu);

            if (_plant == null ||
                _plant.slatedForDeletetion ||
                _plant.room == null ||
                room != _plant.room)
            {
                Destroy();
            }
        }

        public void InitiateSprites(RoomCamera.SpriteLeaser sLeaser, RoomCamera rCam)
        {
            sLeaser.sprites = new FSprite[DewPodPlant.SlotCount];
            for (int i = 0; i < sLeaser.sprites.Length; i++)
            {
                sLeaser.sprites[i] = new FSprite("Circle20");
            }

            AddToContainer(sLeaser, rCam, null);
        }

        public void DrawSprites(
            RoomCamera.SpriteLeaser sLeaser,
            RoomCamera rCam,
            float timeStacker,
            Vector2 camPos)
        {
            if (_plant == null ||
                _plant.slatedForDeletetion ||
                _plant.room != rCam.room)
            {
                sLeaser.CleanSpritesAndRemove();
                return;
            }

            for (int slot = 0; slot < DewPodPlant.SlotCount; slot++)
            {
                FSprite window = sLeaser.sprites[slot];
                bool mature = _plant.IsMatureSlot(slot);
                window.isVisible = mature;
                if (!mature)
                {
                    continue;
                }

                Vector2 tip = _plant.GetPodPosition(slot);
                Vector2 stemRoot = _plant.GetStemRootPosition(slot);
                Vector2 direction = Custom.DirVec(stemRoot, tip);
                if (direction.sqrMagnitude < 0.001f)
                {
                    direction = _plant.SurfaceNormal;
                }

                float scale = PlantSlotScales[slot];
                float shellHeight = 1.18f * scale;
                float shellWidth = 0.79f * scale;
                Vector2 podPos = tip - camPos;
                Vector2 windowPos = podPos + direction * (shellHeight * 5.15f);

                window.x = windowPos.x;
                window.y = windowPos.y;
                window.scaleX = shellWidth * 0.42f;
                window.scaleY = 0.27f * scale;
                window.rotation = Custom.VecToDeg(direction) - 90f;
                window.alpha = 0.72f;
                window.color = WindowColor;
            }
        }

        public void ApplyPalette(
            RoomCamera.SpriteLeaser sLeaser,
            RoomCamera rCam,
            RoomPalette palette)
        {
            for (int i = 0; i < sLeaser.sprites.Length; i++)
            {
                sLeaser.sprites[i].color = WindowColor;
            }
        }

        public void AddToContainer(
            RoomCamera.SpriteLeaser sLeaser,
            RoomCamera rCam,
            FContainer newContainer)
        {
            newContainer ??= rCam.ReturnFContainer("Items");
            for (int i = 0; i < sLeaser.sprites.Length; i++)
            {
                sLeaser.sprites[i].RemoveFromContainer();
                newContainer.AddChild(sLeaser.sprites[i]);
            }
        }
    }

    private static Vector2 RotateLocal(Vector2 local, float degrees)
    {
        float radians = degrees * Mathf.Deg2Rad;
        float sin = Mathf.Sin(radians);
        float cos = Mathf.Cos(radians);
        return new Vector2(
            local.x * cos - local.y * sin,
            local.x * sin + local.y * cos);
    }
}
