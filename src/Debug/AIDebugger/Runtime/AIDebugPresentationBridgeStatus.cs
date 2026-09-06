using System;
using System.Threading;

namespace DryCycle.Debugging.AI;

/// <summary>
/// Cross-assembly handshake between the Rain World main-thread Observatory controller and
/// the optional RWImGUI presentation bridge. This deliberately contains no ImGui/RWImGUI
/// types so DryCycle.dll can report the real presentation state even when the optional
/// bridge or its dependencies are missing.
/// </summary>
internal static class AIDebugPresentationBridgeStatus
{
    private static int bridgeLoaded;
    private static int callbackRegistered;
    private static int presentSeen;
    private static string bridgeVersion = "—";
    private static string apiVersion = "—";
    private static string imguiVersion = "—";
    private static string lastError = string.Empty;

    internal static bool BridgeLoaded => Volatile.Read(ref bridgeLoaded) != 0;
    internal static bool CallbackRegistered => Volatile.Read(ref callbackRegistered) != 0;
    internal static bool PresentSeen => Volatile.Read(ref presentSeen) != 0;
    internal static string LastError => Volatile.Read(ref lastError) ?? string.Empty;

    internal static void MarkBridgeLoaded(string version)
    {
        bridgeVersion = string.IsNullOrWhiteSpace(version) ? "unknown" : version;
        Volatile.Write(ref bridgeLoaded, 1);
    }

    internal static void MarkCallbackRegistered(string rwimguiApiVersion, string rwimguiImGuiVersion)
    {
        apiVersion = string.IsNullOrWhiteSpace(rwimguiApiVersion) ? "unknown" : rwimguiApiVersion;
        imguiVersion = string.IsNullOrWhiteSpace(rwimguiImGuiVersion) ? "unknown" : rwimguiImGuiVersion;
        Volatile.Write(ref callbackRegistered, 1);
        Volatile.Write(ref lastError, string.Empty);
    }

    internal static void MarkPresentSeen() => Volatile.Write(ref presentSeen, 1);

    internal static void MarkFailure(string message)
    {
        Volatile.Write(ref lastError, message ?? "unknown bridge failure");
    }

    internal static string Describe()
    {
        return $"bridgeLoaded={BridgeLoaded}, callbackRegistered={CallbackRegistered}, presentSeen={PresentSeen}, " +
               $"bridgeVersion={bridgeVersion}, apiVersion={apiVersion}, imguiVersion={imguiVersion}" +
               (string.IsNullOrEmpty(LastError) ? string.Empty : $", lastError={LastError}");
    }
}
