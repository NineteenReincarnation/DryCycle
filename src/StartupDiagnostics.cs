using System;
using System.Threading;
using BepInEx.Logging;

namespace DryCycle;

/// <summary>
/// Deterministic startup breadcrumbs for diagnosing loader and hook failures.
///
/// Every startup operation emits BEGIN before touching external/game APIs. Managed failures emit
/// FAIL with the full exception/stack and are then rethrown to the owning transaction. If a native
/// crash terminates the process before managed catch logic can run, the final BEGIN line still
/// identifies the subsystem that was executing.
/// </summary>
internal static class StartupDiagnostics
{
    private static int sequence;
    private static ManualLogSource logger;

    internal static void Begin(ManualLogSource log)
    {
        logger = log;
        Interlocked.Exchange(ref sequence, 0);
        WriteInfo(0, "BOOT", "BEGIN", "DryCycle startup diagnostics active.");
    }

    internal static void Step(string source, Action action)
    {
        if (action == null) return;
        int id = Interlocked.Increment(ref sequence);
        WriteInfo(id, source, "BEGIN", null);
        try
        {
            action();
            WriteInfo(id, source, "OK", null);
        }
        catch (Exception error)
        {
            Failure(id, source, error);
            throw;
        }
    }

    internal static T Step<T>(string source, Func<T> action)
    {
        if (action == null) return default;
        int id = Interlocked.Increment(ref sequence);
        WriteInfo(id, source, "BEGIN", null);
        try
        {
            T result = action();
            WriteInfo(id, source, "OK", null);
            return result;
        }
        catch (Exception error)
        {
            Failure(id, source, error);
            throw;
        }
    }

    internal static bool Optional(string source, Action action)
    {
        if (action == null) return true;
        int id = Interlocked.Increment(ref sequence);
        WriteInfo(id, source, "BEGIN", "optional");
        try
        {
            action();
            WriteInfo(id, source, "OK", "optional");
            return true;
        }
        catch (Exception error)
        {
            Failure(id, source, error, optional: true);
            return false;
        }
    }

    internal static void Marker(string source, string state, string detail = null)
    {
        int id = Interlocked.Increment(ref sequence);
        WriteInfo(id, source, state, detail);
    }

    internal static void Failure(string source, Exception error)
    {
        Failure(Interlocked.Increment(ref sequence), source, error);
    }

    internal static void RollbackAfterFailure(
        string source,
        Exception originalError,
        Action rollback)
    {
        Failure(source, originalError);
        Marker(
            source,
            "ROLLBACK-BEGIN",
            originalError == null
                ? "trigger=<unknown>"
                : "trigger=" + Describe(originalError));

        if (rollback == null)
        {
            Marker(source, "ROLLBACK-END", "no rollback action");
            return;
        }

        try
        {
            rollback();
            Marker(source, "ROLLBACK-END", "cleanup completed");
        }
        catch (Exception rollbackError)
        {
            Failure(source + "/rollback", rollbackError);
            Marker(
                source,
                "ROLLBACK-END",
                "cleanup threw; original failure preserved");
        }
    }

    internal static bool RollbackStep(string source, Action action)
    {
        if (action == null) return true;

        int id = Interlocked.Increment(ref sequence);
        WriteInfo(id, source, "ROLLBACK-BEGIN", null);
        try
        {
            action();
            WriteInfo(id, source, "ROLLBACK-OK", null);
            return true;
        }
        catch (Exception error)
        {
            string prefix = FormatPrefix(id, source, "ROLLBACK-FAIL");
            SafeLogError(prefix + " " + Describe(error));
            SafeLogError(error);
            return false;
        }
    }

    private static void Failure(int id, string source, Exception error, bool optional = false)
    {
        string prefix = FormatPrefix(id, source, optional ? "FAIL-OPTIONAL" : "FAIL");
        SafeLogError(prefix + " " + Describe(error));
        if (error != null)
            SafeLogError(error);
    }

    private static void WriteInfo(int id, string source, string state, string detail)
    {
        string message = FormatPrefix(id, source, state);
        if (!string.IsNullOrWhiteSpace(detail))
            message += " " + detail;
        SafeLogInfo(message);
    }

    private static void SafeLogInfo(string message)
    {
        try
        {
            logger?.LogInfo(message);
        }
        catch
        {
            // Diagnostics must never become a startup failure source. If a third-party BepInEx
            // log listener is broken, preserve game/bootstrap control flow even if this line is lost.
        }
    }

    private static void SafeLogError(object payload)
    {
        try
        {
            logger?.LogError(payload);
        }
        catch
        {
            // Same rule as info logging: cleanup/startup must proceed even when the log sink fails.
        }
    }

    private static string FormatPrefix(int id, string source, string state) =>
        "[DryCycle.Startup][" + id.ToString("D4") + "][" +
        (string.IsNullOrWhiteSpace(state) ? "STATE" : state) + "][" +
        (string.IsNullOrWhiteSpace(source) ? "<unknown>" : source) + "]";

    private static string Describe(Exception error)
    {
        if (error == null) return "<no exception object>";
        string type = error.GetType().FullName ?? error.GetType().Name;
        string target = error.TargetSite == null
            ? string.Empty
            : " target=" + (error.TargetSite.DeclaringType?.FullName ?? "<unknown>") + "." + error.TargetSite.Name;
        return type + ": " + error.Message + target;
    }
}
