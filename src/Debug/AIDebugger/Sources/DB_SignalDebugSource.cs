using DryCycle.Creatures.DesertBatfly;

namespace DryCycle.Debugging.AI;

internal sealed class DB_SignalDebugSource : IAIDebugSource
{
    private readonly DB_ThreatDebugSource inner = new();

    public int Priority => 1400;
    public bool CanInspect(AbstractCreature creature) => inner.CanInspect(creature);

    public AIDebugSnapshot Capture(AbstractCreature creature, RainWorldGame game)
    {
        AIDebugSnapshot snapshot = inner.Capture(creature, game);
        if (snapshot == null || creature?.realizedCreature is not DesertBatfly bat)
            return snapshot;

        bool hasSignal = DB_SignalRuntime.TryGetDebugState(
            bat, out DesertBatflySignalDebugState signal);
        DesertBatflySignalInfluence influence = hasSignal ? signal.Influence : default;
        DesertBatflySignalPacket lastPacket = hasSignal
            ? FindSignalPacket(bat, signal.LastGeneration)
            : null;

        var section = new AIDebugSection("Signal Social Signal Network / 社会信号")
            .Add("Active room signals / 房间信号", "Signal.ActiveRoomSignals",
                hasSignal ? signal.ActiveRoomSignals : 0)
            .Add("Last generation / 最近代号", "Signal.LastGeneration",
                hasSignal ? signal.LastGeneration : 0)
            .Add("Last kind / 最近类型", "Signal.LastKind",
                hasSignal ? signal.LastKind.ToString() : "None")
            .Add("Emitter / 发送者", "Signal.Emitter",
                EntityLabel(lastPacket?.Emitter))
            .Add("Subject / 信号主体", "Signal.Subject",
                EntityLabel(lastPacket?.Subject))
            .Add("Threat / 指向威胁", "Signal.Threat",
                CreatureLabel(lastPacket?.Threat))
            .Add("Receiver / 接收者", "Signal.Receiver",
                EntityLabel(bat))
            .Add("Perception / 感知通道", "Signal.LastPerception",
                hasSignal ? signal.LastPerception.ToString() : "None")
            .Add("Hop / 跳数", "Signal.LastHop",
                hasSignal ? signal.LastHop : 0)
            .Add("Final consumer / 最终消费系统", "Signal.Consumer",
                hasSignal ? ConsumerFor(signal.LastKind) : "None")
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

        bool display = DB_SignalRuntime.TryGetDisplay(
            bat, out DesertBatflySignalDisplayState visual);
        snapshot.Sections.Add(new AIDebugSection("Signal Signal Display / 信号视觉")
            .Add("Displaying / 正在表现", "SignalDisplay.Active", display)
            .Add("Kind / 类型", "SignalDisplay.Kind", display ? visual.Kind.ToString() : "None")
            .Add("Intensity / 强度", "SignalDisplay.Intensity", display ? visual.Intensity : 0f)
            .Add("Ticks / 剩余", "SignalDisplay.Ticks", display ? visual.TicksRemaining : 0));

        snapshot.Decisions.Add(new AIDebugDecisionNode(
            "Signal signal influence / 社会信号影响",
            hasSignal && (influence.AlarmPressure > 0.02f || influence.DistressInterest > 0.02f ||
                          influence.RallyInterest > 0.02f || influence.RoostInterest > 0.02f ||
                          influence.HarassInterest > 0.02f || influence.SafeConfidence > 0.02f)
                ? AIDebugDecisionState.Active
                : hasSignal ? AIDebugDecisionState.Inactive : AIDebugDecisionState.Blocked,
            hasSignal
                ? $"{signal.LastKind}; gen={signal.LastGeneration}; hop={signal.LastHop}; consumer={ConsumerFor(signal.LastKind)}; {signal.LastDecision}"
                : "no realized Signal state",
            "DB_SignalRuntime"));

        return snapshot;
    }

    private static DesertBatflySignalPacket FindSignalPacket(DesertBatfly bat, int generation)
    {
        DesertBatflySignalRoomRuntime.RoomState room =
            DesertBatflySignalRoomRuntime.For(bat?.room);
        if (room == null) return null;
        for (int i = 0; i < room.ActiveSignals.Count; i++)
        {
            DesertBatflySignalPacket packet = room.ActiveSignals[i];
            if (packet != null && packet.Generation == generation)
                return packet;
        }
        return null;
    }

    private static string ConsumerFor(DesertBatflySignalKind kind) => kind switch
    {
        DesertBatflySignalKind.AlarmFlutter => "DesertBatflyAI danger / Task10 cancel",
        DesertBatflySignalKind.DistressCall => "SocialBond + Intimidation rescue/support motivation",
        DesertBatflySignalKind.RallySignal => "Intimidation supporter candidacy",
        DesertBatflySignalKind.RoostCall => "Task10 RoostInvitation / ChainSocialization",
        DesertBatflySignalKind.HarassSignal => "DesertBatflyAI social harass target interest",
        DesertBatflySignalKind.SafeSignal => "Signal signal concern recovery",
        _ => "None"
    };

    private static string EntityLabel(DesertBatfly bat)
    {
        if (bat?.abstractCreature == null) return "— / expired";
        return bat.abstractCreature.ID.ToString();
    }

    private static string CreatureLabel(Creature creature)
    {
        if (creature?.abstractCreature == null) return "—";
        return creature.abstractCreature.ID.ToString();
    }
}
