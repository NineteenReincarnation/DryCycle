using DryCycle.Thirst;
using UnityEngine;

namespace DryCycle.Creatures.DesertBatfly;

internal sealed class DesertBatfly : Fly, IPlayerEdible
{
    internal readonly DesertBatflyAI DesertAI;
    internal readonly DesertBatflyEmergence Emergence;
    internal DesertBatflyState DesertState => (DesertBatflyState)State;
    internal DesertBatflyPersonality Personality => DesertState.Personality;

    private int mealFood = 2;
    private bool runningVanillaUpdate;
    private int rockDeathGuardTicks;
    private bool resolvingNonRockViolence;
    private Player playerHolder;

    private Creature recentLethalDamager;
    private int recentLethalDamageTicks;
    private float recentLethalThreatScale;

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

    internal void BeginRockStunGuard()
    {
        rockDeathGuardTicks = Mathf.Max(rockDeathGuardTicks, 8);
    }

    public override void Update(bool eu)
    {
        TrackPlayerRelease();
        if (rockDeathGuardTicks > 0) rockDeathGuardTicks--;
        if (sandSpitCooldown > 0) sandSpitCooldown--;
        if (recentLethalDamageTicks > 0 && --recentLethalDamageTicks == 0)
        {
            recentLethalDamager = null;
            recentLethalThreatScale = 0f;
        }
        if (!dead) DesertState.TickTrauma();

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

        // Die() owns the one-shot mortality broadcast and Forget(). Corpses must not
        // recreate a runtime morale state merely because persistent Trauma remains in
        // CreatureState; doing so would keep activeStates non-zero until corpse cleanup.
        bool extremeVengeance = false;
        if (!dead)
        {
            DesertBatflyIntimidation.Update(this);
            extremeVengeance = DesertBatflyIntimidation.IsExtremeVengeanceActive(this);
            if (extremeVengeance)
                DesertAI.CancelAttack();
        }

        if (room == null) return;
        Emergence.Update(eu);
        if (!extremeVengeance)
            DesertAI.AfterPhysics(eu);
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
        DesertBatflySandBurst.Emit(
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

        if (source?.owner is Rock)
        {
            DesertAI.Threatened(attacker, true);
            BeginRockStunGuard();

            BodyChunk chunk = hitChunk ?? mainBodyChunk;
            if (momentum.HasValue)
                chunk.vel += Vector2.ClampMagnitude(momentum.Value / chunk.mass, 10f);
            Stun(Mathf.Max(DesertBatflyTuning.RockStun, Mathf.CeilToInt(stunBonus)));
            return;
        }

        bool supportedLethalThreat = damage > 0f &&
            DesertBatflyIntimidation.IsSupportedLethalThreat(attacker);

        // Preserve an intact Fly chain until Die() has captured its witnesses. A hit
        // that does not kill receives the ordinary Threatened transition afterwards.
        if (supportedLethalThreat)
        {
            recentLethalDamager = attacker;
            recentLethalDamageTicks = 240;
            recentLethalThreatScale = attacker is Player
                ? (source?.owner is Spear ? 1f : 0.82f)
                : 1.05f;
        }
        else
        {
            DesertAI.Threatened(attacker, true);
        }

        resolvingNonRockViolence = true;
        try
        {
            base.Violence(source, momentum, hitChunk, appendage, type, damage, stunBonus);
        }
        finally
        {
            resolvingNonRockViolence = false;
        }

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
        else if (grasp?.grabber is Lizard lizard &&
                 DesertBatflyIntimidation.IsSupportedLethalThreat(lizard))
        {
            DesertBatflyIntimidation.BroadcastPredatorCapture(this, lizard, null);
            DesertAI.Threatened(lizard, true);
        }
        else if (grasp?.grabber != null && grasp.grabber is not Fly)
        {
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

        // Vanilla Fly.BitByPlayer calls Die() on the first bite. Attribute that live
        // consumption before entering vanilla so nearby bats treat visibly eating a
        // flockmate as a genuine player kill rather than an unexplained death.
        if (!dead)
        {
            recentLethalDamager = player;
            recentLethalDamageTicks = 2;
            recentLethalThreatScale = 0.90f;
        }

        mealFood = SlugcatStats.NourishmentOfObjectEaten(player.SlugCatClass, this) == 4 ? 1 : 2;
        try
        {
            base.BitByPlayer(grasp, eu);
        }
        finally
        {
            mealFood = 2;
        }

        DesertState.Bites = bites;
        if (bites != 0 || DesertState.MealConsumed) return;
        DesertState.MealConsumed = true;
        ThirstStore.RemoveRuntime(
            player,
            DesertBatflyTuning.MealWater / ThirstConstants.WaterValuePerPip);
    }

    public override void Die()
    {
        if (!dead && rockDeathGuardTicks > 0 && !resolvingNonRockViolence && drown < 1f)
            return;

        if (runningVanillaUpdate && !dead && drown < 1f &&
            grabbedBy.Count > 0 && grabbedBy[0].grabber is Player)
            return;

        bool wasDead = dead;
        Creature killer = !wasDead && recentLethalDamageTicks > 0
            ? recentLethalDamager
            : null;
        float threatScale = recentLethalThreatScale > 0f
            ? recentLethalThreatScale
            : 0.82f;
        Vector2 deathPosition = mainBodyChunk?.pos ?? Vector2.zero;
        DesertBatfly[] chainWitnesses = !wasDead
            ? DesertBatflyIntimidation.SnapshotChainWitnesses(this)
            : System.Array.Empty<DesertBatfly>();
        bool revengeFailed = !wasDead &&
            DesertBatflyIntimidation.IsExtremeVengeanceActive(this);

        playerHolder = null;
        sandStruggleMeter = 0f;
        sandSpitWindup = 0;
        DesertAI?.CancelAttack();
        Emergence?.Cancel();
        base.Die();

        // Broadcast only after the live -> dead transition is confirmed. The chain
        // snapshot above preserves exact eyewitnesses without creating a false death
        // event if an external compatibility hook unexpectedly prevents death.
        if (!wasDead && dead && killer != null)
        {
            if (killer is Player playerKiller)
            {
                DesertBatflyIntimidation.BroadcastPlayerKill(
                    this,
                    playerKiller,
                    deathPosition,
                    chainWitnesses,
                    threatScale,
                    revengeFailed);
            }
            else if (killer is Lizard lizardKiller)
            {
                DesertBatflyIntimidation.BroadcastPredatorKill(
                    this,
                    lizardKiller,
                    deathPosition,
                    chainWitnesses,
                    threatScale,
                    revengeFailed);
            }
        }

        if (!wasDead && dead)
            DesertBatflyIntimidation.Forget(this);

        recentLethalDamager = null;
        recentLethalDamageTicks = 0;
        recentLethalThreatScale = 0f;
    }

    public override void Destroy()
    {
        playerHolder = null;
        recentLethalDamager = null;
        recentLethalDamageTicks = 0;
        recentLethalThreatScale = 0f;
        sandStruggleMeter = 0f;
        sandSpitWindup = 0;
        DesertAI?.CancelAttack();
        DesertBatflyIntimidation.Forget(this);
        base.Destroy();
    }
}