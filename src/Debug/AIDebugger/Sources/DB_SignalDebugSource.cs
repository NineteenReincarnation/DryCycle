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
        if (snapshot == null || creature?.realizedCreature is not DB_Creature bat)
            return snapshot;

        DB_PerceptionRuntime perception = bat.DesertAI?.Perception;
        bool hasPerception = perception != null;
        DB_PerceptionSignalContext signal = hasPerception
            ? perception.Snapshot.Signals
            : default;
        int lastGeneration = hasPerception ? perception.LastSignalGeneration : 0;
        DB_SignalKind lastKind = hasPerception ? perception.LastSignalKind : default;
        DB_PerceptionModality lastModality = hasPerception
            ? perception.LastSignalModality
            : DB_PerceptionModality.None;
        int lastHop = hasPerception ? perception.LastSignalHop : 0;
        string lastDecision = hasPerception ? perception.LastSignalDecision : "—";
        int activeSignals = DB_SignalRoomRuntime.For(bat.room)?.Count ?? 0;
        DB_SignalPacket lastPacket = hasPerception
            ? FindSignalPacket(bat, lastGeneration)
            : null;
        bool hasSignalInfluence =
            signal.AlarmPressure > 0.02f || signal.DistressInterest > 0.02f ||
            signal.RallyInterest > 0.02f || signal.RoostInterest > 0.02f ||
            signal.HarassInterest > 0.02f || signal.SafeConfidence > 0.02f;

        var section = new AIDebugSection("Signal Social Signal Network / 社会信号")
            .Add("Active room signals / 房间信号", "Signal.ActiveRoomSignals", activeSignals)
            .Add("Last generation / 最近代号", "Signal.LastGeneration", lastGeneration)
            .Add("Last kind / 最近类型", "Signal.LastKind",
                hasPerception ? lastKind.ToString() : "None")
            .Add("Emitter / 发送者", "Signal.Emitter", EntityLabel(lastPacket?.Emitter))
            .Add("Subject / 信号主体", "Signal.Subject", EntityLabel(lastPacket?.Subject))
            .Add("Threat / 指向威胁", "Signal.Threat", CreatureLabel(lastPacket?.Threat))
            .Add("Receiver / 接收者", "Signal.Receiver", EntityLabel(bat))
            .Add("Perception / 感知通道", "Signal.LastPerception",
                hasPerception ? lastModality.ToString() : "None")
            .Add("Hop / 跳数", "Signal.LastHop", lastHop)
            .Add("Final consumer / 最终消费系统", "Signal.Consumer",
                hasPerception ? ConsumerFor(lastKind) : "None")
            .Add("Alarm pressure / 警报压力", "Signal.AlarmPressure", signal.AlarmPressure)
            .Add("Alarm origin / 警报来源位置", "Signal.AlarmOrigin",
                signal.AlarmPressure > 0f ? signal.AlarmOrigin.ToString() : "—")
            .Add("Distress interest / 求救关注", "Signal.DistressInterest", signal.DistressInterest)
            .Add("Rally interest / 集结关注", "Signal.RallyInterest", signal.RallyInterest)
            .Add("Roost interest / 倒挂关注", "Signal.RoostInterest", signal.RoostInterest)
            .Add("Harass interest / 骚扰关注", "Signal.HarassInterest", signal.HarassInterest)
            .Add("Safe confidence / 安全信号", "Signal.SafeConfidence", signal.SafeConfidence)
            .Add("Last decision / 最近判定", "Signal.LastDecision", lastDecision);
        snapshot.Sections.Add(section);

        bool display = DB_SignalRuntime.TryGetDisplay(
            bat, out DB_SignalDisplayState visual);
        snapshot.Sections.Add(new AIDebugSection("Signal Signal Display / 信号视觉")
            .Add("Displaying / 正在表现", "SignalDisplay.Active", display)
            .Add("Kind / 类型", "SignalDisplay.Kind", display ? visual.Kind.ToString() : "None")
            .Add("Intensity / 强度", "SignalDisplay.Intensity", display ? visual.Intensity : 0f)
            .Add("Ticks / 剩余", "SignalDisplay.Ticks", display ? visual.TicksRemaining : 0));

        snapshot.Decisions.Add(new AIDebugDecisionNode(
            "Signal perception influence / 社会信号感知影响",
            hasSignalInfluence
                ? AIDebugDecisionState.Active
                : hasPerception ? AIDebugDecisionState.Inactive : AIDebugDecisionState.Blocked,
            hasPerception
                ? $"{lastKind}; gen={lastGeneration}; hop={lastHop}; consumer={ConsumerFor(lastKind)}; {lastDecision}"
                : "no realized Perception state",
            "DB_PerceptionRuntime"));

        return snapshot;
    }

    private static DB_SignalPacket FindSignalPacket(DB_Creature bat, int generation)
    {
        DB_SignalRoomRuntime.RoomState room = DB_SignalRoomRuntime.For(bat?.room);
        if (room == null) return null;
        for (int i = 0; i < room.ActiveSignals.Count; i++)
        {
            DB_SignalPacket packet = room.ActiveSignals[i];
            if (packet != null && packet.Generation == generation)
                return packet;
        }
        return null;
    }

    private static string ConsumerFor(DB_SignalKind kind) => kind switch
    {
        DB_SignalKind.AlarmFlutter => "Perception hazard belief -> Threat/Fear/Arbiter",
        DB_SignalKind.DistressCall => "SocialBond + Fear/Rescue support motivation",
        DB_SignalKind.RallySignal => "Vengeance/supporter candidacy",
        DB_SignalKind.RoostCall => "Social RoostInvitation / ChainSocialization",
        DB_SignalKind.HarassSignal => "Combat/social harass target interest",
        DB_SignalKind.SafeSignal => "Perception reported-hazard recovery",
        _ => "None"
    };

    private static string EntityLabel(DB_Creature bat)
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
