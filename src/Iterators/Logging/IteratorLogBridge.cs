using BepInEx.Logging;

namespace DryCycle.Iterators;

/// <summary>BepInEx 适配留在 Core 外；默认 Logger 不捕获旧的插件实例。</summary>
internal static class IteratorLogBridge
{
    internal static void Enable(ManualLogSource logger)
    {
        IteratorLogger.DefaultSink = (level, message) =>
            logger.Log(level == IteratorLogLevel.Error ? LogLevel.Error :
                level == IteratorLogLevel.Warning ? LogLevel.Warning : LogLevel.Info, message);
    }

    internal static void Disable() => IteratorLogger.DefaultSink = IteratorLogger.WriteTrace;
}
