using System.Reflection;
using System.Runtime.CompilerServices;
using UnityEngine;
using Random = UnityEngine.Random;

namespace DryCycle.Items.DewPod;

/// <summary>
/// Provides clearly audible feedback for sustained Dew Pod drinking and for the
/// exact frame an intact detached pod ruptures. The rupture transition is watched
/// instead of being tied only to TerrainImpact, so weapon/explosion breaks keep the
/// same material identity without replaying sounds for already-broken saved pods.
/// </summary>
internal static class DewPodAudioHooks
{
    private static readonly FieldInfo DrinkPoseFramesField = typeof(DewPod).GetField(
        "_drinkPoseFrames",
        BindingFlags.Instance | BindingFlags.NonPublic);

    private static readonly FieldInfo DrinkPoseTargetField = typeof(DewPod).GetField(
        "_drinkPoseTarget",
        BindingFlags.Instance | BindingFlags.NonPublic);

    private sealed class AudioState
    {
        internal bool Initialized;
        internal bool WasBroken;
        internal int SipCooldown;
    }

    private static readonly ConditionalWeakTable<DewPod, AudioState> States = new();
    private static bool _enabled;

    internal static void Enable()
    {
        if (_enabled)
        {
            return;
        }

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

            // Organic pop + short bright impact gives the fleshy shell a readable
            // fracture without making it sound like a glass bottle.
            room.PlaySound(
                SoundID.Seed_Cob_Pop,
                pos,
                1.08f,
                Random.Range(0.88f, 1.02f));

            room.PlaySound(
                SoundID.Rock_Hit_Wall,
                pos,
                0.68f,
                Random.Range(1.22f, 1.42f));
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

        if (poseFrames <= 0 || pod.WaterWV <= 0f || state.SipCooldown > 0)
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

        // The existing Bite_Water_Nut sample is intentionally soft in vanilla.
        // Lead with the fuller Eat_Water_Nut sample at the mouth, then add a quieter
        // wet bite transient. This remains spatial but is loud enough to survive
        // normal room ambience and rain.
        room.PlaySound(
            SoundID.Slugcat_Eat_Water_Nut,
            mouthPos,
            1.28f,
            Random.Range(0.96f, 1.06f));

        room.PlaySound(
            SoundID.Slugcat_Bite_Water_Nut,
            mouthPos,
            0.82f,
            Random.Range(1.00f, 1.10f));

        state.SipCooldown = Random.Range(6, 10);
    }
}
