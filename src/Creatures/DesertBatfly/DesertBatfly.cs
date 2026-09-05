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
    int IPlayerEdible.FoodPoints => mealFood;

    internal DesertBatfly(AbstractCreature creature, World world) : base(creature, world)
    {
        mainBodyChunk.rad = DesertBatflyTuning.Radius * Personality.Size;
        mainBodyChunk.mass = DesertBatflyTuning.Mass * Personality.Size;
        // Fly's grasp anchor is its main chunk; the enlarged radius is also used by
        // player grabbing, weapons and terrain collision, not just the renderer.
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
        if (room == null) { base.Update(eu); return; }
        DesertState.Thirst = Mathf.Clamp01(DesertState.Thirst + (dead ? 0f : DesertBatflyTuning.ThirstPerTick));
        if (DesertState.Cooldown > 0) DesertState.Cooldown--;
        DesertAI.TickMemory();
        Room currentRoom = room;
        // Vanilla Fly hardcodes room.fliesRoomAi for flocking and burrowing.
        // Only this synchronous call sees the private species pool; never change
        // swarmRoomIndex, tags, vanilla population or the world swarm registry.
        FliesRoomAI original = currentRoom.fliesRoomAi;
        var colony = DesertSwarmRoom.For(currentRoom);
        currentRoom.fliesRoomAi = colony.Hive;
        colony.Hive.AddFly(this);
        try { base.Update(eu); }
        finally { currentRoom.fliesRoomAi = original; }
        if (room == null) return;
        Emergence.Update(eu);
        DesertAI.AfterPhysics(eu);
    }

    public override void Violence(BodyChunk source, Vector2? momentum, BodyChunk hitChunk,
        Appendage.Pos appendage, DamageType type, float damage, float stunBonus)
    {
        if (!RippleViolenceCheck(source)) return;
        Creature attacker = source?.owner as Creature ?? (source?.owner as Weapon)?.thrownBy;
        DesertAI.Threatened(attacker);
        if (source?.owner?.GetType() == typeof(Rock))
        {
            // Do not call base with zero damage: HealthState's quickDeath may
            // still kill a previously injured animal even on a zero-damage hit.
            if (momentum.HasValue)
                (hitChunk ?? mainBodyChunk).vel += Vector2.ClampMagnitude(momentum.Value / (hitChunk ?? mainBodyChunk).mass, 10f);
            Stun(Mathf.Max(DesertBatflyTuning.RockStun, Mathf.CeilToInt(stunBonus)));
            return;
        }
        base.Violence(source, momentum, hitChunk, appendage, type, damage, stunBonus);
    }

    public override void Grabbed(Grasp grasp)
    {
        DesertAI.Threatened(grasp.grabber);
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
        try { base.BitByPlayer(grasp, eu); }
        finally { mealFood = 2; }
        DesertState.Bites = bites;
        if (bites != 0 || DesertState.MealConsumed) return;
        DesertState.MealConsumed = true;
        ThirstStore.RemoveRuntime(player, DesertBatflyTuning.MealWater / ThirstConstants.WaterValuePerPip);
    }

    public override void Die()
    {
        DesertAI?.CancelAttack();
        Emergence?.Cancel();
        base.Die();
    }

    public override void Destroy()
    {
        DesertAI?.CancelAttack();
        base.Destroy();
    }
}
