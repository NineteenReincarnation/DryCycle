using DryCycle.Items.ScavengerLance;
using UnityEngine;

namespace DryCycle.Creatures.LanceScavenger;

/// <summary>Only lifecycle and dispatch here; tactics, motor, graphics and collisions remain local modules.</summary>
internal static class LanceScavengerHooks
{
    private static bool _enabled;
    internal static void Enable()
    {
        if (_enabled) return;
        On.StaticWorld.InitStaticWorld += Relationships;
        On.Scavenger.Act += Act;
        On.Scavenger.CombatUpdate += CombatUpdate;
        On.Scavenger.Throw += Throw;
        On.ScavengerAI.WeaponScore += WeaponScore;
        On.ScavengerAI.CollectScore_PhysicalObject_bool += CollectScore;
        On.ScavengerAI.RealWeapon += RealWeapon;
        On.ScavengerAI.CheckThrow += CheckThrow;
        On.ScavengerAbstractAI.InitGearUp += InitGear;
        _enabled = true;
    }
    internal static void Disable()
    {
        if (!_enabled) return;
        On.StaticWorld.InitStaticWorld -= Relationships;
        On.Scavenger.Act -= Act;
        On.Scavenger.CombatUpdate -= CombatUpdate;
        On.Scavenger.Throw -= Throw;
        On.ScavengerAI.WeaponScore -= WeaponScore;
        On.ScavengerAI.CollectScore_PhysicalObject_bool -= CollectScore;
        On.ScavengerAI.RealWeapon -= RealWeapon;
        On.ScavengerAI.CheckThrow -= CheckThrow;
        On.ScavengerAbstractAI.InitGearUp -= InitGear;
        _enabled = false;
    }

    private static void Act(On.Scavenger.orig_Act orig, Scavenger self)
    {
        if (self is not LanceScavenger lance || lance.Brain == null) { orig(self); return; }
        lance.Brain.Update();
        if (lance.Combat.State == LanceState.Charge && !self.safariControlled) { lance.Motor.Act(); return; }
        bool holdPosition = lance.Motor.OwnsMovement && !self.safariControlled;
        if (holdPosition)
        {
            self.animation = null;
            self.commitToMoveCounter = 0;
            self.moving = false;
        }
        lance.Brain.SkipNextUpdate = true;
        try { orig(self); } finally { lance.Brain.SkipNextUpdate = false; }
        if (holdPosition) lance.Motor.Act();
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
                // ArrangeInventory promotes the highest WeaponScore to grasp 0. A normal
                // spear scores 3, so score the reserved lance below it while a sidearm is
                // carried. Without a sidearm the lance remains the primary held weapon.
                return lance.SidearmSpear != null ? 2 : 12;
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
        CreatureTemplate lance = StaticWorld.GetCreatureTemplate(LanceScavengerDefinition.Type);
        CreatureTemplate ordinary = StaticWorld.GetCreatureTemplate(CreatureTemplate.Type.Scavenger);
        if (lance == null || ordinary == null) return;
        foreach (CreatureTemplate other in StaticWorld.creatureTemplates)
        {
            if (other == null) continue;
            lance.relationships[other.type.Index] = ordinary.CreatureRelationship(other).Duplicate();
            other.relationships[lance.type.Index] = other.CreatureRelationship(ordinary).Duplicate();
            if (other.TopAncestor().type == CreatureTemplate.Type.Scavenger)
            {
                lance.relationships[other.type.Index] = new CreatureTemplate.Relationship(CreatureTemplate.Relationship.Type.Pack, 1f);
                other.relationships[lance.type.Index] = new CreatureTemplate.Relationship(CreatureTemplate.Relationship.Type.Pack, 1f);
            }
            else if (other.smallCreature)
                lance.relationships[other.type.Index] = new CreatureTemplate.Relationship(CreatureTemplate.Relationship.Type.Ignores, 0f);
        }
    }
}
