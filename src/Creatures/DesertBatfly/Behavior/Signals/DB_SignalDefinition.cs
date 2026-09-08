namespace DryCycle.Creatures.DesertBatfly;

/// <summary>
/// Single authority for per-signal transport parameters.
///
/// This owns visual/acoustic ranges, root/ambient TTL, display duration and relay policy.
/// SignalRuntime owns receiver state and response semantics; SignalRoomRuntime owns bounded
/// packet storage/delivery. Keeping these values here prevents Fog, room delivery or emitters
/// from growing independent copies of signal transport constants.
/// </summary>
internal readonly struct DB_SignalDefinition
{
    internal readonly float VisualRange;
    internal readonly float CloseAcousticRange;
    internal readonly int RootTtlTicks;
    internal readonly int AmbientTtlTicks;
    internal readonly int DisplayTicks;
    internal readonly int MaxRelayHops;
    internal readonly float FirstRelayScale;
    internal readonly float SecondRelayScale;

    internal bool CanRelay => MaxRelayHops > 0;

    private DB_SignalDefinition(
        float visualRange,
        float closeAcousticRange,
        int rootTtlTicks,
        int ambientTtlTicks,
        int displayTicks,
        int maxRelayHops = 0,
        float firstRelayScale = 0f,
        float secondRelayScale = 0f)
    {
        VisualRange = visualRange;
        CloseAcousticRange = closeAcousticRange;
        RootTtlTicks = rootTtlTicks;
        AmbientTtlTicks = ambientTtlTicks;
        DisplayTicks = displayTicks;
        MaxRelayHops = maxRelayHops;
        FirstRelayScale = firstRelayScale;
        SecondRelayScale = secondRelayScale;
    }

    internal float RelayScale(int sourceHop)
    {
        if (!CanRelay) return 0f;
        return sourceHop switch
        {
            0 => FirstRelayScale,
            1 => SecondRelayScale,
            _ => 0f
        };
    }

    internal static DB_SignalDefinition For(DB_SignalKind kind) => kind switch
    {
        // Values below are the pre-migration behavior constants, centralized without retuning.
        DB_SignalKind.AlarmFlutter => new(300f, 95f, 135, 135, 38, 2, 0.56f, 0.31f),
        DB_SignalKind.DistressCall => new(250f, 108f, 120, 120, 52),
        DB_SignalKind.RallySignal => new(235f, 0f, 84, 90, 42),
        DB_SignalKind.RoostCall => new(215f, 0f, 110, 110, 30),
        DB_SignalKind.HarassSignal => new(235f, 0f, 90, 90, 30),
        DB_SignalKind.SafeSignal => new(195f, 0f, 90, 90, 30),
        _ => new(200f, 0f, 90, 90, 30)
    };
}
