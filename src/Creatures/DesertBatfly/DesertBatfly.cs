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
    }

    public override void InitiateGraphicsModule()
    {
        if (graphicsModule == null) graphicsModule = new DesertBatflyGraphics(this);
    }

    public override void NewRoom(Room newRoom)
    {
        playerHolder = null;
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
        if (room == null)
        {
            base.Update(eu);
            return;
        }

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

        if (room == null) return;
        Emergence.Update(eu);
        DesertAI.AfterPhysics(eu);
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
                playerHolder = player;
                DesertAI.PlayerGrabbed(player);
            }
        }
        else if (grasp?.grabber is not Fly)
        {
            DesertAI.Threatened(grasp?.grabber, true);
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

        playerHolder = null;
        DesertAI?.CancelAttack();
        Emergence?.Cancel();
        base.Die();
    }

    public override void Destroy()
    {
        playerHolder = null;
        DesertAI?.CancelAttack();
        base.Destroy();
    }
}
