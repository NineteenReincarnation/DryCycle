using System;
using System.Diagnostics;

namespace DryCycle.Iterators;

/// <summary>框架日志等级，与具体日志后端无关。</summary>
public enum IteratorLogLevel
{
    /// <summary>正常事件。</summary>
    Info,
    /// <summary>可恢复问题。</summary>
    Warning,
    /// <summary>操作失败。</summary>
    Error
}

/// <summary>携带 Iterator、Module、Phase 的日志上下文；日志后端异常不会传播到调用者。</summary>
public sealed class IteratorLogger
{
    private readonly IteratorID _id;
    private readonly string _module;
    private readonly string _phase;
    private readonly Action<IteratorLogLevel, string> _sink;
    [ThreadStatic] private static bool _writing;
    internal static Action<IteratorLogLevel, string> DefaultSink = WriteTrace;

    /// <summary>创建默认 Core / Registration 上下文；DryCycle 启用时写入 BepInEx，否则使用 Trace。</summary>
    public IteratorLogger(IteratorID id) : this(id, null, "Core", "Registration") { }

    /// <summary>使用此实例专属的日志接收器。传入 null 时使用框架默认接收器。</summary>
    public IteratorLogger(IteratorID id, Action<IteratorLogLevel, string> sink) : this(id, sink, "Core", "Registration") { }

    private IteratorLogger(IteratorID id, Action<IteratorLogLevel, string> sink, string module, string phase)
    {
        _id = id ?? throw new ArgumentNullException(nameof(id));
        _sink = sink;
        _module = module;
        _phase = phase;
    }

    /// <summary>派生模块上下文，不改变原 Logger。</summary>
    public IteratorLogger ForModule(string module)
    {
        IteratorValidation.RequireText(module, nameof(module));
        return new IteratorLogger(_id, _sink, module, _phase);
    }

    /// <summary>派生生命周期阶段上下文，不改变原 Logger。</summary>
    public IteratorLogger ForPhase(string phase)
    {
        IteratorValidation.RequireText(phase, nameof(phase));
        return new IteratorLogger(_id, _sink, _module, phase);
    }

    /// <summary>记录普通消息；null 消息会显示为 &lt;null&gt;。</summary>
    public void Info(string message) => Write(IteratorLogLevel.Info, message, null);

    /// <summary>记录可恢复问题。</summary>
    public void Warn(string message) => Write(IteratorLogLevel.Warning, message, null);

    /// <summary>记录失败及可选异常。</summary>
    public void Error(string message, Exception exception = null) => Write(IteratorLogLevel.Error, message, exception);

    private void Write(IteratorLogLevel level, string message, Exception exception)
    {
        // A custom sink can itself log. Do not recurse through that sink indefinitely.
        if (_writing)
            return;
        _writing = true;
        try
        {
            string formatted = $"[IteratorFramework][Iterator:{_id}][Module:{_module}][Phase:{_phase}] {message ?? "<null>"}";
            if (exception != null)
                formatted += Environment.NewLine + exception;
            try
            {
                (_sink ?? DefaultSink)(level, formatted);
            }
            catch (Exception sinkFailure)
            {
                WriteTrace(level, formatted);
                WriteTrace(IteratorLogLevel.Error, $"[IteratorFramework] Log sink failed: {sinkFailure.GetType().Name}");
            }
        }
        catch (Exception)
        {
            // Formatting an extension-provided Exception or a TraceListener can also fail.
        }
        finally
        {
            _writing = false;
        }
    }

    internal static void WriteTrace(IteratorLogLevel level, string message) => Trace.WriteLine($"[{level}] {message}");
}
