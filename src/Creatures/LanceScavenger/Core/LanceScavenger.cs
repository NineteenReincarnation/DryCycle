using System;
using DryCycle.Items.ScavengerLance;
using RWCustom;
using UnityEngine;

namespace DryCycle.Creatures.LanceScavenger;

internal sealed class LanceScavenger : Scavenger, ILanceWielder
{
    internal LanceCombatState Combat { get; } = new();
    internal LanceMotor Motor { get; }
    internal LanceScavengerAI Brain => AI as LanceScavengerAI;
    internal LanceScavenger(AbstractCreature creature, World world) : base(creature, world)
    { Motor = new LanceMotor(this); }

    internal ScavengerLance Lance
    {
        get
        {
            if (grasps != null)
                foreach (Grasp grasp in grasps) if (grasp?.grabbed is ScavengerLance lance) return lance;
            return null;
        }
    }

    internal Spear SidearmSpear
    {
        get
        {
            if (grasps == null) return null;
            foreach (Grasp grasp in grasps)
                if (grasp?.grabbed is Spear spear && IsOrdinarySpear(spear)) return spear;
            return null;
        }
    }

    internal bool SidearmInPrimary => grasps != null && grasps.Length > 0 &&
        grasps[0]?.grabbed is Spear spear && IsOrdinarySpear(spear);

    private static bool IsOrdinarySpear(Spear spear) =>
        spear?.abstractPhysicalObject is AbstractSpear data && !data.explosive && !data.electric && !data.needle;

    internal bool IsStableForBrace
    {
        get
        {
            if (room == null || !Consious || grabbedBy.Count > 0 || Submersion >= 0.2f ||
                movMode == MovementMode.Climb || movMode == MovementMode.Swim ||
                Mathf.Abs(mainBodyChunk.vel.x) > 6f || Mathf.Abs(mainBodyChunk.vel.y) > 4f) return false;
            if (bodyChunks[0].ContactPoint.y < 0 || bodyChunks[1].ContactPoint.y < 0) return true;
            for (float below = bodyChunks[1].rad; below <= bodyChunks[1].rad + 24f; below += 6f)
            {
                Room.Tile tile = room.GetTile(bodyChunks[1].pos - Vector2.up * below);
                if (tile.Solid || tile.Terrain == Room.Tile.TerrainType.Floor || tile.Terrain == Room.Tile.TerrainType.Slope)
                    return true;
            }
            return false;
        }
    }

    public override void PlaceInRoom(Room placeRoom)
    { base.PlaceInRoom(placeRoom); EnsureBirthGear(); }

    public override void NewRoom(Room newRoom)
    {
        base.NewRoom(newRoom);
        Combat.ResetForRoom();
        Motor?.Reset();
    }

    public override void Update(bool eu)
    {
        EnsureBirthGear();
        EnsureWeaponSlots(Brain?.SidearmDrawn == true);
        if (!Consious || grabbedBy.Count > 0)
            Combat.Tick(new LanceSituation(false, Lance != null, SidearmSpear != null, false,
                ScavengerAI.ViolenceType.None, false, 999f, false));
        base.Update(eu);

        bool grounded = bodyChunks[0].ContactPoint.y < 0 || bodyChunks[1].ContactPoint.y < 0;
        if (Combat.State == LanceState.Charge)
        {
            if (grounded) Combat.MarkLanding();
            else Combat.MarkAirborne();
        }
        else if (Combat.State == LanceState.FollowUpThrow && grounded)
        {
            Combat.MarkLanding();
        }

        if (room != null) Lance?.SynchronizeGrip(eu);
    }

    private void EnsureBirthGear()
    {
        if (room == null || abstractCreature.state is not LanceScavengerState state) return;
        bool suppress = dead || (abstractCreature.spawnData?.IndexOf("disarmed", StringComparison.OrdinalIgnoreCase) ?? -1) >= 0;

        if (!state.SidearmIssued)
        {
            bool alreadyHasSidearm = false;
            foreach (AbstractPhysicalObject.AbstractObjectStick stick in abstractCreature.stuckObjects)
                if ((stick.A is AbstractSpear a && !a.explosive && !a.electric && !a.needle) ||
                    (stick.B is AbstractSpear b && !b.explosive && !b.electric && !b.needle))
                { alreadyHasSidearm = true; break; }
            state.SidearmIssued = true;
            if (!suppress && !alreadyHasSidearm)
            {
                var data = new AbstractSpear(room.world, null, abstractCreature.pos, room.game.GetNewID(), explosive: false);
                room.abstractRoom.AddEntity(data);
                data.RealizeInRoom();
                if (data.realizedObject is Spear spear)
                {
                    spear.firstChunk.HardSetPosition(mainBodyChunk.pos);
                    int slot = PreferredFreeGrasp(1);
                    if (slot >= 0) Grab(spear, slot, 0, Grasp.Shareability.CanOnlyShareWithNonExclusive, 0.5f, false, false);
                }
            }
        }

        if (!state.GearIssued)
        {
            bool alreadyHasLance = false;
            foreach (AbstractPhysicalObject.AbstractObjectStick stick in abstractCreature.stuckObjects)
                if (stick.A is AbstractScavengerLance || stick.B is AbstractScavengerLance)
                { alreadyHasLance = true; break; }
            state.GearIssued = true;
            if (!suppress && !alreadyHasLance)
            {
                var data = new AbstractScavengerLance(room.world, abstractCreature.pos, room.game.GetNewID());
                room.abstractRoom.AddEntity(data);
                data.RealizeInRoom();
                if (data.realizedObject is ScavengerLance lance)
                {
                    lance.firstChunk.HardSetPosition(mainBodyChunk.pos);
                    int slot = PreferredFreeGrasp(0);
                    if (slot >= 0) Grab(lance, slot, 0, Grasp.Shareability.CanOnlyShareWithNonExclusive, 0.5f, false, false);
                }
            }
        }

        EnsureWeaponSlots();
    }

    private int PreferredFreeGrasp(int preferred)
    {
        if (grasps == null || grasps.Length == 0) return -1;
        if (preferred >= 0 && preferred < grasps.Length && grasps[preferred] == null) return preferred;
        for (int i = 0; i < grasps.Length; i++) if (grasps[i] == null) return i;
        return -1;
    }

    internal void EnsureWeaponSlots(bool sidearmPrimary = false)
    {
        if (grasps == null || grasps.Length < 2) return;
        int sidearm = -1, lance = -1;
        for (int i = 0; i < grasps.Length; i++)
        {
            if (grasps[i]?.grabbed is ScavengerLance) lance = i;
            else if (grasps[i]?.grabbed is Spear spear && IsOrdinarySpear(spear)) sidearm = i;
        }

        // Identity-weapon invariant: as long as the custom lance exists, an ordinary spear may only
        // become grasp 0 for the explicit post-charge FollowUpThrow. The old generic fallback let a
        // temporary lane failure/cooldown draw the sidearm before the scavenger had ever attempted
        // its signature attack, effectively turning the new creature back into an ordinary scavenger.
        if (sidearmPrimary && lance >= 0 && Combat.State != LanceState.FollowUpThrow)
            sidearmPrimary = false;

        if (sidearmPrimary && sidearm >= 0)
        {
            if (sidearm != 0) SwitchGrasps(sidearm, 0);
            return;
        }

        if (lance > 0)
            SwitchGrasps(lance, 0);
        else if (lance < 0 && sidearm > 0 && grasps[0] == null)
            SwitchGrasps(sidearm, 0);
    }

    public override void InitiateGraphicsModule()
    {
        if (graphicsModule == null) graphicsModule = new LanceScavengerGraphics(this);
        graphicsModule.Reset();
    }

    public override void GraphicsModuleUpdated(bool actuallyViewed, bool eu)
    {
        base.GraphicsModuleUpdated(actuallyViewed, eu);
        Lance?.SynchronizeGrip(eu);
        SynchronizeSidearmCarry(eu);
    }

    private void SynchronizeSidearmCarry(bool eu)
    {
        Spear spear = SidearmSpear;
        if (spear == null || SidearmInPrimary || Combat.State == LanceState.FollowUpThrow) return;
        if (Combat.State != LanceState.Backstep && Combat.State != LanceState.Brace && Combat.State != LanceState.Charge &&
            Combat.State != LanceState.CloseDefense) return;
        float face = Brain?.Target != null ? Mathf.Sign(Brain.Target.mainBodyChunk.pos.x - mainBodyChunk.pos.x) : Motor.Direction.x;
        if (face == 0f) face = 1f;
        Vector2 direction = new Vector2(-face * 0.45f, 0.9f).normalized;
        spear.firstChunk.MoveFromOutsideMyUpdate(eu, bodyChunks[1].pos - direction * 3f);
        spear.rotation = direction;
        spear.setRotation = direction;
        spear.firstChunk.vel = mainBodyChunk.vel;
    }

    public override void Violence(BodyChunk source, Vector2? directionAndMomentum, BodyChunk hitChunk,
        Appendage.Pos hitAppendage, DamageType type, float damage, float stunBonus)
    {
        base.Violence(source, directionAndMomentum, hitChunk, hitAppendage, type, damage, stunBonus);
        if (Combat.State == LanceState.Charge || Combat.State == LanceState.Brace || Combat.State == LanceState.Backstep ||
            Combat.State == LanceState.FollowUpThrow)
            Combat.Recover(false);
    }

    public override void TerrainImpact(int chunk, IntVector2 direction, float speed, bool firstContact)
    {
        base.TerrainImpact(chunk, direction, speed, firstContact);
        if (firstContact && direction.x != 0 && speed > 7f && Combat.State == LanceState.Charge)
            ((ILanceWielder)this).LanceImpact(true, speed, 0.12f);
    }

    bool ILanceWielder.TryGetLanceGrip(ScavengerLance lance, out LanceGrip grip)
    {
        LanceState state = Combat.State;
        bool forward = state == LanceState.Backstep || state == LanceState.Brace || state == LanceState.Charge ||
            state == LanceState.CloseDefense || state == LanceState.Threaten;
        Vector2 direction;
        if (state == LanceState.Charge)
        {
            direction = Motor.LanceDirection;
        }
        else if (state == LanceState.Brace && Brain?.AimSolution.Valid == true)
        {
            direction = Brain.AimSolution.LanceDirection;
        }
        else if (forward && Brain?.Target != null)
        {
            Vector2 delta = Brain.Target.mainBodyChunk.pos - mainBodyChunk.pos;
            direction = new Vector2(Mathf.Sign(delta.x), Mathf.Clamp(delta.y / Mathf.Max(40f, Mathf.Abs(delta.x)), -0.25f, 0.3f)).normalized;
        }
        else
        {
            float face = Mathf.Abs(mainBodyChunk.vel.x) > 0.3f ? Mathf.Sign(mainBodyChunk.vel.x) : Mathf.Sign(lookPoint.x - mainBodyChunk.pos.x);
            direction = new Vector2(face == 0f ? 1f : face, movMode == MovementMode.Climb ? 2.5f : 0.9f).normalized;
        }

        // Enhanced hand-held lance: the hand/grip is an anchor, not a point that slides around the
        // body as the weapon rotates. Counter-sweep may turn the blade through 80/120 degrees while
        // the scavenger keeps flying forward; the grip therefore follows facing/charge direction,
        // never direction.x from the rotating lance itself.
        float gripFacing;
        if (state == LanceState.Charge)
            gripFacing = Mathf.Sign(Motor.Direction.x);
        else if (forward && Brain?.Target != null)
            gripFacing = Mathf.Sign(Brain.Target.mainBodyChunk.pos.x - mainBodyChunk.pos.x);
        else if (Mathf.Abs(mainBodyChunk.vel.x) > 0.3f)
            gripFacing = Mathf.Sign(mainBodyChunk.vel.x);
        else
            gripFacing = Mathf.Sign(direction.x);
        if (gripFacing == 0f) gripFacing = 1f;

        Vector2 position = mainBodyChunk.pos + new Vector2(gripFacing * 7f, forward ? -5f : 1f);
        grip = new LanceGrip(position, direction, forward,
            state == LanceState.Charge && Consious && grabbedBy.Count == 0,
            Motor.RunUp, state == LanceState.Charge && Motor.CounterSweepActive,
            state == LanceState.Brace);
        return Lance == lance && !enteringShortCut.HasValue && !inShortcut;
    }

    void ILanceWielder.LanceImpact(bool wall, float speed, float retainedSpeed)
    {
        if (Combat.State != LanceState.Charge) return;
        if (wall)
        {
            Combat.FinishCharge(true);
            foreach (BodyChunk chunk in bodyChunks)
                chunk.vel = new Vector2(-Motor.Direction.x * Mathf.Min(4f, speed * 0.2f), Mathf.Max(2f, chunk.vel.y * 0.3f));
            Stun(22);
            if (speed > 18.5f)
                for (int i = 0; i < grasps.Length; i++) if (grasps[i]?.grabbed is ScavengerLance) ReleaseGrasp(i);
        }
        else if (retainedSpeed < 0.25f)
        {
            Stun(12);
        }
    }
}
