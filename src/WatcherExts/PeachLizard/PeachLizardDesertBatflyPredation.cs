using DryCycle.Creatures.DesertBatfly;
using RWCustom;
using UnityEngine;
using Watcher;

namespace DryCycle.WatcherExts.PeachLizard;

/// <summary>
/// Makes Desert Batfly a native Peach Lizard prey target while preserving Watcher's
/// own hunting, tongue, bite and return-prey pipeline.
///
/// Living bats use the normal PreyTracker -> Hunt -> AggressiveBehavior -> ShootTongue
/// flow. Dead bats with edible remains stay valid prey and are deliberately treated as
/// lower-value scavenging targets; once grasped, vanilla LizardAI switches to ReturnPrey
/// because the Peach->DesertBatfly relationship remains Eats. Safe DryCycle sand is only
/// an optional approach preference for living prey, never a mandatory prerequisite.
/// </summary>
internal static class PeachLizardDesertBatflyPredation
{
    private const float TongueBiteTransferDistance = 18f;
    private const float SurfaceHuntSandBonus = 0.55f;
    private const float BuriedHuntSandBonus = 1.10f;
    private const float HighPreyPenaltyHeight = 230f;

    // Corpses are worthwhile, but living prey should normally remain the more attractive
    // option. Remaining bites scale scavenging value so a half-eaten carcass is still food
    // without competing equally with a fresh, intact bat.
    private const float CorpseAttractivenessMin = 0.28f;
    private const float CorpseAttractivenessMax = 0.78f;
    private const float CorpseCloseTongueBonus = 1.12f;

    private static bool _enabled;

    internal static void Enable()
    {
        if (_enabled) return;
        _enabled = true;
        On.PreyTracker.TrackedPrey.Attractiveness += TrackedPrey_Attractiveness;
        On.PreyTracker.Utility += PreyTracker_Utility;
        On.LizardAI.TravelPreference += LizardAI_TravelPreference;
        On.LizardTongue.Update += LizardTongue_Update;
    }

    internal static void Disable()
    {
        if (!_enabled) return;
        _enabled = false;
        On.PreyTracker.TrackedPrey.Attractiveness -= TrackedPrey_Attractiveness;
        On.PreyTracker.Utility -= PreyTracker_Utility;
        On.LizardAI.TravelPreference -= LizardAI_TravelPreference;
        On.LizardTongue.Update -= LizardTongue_Update;
    }

    private static float TrackedPrey_Attractiveness(
        On.PreyTracker.TrackedPrey.orig_Attractiveness orig,
        PreyTracker.TrackedPrey self)
    {
        float result = orig(self);
        if (self?.owner?.AI is not LizardAI ai ||
            !IsPeach(ai.lizard) ||
            self.critRep?.representedCreature?.realizedCreature is not DesertBatfly prey ||
            prey.room == null || prey.room != ai.lizard.room || prey.inShortcut)
        {
            return result;
        }

        // A completely consumed/deleting bat has no food value. Likewise, Peach does not
        // actively contest a bat currently held by a player. NegativeInfinity is used
        // instead of zero because PreyTracker still selects a zero-attractiveness entry
        // when it is the only tracked prey.
        if (!HasEdibleRemains(prey) || IsHeldByPlayer(prey))
            return float.NegativeInfinity;

        Lizard lizard = ai.lizard;
        float distance = Vector2.Distance(lizard.mainBodyChunk.pos, prey.mainBodyChunk.pos);
        float verticalGap = prey.mainBodyChunk.pos.y - lizard.mainBodyChunk.pos.y;

        if (prey.dead)
        {
            float factor = CorpseFoodValue(prey);

            // A corpse already within easy tongue reach is cheap food, but even an intact
            // carcass should normally remain below a healthy living bat at comparable range.
            if (self.critRep.VisualContact &&
                distance <= lizard.lizardParams.tongueAttackRange * 1.10f)
            {
                factor *= CorpseCloseTongueBonus;
            }

            if (verticalGap > 0f)
            {
                factor *= Mathf.Lerp(
                    1f,
                    0.70f,
                    Mathf.InverseLerp(170f, 340f, verticalGap));
            }

            return result * Mathf.Clamp(factor, 0.20f, 0.90f);
        }

        float speed = prey.mainBodyChunk.vel.magnitude;

        // Native PreyTracker already handles relationship strength, route distance,
        // reachability and memory. This factor only describes how catchable this small
        // flying prey is with Peach's own tongue.
        float liveFactor = 1f;

        if (prey.AI?.behavior == FlyAI.Behavior.Chain)
            liveFactor *= 1.28f;

        liveFactor *= Mathf.Lerp(1.13f, 0.78f, Mathf.InverseLerp(2.5f, 11f, speed));

        if (verticalGap > 0f)
        {
            liveFactor *= Mathf.Lerp(
                1.08f,
                0.56f,
                Mathf.InverseLerp(90f, 340f, verticalGap));
        }

        bool directTongueOpportunity =
            self.critRep.VisualContact &&
            distance <= lizard.lizardParams.tongueAttackRange * 1.12f;
        if (directTongueOpportunity)
            liveFactor *= 1.20f;

        return result * Mathf.Clamp(liveFactor, 0.48f, 1.55f);
    }

    private static float PreyTracker_Utility(
        On.PreyTracker.orig_Utility orig,
        PreyTracker self)
    {
        float result = orig(self);
        if (self?.AI is not LizardAI ai || !IsPeach(ai.lizard) ||
            self.MostAttractivePrey?.representedCreature?.realizedCreature is not DesertBatfly prey)
        {
            return result;
        }

        if (!HasEdibleRemains(prey) || IsHeldByPlayer(prey))
            return 0f;

        // TrackedPrey.Attractiveness chooses between prey entries; PreyTracker.Utility
        // competes against Lurk/Travel/Fear/etc. Scale both for corpses so a one-bite scrap
        // is genuinely opportunistic rather than retaining the full Eats relationship's
        // behavioral urgency. Once grasped, vanilla LizardAI's ReturnPrey override sets
        // utility to 1 independently, so this does not weaken carrying food home.
        return prey.dead
            ? result * CorpseFoodValue(prey)
            : result;
    }

    private static PathCost LizardAI_TravelPreference(
        On.LizardAI.orig_TravelPreference orig,
        LizardAI self,
        MovementConnection connection,
        PathCost cost)
    {
        PathCost result = orig(self, connection, cost);
        Lizard lizard = self?.lizard;

        if (!IsPeach(lizard) || self.behavior != LizardAI.Behavior.Hunt ||
            self.preyTracker?.MostAttractivePrey?.representedCreature?.realizedCreature
                is not DesertBatfly prey ||
            prey.dead || !HasEdibleRemains(prey) || IsHeldByPlayer(prey) ||
            prey.room != lizard.room || prey.inShortcut ||
            !connection.destinationCoord.TileDefined ||
            connection.destinationCoord.room != lizard.room.abstractRoom.index)
        {
            return result;
        }

        float preyDistance = Vector2.Distance(
            lizard.mainBodyChunk.pos,
            prey.mainBodyChunk.pos);
        bool cleanTongueOpportunity =
            self.preyTracker.MostAttractivePrey.VisualContact &&
            preyDistance <= lizard.lizardParams.tongueAttackRange * 1.05f;

        // A direct flat-ground/tongue opportunity always wins. Sand never becomes a
        // mandatory prerequisite for this prey relationship. Dead prey deliberately gets
        // no special hunting bonus: generic Peach terrain/pathing can still reach a corpse,
        // but there is no reason to perform a prey-specific underground ambush on food that
        // cannot escape.
        if (cleanTongueOpportunity ||
            !PeachLizardQuicksandSandMap.TryGetSafeSand(
                lizard.room,
                connection.destinationCoord,
                out float depth))
        {
            return result;
        }

        float bonus = lizard.firstChunk.buried
            ? BuriedHuntSandBonus
            : SurfaceHuntSandBonus;

        float comfortableDepth = 1f - Mathf.Clamp01(Mathf.Abs(depth - 0.42f) / 0.58f);
        bonus *= Mathf.Lerp(0.72f, 1f, comfortableDepth);

        float verticalGap = prey.mainBodyChunk.pos.y - lizard.mainBodyChunk.pos.y;
        if (verticalGap > HighPreyPenaltyHeight)
            bonus *= 0.30f;

        result.resistance = Mathf.Max(0f, result.resistance - bonus);
        return result;
    }

    private static void LizardTongue_Update(
        On.LizardTongue.orig_Update orig,
        LizardTongue self)
    {
        if (self == null || !IsPeach(self.lizard))
        {
            orig(self);
            return;
        }

        // Transfer an already-caught lightweight Desert Batfly into the ordinary
        // lizard bite/grasp pipeline shortly before vanilla clears the small-object
        // reference at the mouth threshold. This works for both live prey and edible
        // corpses, allowing vanilla ReturnPrey to carry the resulting grasp to the den.
        TryTransferTongueCatchToBite(self);

        LizardTongue.State previousState = self.state;
        BodyChunk previousAttached = self.attached;
        orig(self);

        if (self.state != LizardTongue.State.AttachedInSmallObject ||
            self.attached?.owner is not DesertBatfly caught)
        {
            return;
        }

        // Do not tug on a player-held bat and do not waste tongue time on an exhausted
        // carcass. This also closes the small race where the player grabs the corpse after
        // Peach has already committed to ShootTongue but before the tongue actually lands.
        if (!HasEdibleRemains(caught) || IsHeldByPlayer(caught))
        {
            self.Retract();
            return;
        }

        if (!caught.dead &&
            (previousState != LizardTongue.State.AttachedInSmallObject ||
             previousAttached?.owner != caught))
        {
            // Only a LIVE capture is a predator event. Picking up an existing corpse is
            // scavenging and must not generate a second Peach mortality/fear event.
            // Broadcast BEFORE Threatened() dismantles a hanging Fly Chain so the morale
            // layer can snapshot FirstInChain() and mark every chain-mate as a witness.
            DesertBatflyIntimidation.BroadcastPredatorCapture(caught, self.lizard, self);
            caught.DesertAI.Threatened(self.lizard, true);
        }
    }

    private static void TryTransferTongueCatchToBite(LizardTongue tongue)
    {
        Lizard lizard = tongue.lizard;
        if (lizard == null || lizard.room == null || !lizard.Consious ||
            lizard.grasps == null || lizard.grasps.Length == 0 ||
            lizard.grasps[0] != null ||
            tongue.state != LizardTongue.State.AttachedInSmallObject ||
            tongue.attached?.owner is not DesertBatfly bat ||
            !HasEdibleRemains(bat) || IsHeldByPlayer(bat) ||
            bat.room != lizard.room || bat.inShortcut ||
            !Custom.DistLess(
                tongue.attached.pos,
                lizard.mainBodyChunk.pos,
                TongueBiteTransferDistance))
        {
            return;
        }

        BodyChunk caughtChunk = tongue.attached;
        lizard.Bite(caughtChunk);

        // Lizard.Bite accepts dead Eats-relationship creatures as a normal grasp. Once
        // grasp[0] contains this bat, vanilla LizardAI immediately selects ReturnPrey with
        // utility 1 and routes to den. For live prey, DesertBatfly.Grabbed reports capture;
        // for a corpse, Grabbed intentionally stays silent so scavenging is not a kill event.
        if (lizard.grasps[0] != null && lizard.grasps[0].grabbed == bat)
            tongue.Retract();
    }

    internal static bool HasEdibleRemains(DesertBatfly bat)
    {
        return bat != null &&
               !bat.slatedForDeletetion &&
               !bat.DesertState.MealConsumed &&
               bat.bites > 0;
    }

    internal static bool IsHeldByPlayer(DesertBatfly bat)
    {
        if (bat?.grabbedBy == null) return false;
        for (int i = 0; i < bat.grabbedBy.Count; i++)
        {
            if (bat.grabbedBy[i]?.grabber is Player)
                return true;
        }
        return false;
    }

    private static float CorpseFoodValue(DesertBatfly prey)
    {
        float remaining = Mathf.Clamp01(prey.bites / 3f);
        return Mathf.Lerp(
            CorpseAttractivenessMin,
            CorpseAttractivenessMax,
            Mathf.Pow(remaining, 0.75f));
    }

    private static bool IsPeach(Lizard lizard)
    {
        return ModManager.Watcher &&
               lizard?.Template != null &&
               lizard.Template.type == WatcherEnums.CreatureTemplateType.PeachLizard;
    }
}
