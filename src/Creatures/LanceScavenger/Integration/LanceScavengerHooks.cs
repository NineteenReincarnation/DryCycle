using System;
using DryCycle.Framework.Creature.Core;
using DryCycle.Items.ScavengerLance;
using UnityEngine;

namespace DryCycle.Creatures.LanceScavenger;

/// <summary>Only lifecycle and dispatch here; tactics, motor, graphics and collisions remain local modules.</summary>
internal static class LanceScavengerHooks
{
    private static bool _enabled;
    private static bool _relationshipsHook;
    private static bool _actHook;
    private static bool _combatUpdateHook;
    private static bool _throwHook;
    private static bool _weaponScoreHook;
    private static bool _collectScoreHook;
    private static bool _realWeaponHook;
    private static bool _checkThrowHook;
    private static bool _initGearHook;

    internal static void Enable()
    {
        if (_enabled && AllHooksInstalled())
        {
            return;
        }

        _enabled = false;
        try
        {
            InstallHook(
                ref _relationshipsHook,
                () => On.StaticWorld.InitStaticWorld += Relationships);
            InstallHook(
                ref _actHook,
                () => On.Scavenger.Act += Act);
            InstallHook(
                ref _combatUpdateHook,
                () => On.Scavenger.CombatUpdate += CombatUpdate);
            InstallHook(
                ref _throwHook,
                () => On.Scavenger.Throw += Throw);
            InstallHook(
                ref _weaponScoreHook,
                () => On.ScavengerAI.WeaponScore += WeaponScore);
            InstallHook(
                ref _collectScoreHook,
                () => On.ScavengerAI.CollectScore_PhysicalObject_bool += CollectScore);
            InstallHook(
                ref _realWeaponHook,
                () => On.ScavengerAI.RealWeapon += RealWeapon);
            InstallHook(
                ref _checkThrowHook,
                () => On.ScavengerAI.CheckThrow += CheckThrow);
            InstallHook(
                ref _initGearHook,
                () => On.ScavengerAbstractAI.InitGearUp += InitGear);

            _enabled = true;
        }
        catch (Exception error)
        {
            global::DryCycle.StartupDiagnostics.RollbackAfterFailure(
                "LanceScavengerHooks.Enable",
                error,
                () => CleanupHooks("enable rollback"));
            throw;
        }
    }

    internal static void Disable()
    {
        if (!_enabled && !AnyHookInstalled())
        {
            return;
        }

        CleanupHooks("disable");
    }

    private static void InstallHook(ref bool installed, Action install)
    {
        if (installed)
        {
            return;
        }

        install();
        installed = true;
    }

    private static void CleanupHooks(string phase)
    {
        _enabled = false;

        RemoveHook(
            ref _initGearHook,
            phase + "/ScavengerAbstractAI.InitGearUp",
            () => On.ScavengerAbstractAI.InitGearUp -= InitGear);
        RemoveHook(
            ref _checkThrowHook,
            phase + "/ScavengerAI.CheckThrow",
            () => On.ScavengerAI.CheckThrow -= CheckThrow);
        RemoveHook(
            ref _realWeaponHook,
            phase + "/ScavengerAI.RealWeapon",
            () => On.ScavengerAI.RealWeapon -= RealWeapon);
        RemoveHook(
            ref _collectScoreHook,
            phase + "/ScavengerAI.CollectScore",
            () => On.ScavengerAI.CollectScore_PhysicalObject_bool -= CollectScore);
        RemoveHook(
            ref _weaponScoreHook,
            phase + "/ScavengerAI.WeaponScore",
            () => On.ScavengerAI.WeaponScore -= WeaponScore);
        RemoveHook(
            ref _throwHook,
            phase + "/Scavenger.Throw",
            () => On.Scavenger.Throw -= Throw);
        RemoveHook(
            ref _combatUpdateHook,
            phase + "/Scavenger.CombatUpdate",
            () => On.Scavenger.CombatUpdate -= CombatUpdate);
        RemoveHook(
            ref _actHook,
            phase + "/Scavenger.Act",
            () => On.Scavenger.Act -= Act);
        RemoveHook(
            ref _relationshipsHook,
            phase + "/StaticWorld.InitStaticWorld",
            () => On.StaticWorld.InitStaticWorld -= Relationships);

        if (AnyHookInstalled())
        {
            global::DryCycle.StartupDiagnostics.Marker(
                "LanceScavengerHooks/" + phase,
                "ROLLBACK-INCOMPLETE",
                "one or more HookGen subscriptions remain installed; next Enable will reuse them instead of duplicating them");
        }
    }

    private static void RemoveHook(ref bool installed, string source, Action remove)
    {
        if (!installed)
        {
            return;
        }

        if (global::DryCycle.StartupDiagnostics.RollbackStep(
                "LanceScavengerHooks/" + source,
                remove))
        {
            installed = false;
        }
    }

    private static bool AllHooksInstalled() =>
        _relationshipsHook &&
        _actHook &&
        _combatUpdateHook &&
        _throwHook &&
        _weaponScoreHook &&
        _collectScoreHook &&
        _realWeaponHook &&
        _checkThrowHook &&
        _initGearHook;

    private static bool AnyHookInstalled() =>
        _relationshipsHook ||
        _actHook ||
        _combatUpdateHook ||
        _throwHook ||
        _weaponScoreHook ||
        _collectScoreHook ||
        _realWeaponHook ||
        _checkThrowHook ||
        _initGearHook;

    private static void Act(On.Scavenger.orig_Act orig, Scavenger self)
    {
        if (self is not LanceScavenger lance || lance.Brain == null) { orig(self); return; }
        lance.Brain.Update();

        // A close-defense lift is a real multi-frame weapon motion. Advance its desired angle even
        // if the state machine leaves CloseDefense after the first hit/knockback, so the lance does
        // not freeze halfway through the arc while its remaining collision frames are still active.
        lance.Lance?.UpdateDefensiveLiftPose();

        if (lance.Combat.State == LanceState.Charge && !self.safariControlled) { lance.Motor.Act(); return; }

        bool holdPosition = lance.Motor.OwnsMovement && !self.safariControlled;
        bool runWeaponMotor = lance.Motor.OwnsWeaponAction && !self.safariControlled;
        if (holdPosition)
        {
            self.animation = null;
            self.commitToMoveCounter = 0;
            self.moving = false;
        }

        lance.Brain.SkipNextUpdate = true;
        try { orig(self); } finally { lance.Brain.SkipNextUpdate = false; }

        // CloseDefense reaches this path with holdPosition=false: vanilla Scavenger.Act therefore
        // keeps all flee/turn/jump/pathing behavior, while LanceMotor owns only the weapon response.
        if (runWeaponMotor) lance.Motor.Act();
    }

    private static void CombatUpdate(On.Scavenger.orig_CombatUpdate orig, Scavenger self)
    {
        if (self is LanceScavenger lance && lance.Lance != null)
        {
            if (lance.Brain?.AllowVanillaSidearmCombat == true &&
                lance.SidearmSpear != null && self.grasps[0]?.grabbed == lance.SidearmSpear)
                orig(self);
            return;
        }
        orig(self);
    }

    private static void Throw(On.Scavenger.orig_Throw orig, Scavenger self, Vector2 direction)
    {
        if (self is LanceScavenger lance)
        {
            // The identity lance is never fed into vanilla Throw. Only the reserve
            // ordinary spear may temporarily occupy grasp 0 for an approved throw.
            if (self.grasps[0]?.grabbed is ScavengerLance) return;
            if (lance.Lance != null && self.grasps[0]?.grabbed is Spear &&
                lance.Combat.State != LanceState.FollowUpThrow && lance.Brain?.AllowVanillaSidearmCombat != true)
                return;
        }
        orig(self, direction);
    }

    private static void CheckThrow(On.ScavengerAI.orig_CheckThrow orig, ScavengerAI self)
    {
        if (self.scavenger is LanceScavenger lance && lance.Lance != null)
        {
            if (lance.Brain?.SidearmThrowPass == true &&
                lance.SidearmSpear != null && lance.grasps[0]?.grabbed == lance.SidearmSpear)
                orig(self);
            return;
        }
        orig(self);
    }

    private static int WeaponScore(On.ScavengerAI.orig_WeaponScore orig, ScavengerAI self,
        PhysicalObject obj, bool pickupDropInsteadOfWeaponSelection, bool reallyWantsSpear)
    {
        if (obj is ScavengerLance)
        {
            if (self.scavenger is LanceScavenger lance)
            {
                // Default: make the lance the unequivocal primary weapon so vanilla
                // ArrangeInventory keeps it in grasp 0. During a real ThrowCharge only,
                // temporarily let the ordinary spear outrank it until that throw ends.
                return lance.Brain?.SidearmDrawn == true ? 2 : 12;
            }
            return 3;
        }
        return orig(self, obj, pickupDropInsteadOfWeaponSelection, reallyWantsSpear);
    }

    private static void InitGear(On.ScavengerAbstractAI.orig_InitGearUp orig, ScavengerAbstractAI self)
    {
        if (self.parent.creatureTemplate.type != LanceScavengerDefinition.Type) orig(self);
    }

    private static int CollectScore(On.ScavengerAI.orig_CollectScore_PhysicalObject_bool orig, ScavengerAI self, PhysicalObject obj, bool weaponFiltered)
    {
        if (obj is not ScavengerLance) return orig(self, obj, weaponFiltered);
        SocialEventRecognizer.OwnedItemOnGround owned = self.scavenger.room?.socialEventRecognizer?.ItemOwnership(obj);
        if (owned?.offeredTo != null && owned.offeredTo != self.scavenger) return 0;
        return self.scavenger is LanceScavenger ? 12 : 3;
    }

    private static bool RealWeapon(On.ScavengerAI.orig_RealWeapon orig, ScavengerAI self, PhysicalObject obj) =>
        obj is ScavengerLance || orig(self, obj);

    private static void Relationships(On.StaticWorld.orig_InitStaticWorld orig)
    {
        orig();

        if (CreatureRegistry.IsQuarantined(LanceScavengerDefinition.Type))
        {
            global::DryCycle.StartupDiagnostics.Marker(
                "LanceScavenger/StaticWorld.InitStaticWorld",
                "SKIP-QUARANTINED",
                "LanceScavenger template is quarantined");
            return;
        }

        try
        {
            CreatureTemplate lance = StaticWorld.GetCreatureTemplate(LanceScavengerDefinition.Type);
            CreatureTemplate ordinary = StaticWorld.GetCreatureTemplate(CreatureTemplate.Type.Scavenger);
            if (lance?.relationships == null || ordinary?.relationships == null) return;

            foreach (CreatureTemplate other in StaticWorld.creatureTemplates)
            {
                if (other?.type == null ||
                    other.relationships == null ||
                    other.type.Index < 0 ||
                    other.type.Index >= lance.relationships.Length ||
                    lance.type == null ||
                    lance.type.Index < 0 ||
                    lance.type.Index >= other.relationships.Length)
                {
                    continue;
                }

                lance.relationships[other.type.Index] = ordinary.CreatureRelationship(other).Duplicate();
                other.relationships[lance.type.Index] = other.CreatureRelationship(ordinary).Duplicate();
                if (other.TopAncestor().type == CreatureTemplate.Type.Scavenger)
                {
                    lance.relationships[other.type.Index] =
                        new CreatureTemplate.Relationship(CreatureTemplate.Relationship.Type.Pack, 1f);
                    other.relationships[lance.type.Index] =
                        new CreatureTemplate.Relationship(CreatureTemplate.Relationship.Type.Pack, 1f);
                }
                else if (other.smallCreature)
                {
                    lance.relationships[other.type.Index] =
                        new CreatureTemplate.Relationship(CreatureTemplate.Relationship.Type.Ignores, 0f);
                }
            }
        }
        catch (Exception error)
        {
            global::DryCycle.StartupDiagnostics.Failure(
                "LanceScavenger/StaticWorld.InitStaticWorld",
                error);
            global::DryCycle.StartupDiagnostics.Marker(
                "LanceScavenger/StaticWorld.InitStaticWorld",
                "ISOLATED",
                "relationship setup failed; StaticWorld startup will continue");
        }
    }
}
