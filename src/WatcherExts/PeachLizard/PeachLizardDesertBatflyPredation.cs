using DryCycle.Creatures.DesertBatfly;
using RWCustom;
using UnityEngine;
using Watcher;

namespace DryCycle.WatcherExts.PeachLizard;

/// <summary>
/// Makes Desert Batfly a native Peach Lizard prey target while preserving Watcher's
/// own hunting, tongue, bite and return-prey pipeline.
///
/// This deliberately does not create a custom hunt state. Flat-ground hunting works
/// through the ordinary PreyTracker -> Hunt -> AggressiveBehavior -> ShootTongue path.
/// Safe DryCycle sand is only a mild optional route preference while the prey is still
/// outside a clean tongue opportunity.
///
/// One narrow compatibility fix is required for the tongue itself: LizardTongue treats
/// objects below 0.2 total mass as AttachedInSmallObject. Desert Batfly falls into that
/// branch, which can retract a free lightweight creature to the mouth without converting
/// it into the lizard's grasp. We therefore call the *vanilla Lizard.Bite* only once the
/// already-caught bat has physically reached the mouth. Collision, tongue flight,
/// attachment, retraction, grasp, damage, ShakePrey and ReturnPrey remain vanilla.
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

        // Native PreyTracker already handles relationship strength, estimated route
        // distance, reachability and memory. This factor only teaches Peach what makes
        // a tiny flying prey realistically catchable with its own tongue.
        float factor = 1f;

        // Hanging or nearly stationary bats are excellent tongue targets. Chain is
        // the native Fly behavior used by Desert Batfly's real roost implementation.
        if (!prey.dead && prey.AI?.behavior == FlyAI.Behavior.Chain)
            factor *= 1.28f;

        factor *= Mathf.Lerp(1.13f, 0.78f, Mathf.InverseLerp(2.5f, 11f, speed));

        if (verticalGap > 0f)
        {
            // Low air targets remain attractive; very high targets become a poor use
            // of hunting time even if their abstract tile is still remembered.
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

        // Keep the modifier bounded so native prey hierarchy and persistence remain
        // authoritative. It should bias target choice, never replace PreyTracker.
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

        // Hot path: leave every non-Peach connection after two cheap checks. There is
        // no creature list scan and no target search here; PreyTracker already owns it.
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

        // Once a normal tongue shot is available, do not bias the route back into
        // sand. This is what keeps ordinary flat-ground predation fully native.
        if (cleanTongueOpportunity ||
            !PeachLizardQuicksandSandMap.TryGetSafeSand(
                lizard.room,
                connection.destinationCoord,
                out float depth))
        {
            return result;
        }

        // Sand is an optional approach tool, never a requirement. The existing
        // Quicksand adapter has already removed Peach's accidental swimmer surcharge
        // on these verified safe cells; this small extra preference only matters while
        // actively hunting this species. A Peach already underground is more willing
        // to stay underground than one standing on an equally good flat route.
        float bonus = lizard.firstChunk.buried
            ? BuriedHuntSandBonus
            : SurfaceHuntSandBonus;

        float comfortableDepth = 1f - Mathf.Clamp01(Mathf.Abs(depth - 0.42f) / 0.58f);
        bonus *= Mathf.Lerp(0.72f, 1f, comfortableDepth);

        // Burrowing cannot solve a prey that is far above tongue reach. Keep only a
        // weak preference in that case so vanilla frustration/retargeting can win.
        float verticalGap = prey.mainBodyChunk.pos.y - lizard.mainBodyChunk.pos.y;
        if (verticalGap > HighPreyPenaltyHeight)
        {
            bonus *= 0.30f;
        }

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
        // lizard bite/grasp pipeline shortly before vanilla would finish retracting.
        // This runs before orig so the AttachedInSmallObject branch cannot clear the
        // reference at the 10px mouth threshold first.
        TryTransferTongueCatchToBite(self);

        LizardTongue.State previousState = self.state;
        BodyChunk previousAttached = self.attached;
        orig(self);

        // A fresh tongue hit on a hanging bat must immediately break the Fly chain.
        // Threatened() owns that existing behavior and raises the same local alarm as
        // every other real predator attack; no duplicate chain implementation here.
        if (self.state == LizardTongue.State.AttachedInSmallObject &&
            self.attached?.owner is DesertBatfly caught &&
            !caught.dead &&
            (previousState != LizardTongue.State.AttachedInSmallObject ||
             previousAttached?.owner != caught))
        {
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

        // Eats guarantees that vanilla Bite attempts a normal lizard grasp. Only
        // detach the tongue if that grasp really succeeded; otherwise let vanilla
        // tongue behavior continue without manufacturing ownership.
        if (lizard.grasps[0] != null && lizard.grasps[0].grabbed == bat)
        {
            tongue.Retract();
        }
    }

    private static bool IsPeach(Lizard lizard)
    {
        return ModManager.Watcher &&
               lizard?.Template != null &&
               lizard.Template.type == WatcherEnums.CreatureTemplateType.PeachLizard;
    }
}
