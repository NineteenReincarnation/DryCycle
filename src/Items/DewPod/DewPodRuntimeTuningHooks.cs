using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using UnityEngine;

namespace DryCycle.Items.DewPod;

/// <summary>
/// Runtime tuning for two presentation/gameplay details that should remain
/// independent from the Dew Pod's core physics and plant attachment code:
/// - held pods settle with the translucent top membrane facing upward;
/// - fresh mother plants use a deterministic weighted 2/3/4-pod distribution.
/// </summary>
internal static class DewPodRuntimeTuningHooks
{
    // The requested 75 / 35 / 5 values are treated as relative weights because
    // they total 115 rather than 100. Normalized probabilities are therefore
    // 65.217...% / 30.434...% / 4.347...% for 2 / 3 / 4 mature pods.
    private const uint TwoPodWeight = 75u;
    private const uint ThreePodWeight = 35u;
    private const uint FourPodWeight = 5u;
    private const uint TotalPodWeight = TwoPodWeight + ThreePodWeight + FourPodWeight;

    private static readonly FieldInfo PodRotationField = typeof(DewPod).GetField(
        "_rotation",
        BindingFlags.Instance | BindingFlags.NonPublic);

    private static readonly FieldInfo PodLastRotationField = typeof(DewPod).GetField(
        "_lastRotation",
        BindingFlags.Instance | BindingFlags.NonPublic);

    private static readonly FieldInfo PodAngularVelocityField = typeof(DewPod).GetField(
        "_angularVelocity",
        BindingFlags.Instance | BindingFlags.NonPublic);

    private static readonly FieldInfo PlantRuntimeStateField = typeof(DewPodPlant).GetField(
        "_runtimeState",
        BindingFlags.Instance | BindingFlags.NonPublic);

    private sealed class PodHeldState
    {
        internal bool WasHeld;
    }

    private sealed class PlantTuningState
    {
        internal bool Applied;
    }

    private static readonly ConditionalWeakTable<DewPod, PodHeldState> PodStates = new();
    private static readonly ConditionalWeakTable<DewPodPlant, PlantTuningState> PlantStates = new();

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
            if (self.updateList[i] is DewPod pod &&
                pod.room == self &&
                !pod.slatedForDeletetion)
            {
                UpdateHeldOrientation(pod);
                continue;
            }

            if (self.updateList[i] is DewPodPlant plant &&
                plant.room == self &&
                !plant.slatedForDeletetion)
            {
                ApplyPlantDistributionOnce(plant);
            }
        }
    }

    private static void UpdateHeldOrientation(DewPod pod)
    {
        if (PodRotationField == null ||
            PodLastRotationField == null ||
            PodAngularVelocityField == null)
        {
            return;
        }

        PodHeldState state = PodStates.GetOrCreateValue(pod);
        bool held = pod.grabbedBy != null && pod.grabbedBy.Count > 0;

        if (!held)
        {
            state.WasHeld = false;
            return;
        }

        float rotation = PodRotationField.GetValue(pod) is float r ? r : 0f;
        float angularVelocity = PodAngularVelocityField.GetValue(pod) is float av ? av : 0f;

        if (!state.WasHeld)
        {
            // On the pickup frame, establish the canonical presentation immediately:
            // rotation zero means the classic top-window overlay points to world +Y.
            // This avoids a pod being held upside-down for several frames after a roll.
            PodLastRotationField.SetValue(pod, 0f);
            PodRotationField.SetValue(pod, 0f);
            PodAngularVelocityField.SetValue(pod, 0f);
            state.WasHeld = true;
            return;
        }

        // While carried, use a critically damped-ish spring instead of hard-locking
        // every frame. Small impulses therefore still read visually, but the membrane
        // always settles back toward the top before the pod is thrown again.
        float error = Mathf.DeltaAngle(rotation, 0f);
        angularVelocity = angularVelocity * 0.42f + error * 0.18f;
        angularVelocity = Mathf.Clamp(angularVelocity, -18f, 18f);
        rotation = Mathf.Repeat(rotation + angularVelocity, 360f);

        if (Mathf.Abs(error) < 0.35f && Mathf.Abs(angularVelocity) < 0.25f)
        {
            rotation = 0f;
            angularVelocity = 0f;
        }

        PodRotationField.SetValue(pod, rotation);
        PodAngularVelocityField.SetValue(pod, angularVelocity);
        state.WasHeld = true;
    }

    private static void ApplyPlantDistributionOnce(DewPodPlant plant)
    {
        PlantTuningState marker = PlantStates.GetOrCreateValue(plant);
        if (marker.Applied)
        {
            return;
        }

        marker.Applied = true;

        if (PlantRuntimeStateField?.GetValue(plant) is not DewPodPlantHooks.PlantRuntimeState runtime ||
            runtime.Dormant ||
            runtime.ConsumptionReported ||
            runtime.HarvestedMask != 0)
        {
            return;
        }

        runtime.InitialMask = BuildWeightedSpawnMask(
            plant.OriginRoom,
            plant.PlacedObjectIndex,
            runtime.CycleNumber);
    }

    private static int BuildWeightedSpawnMask(
        int roomIndex,
        int placedObjectIndex,
        int cycleNumber)
    {
        unchecked
        {
            // SplitMix-style avalanche. Using several independently mixed inputs
            // avoids the visible patterns produced by simple roomIndex*constant + slot
            // schemes, especially when many plants are adjacent in one room.
            ulong seed = 0xD1B54A32D192ED03UL;
            seed ^= Mix64((ulong)(uint)roomIndex + 0x9E3779B97F4A7C15UL);
            seed = RotateLeft(seed, 21);
            seed ^= Mix64((ulong)(uint)placedObjectIndex + 0x94D049BB133111EBUL);
            seed = RotateLeft(seed, 29);
            seed ^= Mix64((ulong)(uint)(cycleNumber + 1) + 0xBF58476D1CE4E5B9UL);
            seed = Mix64(seed ^ 0xA0761D6478BD642FUL);

            ulong state = seed;
            uint weightedRoll = NextBounded(ref state, TotalPodWeight);

            int matureCount;
            if (weightedRoll < TwoPodWeight)
            {
                matureCount = 2;
            }
            else if (weightedRoll < TwoPodWeight + ThreePodWeight)
            {
                matureCount = 3;
            }
            else
            {
                matureCount = 4;
            }

            // Fisher-Yates using rejection-sampled bounded values. Rejection avoids
            // modulo bias, so every combination of mature slots is equiprobable for
            // a given matureCount instead of slightly favoring low slot indices.
            int[] slots = { 0, 1, 2, 3 };
            for (int i = slots.Length - 1; i > 0; i--)
            {
                int j = (int)NextBounded(ref state, (uint)(i + 1));
                (slots[i], slots[j]) = (slots[j], slots[i]);
            }

            int mask = 0;
            for (int i = 0; i < matureCount; i++)
            {
                mask |= 1 << slots[i];
            }

            return mask;
        }
    }

    private static uint NextBounded(ref ulong state, uint bound)
    {
        if (bound <= 1u)
        {
            Next64(ref state);
            return 0u;
        }

        // Lemire-style rejection threshold for an unbiased bounded result.
        uint threshold = unchecked((uint)(0u - bound)) % bound;
        while (true)
        {
            uint value = (uint)(Next64(ref state) >> 32);
            ulong product = (ulong)value * bound;
            uint low = (uint)product;
            if (low >= threshold)
            {
                return (uint)(product >> 32);
            }
        }
    }

    private static ulong Next64(ref ulong state)
    {
        state += 0x9E3779B97F4A7C15UL;
        return Mix64(state);
    }

    private static ulong Mix64(ulong value)
    {
        value ^= value >> 30;
        value *= 0xBF58476D1CE4E5B9UL;
        value ^= value >> 27;
        value *= 0x94D049BB133111EBUL;
        value ^= value >> 31;
        return value;
    }

    private static ulong RotateLeft(ulong value, int bits)
    {
        return (value << bits) | (value >> (64 - bits));
    }
}
