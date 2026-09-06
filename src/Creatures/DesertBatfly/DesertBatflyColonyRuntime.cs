using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using UnityEngine;
using Watcher;

namespace DryCycle.Creatures.DesertBatfly;

/// <summary>
/// Save-backed Task 09 coordinator. It owns colony/individual ecology identity and
/// low-frequency cycle settlement; it never controls room-local flight velocity.
/// </summary>
internal static class DesertBatflyColonyRuntime
{
    private const string SavePrefix = "DCBATCOLONY09<svB>";
    private const string PayloadVersion = "V1";

    internal sealed class IndividualRecord
    {
        internal readonly int Spawner;
        internal readonly int Number;
        internal string CurrentColony = string.Empty;
        internal string PreviousColony = string.Empty;
        internal string PendingMigrationColony = string.Empty;
        internal int LastMigrationCycle = int.MinValue;

        internal IndividualRecord(EntityID id)
        {
            Spawner = id.spawner;
            Number = id.number;
        }

        internal string Key => Spawner.ToString(CultureInfo.InvariantCulture) + "," +
                               Number.ToString(CultureInfo.InvariantCulture);
    }

    private static Dictionary<string, DesertBatflyColonyState> colonies =
        new(StringComparer.OrdinalIgnoreCase);
    private static Dictionary<string, IndividualRecord> individuals =
        new(StringComparer.Ordinal);
    private static HashSet<string> deathsThisCycle = new(StringComparer.Ordinal);

    // Rebuilt once when World changes. These indexes remove repeated whole-region scans
    // from the 120-tick ecology loop while keeping AbstractCreature identity authoritative.
    private static Dictionary<string, AbstractRoom> roomLookup =
        new(StringComparer.OrdinalIgnoreCase);
    private static List<AbstractRoom> colonyRooms = new();
    private static Dictionary<string, AbstractCreature> trackedBats =
        new(StringComparer.Ordinal);
    private static readonly List<string> staleBatKeys = new(16);
    private static readonly List<AbstractCreature> sourceMembersScratch = new(64);

    private static WeakReference activeWorld;
    private static bool enabled;
    private static int abstractTick;

    internal static IEnumerable<DesertBatflyColonyState> Colonies => colonies.Values;

    internal static void Enable()
    {
        if (enabled) return;
        enabled = true;
        On.SaveState.SaveToString += SaveState_SaveToString;
        On.SaveState.LoadGame += SaveState_LoadGame;
        On.SaveState.SessionEnded += SaveState_SessionEnded;
        On.RainCycle.Update += RainCycle_Update;
    }

    internal static void Disable()
    {
        if (!enabled) return;
        enabled = false;
        On.SaveState.SaveToString -= SaveState_SaveToString;
        On.SaveState.LoadGame -= SaveState_LoadGame;
        On.SaveState.SessionEnded -= SaveState_SessionEnded;
        On.RainCycle.Update -= RainCycle_Update;
        ResetAll();
    }

    internal static void ResetAll()
    {
        colonies = new Dictionary<string, DesertBatflyColonyState>(StringComparer.OrdinalIgnoreCase);
        individuals = new Dictionary<string, IndividualRecord>(StringComparer.Ordinal);
        deathsThisCycle = new HashSet<string>(StringComparer.Ordinal);
        roomLookup = new Dictionary<string, AbstractRoom>(StringComparer.OrdinalIgnoreCase);
        colonyRooms = new List<AbstractRoom>();
        trackedBats = new Dictionary<string, AbstractCreature>(StringComparer.Ordinal);
        staleBatKeys.Clear();
        sourceMembersScratch.Clear();
        activeWorld = null;
        abstractTick = 0;
        DesertBatflyRefuge.Reset();
        DesertBatflyTravelNavigation.Reset();
    }

    internal static DesertBatflyColonyState TryGetColony(string roomName)
    {
        if (string.IsNullOrWhiteSpace(roomName)) return null;
        string normalized = roomName.Trim().ToUpperInvariant();
        foreach (DesertBatflyColonyState state in colonies.Values)
            if (state.RoomName == normalized) return state;
        return null;
    }

    internal static DesertBatflyColonyState TryGetColony(AbstractRoom room)
    {
        if (room == null || room.world?.region == null) return null;
        colonies.TryGetValue(ColonyKey(room.world.region.name, room.name), out DesertBatflyColonyState state);
        return state;
    }

    internal static IndividualRecord RecordFor(AbstractCreature creature, bool create = true)
    {
        if (creature == null) return null;
        string key = IdentityKey(creature.ID);
        if (individuals.TryGetValue(key, out IndividualRecord record)) return record;
        if (!create) return null;
        record = new IndividualRecord(creature.ID);
        individuals.Add(key, record);
        return record;
    }

    internal static void EnsureIndividualOwnership(AbstractCreature creature)
    {
        if (!IsDesertBatfly(creature)) return;
        TrackCreature(creature);
        IndividualRecord record = RecordFor(creature);
        if (!string.IsNullOrEmpty(record.CurrentColony)) return;
        AbstractRoom physical = creature.Room;
        if (DesertSwarmRoom.IsDesertSwarmRoom(physical))
            record.CurrentColony = physical.name.Trim().ToUpperInvariant();
    }

    internal static void CollectOwnedBats(string colonyRoom, List<AbstractCreature> output)
    {
        output?.Clear();
        if (output == null || string.IsNullOrWhiteSpace(colonyRoom)) return;
        string normalized = colonyRoom.Trim().ToUpperInvariant();
        foreach (AbstractCreature creature in trackedBats.Values)
        {
            if (!LivingDesertBatfly(creature)) continue;
            IndividualRecord record = RecordFor(creature, false);
            if (record != null && string.Equals(
                    record.CurrentColony, normalized, StringComparison.OrdinalIgnoreCase))
                output.Add(creature);
        }
    }

    internal static void CompletePermanentMigration(AbstractCreature creature, string destinationRoom, int cycle)
    {
        if (!IsDesertBatfly(creature) || string.IsNullOrWhiteSpace(destinationRoom)) return;
        EnsureIndividualOwnership(creature);
        IndividualRecord record = RecordFor(creature);
        string destination = destinationRoom.Trim().ToUpperInvariant();
        if (!string.Equals(record.CurrentColony, destination, StringComparison.OrdinalIgnoreCase))
        {
            record.PreviousColony = record.CurrentColony;
            record.CurrentColony = destination;
        }
        record.PendingMigrationColony = string.Empty;
        record.LastMigrationCycle = cycle;
        RefreshPopulationCounts(creature.world);
    }

    internal static void ReportDeath(DesertBatfly victim, Creature killer)
    {
        if (victim?.abstractCreature == null || victim.world == null) return;
        EnsureWorld(victim.world);
        EnsureIndividualOwnership(victim.abstractCreature);
        string identity = IdentityKey(victim.abstractCreature.ID);
        if (!deathsThisCycle.Add(identity)) return;

        IndividualRecord record = RecordFor(victim.abstractCreature, false);
        DesertBatflyColonyState colony = TryGetColony(record?.CurrentColony);
        if (colony == null && DesertSwarmRoom.IsDesertSwarmRoom(victim.abstractCreature.Room))
            colony = TryGetColony(victim.abstractCreature.Room);
        colony?.RecordDeath(IsPeach(killer));
    }

    internal static void SampleRoom(Room room, float sampleSeconds)
    {
        if (room?.world == null || room.abstractRoom == null || sampleSeconds <= 0f) return;
        EnsureWorld(room.world);
        DesertBatflyColonyState colony = TryGetColony(room.abstractRoom);
        if (colony == null) return;

        DesertBatflyWeatherEcologySample weather =
            DesertBatflyWeatherEcology.Sample(room.world, room.abstractRoom);
        colony.SampleEnvironment(weather.MigrationStress, sampleSeconds);

        bool predatorPresent = false;
        bool guardingHive = false;
        for (int i = 0; i < room.abstractRoom.creatures.Count; i++)
        {
            AbstractCreature abs = room.abstractRoom.creatures[i];
            if (abs?.realizedCreature is not Creature creature || !IsPeach(creature) || creature.dead) continue;
            predatorPresent = true;
            if (room.hives != null && room.hives.Length > 0 && room.hives[0] != null && room.hives[0].Length > 0)
            {
                Vector2 hive = room.MiddleOfTile(room.hives[0][0]);
                guardingHive |= Vector2.Distance(creature.mainBodyChunk.pos, hive) <= 280f;
            }
        }
        if (predatorPresent) colony.SamplePredatorPresence(sampleSeconds, guardingHive);
    }

    internal static void ReportExternalRefuge(string homeColony, string refugeRoom, float severity)
    {
        DesertBatflyColonyState colony = TryGetColony(homeColony);
        if (colony == null) return;
        colony.RecordShelterFailure(severity);
        if (!string.IsNullOrWhiteSpace(refugeRoom))
            colony.LastSuccessfulRefuge = refugeRoom.Trim().ToUpperInvariant();
    }

    internal static void EnsureWorld(World world)
    {
        if (world?.abstractRooms == null || world.region == null || world.game == null) return;
        bool changed = activeWorld == null || !activeWorld.IsAlive || !ReferenceEquals(activeWorld.Target, world);
        if (!changed) return;

        activeWorld = new WeakReference(world);
        deathsThisCycle.Clear();
        RebuildWorldIndex(world);
        DesertBatflyRefuge.Reset();

        CreatureTemplate template = StaticWorld.GetCreatureTemplate(DesertBatflyDefinition.CreatureType);
        for (int i = 0; i < colonyRooms.Count; i++)
        {
            AbstractRoom room = colonyRooms[i];
            int physical = CountPhysical(room);
            string key = ColonyKey(world.region.name, room.name);
            if (!colonies.TryGetValue(key, out DesertBatflyColonyState colony))
            {
                int preferred = PreferredPopulation(room, physical);
                colony = new DesertBatflyColonyState(world.region.name, room.name, preferred);
                colonies.Add(key, colony);

                // First Task-09 bootstrap only. Once this ledger entry exists, an empty
                // colony can recover only through slow background recovery or immigration.
                if (physical == 0 && template != null)
                {
                    for (int n = 0; n < preferred; n++)
                    {
                        AbstractCreature created = CreateAbstract(world, room, template);
                        if (created == null) break;
                        IndividualRecord record = RecordFor(created);
                        record.CurrentColony = colony.RoomName;
                        physical++;
                    }
                }
            }
            else
            {
                colony.ConfigurePopulation(Mathf.Max(colony.PreferredPopulation,
                    PreferredPopulation(room, physical)));
            }
        }

        foreach (AbstractCreature creature in trackedBats.Values)
            EnsureIndividualOwnership(creature);
        RefreshPopulationCounts(world);

        // Restore persisted migration routes only after room and ownership indexes exist.
        DesertBatflyTravelNavigation.OnWorldChanged(world);
    }

    internal static int CurrentCycle(World world)
    {
        try { return world?.game?.GetStorySession?.saveState?.cycleNumber ?? 0; }
        catch { return 0; }
    }

    internal static AbstractRoom FindRoom(World world, string roomName)
    {
        if (world?.abstractRooms == null || string.IsNullOrWhiteSpace(roomName)) return null;
        if (activeWorld?.IsAlive == true && ReferenceEquals(activeWorld.Target, world) &&
            roomLookup.TryGetValue(roomName.Trim().ToUpperInvariant(), out AbstractRoom indexed))
            return indexed;

        for (int i = 0; i < world.abstractRooms.Length; i++)
        {
            AbstractRoom room = world.abstractRooms[i];
            if (room != null && string.Equals(room.name, roomName, StringComparison.OrdinalIgnoreCase))
                return room;
        }
        return null;
    }

    internal static float PredatorRisk(AbstractRoom room)
    {
        DesertBatflyColonyState colony = TryGetColony(room);
        if (colony != null) return colony.PredatorPressure;
        if (room?.creatures == null) return 0f;
        for (int i = 0; i < room.creatures.Count; i++)
            if (room.creatures[i]?.realizedCreature is Creature creature && IsPeach(creature) && !creature.dead)
                return 0.65f;
        return 0f;
    }

    internal static float RefugeCrowding(AbstractRoom room)
    {
        if (room?.creatures == null) return 0f;
        int bats = 0;
        for (int i = 0; i < room.creatures.Count; i++)
            if (LivingDesertBatfly(room.creatures[i])) bats++;
        return Mathf.Clamp01(bats / 18f);
    }

    private static string SaveState_SaveToString(On.SaveState.orig_SaveToString orig, SaveState self)
    {
        PrepareSave(self);
        return orig(self);
    }

    private static void SaveState_LoadGame(On.SaveState.orig_LoadGame orig, SaveState self,
        string str, RainWorldGame game)
    {
        orig(self, str, game);
        LoadLedger(self);
    }

    private static void SaveState_SessionEnded(On.SaveState.orig_SessionEnded orig, SaveState self,
        RainWorldGame game, bool survived, bool newMalnourished)
    {
        if (survived && game?.world != null)
            SettleSurvivedCycle(game.world, self?.cycleNumber ?? 0, self?.seed ?? 0);
        orig(self, game, survived, newMalnourished);
    }

    private static void RainCycle_Update(On.RainCycle.orig_Update orig, RainCycle self)
    {
        orig(self);
        if (self?.world?.game == null || ++abstractTick < 120) return;
        abstractTick = 0;
        EnsureWorld(self.world);
        DesertBatflyTravelNavigation.UpdateAbstractWorld(self.world);
        DesertBatflyTravelNavigation.EvaluateColonyWeather(self.world);
    }

    private static void SettleSurvivedCycle(World world, int cycle, int saveSeed)
    {
        EnsureWorld(world);
        RefreshPopulationCounts(world);
        List<DesertBatflyColonyState> regional = RegionColonies(world);
        float regionalEnvironment = DesertBatflyColonyMigration.RegionalEnvironmentalAverage(regional);

        for (int i = 0; i < regional.Count; i++)
            regional[i].SettleCycle(regional[i].CurrentPopulation, regionalEnvironment, cycle);

        regionalEnvironment = DesertBatflyColonyMigration.RegionalEnvironmentalAverage(regional);
        for (int i = 0; i < regional.Count; i++)
        {
            DesertBatflyColonyState colony = regional[i];
            colony.RegionalWeatherStress = regionalEnvironment;
            colony.RelativeHabitatStress = DesertBatflyColonyState.ComputeRelativeHabitatStress(
                colony.EnvironmentalPressure, regionalEnvironment, colony.ShelterFailureMemory);
            colony.MigrationPressure = DesertBatflyColonyState.ComputeMigrationPressure(
                colony.MortalityPressure, colony.PredatorPressure,
                colony.RelativeHabitatStress, colony.ShelterFailureMemory,
                colony.CurrentPopulation, colony.PreferredPopulation);
            colony.MigrationActive = DesertBatflyColonyState.NextMigrationState(
                colony.MigrationActive, colony.MigrationPressure);
            colony.RecalculateRecoveryCeiling();
        }

        for (int i = 0; i < regional.Count; i++)
            ScheduleMigrationBatch(world, regional[i], cycle);
        RefreshPopulationCounts(world);
        for (int i = 0; i < regional.Count; i++)
            TryNaturalRecovery(world, regional[i], cycle, saveSeed);
        RefreshPopulationCounts(world);
        deathsThisCycle.Clear();
    }

    private static void ScheduleMigrationBatch(World world, DesertBatflyColonyState source, int cycle)
    {
        if (!DesertBatflyColonyMigration.CanScheduleBatch(source)) return;
        CollectOwnedBats(source.RoomName, sourceMembersScratch);
        if (sourceMembersScratch.Count == 0 ||
            !TryChooseMigrationDestination(world, source, sourceMembersScratch,
                out DesertBatflyColonyState destination, out DesertBatflyWorldRoute sharedRoute))
            return;

        int wanted = source.RecommendedBatchSize();
        if (wanted <= 0) return;
        List<(AbstractCreature Creature, float Score)> candidates = new();
        for (int i = 0; i < sourceMembersScratch.Count; i++)
        {
            AbstractCreature creature = sourceMembersScratch[i];
            if (!LivingDesertBatfly(creature)) continue;
            IndividualRecord record = RecordFor(creature);
            if (!string.IsNullOrEmpty(record.PendingMigrationColony) ||
                creature.state is not DesertBatflyState state)
                continue;

            float capability = AbstractPhysicalCapability(state);
            bool severe = state.WingMean >= 0.60f ||
                          Mathf.Max(state.LeftWingInjury, state.RightWingInjury) >= 0.82f;
            bool recovering = creature.realizedCreature is DesertBatfly realized && realized.Injury.IsRecovering;
            float bondAtHome = BondPartnerOwnedBy(state.SocialBondTarget, source.RoomName)
                ? state.SocialBondStrength : state.SocialBondStrength * 0.25f;
            float score = DesertBatflyColonyMigration.IndividualPropensity(
                state.Personality, capability, severe, recovering,
                DesertBatflyColonyMigration.ActiveTrauma(state), bondAtHome,
                source.ShelterFailureMemory, cycle, record.LastMigrationCycle);
            if (score > 0f) candidates.Add((creature, score));
        }

        candidates.Sort((a, b) =>
        {
            int byScore = b.Score.CompareTo(a.Score);
            if (byScore != 0) return byScore;
            return a.Creature.ID.number.CompareTo(b.Creature.ID.number);
        });

        int selected = Mathf.Min(wanted, candidates.Count);
        for (int i = 0; i < selected; i++)
        {
            AbstractCreature creature = candidates[i].Creature;
            IndividualRecord record = RecordFor(creature);
            record.PendingMigrationColony = destination.RoomName;
            int stagger = 60 + StableInt(creature.ID.RandomSeed ^ cycle * 7919, 0, 360);
            DesertBatflyTravelNavigation.RequestPermanentMigration(
                creature, destination.RoomName, sharedRoute, stagger);
        }
        if (selected <= 0) return;

        source.LastOutboundBatch = selected;
        source.LastMigrationCycle = cycle;
        source.ColonyMigrationCooldown = DesertBatflyColonyMigration.ColonyCooldownCycles;
        destination.LastInboundBatch += selected;
    }

    private static bool TryChooseMigrationDestination(
        World world,
        DesertBatflyColonyState source,
        List<AbstractCreature> sourceMembers,
        out DesertBatflyColonyState destination,
        out DesertBatflyWorldRoute route)
    {
        destination = null;
        route = default;
        AbstractRoom sourceRoom = FindRoom(world, source.RoomName);
        CreatureTemplate template = StaticWorld.GetCreatureTemplate(DesertBatflyDefinition.CreatureType);
        if (sourceRoom == null || template == null) return false;

        float best = float.NegativeInfinity;
        List<DesertBatflyColonyState> regional = RegionColonies(world);
        for (int i = 0; i < regional.Count; i++)
        {
            DesertBatflyColonyState candidate = regional[i];
            if (candidate == source) continue;
            AbstractRoom targetRoom = FindRoom(world, candidate.RoomName);
            if (targetRoom == null || !DesertSwarmRoom.IsDesertSwarmRoom(targetRoom)) continue;

            if (!DesertBatflyWorldRoutePlanner.TryPlan(
                    world, sourceRoom.index, targetRoom.index, template,
                    DesertBatflyTravelPurpose.ColonyMigration,
                    r => PermanentTravelRisk(world, r),
                    DesertBatflyWorldRoutePlanner.MigrationMaxHops,
                    out DesertBatflyWorldRoute candidateRoute))
                continue;

            float travel = DesertBatflyWorldRoutePlanner.NormalizedTravelCost(
                candidateRoute, DesertBatflyWorldRoutePlanner.MigrationMaxHops);
            float habitat = Mathf.Clamp01(1f -
                candidate.EnvironmentalPressure * 0.50f -
                candidate.PredatorPressure * 0.20f -
                candidate.ShelterFailureMemory * 0.30f);
            DestinationAffinity(sourceMembers, candidate.RoomName,
                out float familiarity, out float bondPresence);
            float score = DesertBatflyColonyMigration.DestinationSuitability(
                candidate, habitat, travel, familiarity, bondPresence);
            if (score <= best) continue;
            best = score;
            destination = candidate;
            route = candidateRoute;
        }
        return destination != null && route.Valid && best > -0.35f;
    }

    private static void DestinationAffinity(
        List<AbstractCreature> sourceMembers,
        string destinationRoom,
        out float formerColonyFamiliarity,
        out float bondPartnerPresence)
    {
        formerColonyFamiliarity = 0f;
        bondPartnerPresence = 0f;
        if (sourceMembers == null || sourceMembers.Count == 0 || string.IsNullOrWhiteSpace(destinationRoom))
            return;

        int formerResidents = 0;
        float strongestBond = 0f;
        for (int i = 0; i < sourceMembers.Count; i++)
        {
            AbstractCreature creature = sourceMembers[i];
            IndividualRecord record = RecordFor(creature, false);
            if (record != null && string.Equals(
                    record.PreviousColony, destinationRoom, StringComparison.OrdinalIgnoreCase))
                formerResidents++;

            if (creature?.state is not DesertBatflyState state ||
                !state.SocialBondTarget.HasValue || state.SocialBondStrength <= strongestBond)
                continue;
            if (BondPartnerOwnedBy(state.SocialBondTarget, destinationRoom))
                strongestBond = state.SocialBondStrength;
        }

        // Both are deliberately weak. A recovered former colony wins only when its
        // actual safety/capacity is already competitive; familiarity never overrides danger.
        formerColonyFamiliarity = Mathf.Clamp01(
            formerResidents / Mathf.Max(1f, sourceMembers.Count * 0.30f));
        bondPartnerPresence = Mathf.Clamp01(strongestBond);
    }

    private static float PermanentTravelRisk(World world, AbstractRoom room)
    {
        DesertBatflyWeatherEcologySample weather = DesertBatflyWeatherEcology.Sample(world, room);
        if (weather.LethalNow) return 8f;
        return Mathf.Clamp01(weather.TravelExposure * 0.78f + PredatorRisk(room) * 0.22f);
    }

    private static void TryNaturalRecovery(World world, DesertBatflyColonyState colony, int cycle, int saveSeed)
    {
        if (colony.CurrentPopulation >= colony.NaturalRecoveryCeiling) return;
        AbstractRoom room = FindRoom(world, colony.RoomName);
        if (room == null) return;
        DesertBatflyWeatherEcologySample weather = DesertBatflyWeatherEcology.Sample(world, room);
        if (weather.LethalNow || weather.ImmediateDanger >= 0.82f) return;

        float roll = Stable01(saveSeed, cycle, colony.Key, 0x51A7);
        if (roll > colony.RecoveryChance()) return;
        CreatureTemplate template = StaticWorld.GetCreatureTemplate(DesertBatflyDefinition.CreatureType);
        AbstractCreature created = CreateAbstract(world, room, template);
        if (created == null) return;
        IndividualRecord record = RecordFor(created);
        record.CurrentColony = colony.RoomName;
        colony.CurrentPopulation++;
    }

    private static AbstractCreature CreateAbstract(World world, AbstractRoom room, CreatureTemplate template)
    {
        if (world?.game == null || room == null || template == null) return null;
        int node = ColonyNode(room, template);
        WorldCoordinate coordinate = new(room.index, -1, -1, node);
        AbstractCreature creature = new(world, template, null, coordinate, world.game.GetNewID());
        if (creature.state is DesertBatflyState state) state.InHive = room.batHives > 0;
        room.AddEntity(creature);
        TrackCreature(creature);
        return creature;
    }

    private static int ColonyNode(AbstractRoom room, CreatureTemplate template)
    {
        if (room?.nodes == null || room.nodes.Length == 0) return -1;
        int fallback = -1;
        for (int i = 0; i < room.nodes.Length; i++)
        {
            if (template != null && room.CommonToCreatureSpecificNodeIndex(i, template) < 0) continue;
            if (fallback < 0) fallback = i;
            if (room.nodes[i].type == AbstractRoomNode.Type.BatHive) return i;
        }
        return fallback;
    }

    private static int PreferredPopulation(AbstractRoom room, int existing)
    {
        int ecologicalBaseline = 8 + Mathf.Max(1, room?.batHives ?? 0) * 6;
        int legacyBaseline = DesertBatflyTuning.HivePopulation + DesertBatflyTuning.CurvePopulation;
        return Mathf.Clamp(Mathf.Max(existing, ecologicalBaseline, legacyBaseline), 6, 40);
    }

    private static int CountPhysical(AbstractRoom room)
    {
        if (room?.creatures == null) return 0;
        int count = 0;
        for (int i = 0; i < room.creatures.Count; i++)
            if (LivingDesertBatfly(room.creatures[i])) count++;
        return count;
    }

    private static void RefreshPopulationCounts(World world)
    {
        if (world == null) return;
        string region = world.region?.name?.Trim().ToUpperInvariant();
        foreach (DesertBatflyColonyState colony in colonies.Values)
            if (!string.IsNullOrEmpty(region) && colony.RegionName == region)
                colony.CurrentPopulation = 0;

        staleBatKeys.Clear();
        foreach (KeyValuePair<string, AbstractCreature> pair in trackedBats)
        {
            AbstractCreature creature = pair.Value;
            if (creature == null || creature.world != world || creature.slatedForDeletion)
            {
                staleBatKeys.Add(pair.Key);
                continue;
            }
            if (!LivingDesertBatfly(creature)) continue;
            EnsureIndividualOwnership(creature);
            IndividualRecord record = RecordFor(creature, false);
            DesertBatflyColonyState colony = TryGetColony(record?.CurrentColony);
            if (colony != null) colony.CurrentPopulation++;
        }
        for (int i = 0; i < staleBatKeys.Count; i++) trackedBats.Remove(staleBatKeys[i]);
    }

    private static List<DesertBatflyColonyState> RegionColonies(World world)
    {
        List<DesertBatflyColonyState> result = new();
        string region = world?.region?.name?.Trim().ToUpperInvariant();
        if (string.IsNullOrEmpty(region)) return result;
        foreach (DesertBatflyColonyState colony in colonies.Values)
            if (colony.RegionName == region) result.Add(colony);
        return result;
    }

    private static bool BondPartnerOwnedBy(EntityID? partner, string colonyRoom)
    {
        if (!partner.HasValue || string.IsNullOrEmpty(colonyRoom)) return false;
        if (!individuals.TryGetValue(IdentityKey(partner.Value), out IndividualRecord record)) return false;
        return string.Equals(record.CurrentColony, colonyRoom, StringComparison.OrdinalIgnoreCase);
    }

    private static float AbstractPhysicalCapability(DesertBatflyState state)
    {
        if (state == null) return 0f;
        float wingSeverity = Mathf.SmoothStep(0f, 1f, state.WingMean);
        return Mathf.Clamp01(1f -
            0.18f * (1f - Mathf.Clamp01(state.health)) -
            0.46f * wingSeverity -
            0.15f * state.WingAsymmetry);
    }

    private static void RebuildWorldIndex(World world)
    {
        roomLookup = new Dictionary<string, AbstractRoom>(StringComparer.OrdinalIgnoreCase);
        colonyRooms = new List<AbstractRoom>();
        trackedBats = new Dictionary<string, AbstractCreature>(StringComparer.Ordinal);
        if (world?.abstractRooms == null) return;

        for (int r = 0; r < world.abstractRooms.Length; r++)
        {
            AbstractRoom room = world.abstractRooms[r];
            if (room == null) continue;
            roomLookup[room.name.Trim().ToUpperInvariant()] = room;
            if (DesertSwarmRoom.IsDesertSwarmRoom(room)) colonyRooms.Add(room);
            if (room.creatures == null) continue;
            for (int i = 0; i < room.creatures.Count; i++)
                TrackCreature(room.creatures[i]);
        }
    }

    private static void TrackCreature(AbstractCreature creature)
    {
        if (!IsDesertBatfly(creature)) return;
        trackedBats[IdentityKey(creature.ID)] = creature;
    }

    private static void PrepareSave(SaveState save)
    {
        if (save?.unrecognizedSaveStrings == null) return;
        if (activeWorld?.IsAlive == true && activeWorld.Target is World world)
            RefreshPopulationCounts(world);
        for (int i = save.unrecognizedSaveStrings.Count - 1; i >= 0; i--)
            if (save.unrecognizedSaveStrings[i]?.StartsWith(SavePrefix, StringComparison.Ordinal) == true)
                save.unrecognizedSaveStrings.RemoveAt(i);
        save.unrecognizedSaveStrings.Add(SavePrefix + Encode(SerializeLedger()));
    }

    private static string SerializeLedger()
    {
        StringBuilder b = new();
        b.AppendLine(PayloadVersion);
        foreach (DesertBatflyColonyState colony in colonies.Values)
            b.Append("C|").Append(Encode(colony.Serialize())).Append('\n');
        foreach (IndividualRecord record in individuals.Values)
        {
            string line = string.Join(";", new[]
            {
                record.Key,
                Encode(record.CurrentColony),
                Encode(record.PreviousColony),
                record.LastMigrationCycle.ToString(CultureInfo.InvariantCulture),
                Encode(record.PendingMigrationColony)
            });
            b.Append("I|").Append(Encode(line)).Append('\n');
        }
        return b.ToString();
    }

    private static void LoadLedger(SaveState save)
    {
        colonies.Clear();
        individuals.Clear();
        deathsThisCycle.Clear();
        roomLookup.Clear();
        colonyRooms.Clear();
        trackedBats.Clear();
        activeWorld = null;
        DesertBatflyRefuge.Reset();
        DesertBatflyTravelNavigation.Reset();
        if (save?.unrecognizedSaveStrings == null) return;

        string encoded = null;
        for (int i = 0; i < save.unrecognizedSaveStrings.Count; i++)
        {
            string raw = save.unrecognizedSaveStrings[i];
            if (raw?.StartsWith(SavePrefix, StringComparison.Ordinal) == true)
            {
                encoded = raw.Substring(SavePrefix.Length);
                break;
            }
        }
        string payload = Decode(encoded);
        if (string.IsNullOrEmpty(payload)) return;
        string[] lines = payload.Replace("\r", string.Empty).Split('\n');
        if (lines.Length == 0 || lines[0] != PayloadVersion) return;
        for (int i = 1; i < lines.Length; i++)
        {
            string line = lines[i];
            if (line.StartsWith("C|", StringComparison.Ordinal))
            {
                if (DesertBatflyColonyState.TryDeserialize(Decode(line.Substring(2)), out DesertBatflyColonyState colony))
                    colonies[colony.Key] = colony;
            }
            else if (line.StartsWith("I|", StringComparison.Ordinal))
            {
                string decoded = Decode(line.Substring(2));
                string[] v = decoded.Split(';');
                if (v.Length < 4 || !TryIdentity(v[0], out EntityID id)) continue;
                IndividualRecord record = new(id)
                {
                    CurrentColony = Decode(v[1]),
                    PreviousColony = Decode(v[2]),
                    LastMigrationCycle = ParseInt(v[3], int.MinValue),
                    PendingMigrationColony = v.Length > 4 ? Decode(v[4]) : string.Empty
                };
                individuals[record.Key] = record;
            }
        }
    }

    private static bool LivingDesertBatfly(AbstractCreature creature) =>
        IsDesertBatfly(creature) && creature.state?.alive != false && !creature.slatedForDeletion;

    private static bool IsDesertBatfly(AbstractCreature creature) =>
        creature?.creatureTemplate?.type == DesertBatflyDefinition.CreatureType;

    private static bool IsPeach(Creature creature) =>
        ModManager.Watcher && creature is Lizard lizard && lizard.Template?.type == WatcherEnums.CreatureTemplateType.PeachLizard;

    private static string ColonyKey(string region, string room) =>
        (region ?? string.Empty).Trim().ToUpperInvariant() + "|" +
        (room ?? string.Empty).Trim().ToUpperInvariant();

    private static string IdentityKey(EntityID id) =>
        id.spawner.ToString(CultureInfo.InvariantCulture) + "," +
        id.number.ToString(CultureInfo.InvariantCulture);

    private static bool TryIdentity(string raw, out EntityID id)
    {
        id = default;
        string[] p = (raw ?? string.Empty).Split(',');
        if (p.Length != 2 ||
            !int.TryParse(p[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int spawner) ||
            !int.TryParse(p[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int number))
            return false;
        id = new EntityID(spawner, number);
        return true;
    }

    private static int ParseInt(string raw, int fallback) =>
        int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value) ? value : fallback;

    private static string Encode(string text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(text));
    }

    private static string Decode(string text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        try { return Encoding.UTF8.GetString(Convert.FromBase64String(text)); }
        catch { return string.Empty; }
    }

    private static float Stable01(int seed, int cycle, string key, int salt)
    {
        unchecked
        {
            uint h = 2166136261u;
            h = (h ^ (uint)seed) * 16777619u;
            h = (h ^ (uint)cycle) * 16777619u;
            h = (h ^ (uint)salt) * 16777619u;
            for (int i = 0; i < (key?.Length ?? 0); i++) h = (h ^ key[i]) * 16777619u;
            return (h & 0x00FFFFFFu) / 16777215f;
        }
    }

    private static int StableInt(int seed, int minInclusive, int maxExclusive)
    {
        if (maxExclusive <= minInclusive) return minInclusive;
        unchecked
        {
            uint x = (uint)seed;
            x ^= x >> 16; x *= 0x7FEB352Du; x ^= x >> 15; x *= 0x846CA68Bu; x ^= x >> 16;
            return minInclusive + (int)(x % (uint)(maxExclusive - minInclusive));
        }
    }
}
