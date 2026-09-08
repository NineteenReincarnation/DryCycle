using System;
using System.Reflection;
using System.Runtime.Serialization;

internal static partial class Program
{
    private static void RunThreatEventSemantics()
    {
        Type registry = mod.GetType(
            "DryCycle.Creatures.DesertBatfly.DB_ThreatClassifier", true);
        Type evidenceType = mod.GetType(
            "DryCycle.Creatures.DesertBatfly.DB_ThreatEvidence", true);
        MethodInfo classify = registry.GetMethod("Classify", Flags);
        MethodInfo firecrackerEvent = registry.GetMethod("FirecrackerStartleEvidence", Flags);
        Check(classify != null && firecrackerEvent != null,
            "Threat threat adapter exposes event classification and explicit firecracker-startle evidence");

        Assembly gameAssembly = typeof(Spear).Assembly;
        Type electricSpearType = gameAssembly.GetType("MoreSlugcats.ElectricSpear", true);
        Type explosiveSpearType = gameAssembly.GetType("ExplosiveSpear", true);
        PhysicalObject electricSpear = (PhysicalObject)FormatterServices.GetUninitializedObject(electricSpearType);
        PhysicalObject explosiveSpear = (PhysicalObject)FormatterServices.GetUninitializedObject(explosiveSpearType);
        PhysicalObject firecracker = (PhysicalObject)FormatterServices.GetUninitializedObject(typeof(FirecrackerPlant));

        object Classify(PhysicalObject source, Creature.DamageType damageType, float damage, float stun, bool projectile) =>
            classify.Invoke(null, new object[] { source, damageType, damage, stun, projectile });
        float Score(object evidence, string name) =>
            (float)evidenceType.GetField(name, Flags).GetValue(evidence);

        object electricCue = Classify(electricSpear, null, 0f, 0f, false);
        object electricNearMiss = Classify(electricSpear, null, 0f, 0f, true);
        object electricHit = Classify(
            electricSpear, Creature.DamageType.Electric, 0.1f, 140f, true);
        Check(Score(electricCue, "Shock") > 0f,
            "visible ElectricSpear may advertise a current Shock cue");
        Check(Score(electricNearMiss, "Projectile") > 0f &&
              Score(electricNearMiss, "Piercing") > 0f &&
              Score(electricNearMiss, "Shock") == 0f,
            "ElectricSpear near-miss teaches projectile/piercing but not Shock before a real electric event");
        Check(Score(electricHit, "Shock") > 0f,
            "real DamageType.Electric event teaches ShockPressure");

        object firecrackerCue = Classify(firecracker, null, 0f, 0f, false);
        object firecrackerNearMiss = Classify(firecracker, null, 0f, 0f, true);
        object startleEvent = firecrackerEvent.Invoke(null, Array.Empty<object>());
        Check(Score(firecrackerCue, "Startle") > 0f,
            "visible FirecrackerPlant may advertise a current Startle cue");
        Check(Score(firecrackerNearMiss, "Projectile") > 0f &&
              Score(firecrackerNearMiss, "Startle") == 0f,
            "untriggered FirecrackerPlant near-miss does not train StartlePressure");
        Check(Score(startleEvent, "Startle") > 0f &&
              Score(startleEvent, "Piercing") == 0f,
            "real firecracker pop trains Startle without inventing Piercing evidence");

        object explosiveCue = Classify(explosiveSpear, null, 0f, 0f, false);
        object explosiveNearMiss = Classify(explosiveSpear, null, 0f, 0f, true);
        object explosiveEvent = Classify(
            explosiveSpear, Creature.DamageType.Explosion, 1f, 80f, true);
        Check(Score(explosiveCue, "Explosion") > 0f,
            "visible ExplosiveSpear may advertise a current Explosion cue");
        Check(Score(explosiveNearMiss, "Projectile") > 0f &&
              Score(explosiveNearMiss, "Piercing") > 0f &&
              Score(explosiveNearMiss, "Explosion") == 0f,
            "unexploded ExplosiveSpear near-miss does not claim an explosion already occurred");
        Check(Score(explosiveEvent, "Projectile") > 0f &&
              Score(explosiveEvent, "Piercing") > 0f &&
              Score(explosiveEvent, "Explosion") > 0f &&
              Score(explosiveEvent, "AreaDenial") > 0f,
            "real explosive-spear explosion can train projectile/piercing/explosion/area-denial together");

        Console.WriteLine(
            "Threat event semantics: visible-item cues are separated from persistent Shock/Startle/Explosion evidence; real events train the corresponding dimensions.");
    }
}
