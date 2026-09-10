using System;
using System.Reflection;

internal static partial class Program
{
    private static void RunArchitecturePerception()
    {
        Type roomContext = mod.GetType("DryCycle.Creatures.DesertBatfly.DB_RoomContext", true);
        Type visibility = mod.GetType("DryCycle.Creatures.DesertBatfly.DB_VisibilityPolicy", true);
        Type visibilityChannel = mod.GetType("DryCycle.Creatures.DesertBatfly.DB_VisibilityChannel", true);
        Type perception = mod.GetType("DryCycle.Creatures.DesertBatfly.DB_PerceptionRuntime", true);
        Type perceptionTrack = mod.GetType("DryCycle.Creatures.DesertBatfly.DB_PerceptionTrack", true);
        Type perceptionSnapshot = mod.GetType("DryCycle.Creatures.DesertBatfly.DB_PerceptionSnapshot", true);
        Type perceptionScoring = mod.GetType("DryCycle.Creatures.DesertBatfly.DB_PerceptionScoring", true);
        Type perceptionSource = mod.GetType("DryCycle.Creatures.DesertBatfly.DB_PerceptionSource", true);
        Type perceptionModality = mod.GetType("DryCycle.Creatures.DesertBatfly.DB_PerceptionModality", true);
        Type heldObservation = mod.GetType("DryCycle.Creatures.DesertBatfly.DB_HeldThreatObservation", true);
        Type ai = mod.GetType("DryCycle.Creatures.DesertBatfly.DB_AI", true);
        Type threat = mod.GetType("DryCycle.Creatures.DesertBatfly.DB_ThreatRuntime", true);
        Type signalRuntime = mod.GetType("DryCycle.Creatures.DesertBatfly.DB_SignalRuntime", true);
        Type signalDefinition = mod.GetType("DryCycle.Creatures.DesertBatfly.DB_SignalDefinition", true);

        Check(roomContext.GetMethod("For", Flags) != null &&
              roomContext.GetMethod("TryGetExisting", Flags) != null &&
              roomContext.GetMethod("PlayerBySlot", Flags) != null,
            "Perception R2 keeps DB_RoomContext as the single shared room-observation authority");
        foreach (string property in new[] { "Creatures", "Bats", "Players", "Weapons", "ThrownWeapons" })
            Check(roomContext.GetProperty(property, Flags) != null,
                "Perception R2 shared room context exposes " + property);

        string[] channels = Enum.GetNames(visibilityChannel);
        foreach (string channel in new[] { "Creature", "Player", "Social", "Signal", "HeldItem", "Projectile" })
            Check(Array.IndexOf(channels, channel) >= 0,
                "Perception R2 central visibility policy keeps " + channel + " channel");

        MethodInfo effectiveRange = visibility.GetMethod("EffectiveRange", Flags);
        object playerChannel = Enum.Parse(visibilityChannel, "Player");
        object signalChannel = Enum.Parse(visibilityChannel, "Signal");
        object projectileChannel = Enum.Parse(visibilityChannel, "Projectile");
        float clearPlayer = (float)effectiveRange.Invoke(null, new object[] { 430f, 1f, playerChannel, false });
        float densePlayer = (float)effectiveRange.Invoke(null, new object[] { 430f, 0.25f, playerChannel, false });
        float denseSignal = (float)effectiveRange.Invoke(null, new object[] { 300f, 0.25f, signalChannel, false });
        float denseProjectile = (float)effectiveRange.Invoke(null, new object[] { 230f, 0.25f, projectileChannel, true });
        float closeFloor = (float)visibility.GetField("CloseProjectileFloor", Flags).GetRawConstantValue();
        Check(Math.Abs(clearPlayer - 430f) < 0.001f && densePlayer < clearPlayer && denseSignal < 300f,
            "Perception R2 fog reduces long-range direct recognition without changing clear-weather range");
        Check(denseProjectile >= Math.Min(230f, closeFloor) && denseProjectile <= 230f,
            "Perception R2 preserves the bounded close real-projectile visibility floor");

        Check(perception.GetMethod("RefreshState", Flags) != null &&
              perception.GetProperty("Snapshot", Flags) != null &&
              perception.GetMethod("TryGetIncomingProjectile", Flags) != null &&
              perception.GetMethod("TryGetSignalContext", Flags) != null &&
              perception.GetMethod("ReceiveSignal", Flags) != null &&
              perception.GetMethod("TryGetDebugState", Flags) != null,
            "Perception R2 exposes one receiver refresh/snapshot/signal/debug surface");
        Check(ai.GetProperty("Perception", Flags)?.PropertyType == perception,
            "DB_AI now owns DB_PerceptionRuntime directly with no transitional creature-perception type");
        Check(mod.GetType("DryCycle.Creatures.DesertBatfly.DB_CreaturePerception", false) == null,
            "retired DB_CreaturePerception bridge is absent after direct R2 ownership migration");

        foreach (string field in new[]
        {
            "Target", "ObservedPosition", "EstimatedPosition", "ObservedVelocity", "Confidence",
            "Salience", "ThreatUrgency", "AttentionScore", "LastObservedTick", "AgeTicks",
            "Source", "Modality", "DirectObservation"
        })
            Check(perceptionTrack.GetField(field, Flags) != null,
                "Perception R2 bounded track exposes " + field);
        foreach (string field in new[] { "PrimaryThreat", "SecondaryThreat", "LostThreat", "IncomingProjectile", "Signals" })
            Check(perceptionSnapshot.GetField(field, Flags) != null,
                "Perception R2 snapshot exposes fixed slot " + field);

        string[] sources = Enum.GetNames(perceptionSource);
        foreach (string source in new[] { "DirectCreature", "DirectPlayer", "HeldItem", "Projectile", "Signal", "Predicted" })
            Check(Array.IndexOf(sources, source) >= 0, "Perception R2 source vocabulary exposes " + source);
        string[] modalities = Enum.GetNames(perceptionModality);
        foreach (string modality in new[] { "Visual", "Acoustic", "Reported", "Predicted" })
            Check(Array.IndexOf(modalities, modality) >= 0, "Perception R2 modality vocabulary exposes " + modality);

        MethodInfo threatScore = perceptionScoring.GetMethod("ThreatAttentionScore", Flags);
        MethodInfo projectileRisk = perceptionScoring.GetMethod("ProjectileRisk", Flags);
        MethodInfo signalConfidence = perceptionScoring.GetMethod("SignalConfidence", Flags);
        MethodInfo lostConfidence = perceptionScoring.GetMethod("LostConfidence", Flags);
        MethodInfo shouldSwitch = perceptionScoring.GetMethod("ShouldSwitchAttention", Flags);
        MethodInfo betterScore = perceptionScoring.GetMethod("BetterScore", Flags);
        Check(threatScore != null && projectileRisk != null && signalConfidence != null &&
              lostConfidence != null && shouldSwitch != null && betterScore != null,
            "Perception R2 scoring is centralized in pure callable helpers");

        float receding = (float)threatScore.Invoke(null, new object[] { 0.8f, 85f, 180f, -3f, 0.1f, 0.9f, 0.1f, 0.5f });
        float charging = (float)threatScore.Invoke(null, new object[] { 0.8f, 105f, 180f, 7f, 0.9f, 0.9f, 0.1f, 0.5f });
        Check(charging > receding,
            "Perception R2 attention prefers a strongly charging threat over a slightly nearer receding one");

        float grazingRock = (float)projectileRisk.Invoke(null, new object[] { 1.8f, 28f, 38f, 7f, 0.34f, 0.92f, 0f });
        float imminentSpear = (float)projectileRisk.Invoke(null, new object[] { 0.30f, 3f, 38f, 15f, 1f, 0.92f, 0f });
        Check(imminentSpear > grazingRock,
            "Perception R2 projectile risk prefers imminent lethal intersection over later grazing blunt flight");

        float rootSignal = (float)signalConfidence.Invoke(null, new object[] { 0.8f, 0.8f, 0, 0.7f, 0.4f, false });
        float hopOne = (float)signalConfidence.Invoke(null, new object[] { 0.8f, 0.8f, 1, 0.7f, 0.4f, false });
        float hopTwo = (float)signalConfidence.Invoke(null, new object[] { 0.8f, 0.8f, 2, 0.7f, 0.4f, false });
        Check(rootSignal > hopOne && hopOne > hopTwo,
            "Perception R2 reported-signal confidence decreases monotonically with relay hops");

        float lost0 = (float)lostConfidence.Invoke(null, new object[] { 0.9f, 0 });
        float lost16 = (float)lostConfidence.Invoke(null, new object[] { 0.9f, 16 });
        float lost40 = (float)lostConfidence.Invoke(null, new object[] { 0.9f, 40 });
        float lostMax = (float)lostConfidence.Invoke(null, new object[] { 0.9f, 52 });
        Check(lost0 > lost16 && lost16 > lost40 && lostMax == 0f,
            "Perception R2 lost-target confidence decays monotonically to zero in a bounded window");

        Check(!(bool)shouldSwitch.Invoke(null, new object[] { 0.70f, 0.72f, 0.9f, false }) &&
              (bool)shouldSwitch.Invoke(null, new object[] { 0.70f, 0.90f, 0.9f, false }) &&
              (bool)shouldSwitch.Invoke(null, new object[] { 0.70f, 0.71f, 0.9f, true }),
            "Perception R2 attention hysteresis rejects micro-switches but allows material or imminent challenges");

        // Equal scores use a stable key rather than candidate enumeration order. Exercise a
        // larger identity range so this stays an ordering contract rather than one hard-coded pair.
        bool stableTieOrder = true;
        for (int key = 2; key < 128; key++)
        {
            bool lowerWins = (bool)betterScore.Invoke(null, new object[] { 0.5f, key - 1, 0.5f, key });
            bool higherLoses = (bool)betterScore.Invoke(null, new object[] { 0.5f, key, 0.5f, key - 1 });
            stableTieOrder &= lowerWins && !higherLoses;
        }
        Check(stableTieOrder,
            "Perception R2 exact-score ties resolve deterministically by stable identity across candidate orderings");

        MethodInfo scanCreatures = perception.GetMethod("ScanCreatures", Flags);
        MethodInfo scanProjectiles = perception.GetMethod("RefreshIncomingProjectile", Flags);
        Check(scanCreatures != null && MethodCallOffset(scanCreatures, roomContext, "For") >= 0 &&
              MethodCallOffset(scanCreatures, visibility, "CanObserve") >= 0 &&
              MethodCallOffset(scanCreatures, perceptionScoring, "ThreatAttentionScore") >= 0,
            "Perception R2 creature attention consumes shared room facts + visibility + central scoring");
        Check(scanProjectiles != null && MethodCallOffset(scanProjectiles, roomContext, "For") >= 0 &&
              MethodCallOffset(scanProjectiles, visibility, "CanObserve") >= 0 &&
              MethodCallOffset(scanProjectiles, perceptionScoring, "ProjectileRisk") >= 0,
            "Perception R2 projectile receiver ranks cached thrown weapons instead of accepting first match");

        int canObserveOffset = MethodCallOffset(scanCreatures, visibility, "CanObserve");
        int exactDistanceOffset = MethodCallOffset(scanCreatures, typeof(UnityEngine.Vector2), "Distance");
        Check(canObserveOffset >= 0 && exactDistanceOffset > canObserveOffset,
            "Perception R2 keeps exact creature distance sqrt behind cheap range + LOS rejection");

        int scanInterval = (int)perception.GetField("ScanIntervalTicks", Flags).GetRawConstantValue();
        MethodInfo scanPhase = perception.GetMethod("ScanPhase", Flags);
        bool[] phaseBuckets = new bool[scanInterval];
        for (int seed = 0; seed < 256; seed++)
        {
            int phase = (int)scanPhase.Invoke(null, new object[] { seed });
            Check(phase >= 1 && phase <= scanInterval, "Perception R2 staggered scan phase stays bounded");
            phaseBuckets[phase - 1] = true;
        }
        bool allBuckets = scanInterval == 8;
        for (int i = 0; i < phaseBuckets.Length; i++) allBuckets &= phaseBuckets[i];
        Check(allBuckets, "Perception R2 disperses creature scans across all eight stable phase buckets");

        Check(!MethodWritesField(perception.GetMethod("RefreshState", Flags), typeof(BodyChunk), "vel") &&
              !MethodWritesField(scanCreatures, typeof(BodyChunk), "vel") &&
              !MethodWritesField(scanProjectiles, typeof(BodyChunk), "vel"),
            "Perception R2 receiver never writes BodyChunk velocity or owns locomotion");

        // Long-term threat memory and signal transport stay in their existing domains while
        // all current direct player, held-item and projectile facts live behind Perception R2.
        Check(threat.GetMethod("RefreshState", Flags) != null &&
              signalDefinition.GetMethod("For", Flags) != null &&
              signalRuntime.GetMethod("EmitAlarm", Flags) != null,
            "Perception R2 preserves Threat memory/assessment and Signal emission/transport domain boundaries");
        Check(mod.GetType("DryCycle.Creatures.DesertBatfly.DB_WeaponPerception", false) == null,
            "retired DB_WeaponPerception bridge is absent after current-observation migration");
        Check(perception.GetMethod("TryGetObservedPlayer", Flags) != null &&
              perception.GetMethod("TryGetHeldThreats", Flags) != null &&
              heldObservation.GetField("VisibleSpear", Flags) != null,
            "Perception R2 owns bounded current observed-player and held-item facts");
        Check(ai.GetMethod("ScanWeapons", Flags) == null && ai.GetMethod("ScanCreatures", Flags) == null,
            "Perception R2 does not resurrect DB_AI scanning facades");

        Console.WriteLine("Architecture Perception R2: direct DB_AI ownership, fixed perception slots, bounded observed-player/held-item facts, order-independent attention/projectile scoring, bounded lost tracking, relay confidence, hysteresis, staggered scans and ownership separation verified.");
    }
}
