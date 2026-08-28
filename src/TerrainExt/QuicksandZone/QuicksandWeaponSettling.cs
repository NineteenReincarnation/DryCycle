using System.Runtime.CompilerServices;
using UnityEngine;

namespace DryCycle.TerrainExt.QuicksandZone;

/// <summary>
/// Keeps loose weapons from spinning forever after they enter quicksand.
/// Translational sinking is owned exclusively by QuicksandSinkRateLimiter.
/// </summary>
internal static class QuicksandWeaponSettling
{
    private sealed class WeaponState
    {
        internal bool CapturedPose;
        internal Vector2 Pose;
    }

    private static readonly ConditionalWeakTable<Weapon, WeaponState> States = new();
    private static bool _enabled;

    internal static void Enable()
    {
        if (_enabled)
        {
            return;
        }

        _enabled = true;
        On.Weapon.Update += Weapon_Update;
    }

    internal static void Disable()
    {
        if (!_enabled)
        {
            return;
        }

        _enabled = false;
        On.Weapon.Update -= Weapon_Update;
    }

    private static void Weapon_Update(On.Weapon.orig_Update orig, Weapon self, bool eu)
    {
        orig(self, eu);

        if (self == null || self.firstChunk == null)
        {
            return;
        }

        WeaponState state = States.GetValue(self, _ => new WeaponState());

        if (self.room == null ||
            self.grabbedBy == null ||
            self.grabbedBy.Count > 0 ||
            !QuicksandSinkRateLimiter.TryGetVisualSink(
                self,
                out _,
                out _,
                out float immersion) ||
            immersion <= 0.015f)
        {
            state.CapturedPose = false;
            return;
        }

        if (!state.CapturedPose)
        {
            state.Pose = self.rotation;
            state.CapturedPose = true;

            if (self.mode == Weapon.Mode.Thrown)
            {
                self.ChangeMode(Weapon.Mode.Free);
            }
        }

        self.rotationSpeed = 0f;
        self.rotation = state.Pose;
        self.lastRotation = state.Pose;
        self.setRotation = state.Pose;
        self.vibrate = 0;

        if (self is Spear spear)
        {
            spear.spinning = false;
        }
    }
}
