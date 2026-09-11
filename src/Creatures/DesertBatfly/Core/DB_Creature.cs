using DryCycle.Thirst;
using UnityEngine;

namespace DryCycle.Creatures.DesertBatfly;

internal sealed class DB_Creature : Fly, IPlayerEdible
{
    private const float RockSurvivalHealthFloor = 0.01f;

    private DB_Injury injury;
    internal DB_Injury Injury => injury ??= new DB_Injury(this);
    internal readonly DB_AI DesertAI;
    internal readonly DB_Emergence Emergence;
    internal DB_State DesertState => (DB_State)State;
    internal DB_Personality Personality => DesertState.Personality;
    internal World world => abstractCreature?.world;

    private int mealFood = 2;
    private bool runningVanillaUpdate;
    private bool resolvingRockViolence;
    internal readonly DB_SandSpitRuntime SandSpit;
    internal readonly DB_RestraintRuntime Restraint;
    internal readonly DB_RescueRuntime Rescue;
    internal readonly DB_DehydrationFeedingRuntime Feeding;
    internal readonly DB_Runtime Runtime;
    internal bool SandSpitWindingUp => SandSpit.WindingUp;
    internal int SandSpitWindupRemaining => SandSpit.WindupRemaining;

    int IPlayerEdible.FoodPoints => mealFood;

    internal DB_Creature(AbstractCreature creature, World world) : base(creature, world)
    {
        mainBodyChunk.rad = DB_Tuning.Radius * Personality.Size;
        mainBodyChunk.mass = DB_Tuning.Mass * Personality.Size;
        airFriction = 0.975f;
        bites = DesertState.Bites;
        if (DesertState.MealConsumed) eaten = 1;
        DesertAI = new DB_AI(this);
        Emergence = new DB_Emergence(this);
        SandSpit = new DB_SandSpitRuntime(this);
        Restraint = new DB_RestraintRuntime(this);
        Rescue = new DB_RescueRuntime(this);
        Feeding = new DB_DehydrationFeedingRuntime(this);
        Runtime = new DB_Runtime(this);
    }

    public override void InitiateGraphicsModule()
    {
        if (graphicsModule == null) graphicsModule = new DB_Graphics(this);
    }

    public override void NewRoom(Room newRoom)
    {
        injury?.ClearTransient();
        Feeding.ClearTransient();
        Rescue.ClearTransient();
        DesertAI?.ResetRoom();
        Runtime.BeforeNewRoom();
        base.NewRoom(newRoom);
    }

    public override void PlaceInRoom(Room placeRoom)
    {
        base.PlaceInRoom(placeRoom);
        if (!DesertState.InHive || dead) return;

        // InHive is save-backed. A moved/migrated creature can therefore realize in a room whose
        // authored geometry has no BatHive even though the previous abstract state said it was
        // resting in one. Repair that stale flag instead of leaving a realized active bat marked
        // permanently invisible to flock/hive lifecycle code.
        if (placeRoom?.hives == null || placeRoom.hives.Length == 0)
        {
            DesertState.InHive = false;
            return;
        }

        var hive = DB_SwarmRoom.For(placeRoom).Hive;
        if (!hive.inHive.Contains(this)) hive.MoveFlyToHive(this);
    }

    public override void Update(bool eu)
    {
        Vector2 previousFlightVelocity = Runtime.BeforeVanillaUpdate();
        if (room == null)
        {
            base.Update(eu);
            return;
        }

        Room currentRoom = room;
        FliesRoomAI original = currentRoom.fliesRoomAi;
        var colony = DB_SwarmRoom.For(currentRoom);
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

        Runtime.AfterVanillaUpdate(eu, previousFlightVelocity);
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
            DB_FearRuntime.IsSupportedLethalThreat(attacker);

        // Preserve an intact Fly chain until Die() has captured its witnesses. A hit
        // that does not kill receives the ordinary Threatened transition afterwards.
        if (!supportedLethalThreat)
            DesertAI.Threatened(attacker, true);

        float healthBefore = DesertState.health;
        DB_EventHub.ViolenceTransaction eventTransaction =
            DB_EventHub.BeginViolence(this, source, type, damage, stunBonus);
        bool vanillaCompleted = false;
        resolvingRockViolence = rockHit;
        try
        {
            base.Violence(source, momentum, hitChunk, appendage, type, damage, stunBonus);
            vanillaCompleted = true;
        }
        finally
        {
            // End the semantic transaction before applying the rock survival floor. This
            // preserves the old hook ordering: vanilla Violence -> Damage event -> rock floor.
            DB_EventHub.EndViolence(eventTransaction, vanillaCompleted);

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
            Feeding.CancelForGrab();
            if (Restraint.BeginPlayerHold(player, grasp))
                DesertAI.PlayerGrabbed(player);
        }
        else if (grasp?.grabber != null && grasp.grabber is not Fly)
        {
            // Capture fear/signals are semantic-event consumers. Creature retains only its
            // immediate native danger response; DB_EventHub dedupes tongue -> grasp transfer.
            Feeding.ClearTransient();
            DesertAI.Threatened(grasp.grabber, true);
        }

        DesertAI.CancelAttack();
        Emergence.Cancel();
        DB_HiveTraffic.Forget(this);
        DB_SocialRuntime.CancelForPriority(this, "grabbed / restraint");

        // Report before vanilla installs this grasp into grabbedBy. The EventHub uses that
        // pre-base fact to keep tongue -> grasp transfer within one capture session.
        DB_EventHub.ReportGraspCapture(this, grasp);
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
            DB_Tuning.MealWater / ThirstConstants.WaterValuePerPip);
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
        Feeding.ClearTransient();
        Rescue.ClearTransient();
        Restraint.ClearTransient();
        DesertAI?.CancelAttack();
        Emergence?.Cancel();
        DB_HiveTraffic.Forget(this);

        // Capture chain witnesses and killer attribution at the exact point where the former
        // On.Creature.Die hook observed this lifecycle: immediately before base.Die.
        DB_EventHub.MortalityTransaction mortalityTransaction =
            DB_EventHub.PrepareMortality(this);
        base.Die();
        DB_EventHub.CompleteMortality(mortalityTransaction);

        if (!wasDead && dead)
            injury?.ClearTransient();
    }

    public override void Destroy()
    {
        injury?.ClearTransient();
        Feeding.ClearTransient();
        Rescue.ClearTransient();
        Restraint.ClearTransient();
        DesertAI?.CancelAttack();
        DB_HiveTraffic.Forget(this);
        DB_FearRuntime.Forget(this);
        base.Destroy();
    }
}
