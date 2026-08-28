using System.Reflection;
using System.Runtime.CompilerServices;
using UnityEngine;
using Random = UnityEngine.Random;

namespace DryCycle.Items.DewPod;

/// <summary>
/// Provides Dew Pod drinking/rupture audio. Drinking uses the Ancient Site mod's
/// external soundeffects/DC_DrinkWater.ogg sample through a custom SoundID, while
/// rupture audio keeps the existing organic pop + disguised crack transient.
/// </summary>
internal static class DewPodAudioHooks
{
    private const string DrinkWaterSoundName = "DC_DrinkWater";

    private static readonly FieldInfo DrinkPoseFramesField = typeof(DewPod).GetField(
        "_drinkPoseFrames",
        BindingFlags.Instance | BindingFlags.NonPublic);

    private static readonly FieldInfo DrinkPoseTargetField = typeof(DewPod).GetField(
        "_drinkPoseTarget",
        BindingFlags.Instance | BindingFlags.NonPublic);

    private static readonly FieldInfo LegacyDrinkSoundCooldownField = typeof(DewPod).GetField(
        "_drinkSoundCooldown",
        BindingFlags.Instance | BindingFlags.NonPublic);

    private sealed class AudioState
    {
        internal bool Initialized;
        internal bool WasBroken;
        internal int SipCooldown;
    }

    private static readonly ConditionalWeakTable<DewPod, AudioState> States = new();
    private static bool _enabled;

    internal static SoundID DrinkWaterSound { get; private set; }

    /// <summary>
    /// Must run before Rain World's SoundLoader builds its SoundID trigger array.
    /// Plugin.OnEnable calls this before any RainWorld init hooks are registered.
    /// </summary>
    internal static void InitializeSoundIds()
    {
        if (DrinkWaterSound == null)
        {
            DrinkWaterSound = new SoundID(DrinkWaterSoundName, register: true);
        }
    }

    internal static void Enable()
    {
        if (_enabled)
        {
            return;
        }

        InitializeSoundIds();
        _enabled = true;
        On.Room.Update += Room_Update;
    }

    internal static void Disable()
    {
        if (!_enabled)
        {
            return;
        }

        _enabled = false;
        On.Room.Update -= Room_Update;
    }

    private static void Room_Update(On.Room.orig_Update orig, Room self)
    {
        // DewPod.MarkDrinking still owns the pose state and previously played the
        // vanilla Water Nut bite sample. Keep its tiny cooldown above zero before
        // the room update so that legacy sound never fires. A value of 3 is used
        // rather than a huge sentinel, so disabling this hook restores vanilla
        // behavior naturally within a few ticks.
        SuppressLegacyDrinkSound(self);

        orig(self);

        if (self?.updateList == null)
        {
            return;
        }

        for (int i = 0; i < self.updateList.Count; i++)
        {
            if (self.updateList[i] is not DewPod pod ||
                pod.room != self ||
                pod.slatedForDeletetion)
            {
                continue;
            }

            AudioState state = States.GetOrCreateValue(pod);
            UpdateRuptureAudio(self, pod, state);
            UpdateDrinkingAudio(self, pod, state);
        }
    }

    private static void SuppressLegacyDrinkSound(Room room)
    {
        if (room?.updateList == null || LegacyDrinkSoundCooldownField == null)
        {
            return;
        }

        for (int i = 0; i < room.updateList.Count; i++)
        {
            if (room.updateList[i] is DewPod pod &&
                pod.room == room &&
                !pod.slatedForDeletetion)
            {
                LegacyDrinkSoundCooldownField.SetValue(pod, 3);
            }
        }
    }

    private static void UpdateRuptureAudio(Room room, DewPod pod, AudioState state)
    {
        bool broken = pod.Broken;

        if (!state.Initialized)
        {
            // A broken pod loaded from a save should not make a phantom crack the
            // first frame it appears. Only a live intact->broken transition plays.
            state.Initialized = true;
            state.WasBroken = broken;
            return;
        }

        if (!state.WasBroken && broken)
        {
            Vector2 pos = pod.firstChunk != null
                ? pod.firstChunk.pos
                : Vector2.zero;

            // Organic pop carries the recognizable body of the rupture sound.
            room.PlaySound(
                SoundID.Seed_Cob_Pop,
                pos,
                1.08f,
                Random.Range(0.88f, 1.02f));

            // Rock_Hit_Wall is pushed far outside its normal pitch and reduced to
            // a quiet micro-transient so it reads as fibrous shell fracture.
            room.PlaySound(
                SoundID.Rock_Hit_Wall,
                pos,
                Random.Range(0.24f, 0.34f),
                Random.Range(1.62f, 1.92f));
        }

        state.WasBroken = broken;
    }

    private static void UpdateDrinkingAudio(Room room, DewPod pod, AudioState state)
    {
        if (state.SipCooldown > 0)
        {
            state.SipCooldown--;
        }

        int poseFrames = DrinkPoseFramesField?.GetValue(pod) is int frames
            ? frames
            : 0;

        if (poseFrames <= 0 ||
            pod.WaterWV <= 0f ||
            state.SipCooldown > 0 ||
            DrinkWaterSound == null)
        {
            return;
        }

        Vector2 mouthPos = pod.firstChunk != null
            ? pod.firstChunk.pos
            : Vector2.zero;

        if (DrinkPoseTargetField?.GetValue(pod) is Vector2 target)
        {
            mouthPos = target;
        }

        // Play the authored Ancient Site sample without extra pitch coloration.
        // Frequency stays at the previous fast sip cadence requested for Dew Pods.
        room.PlaySound(
            DrinkWaterSound,
            mouthPos,
            1f,
            1f);

        state.SipCooldown = Random.Range(6, 10);
    }
}
