using DryCycle.Creatures.DesertBatfly;
using RWCustom;
using UnityEngine;
using Watcher;

namespace DryCycle.WatcherExts.PeachLizard;

/// <summary>
/// Makes Desert Batfly a native Peach Lizard prey target while preserving Watcher's
/// own hunting, tongue, bite and return-prey pipeline.
///
/// Flat-ground hunting remains PreyTracker -> Hunt -> AggressiveBehavior -> ShootTongue.
/// Safe DryCycle sand is only an optional approach preference. Capture hooks also feed
/// Desert Batfly's mortality/morale layer so nearby colony members can panic, propagate
/// chain fear, or in very rare personalities attempt rescue/extreme vengeance.
/// </summary>
internal static class PeachLizardDesertBatflyPredation
{
    private const float TongueBiteTransferDistance = 18f;
    private const float SurfaceHuntSandBonus = 0.55f;
    private const float BuriedHuntSandBonus = 1.10f;
    private const float HighPreyPenaltyHeight = 230f;

    private static bool _enabled;

    internal static void Enable()
    {
        if (_enabled) return;
        _enabled = true;
        On.PreyTracker.TrackedPrey.Attractiveness += TrackedPrey_Attractiveness;
        On.LizardAI.TravelPreference += LizardAI_TravelPreference;
        On.LizardTongue.Update += LizardTongue_Update;
    }

    internal static void Disable()
    {
        if (!_enabled) return;
        _enabled = false;
        On.PreyTracker.TrackedPrey.Attractiveness -= TrackedPrey_Attractiveness;
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

        Lizard lizard = ai.lizard;
        float distance = Vector2.Distance(lizard.mainBodyChunk.pos, prey.mainBodyChunk.pos);
        float verticalGap = prey.mainBodyChunk.pos.y - lizard.mainBodyChunk.pos.y;
        float speed = prey.mainBodyChunk.vel.magnitude;

        // Native PreyTracker already handles relationship strength, route distance,
        // reachability and memory. This factor only describes how catchable this small
        // flying prey is with Peach's own tongue.
        float factor = 1f;

        if (!prey.dead && prey.AI?.behavior == FlyAI.Behavior.Chain)
            factor *= 1.28f;

        factor *= Mathf.Lerp(1.13f, 0.78f, Mathf.InverseLerp(2.5f, 11f, speed));

        if (verticalGap > 0f)
        {
            factor *= Mathf.Lerp(
                1.08f,
                0.56f,
                Mathf.InverseLerp(90f, 340f, verticalGap));
        }

        bool directTongueOpportunity =
            self.critRep.VisualContact &&
            distance <= lizard.lizardParams.tongueAttackRange * 1.12f;
        if (directTongueOpportunity)
            factor *= 1.20f;

        return result * Mathf.Clamp(factor, 0.48f, 1.55f);
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
            prey.dead || prey.room != lizard.room || prey.inShortcut ||
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
        // mandatory prerequisite for this prey relationship.
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
        // reference at the mouth threshold.
        TryTransferTongueCatchToBite(self);

        LizardTongue.State previousState = self.state;
        BodyChunk previousAttached = self.attached;
        orig(self);

        if (self.state == LizardTongue.State.AttachedInSmallObject &&
            self.attached?.owner is DesertBatfly caught &&
            !caught.dead &&
            (previousState != LizardTongue.State.AttachedInSmallObject ||
             previousAttached?.owner != caught))
        {
            // Broadcast BEFORE Threatened() dismantles a hanging Fly Chain. The
            // mortality layer can therefore snapshot FirstInChain() and mark every
            // chain-mate as a direct witness. The victim's own threat response follows.
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
            bat.slatedForDeletetion || bat.room != lizard.room || bat.inShortcut ||
            !Custom.DistLess(
                tongue.attached.pos,
                lizard.mainBodyChunk.pos,
                TongueBiteTransferDistance))
        {
            return;
        }

        BodyChunk caughtChunk = tongue.attached;
        lizard.Bite(caughtChunk);

        // If Bite actually created the vanilla grasp, DesertBatfly.Grabbed reports
        // that Peach capture too. BroadcastPredatorCapture has a 90-tick per-victim /
        // predator debounce, so the earlier tongue event remains the single event.
        if (lizard.grasps[0] != null && lizard.grasps[0].grabbed == bat)
            tongue.Retract();
    }

    private static bool IsPeach(Lizard lizard)
    {
        return ModManager.Watcher &&
               lizard?.Template != null &&
               lizard.Template.type == WatcherEnums.CreatureTemplateType.PeachLizard;
    }
}
