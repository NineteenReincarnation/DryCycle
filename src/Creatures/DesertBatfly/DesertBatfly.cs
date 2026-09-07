using DryCycle.Thirst;
using UnityEngine;

namespace DryCycle.Creatures.DesertBatfly;

internal sealed class DesertBatfly : Fly, IPlayerEdible
{
    private const float RockSurvivalHealthFloor = 0.01f;

    private DesertBatflyInjury injury;
    internal DesertBatflyInjury Injury => injury ??= new DesertBatflyInjury(this);
    internal readonly DesertBatflyAI DesertAI;
    internal readonly DesertBatflyEmergence Emergence;
    internal DesertBatflyState DesertState => (DesertBatflyState)State;
    internal DesertBatflyPersonality Personality => DesertState.Personality;
    internal World world => abstractCreature?.world;

    private int mealFood = 2;
    private int socialSampleTicks;
    private bool runningVanillaUpdate;
    private bool resolvingRockViolence;
    private Player playerHolder;

    private float sandStruggleMeter, sandSpitThreshold;
    private int sandSpitCooldown, sandSpitWindup, sandSpitCycle;

    internal bool SandSpitWindingUp => sandSpitWindup > 0;
    internal int SandSpitWindupRemaining => sandSpitWindup;

    int IPlayerEdible.FoodPoints => mealFood;

    internal DesertBatfly(AbstractCreature creature, World world) : base(creature, world)
    {
        mainBodyChunk.rad = DesertBatflyTuning.Radius * Personality.Size;
        mainBodyChunk.mass = DesertBatflyTuning.Mass * Personality.Size;
        airFriction = 0.975f;
        bites = DesertState.Bites;
        if (DesertState.MealConsumed) eaten = 1;
        DesertAI = new DesertBatflyAI(this);
        Emergence = new DesertBatflyEmergence(this);
        PrepareNextSandThreshold();
    }

    public override void InitiateGraphicsModule()
    {
        if (graphicsModule == null) graphicsModule = new DesertBatflyGraphics(this);
    }

    public override void NewRoom(Room newRoom)
    {
        injury?.ClearTransient();
        DesertAI?.ResetRoom();
        base.NewRoom(newRoom);
    }

    public override void PlaceInRoom(Room placeRoom)
    {
        base.PlaceInRoom(placeRoom);
        if (!DesertState.InHive || dead || placeRoom.hives.Length == 0) return;
        var hive = DesertSwarmRoom.For(placeRoom).Hive;
        if (!hive.inHive.Contains(this)) hive.MoveFlyToHive(this);
    }

    public override void Update(bool eu)
    {
        Injury.Tick();
        Vector2 previousFlightVelocity = mainBodyChunk?.vel ?? Vector2.zero;
        TrackPlayerRelease();
        if (sandSpitCooldown > 0) sandSpitCooldown--;
        if (!dead)
        {
            DesertState.TickTrauma();
            DesertState.TickGrief();
            if (++socialSampleTicks >= 180)
            {
                socialSampleTicks = 0;
                DB_SocialBond.SampleChain(this);
            }
        }

        if (room == null)
        {
            base.Update(eu);
            return;
        }

        UpdateHeldSandStruggle();

        DesertState.Thirst = Mathf.Clamp01(
            DesertState.Thirst + (dead ? 0f : DesertBatflyTuning.ThirstPerTick));
        if (DesertState.Cooldown > 0) DesertState.Cooldown--;
        DesertAI.TickMemory();
        if (!dead)
            DesertBatflyIntimidation.UpdateState(this);

        Room currentRoom = room;
        FliesRoomAI original = currentRoom.fliesRoomAi;
        var colony = DesertSwarmRoom.For(currentRoom);
        currentRoom.fliesRoomAi = colony.Hive;
        colony.Hive.AddFly(this);
        runningVanillaUpdate = true;
        try
        {
            base.Update(eu);
        }
        finally
        {
            runningVanillaUpdate = false;
            currentRoom.fliesRoomAi = original;
        }

        // Vengeance state/fear was refreshed before base.Update so the R3 arbiter saw the
        // current facts. Movement itself can only have run through DB_VengeanceExecutor.
        bool extremeVengeance = !dead && DesertBatflyIntimidation.IsExtremeVengeanceActive(this);
        if (extremeVengeance) DesertAI.CancelAttack();

        if (room == null) return;
        Emergence.Update(eu);
        if (!extremeVengeance)
            DesertAI.Combat.AfterPhysics(eu);
        DB_FlightMotor.ApplyPostPhysics(this, previousFlightVelocity);
    }

    private void UpdateHeldSandStruggle()
    {
        if (playerHolder == null || !Personality.CanSandSpit || dead || !Consious ||
            inShortcut || playerHolder.room != room)
        {
            sandSpitWindup = 0;
            sandStruggleMeter = Mathf.Max(0f, sandStruggleMeter - 0.02f);
            return;
        }

        if (sandSpitWindup > 0)
        {
            sandSpitWindup--;
            if (sandSpitWindup == 0)
                EmitSandSpit();
            return;
        }

        if (sandSpitCooldown > 0) return;

        float movement = Mathf.Clamp01(playerHolder.mainBodyChunk.vel.magnitude / 8f);
        sandStruggleMeter += Personality.SandSpitMeterRate +
            movement * DesertBatflyTuning.SandSpitMovementBonus;

        if (sandStruggleMeter < sandSpitThreshold) return;
        sandStruggleMeter = 0f;
        sandSpitWindup = DesertBatflyTuning.SandSpitWindupTicks;
    }

    private void EmitSandSpit()
    {
        if (room == null || playerHolder == null || dead || !Consious ||
            !Personality.CanSandSpit) return;

        int seed = unchecked(Personality.VisualSeed ^ (sandSpitCycle * 1103515245));
        DB_SandBurst.Emit(
            room,
            this,
            playerHolder,
            Personality.SandSpitIntensity,
            seed);

        float cooldownT = Stable01(0x45D9F3B + sandSpitCycle * 17);
        sandSpitCooldown = Mathf.RoundToInt(Mathf.Lerp(
            DesertBatflyTuning.SandSpitCooldownMaxTicks,
            DesertBatflyTuning.SandSpitCooldownMinTicks,
            Mathf.Clamp01(Personality.SandSpitDrive * 0.7f + cooldownT * 0.3f)));

        sandSpitCycle++;
        PrepareNextSandThreshold();
    }

    private void PrepareNextSandThreshold()
    {
        float t = Stable01(0x1F123BB5 + sandSpitCycle * 31);
        sandSpitThreshold = Mathf.Lerp(
            DesertBatflyTuning.SandSpitThresholdMin,
            DesertBatflyTuning.SandSpitThresholdMax,
            t);
    }

    private float Stable01(int salt)
    {
        unchecked
        {
            uint x = (uint)(Personality.VisualSeed * 1103515245 + salt * 12345);
            x ^= x >> 16;
            x *= 0x7FEB352Du;
            x ^= x >> 15;
            x *= 0x846CA68Bu;
            x ^= x >> 16;
            return (x & 0x00FFFFFFu) / 16777215f;
        }
    }

    private void BeginPlayerHold(Player player)
    {
        playerHolder = player;
        sandStruggleMeter = 0f;
        sandSpitWindup = 0;
        sandSpitCooldown = Mathf.Max(sandSpitCooldown, 18);
        PrepareNextSandThreshold();
    }

    private void TrackPlayerRelease()
    {
        if (playerHolder == null) return;

        bool stillHeld = false;
        for (int i = 0; i < grabbedBy.Count; i++)
        {
            if (grabbedBy[i]?.grabber == playerHolder)
            {
                stillHeld = true;
                break;
            }
        }
        if (stillHeld) return;

        Player releasedBy = playerHolder;
        playerHolder = null;
        sandStruggleMeter = 0f;
        sandSpitWindup = 0;
        if (!dead && !slatedForDeletetion)
            DesertAI.PlayerReleased(releasedBy, mainBodyChunk.vel.magnitude);
    }

    public override void Stun(int ticks)
    {
        if (!dead && ticks >= 40) Injury.ApplyShock(Mathf.Clamp(ticks / 300f, 0.08f, 0.65f));
        base.Stun(ticks);
    }

    public override void Violence(
        BodyChunk source,
        Vector2? momentum,
        BodyChunk hitChunk,
        Appendage.Pos appendage,
        DamageType type,
        float damage,
        float stunBonus)
    {
        if (!RippleViolenceCheck(source)) return;
        Creature attacker = source?.owner as Creature ?? (source?.owner as Weapon)?.thrownBy;
        bool rockHit = source?.owner is Rock;

        // Rocks now deal their ordinary blunt health damage and therefore participate in
        // Injury/Trauma. They are deliberately excluded from lethal attribution because
        // a rock impact itself is never allowed to perform the live -> dead transition.
        bool supportedLethalThreat = !rockHit && damage > 0f &&
            DesertBatflyIntimidation.IsSupportedLethalThreat(attacker);

        // Preserve an intact Fly chain until Die() has captured its witnesses. A hit
        // that does not kill receives the ordinary Threatened transition afterwards.
        if (!supportedLethalThreat)
            DesertAI.Threatened(attacker, true);

        float healthBefore = DesertState.health;
        resolvingRockViolence = rockHit;
        try
        {
            base.Violence(source, momentum, hitChunk, appendage, type, damage, stunBonus);
        }
        finally
        {
            // Creature.Violence can call Die() both through quick-death rolls and the
            // instant-death damage limit. Die() is suppressed only while this exact rock
            // Violence call is resolving; afterwards the bat remains fully killable by
            // spears, bites, drowning and every other ordinary cause.
            if (rockHit && !dead && DesertState.health < RockSurvivalHealthFloor)
                DesertState.health = RockSurvivalHealthFloor;
            resolvingRockViolence = false;
        }

        Injury.OnHealthLoss(healthBefore, type, source?.owner, attacker, momentum);

        if (supportedLethalThreat && !dead && !slatedForDeletetion)
            DesertAI.Threatened(attacker, true);
    }

    public override void Grabbed(Grasp grasp)
    {
        if (grasp?.grabber is Player player)
        {
            if (playerHolder != player)
            {
                BeginPlayerHold(player);
                DesertAI.PlayerGrabbed(player);
            }
        }
        else if (grasp?.grabber != null && grasp.grabber is not Fly)
        {
            // Capture fear/signals are semantic-event consumers. Creature retains only its
            // immediate native danger response; DB_EventHub dedupes tongue -> grasp transfer.
            DesertAI.Threatened(grasp.grabber, true);
        }

        DesertAI.CancelAttack();
        Emergence.Cancel();
        base.Grabbed(grasp);
    }

    void IPlayerEdible.BitByPlayer(Grasp grasp, bool eu)
    {
        if (DesertState.MealConsumed || bites <= 0 || grasp?.grabber is not Player player) return;
        if (SlugcatStats.NourishmentOfObjectEaten(player.SlugCatClass, this) < 0) return;

        // Vanilla Fly.BitByPlayer performs the first live -> dead transition without a
        // Creature.Violence damage fact. Record this explicit action before vanilla so the
        // canonical MortalityEvent can attribute consumption without treating every grasp
        // as a kill.
        if (!dead)
            DB_EventHub.RecordConsumptionAttribution(this, player);

        mealFood = SlugcatStats.NourishmentOfObjectEaten(player.SlugCatClass, this) == 4 ? 1 : 2;
        base.BitByPlayer(grasp, eu);
        mealFood = 2;

        DesertState.Bites = bites;
        if (bites != 0 || DesertState.MealConsumed) return;
        DesertState.MealConsumed = true;
        ThirstStore.RemoveRuntime(
            player,
            DesertBatflyTuning.MealWater / ThirstConstants.WaterValuePerPip);
    }

    public override void Die()
    {
        // Rocks are allowed to injure and heavily stun Desert Batflies, but they can
        // never be the direct finishing blow. Creature.Violence may ask for Die() while
        // resolving a rock hit; keep the individual barely alive and let later non-rock
        // damage or environmental hazards kill it normally.
        if (resolvingRockViolence && !dead)
        {
            DesertState.health = Mathf.Max(DesertState.health, RockSurvivalHealthFloor);
            return;
        }

        if (runningVanillaUpdate && !dead && drown < 1f &&
            grabbedBy.Count > 0 && grabbedBy[0].grabber is Player)
            return;

        bool wasDead = dead;
        playerHolder = null;
        sandStruggleMeter = 0f;
        sandSpitWindup = 0;
        DesertAI?.CancelAttack();
        Emergence?.Cancel();
        base.Die();

        // DB_EventHub observes the confirmed live -> dead transition inside base.Die(),
        // owns killer attribution and dispatches all mortality-domain reactions once.
        if (!wasDead && dead)
            injury?.ClearTransient();
    }

    public override void Destroy()
    {
        injury?.ClearTransient();
        playerHolder = null;
        sandStruggleMeter = 0f;
        sandSpitWindup = 0;
        DesertAI?.CancelAttack();
        DesertBatflyIntimidation.Forget(this);
        base.Destroy();
    }
}