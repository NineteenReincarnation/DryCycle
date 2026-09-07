using DryCycle.Creatures.DesertBatfly;

namespace DryCycle.Debugging.AI;

/// <summary>
/// Social Observatory enrichment. Travel / Colony remains the inner source so colony/travel
/// diagnostics are preserved while this layer appends realized-only neutral social life.
/// </summary>
internal sealed class DB_SocialDebugSource : IAIDebugSource
{
    private readonly DB_TravelDebugSource inner = new();

    public int Priority => 1200;
    public bool CanInspect(AbstractCreature creature) => inner.CanInspect(creature);

    public AIDebugSnapshot Capture(AbstractCreature creature, RainWorldGame game)
    {
        AIDebugSnapshot snapshot = inner.Capture(creature, game);
        if (snapshot == null || creature?.realizedCreature is not DesertBatfly bat)
            return snapshot;

        bool hasSocial = DesertBatflySocialLife.TryGetDebugState(
            bat, out DesertBatflySocialDebugState social);

        var section = new AIDebugSection("Social Neutral Social Life / 中性社会生活")
            .Add("Social eligible / 可社交", "DesertBatflySocialLife.Eligible",
                hasSocial && social.Eligible)
            .Add("Social drive / 社交驱动", "DesertBatflySocialLife.SocialDrive",
                hasSocial ? social.SocialDrive : 0f)
            .Add("Social cooldown / 社交冷却", "DesertBatflySocialLife.SocialCooldown",
                hasSocial ? social.SocialCooldown : 0)
            .Add("Social mode / 当前互动", "DesertBatflySocialLife.Mode",
                hasSocial ? social.Mode.ToString() : "None")
            .Add("Interaction ticks / 互动进度", "DesertBatflySocialLife.InteractionTicks",
                hasSocial ? $"{social.InteractionTicks}/{social.Duration}" : "0/0")
            .Add("Temporary partner / 临时伙伴", "DesertBatflySocialLife.Partner",
                hasSocial ? social.Partner : "—")
            .Add("Temporary anchor / 临时锚点", "DesertBatflySocialLife.Anchor",
                hasSocial ? social.Anchor : "—")
            .Add("MicroFlock / 微群", "DesertBatflySocialLife.MicroFlock",
                hasSocial && social.MicroFlockId != 0
                    ? $"id={social.MicroFlockId}, size={social.MicroFlockSize}"
                    : "—")
            .Add("Last interaction / 上次互动", "DesertBatflySocialLife.LastInteractionType",
                hasSocial ? social.LastInteractionType.ToString() : "None")
            .Add("Decision reason / 决策原因", "DesertBatflySocialLife.DecisionReason",
                hasSocial && !string.IsNullOrEmpty(social.DecisionReason) ? social.DecisionReason : "—")
            .Add("Candidate count / 候选数量", "DesertBatflySocialLife.CandidateCount",
                hasSocial ? social.CandidateCount : 0)
            .Add("Roost target / 倒挂目标", "DesertBatflySocialLife.RoostTarget",
                hasSocial && social.RoostTarget.HasValue ? social.RoostTarget.Value.ToString() : "—")
            .Add("Negotiation side / 协商方向", "DesertBatflySocialLife.NegotiationSide",
                hasSocial ? social.NegotiationSide : 0);
        snapshot.Sections.Add(section);

        snapshot.Decisions.Add(new AIDebugDecisionNode(
            "Social social / 中性社会互动",
            hasSocial && social.Mode != DesertBatflySocialMode.None
                ? AIDebugDecisionState.Active
                : hasSocial && !social.Eligible
                    ? AIDebugDecisionState.Blocked
                    : AIDebugDecisionState.Inactive,
            hasSocial
                ? $"{social.Mode}; drive={social.SocialDrive:0.00}; cooldown={social.SocialCooldown}; {social.DecisionReason}"
                : "no realized Social state",
            "DesertBatflySocialLife"));

        return snapshot;
    }
}
