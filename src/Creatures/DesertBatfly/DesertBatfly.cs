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

    // Short damage attribution window used only to identify a genuine player kill.
    // Rock never enters this path because it is stun-only for this species.
    private Player recentPlayerDamager;
    private int recentPlayerDamageTicks;
    private float recentPlayerThreatScale;

    private float sandStruggleMeter, sandSpitThreshold;
    private int sandSpitCooldown, sandSpitWindup, sandSpitCycle;

    internal bool SandSpitWindingUp => sandSpitWindup > 0;
    internal int SandSpitWindupRemaining => sandSpitWindup;

    int IPlayerEdible.FoodPoints => mealFood;

    internal DesertBatfly(AbstractCreature creature, World world) : base(creature, world)
    {
        mainBodyChunk.rad = DesertBatflyTuning.Radius * Personality.Size;
        mainBodyChunk.mass = DesertBatflyTuning.Mass * Personality.Size;
        // Fly's grasp anchor is its main chunk; the personality scale therefore
        // affects grabbing, weapons and terrain collision as well as rendering.
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
        // Keep playerHolder across a carried room transition. Release tracking then
        // still fires correctly if the player drops/throws the bat in the new room.
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
        // Rock.HitSomething can pass through several pieces of vanilla weapon and
        // creature code in one collision. A normal Rock may stun, but that collision
        // itself must never transition this creature into the dead state.
        rockDeathGuardTicks = Mathf.Max(rockDeathGuardTicks, 8);
    }

    public override void Update(bool eu)
    {
        TrackPlayerRelease();
        if (rockDeathGuardTicks > 0) rockDeathGuardTicks--;
        if (sandSpitCooldown > 0) sandSpitCooldown--;
        if (recentPlayerDamageTicks > 0 && --recentPlayerDamageTicks == 0)
        {
            recentPlayerDamager = null;
            recentPlayerThreatScale = 0f;
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

        Room currentRoom = room;
        // Vanilla Fly hardcodes room.fliesRoomAi for flocking and burrowing. Only
        // this synchronous call sees the private species pool; never alter vanilla
        // SWARMROOM registration or population state globally.
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

        // Mortality awareness is enforced after the normal AI pass but before
        // Attach/Interfere physics. If an intimidated bat tried to reacquire the
        // demonstrated killer this tick, the intimidation layer can cancel it before
        // any drain or movement interference is applied.
        DesertBatflyIntimidation.Update(this);

        if (room == null) return;
        Emergence.Update(eu);
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

        // Short deterministic wind-up: FlyGraphics intensifies the existing grabbed
        // wing struggle during these few ticks, making the burst readable before the
        // screen effect appears.
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
        // A new grab never produces an immediate burst. Existing post-spit cooldown
        // is preserved so rapid grab/drop cycling cannot bypass it.
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
        DesertAI.Threatened(attacker, true);

        if (source?.owner is Rock)
        {
            BeginRockStunGuard();

            // Rock is a control tool for this species. Do not call Creature.Violence
            // at all: even tiny/zero damage can enter HealthState.quickDeath when an
            // animal was previously injured. Keep only impact impulse and stun.
            BodyChunk chunk = hitChunk ?? mainBodyChunk;
            if (momentum.HasValue)
                chunk.vel += Vector2.ClampMagnitude(momentum.Value / chunk.mass, 10f);
            Stun(Mathf.Max(DesertBatflyTuning.RockStun, Mathf.CeilToInt(stunBonus)));
            return;
        }

        // Attribute only genuine damaging player attacks. Spears are the clearest
        // demonstration of lethal force and receive full intimidation weight; other
        // player-caused lethal damage still counts but is slightly weaker.
        if (damage > 0f && attacker is Player playerAttacker)
        {
            recentPlayerDamager = playerAttacker;
            recentPlayerDamageTicks = 240;
            recentPlayerThreatScale = source?.owner is Spear ? 1f : 0.82f;
        }

        // A spear, predator bite or any other genuine damage event must remain able
        // to kill even if it happens immediately after a Rock impact.
        resolvingNonRockViolence = true;
        try
        {
            base.Violence(source, momentum, hitChunk, appendage, type, damage, stunBonus);
        }
        finally
        {
            resolvingNonRockViolence = false;
        }
    }

    public override void Grabbed(Grasp grasp)
    {
        // Fly-on-Fly grasps are the vanilla hanging-chain mechanism, not an attack.
        // Treating them as danger would instantly dismantle every chain we create.
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

        // Arena ObjectEaten reads FoodPoints directly instead of nourishment.
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
        // A normal Rock is defined as stun-only for this species. Guard the whole
        // Rock collision stack rather than relying solely on Violence damage values;
        // non-Rock Violence explicitly bypasses this protection above.
        if (!dead && rockDeathGuardTicks > 0 && !resolvingNonRockViolence && drown < 1f)
            return;

        // Vanilla Fly.Update has an intentional chance to die while held by a player.
        // Desert Batflies must survive being picked up and remain alive after release.
        if (runningVanillaUpdate && !dead && drown < 1f &&
            grabbedBy.Count > 0 && grabbedBy[0].grabber is Player)
            return;

        bool wasDead = dead;
        Player killer = !wasDead && recentPlayerDamageTicks > 0
            ? recentPlayerDamager
            : null;
        float threatScale = recentPlayerThreatScale > 0f
            ? recentPlayerThreatScale
            : 0.82f;
        Vector2 deathPosition = mainBodyChunk?.pos ?? Vector2.zero;
        Fly preDeathChainRoot = !wasDead && AI != null && AI.behavior == FlyAI.Behavior.Chain
            ? FirstInChain()
            : null;

        playerHolder = null;
        sandStruggleMeter = 0f;
        sandSpitWindup = 0;
        DesertAI?.CancelAttack();
        Emergence?.Cancel();
        base.Die();

        // Broadcast only on the single live -> dead transition and only when a recent
        // genuine player damage event can be identified. Predator kills, drowning and
        // unrelated deaths therefore never manufacture player intimidation.
        if (!wasDead && dead && killer != null)
        {
            DesertBatflyIntimidation.BroadcastPlayerKill(
                this,
                killer,
                deathPosition,
                preDeathChainRoot,
                threatScale);
        }

        recentPlayerDamager = null;
        recentPlayerDamageTicks = 0;
        recentPlayerThreatScale = 0f;
    }

    public override void Destroy()
    {
        playerHolder = null;
        recentPlayerDamager = null;
        recentPlayerDamageTicks = 0;
        recentPlayerThreatScale = 0f;
        sandStruggleMeter = 0f;
        sandSpitWindup = 0;
        DesertAI?.CancelAttack();
        base.Destroy();
    }
}
