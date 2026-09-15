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
    internal bool IsStableForBrace
    {
        get
        {
            if (room == null || !Consious || grabbedBy.Count > 0 || Submersion >= 0.2f ||
                movMode == MovementMode.Climb || movMode == MovementMode.Swim ||
                Mathf.Abs(mainBodyChunk.vel.x) > 6f || Mathf.Abs(mainBodyChunk.vel.y) > 4f) return false;
            // Scavengers stand on procedural limbs; their chest need not touch terrain.
            // Test support below the hips, including one-way floors and slopes.
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
    { base.PlaceInRoom(placeRoom); EnsureBirthLance(); }

    public override void NewRoom(Room newRoom)
    {
        base.NewRoom(newRoom);
        Combat.ResetForRoom();
        Motor?.Reset();
    }

    public override void Update(bool eu)
    {
        EnsureBirthLance();
        if (!Consious || grabbedBy.Count > 0)
            Combat.Tick(new LanceSituation(false, Lance != null, false, ScavengerAI.ViolenceType.None, false,
                999f, false, false));
        base.Update(eu);
        if (room != null) Lance?.SynchronizeGrip(eu);
    }

    private void EnsureBirthLance()
    {
        if (room == null || abstractCreature.state is not LanceScavengerState state || state.GearIssued) return;
        foreach (AbstractPhysicalObject.AbstractObjectStick stick in abstractCreature.stuckObjects)
            if (stick.A is AbstractScavengerLance || stick.B is AbstractScavengerLance)
            { state.GearIssued = true; return; }
        // This flag belongs to birth provisioning, not to the weapon's physical state.
        // Death, theft, another room and another realization cannot issue another lance.
        state.GearIssued = true;
        if (dead || (abstractCreature.spawnData?.IndexOf("disarmed", StringComparison.OrdinalIgnoreCase) ?? -1) >= 0) return;
        var data = new AbstractScavengerLance(room.world, abstractCreature.pos, room.game.GetNewID());
        room.abstractRoom.AddEntity(data);
        data.RealizeInRoom();
        if (data.realizedObject is ScavengerLance lance)
        {
            lance.firstChunk.HardSetPosition(mainBodyChunk.pos);
            if (grasps[0] == null) Grab(lance, 0, 0, Grasp.Shareability.CanOnlyShareWithNonExclusive, 0.5f, false, false);
        }
    }

    public override void InitiateGraphicsModule()
    {
        if (graphicsModule == null) graphicsModule = new LanceScavengerGraphics(this);
        graphicsModule.Reset();
    }

    public override void GraphicsModuleUpdated(bool actuallyViewed, bool eu)
    { base.GraphicsModuleUpdated(actuallyViewed, eu); Lance?.SynchronizeGrip(eu); }

    public override void Violence(BodyChunk source, Vector2? directionAndMomentum, BodyChunk hitChunk,
        Appendage.Pos hitAppendage, DamageType type, float damage, float stunBonus)
    {
        // Do not create a private "recent attacker" hostility path here. Vanilla
        // ScavengerAI/social events own reputation, personal memory and retaliation.
        base.Violence(source, directionAndMomentum, hitChunk, hitAppendage, type, damage, stunBonus);
        if (Combat.State == LanceState.Charge || Combat.State == LanceState.Brace) Combat.Recover(false);
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
        bool forward = state == LanceState.Brace || state == LanceState.Charge || state == LanceState.CloseDefense || state == LanceState.Threaten;
        Vector2 direction;
        if (state == LanceState.Charge) direction = Motor.Direction;
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
        Vector2 position = mainBodyChunk.pos + new Vector2(direction.x * 7f, forward ? -5f : 1f);
        grip = new LanceGrip(position, direction, forward, state == LanceState.Charge && Consious && grabbedBy.Count == 0, Motor.RunUp);
        return Lance == lance && !enteringShortCut.HasValue && !inShortcut;
    }

    void ILanceWielder.LanceImpact(bool wall, float speed, float retainedSpeed)
    {
        if (Combat.State != LanceState.Charge) return;
        Combat.Recover(wall);
        if (wall)
        {
            foreach (BodyChunk chunk in bodyChunks)
                chunk.vel = new Vector2(-Motor.Direction.x * Mathf.Min(4f, speed * 0.2f), Mathf.Max(2f, chunk.vel.y * 0.3f));
            Stun(22);
            if (speed > 18.5f)
                for (int i = 0; i < grasps.Length; i++) if (grasps[i]?.grabbed is ScavengerLance) ReleaseGrasp(i);
        }
        else if (retainedSpeed < 0.25f) Stun(12);
    }
}
