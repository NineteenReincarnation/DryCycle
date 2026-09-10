namespace DryCycle.Creatures.DesertBatfly;

/// <summary>
/// Transitional type bridge for the R6 DB_AI field shape. All perception behavior now lives
/// in DB_PerceptionRuntime; this class owns no scan, scoring, signal or movement logic.
/// It is removed when DB_AI's declared field type is migrated in the final R2 cleanup.
/// </summary>
internal sealed class DB_CreaturePerception : DB_PerceptionRuntime
{
    internal DB_CreaturePerception(DB_AI brain, DB_Creature fly) : base(brain, fly) { }
}
