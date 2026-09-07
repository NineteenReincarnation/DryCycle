using DryCycle.Creatures.DesertBatfly;

namespace DryCycle.Debugging.AI;

internal sealed class DesertBatflyTask12DebugSource : IAIDebugSource
{
    private readonly DesertBatflyTask11DebugSource inner = new();

    public int Priority => 1400;
    public bool CanInspect(AbstractCreature creature) => inner.CanInspect(creature);

    public AIDebugSnapshot Capture(AbstractCreature creature, RainWorldGame game)
    {
        AIDebugSnapshot snapshot = inner.Capture(creature, game);
        if (snapshot == null || creature?.realizedCreature is not DesertBatfly bat)
            return snapshot;

        bool hasSignal = DesertBatflySignalRuntime.TryGetDebugState(
            bat, out DesertBatflySignalDebugState signal);
        DesertBatflySignalInfluence influence = hasSignal ? signal.Influence : default;

        var section = new AIDebugSection("Task 12 Social Signal Network / 社会信号")
            .Add("Active room signals / 房间信号", "Signal.ActiveRoomSignals",
                hasSignal ? signal.ActiveRoomSignals : 0)
            .Add("Last generation / 最近代号", "Signal.LastGeneration",
                hasSignal ? signal.LastGeneration : 0)
            .Add("Last kind / 最近类型", "Signal.LastKind",
                hasSignal ? signal.LastKind.ToString() : "None")
            .Add("Perception / 感知通道", "Signal.LastPerception",
                hasSignal ? signal.LastPerception.ToString() : "None")
            .Add("Hop / 跳数", "Signal.LastHop",
                hasSignal ? signal.LastHop : 0)
            .Add("Alarm pressure / 警报压力", "Signal.AlarmPressure",
                influence.AlarmPressure)
            .Add("Alarm origin / 警报来源位置", "Signal.AlarmOrigin",
                influence.AlarmPressure > 0f ? influence.AlarmOrigin.ToString() : "—")
            .Add("Distress interest / 求救关注", "Signal.DistressInterest",
                influence.DistressInterest)
            .Add("Rally interest / 集结关注", "Signal.RallyInterest",
                influence.RallyInterest)
            .Add("Roost interest / 倒挂关注", "Signal.RoostInterest",
                influence.RoostInterest)
            .Add("Harass interest / 骚扰关注", "Signal.HarassInterest",
                influence.HarassInterest)
            .Add("Safe confidence / 安全信号", "Signal.SafeConfidence",
                influence.SafeConfidence)
            .Add("Last decision / 最近判定", "Signal.LastDecision",
                hasSignal ? signal.LastDecision : "—");
        snapshot.Sections.Add(section);

        bool display = DesertBatflySignalRuntime.TryGetDisplay(
            bat, out DesertBatflySignalDisplayState visual);
        snapshot.Sections.Add(new AIDebugSection("Task 12 Signal Display / 信号视觉")
            .Add("Displaying / 正在表现", "SignalDisplay.Active", display)
            .Add("Kind / 类型", "SignalDisplay.Kind", display ? visual.Kind.ToString() : "None")
            .Add("Intensity / 强度", "SignalDisplay.Intensity", display ? visual.Intensity : 0f)
            .Add("Ticks / 剩余", "SignalDisplay.Ticks", display ? visual.TicksRemaining : 0));

        snapshot.Decisions.Add(new AIDebugDecisionNode(
            "Task 12 signal influence / 社会信号影响",
            hasSignal && (influence.AlarmPressure > 0.02f || influence.DistressInterest > 0.02f ||
                          influence.RallyInterest > 0.02f || influence.RoostInterest > 0.02f ||
                          influence.HarassInterest > 0.02f || influence.SafeConfidence > 0.02f)
                ? AIDebugDecisionState.Active
                : hasSignal ? AIDebugDecisionState.Inactive : AIDebugDecisionState.Blocked,
            hasSignal
                ? $"{signal.LastKind}; gen={signal.LastGeneration}; hop={signal.LastHop}; {signal.LastDecision}"
                : "no realized Task12 state",
            "DesertBatflySignalRuntime"));

        return snapshot;
    }
}
