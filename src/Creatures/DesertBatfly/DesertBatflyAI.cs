using RWCustom;
using UnityEngine;
using DryCycle.Thirst;

namespace DryCycle.Creatures.DesertBatfly;

internal sealed class DesertBatflyAI
{
    internal enum Activity { Flight, Observe, Approach, Circle, FakeDive, Dive, Attach, Escape, Cooldown, Roost }
    private readonly DesertBatfly fly;
    internal Activity Mode { get; private set; }
    internal Creature Target { get; private set; }
    private Creature attacker;
    private Creature danger;
    private int memory, retreat, ticks, scan, pursuit, unseen, interest;
    private bool hasSlot;
    private float drainedWater;
    private Vector2 escapeFrom, attachOffset, roost;
    private BodyChunk attachedChunk;
    internal bool PullingUp => Mode == Activity.FakeDive && ticks > DesertBatflyTuning.FakeDivePullUpTicks;
    internal bool FormalAttack => hasSlot && Mode is Activity.Approach or Activity.Circle or Activity.Dive or Activity.Attach;

    internal DesertBatflyAI(DesertBatfly fly) { this.fly = fly; }

    internal void TickMemory()
    {
        if (memory > 0 && --memory == 0) attacker = null;
        if (!fly.Consious || fly.grabbedBy.Count > 0 || fly.inShortcut)
        {
            CancelAttack();
            return;
        }
        if (retreat > 0) retreat--;
    }

    internal void ResetRoom()
    {
        CancelAttack();
        attacker = danger = null;
        memory = retreat = pursuit = unseen = 0;
    }

    internal void Threatened(Creature source)
    {
        if (source != null && source != fly && source is not DesertBatfly)
        {
            attacker = source;
            memory = DesertBatflyTuning.AttackerMemory;
            escapeFrom = source.mainBodyChunk.pos;
        }
        else escapeFrom = fly.mainBodyChunk.pos - Vector2.up * 20f;
        retreat = DesertBatflyTuning.RetreatTicks;
        CancelAttack();
        SetMode(Activity.Escape);
        if (fly.room == null) return;
        foreach (var other in DesertSwarmRoom.For(fly.room).Hive.flies)
        {
            if (other is not DesertBatfly bat || bat == fly ||
                !Custom.DistLess(fly.mainBodyChunk.pos, bat.mainBodyChunk.pos, DesertBatflyTuning.AlarmRadius)) continue;
            // Local, non-recursive alert; neighbours do not acquire a revenge target.
            bat.DesertAI.escapeFrom = escapeFrom;
            bat.DesertAI.retreat = Mathf.Max(bat.DesertAI.retreat, 25);
        }
    }

    internal void CancelAttack()
    {
        hasSlot = false;
        attachedChunk = null;
        Target = null;
        drainedWater = 0f;
        interest = 0;
        SetMode(Activity.Flight);
    }

    private void SetMode(Activity next)
    {
        if (Mode == next) return;
        Mode = next;
        ticks = 0;
    }

    // Called immediately after vanilla FlyAI.Update, before Fly chooses its flight
    // physics. Vanilla still owns navigation, flocking, lure, rain and hive motion.
    internal void Update()
    {
        if (fly.room == null) return;
        ticks++;
        if (fly.Emergence.Active || fly.grabbedBy.Count > 0 || !fly.Consious || fly.inShortcut)
        {
            CancelAttack();
            fly.movMode = Fly.MovementMode.Passive;
            return;
        }
        if (++scan >= 8)
        {
            scan = 0;
            ScanCreatures();
            ScanWeapons();
        }
        if (fly.AI.fleeFromRain || fly.AI.behavior == FlyAI.Behavior.Burrow || fly.AI.luredCounter > 0 || fly.safariControlled)
        {
            CancelAttack();
            return;
        }
        if (danger != null || retreat > 0)
        {
            hasSlot = false;
            attachedChunk = null;
            Target = null;
            SetMode(Activity.Escape);
            if (danger != null) escapeFrom = danger.mainBodyChunk.pos;
            Steer(fly.mainBodyChunk.pos + Custom.DirVec(escapeFrom, fly.mainBodyChunk.pos) * 160f + Vector2.up * 50f, 8f);
            return;
        }
        if (fly.DesertState.Cooldown > 0 && Mode != Activity.Attach)
        {
            CancelAttack();
            SetMode(Activity.Cooldown);
            return;
        }
        if (Mode == Activity.Escape) SetMode(Activity.Flight);
        if (!fly.Personality.Aggressive)
        {
            UpdateRoost();
            return;
        }
        if (!Valid(Target))
        {
            CancelAttack();
            if (memory > 0 && Valid(attacker) && CanHarass(attacker)) Target = attacker;
            if (Target == null) { UpdateRoost(); return; }
            SetMode(Activity.Observe);
        }
        if (!fly.room.VisualContact(fly.mainBodyChunk.pos, Target.mainBodyChunk.pos)) unseen++;
        else unseen = 0;
        if (++interest > DesertBatflyTuning.InterestTicks || unseen > 35 || !Custom.DistLess(fly.mainBodyChunk.pos, Target.mainBodyChunk.pos, 430f))
        {
            Finish(false);
            return;
        }
        Vector2 center = Target.mainBodyChunk.pos;
        float distance = Vector2.Distance(fly.mainBodyChunk.pos, center);
        if (Mode is Activity.Flight or Activity.Cooldown or Activity.Roost) SetMode(Activity.Observe);
        switch (Mode)
        {
            case Activity.Observe:
                Steer(center + Orbit(150f, 90f), 4.5f);
                if (ticks > DesertBatflyTuning.ObserveTicks)
                {
                    bool thirsty = fly.DesertState.Thirst > DesertBatflyTuning.AttackThirst;
                    bool counter = Target == attacker && memory > 0 && fly.DesertState.Thirst > DesertBatflyTuning.CounterThirst;
                    if (Random.value < DesertBatflyTuning.FakeDiveChance || !(thirsty || counter)) SetMode(Activity.FakeDive);
                    else if (AcquireSlot()) SetMode(Activity.Approach);
                    else ticks = DesertBatflyTuning.ObserveTicks / 2;
                }
                break;
            case Activity.Approach:
                Steer(center + Vector2.up * 100f, 6f);
                if (ticks > DesertBatflyTuning.ApproachTicks || distance < 110f) SetMode(Activity.Circle);
                break;
            case Activity.Circle:
                Steer(center + Orbit(95f, 65f), 6.5f);
                if (ticks > DesertBatflyTuning.CircleTicks) SetMode(Activity.Dive);
                break;
            case Activity.FakeDive:
                // First half matches Dive; pull up before contact, never drain.
                if (distance < 52f || ticks > DesertBatflyTuning.FakeDivePullUpTicks) ticks = Mathf.Max(DesertBatflyTuning.FakeDivePullUpTicks + 1, ticks);
                Steer(PullingUp ? center + Vector2.up * 160f + Custom.DirVec(center, fly.mainBodyChunk.pos) * 80f : center, PullingUp ? 10f : 12f);
                if (ticks > DesertBatflyTuning.FakeDiveTicks) SetMode(Activity.Observe);
                break;
            case Activity.Dive:
                Steer(center + Target.mainBodyChunk.vel * 1.5f, 12f);
                BodyChunk contact = FindContact();
                if (contact != null && unseen == 0)
                {
                    attachedChunk = contact;
                    attachOffset = Custom.DirVec(contact.pos, fly.mainBodyChunk.pos) * (contact.rad + fly.mainBodyChunk.rad * 0.5f);
                    drainedWater = 0f;
                    SetMode(Activity.Attach);
                }
                else if (ticks > DesertBatflyTuning.DiveTicks) Finish(false);
                break;
            case Activity.Attach:
                fly.movMode = Fly.MovementMode.Passive;
                if (ticks >= DesertBatflyTuning.AttachTicks) Finish(drainedWater > 0.001f);
                break;
        }
    }

    private BodyChunk FindContact()
    {
        foreach (BodyChunk chunk in Target.bodyChunks)
            if (Custom.DistLess(chunk.pos, fly.mainBodyChunk.pos, chunk.rad + fly.mainBodyChunk.rad + 3f)) return chunk;
        return null;
    }

    internal void AfterPhysics(bool eu)
    {
        if (Mode != Activity.Attach) return;
        if (!Valid(Target) || attachedChunk == null || !fly.Consious || fly.grabbedBy.Count > 0 ||
            fly.inShortcut || Target.inShortcut || !hasSlot ||
            !Custom.DistLess(fly.mainBodyChunk.pos, attachedChunk.pos, 70f))
        {
            Finish(drainedWater > 0.001f);
            return;
        }
        Vector2 position = attachedChunk.pos + attachOffset;
        if (fly.room.GetTile(position).Solid || !fly.room.VisualContact(fly.mainBodyChunk.pos, position))
        {
            Finish(drainedWater > 0.001f);
            return;
        }
        fly.mainBodyChunk.MoveFromOutsideMyUpdate(eu, position);
        fly.mainBodyChunk.vel = attachedChunk.vel;

        // Stay attached for several seconds. Fluid transfer is spread across the
        // middle of that window rather than being a single hidden -30 event.
        if (ticks >= DesertBatflyTuning.DrainStartTicks &&
            ticks <= DesertBatflyTuning.DrainEndTicks &&
            drainedWater < DesertBatflyTuning.AttackWater)
        {
            float drainTicks = DesertBatflyTuning.DrainEndTicks - DesertBatflyTuning.DrainStartTicks + 1f;
            float amount = Mathf.Min(
                DesertBatflyTuning.AttackWater - drainedWater,
                DesertBatflyTuning.AttackWater / drainTicks);
            bool transferred = true;

            if (Target is Player player)
            {
                transferred = ThirstStore.RemoveRuntime(player, amount / ThirstConstants.WaterValuePerPip);
                // The existing thirst HUD already follows the true runtime value
                // continuously. Keeping it open here makes the slow loss visible.
                player.showKarmaFoodRainTime = Mathf.Max(
                    player.showKarmaFoodRainTime,
                    ThirstConstants.HydrationLossHudHoldFrames);
            }

            if (transferred)
            {
                drainedWater += amount;
                fly.DesertState.Thirst = Mathf.Max(
                    0f,
                    fly.DesertState.Thirst - DesertBatflyTuning.DrainRelief *
                    (amount / DesertBatflyTuning.AttackWater));
                fly.DesertState.Cooldown = DesertBatflyTuning.Cooldown;
            }
        }
    }

    private void Finish(bool success)
    {
        Vector2 from = Target?.mainBodyChunk.pos ?? fly.mainBodyChunk.pos - Vector2.up;
        CancelAttack();
        fly.DesertState.Cooldown = Mathf.Max(fly.DesertState.Cooldown, success ? DesertBatflyTuning.Cooldown : DesertBatflyTuning.FailedCooldown);
        escapeFrom = from;
        retreat = 75;
        fly.mainBodyChunk.vel += Custom.DirVec(from, fly.mainBodyChunk.pos) * 5f + Vector2.up * 3f;
        SetMode(Activity.Escape);
    }

    private bool AcquireSlot()
    {
        int count = 0;
        foreach (AbstractCreature abstractCreature in fly.room.abstractRoom.creatures)
            if (abstractCreature.realizedCreature is DesertBatfly other && other != fly && other.room == fly.room &&
                other.Consious && other.grabbedBy.Count == 0 && other.DesertAI.Target == Target && other.DesertAI.FormalAttack) count++;
        hasSlot = count < DesertBatflyTuning.AttackSlots;
        return hasSlot;
    }

    private bool Valid(Creature creature) => creature != null && !creature.dead && !creature.slatedForDeletetion &&
        creature.room == fly.room && !creature.inShortcut && creature.grabbedBy.Count == 0 &&
        (creature.abstractCreature.rippleLayer == fly.abstractCreature.rippleLayer || creature.abstractCreature.rippleBothSides || fly.abstractCreature.rippleBothSides);

    private bool CanHarass(Creature creature)
    {
        if (creature == fly || creature is DesertBatfly || !Valid(creature)) return false;
        if (creature is Player) return true;
        var relation = fly.Template.CreatureRelationship(creature.Template);
        var reverse = creature.Template.CreatureRelationship(fly.Template);
        return creature.TotalMass <= DesertBatflyTuning.LightTargetMass &&
            relation.type != CreatureTemplate.Relationship.Type.Afraid &&
            reverse.type != CreatureTemplate.Relationship.Type.Eats && reverse.type != CreatureTemplate.Relationship.Type.Attacks;
    }

    private void ScanCreatures()
    {
        danger = null;
        Creature candidate = null;
        float closest = DesertBatflyTuning.SightRange;
        foreach (AbstractCreature abs in fly.room.abstractRoom.creatures)
        {
            Creature creature = abs.realizedCreature;
            if (creature == fly || creature is DesertBatfly || !Valid(creature)) continue;
            float distance = Vector2.Distance(fly.mainBodyChunk.pos, creature.mainBodyChunk.pos);
            if (distance > DesertBatflyTuning.SightRange || !fly.room.VisualContact(fly.mainBodyChunk.pos, creature.mainBodyChunk.pos)) continue;
            var relation = fly.Template.CreatureRelationship(creature.Template);
            var reverse = creature.Template.CreatureRelationship(fly.Template);
            bool predator = creature is not Player && (relation.type == CreatureTemplate.Relationship.Type.Afraid ||
                reverse.type == CreatureTemplate.Relationship.Type.Eats || reverse.type == CreatureTemplate.Relationship.Type.Attacks);
            if (predator && distance < Mathf.Lerp(90f, 260f, Mathf.Clamp01(creature.TotalMass))) danger = creature;
            if (creature is Player && distance < 110f)
            {
                float closing = Vector2.Dot(creature.mainBodyChunk.vel, Custom.DirVec(creature.mainBodyChunk.pos, fly.mainBodyChunk.pos));
                if (closing > 2.8f) pursuit += 8;
                else pursuit = Mathf.Max(0, pursuit - 4);
                if (pursuit >= 24) { Threatened(creature); pursuit = 0; }
            }
            if (distance < closest && CanHarass(creature)) { closest = distance; candidate = creature; }
        }
        if (Target == null && fly.Personality.Aggressive && fly.DesertState.Cooldown == 0 && retreat == 0 &&
            (fly.DesertState.Thirst > DesertBatflyTuning.ObserveThirst || memory > 0))
        {
            Target = Valid(attacker) && CanHarass(attacker) ? attacker : candidate;
            if (Target != null) SetMode(Activity.Observe);
        }
    }

    private void ScanWeapons()
    {
        foreach (var layer in fly.room.physicalObjects)
        foreach (PhysicalObject obj in layer)
        {
            if (obj is not Weapon weapon || weapon.thrownBy == fly) continue;
            if (weapon is Spear && weapon.grabbedBy.Count > 0 &&
                Custom.DistLess(weapon.firstChunk.pos, fly.mainBodyChunk.pos, 65f) &&
                (weapon.firstChunk.pos - weapon.firstChunk.lastPos).sqrMagnitude > 36f)
            {
                Threatened(weapon.grabbedBy[0].grabber);
                continue;
            }
            if (weapon.mode != Weapon.Mode.Thrown) continue;
            Vector2 delta = fly.mainBodyChunk.pos - weapon.firstChunk.pos;
            Vector2 velocity = weapon.firstChunk.vel;
            float time = Mathf.Clamp(Vector2.Dot(delta, velocity) / Mathf.Max(1f, velocity.sqrMagnitude), 0f, 5f);
            if ((delta - velocity * time).sqrMagnitude < 32f * 32f && delta.sqrMagnitude < 170f * 170f)
            { Threatened(weapon.thrownBy); break; }
        }
    }

    private Vector2 Orbit(float width, float height)
    {
        float angle = (fly.room.game.clock + (fly.Personality.VisualSeed & 1023)) * 0.025f;
        return new Vector2(Mathf.Cos(angle) * width, 55f + Mathf.Sin(angle) * height * 0.45f);
    }

    private void Steer(Vector2 goal, float speed)
    {
        fly.LoseAllGrasps();
        fly.burrowOrHangSpot = null;
        fly.AI.behavior = FlyAI.Behavior.Idle;
        fly.AI.followingDijkstraMap = -1;
        fly.movMode = Fly.MovementMode.BatFlight;
        Vector2 direction = Custom.DirVec(fly.mainBodyChunk.pos, goal);
        if (fly.room.GetTile(fly.mainBodyChunk.pos + direction * 25f).Solid ||
            (fly.room.terrain != null && fly.room.terrain.Contains(fly.mainBodyChunk.pos + direction * 25f)))
        {
            goal = fly.mainBodyChunk.pos + Vector2.up * 70f;
            speed = 4f;
        }
        fly.AI.localGoal = goal;
        fly.mainBodyChunk.vel = Vector2.Lerp(fly.mainBodyChunk.vel, Custom.DirVec(fly.mainBodyChunk.pos, goal) * speed, 0.22f);
    }

    private void UpdateRoost()
    {
        if (Mode == Activity.Roost)
        {
            if (ticks > (fly.Personality.Aggressive ? DesertBatflyTuning.AggressiveRoostTicks : DesertBatflyTuning.DocileRoostTicks)) { SetMode(Activity.Flight); return; }
            fly.burrowOrHangSpot = roost;
            fly.movMode = Fly.MovementMode.Hang;
            fly.mainBodyChunk.vel *= 0.5f;
            return;
        }
        if (scan != 0 || Random.value > (fly.Personality.Aggressive ? DesertBatflyTuning.AggressiveRoostChance : DesertBatflyTuning.DocileRoostChance)) return;
        if (fly.room.GetTile(fly.mainBodyChunk.pos + Vector2.up * 20f).Solid)
        {
            roost = fly.mainBodyChunk.pos;
            SetMode(Activity.Roost);
        }
    }
}
