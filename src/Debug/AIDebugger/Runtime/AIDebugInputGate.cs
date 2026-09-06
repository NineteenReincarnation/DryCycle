using System;
using System.Reflection;
using System.Text;
using BepInEx.Logging;
using MonoMod.RuntimeDetour;
using UnityEngine;

namespace DryCycle.Debugging.AI;

internal static class AIDebugInputGate
{
    private delegate Player.InputPackage PlayerInputOrig2(int categoryID, int playerNumber);
    private delegate Player.InputPackage PlayerInputDetour2(PlayerInputOrig2 orig, int categoryID, int playerNumber);
    private delegate Player.InputPackage PlayerInputOrig3(int categoryID, int playerNumber, RainWorld rainWorld);
    private delegate Player.InputPackage PlayerInputDetour3(PlayerInputOrig3 orig, int categoryID, int playerNumber, RainWorld rainWorld);

    private static Hook hook;
    private static ManualLogSource logger;
    private static bool overloadsLogged;

    internal static bool Installed => hook != null;

    internal static void Install(ManualLogSource log)
    {
        logger = log;
        if (hook != null) return;

        try
        {
            MethodInfo[] overloads = FindRuntimeOverloads();
            LogRuntimeOverloads(overloads);

            MethodInfo method = FindCompatible(overloads, typeof(int), typeof(int));
            if (method != null)
            {
                hook = new Hook(method, (PlayerInputDetour2)PlayerInputLogicHook2);
                logger?.LogInfo("DryCycle AI Observatory input gate installed on " + Describe(method) + ".");
                return;
            }

            // Rain World retains an obsolete three-argument wrapper in some builds. If
            // the two-argument implementation is not exposed by the actual runtime
            // assembly, hook that complete signature instead of assuming a decompiler or
            // PUBLIC assembly signature is authoritative.
            method = FindCompatible(overloads, typeof(int), typeof(int), typeof(RainWorld));
            if (method != null)
            {
                hook = new Hook(method, (PlayerInputDetour3)PlayerInputLogicHook3);
                logger?.LogInfo("DryCycle AI Observatory input gate installed on " + Describe(method) + ".");
                return;
            }

            logger?.LogWarning(
                "DryCycle AI Observatory input gate unavailable: no compatible runtime " +
                "RWInput.PlayerInputLogic overload returning Player.InputPackage was found. " +
                "The Observatory UI will remain available, but INTERACT input isolation is disabled.");
        }
        catch (Exception error)
        {
            logger?.LogWarning("DryCycle AI Observatory input gate unavailable: " + error);
            try { hook?.Dispose(); } catch { }
            hook = null;
        }
    }

    internal static void Uninstall()
    {
        try { hook?.Dispose(); }
        catch (Exception error) { logger?.LogWarning("AI Observatory input gate dispose failed: " + error.Message); }
        hook = null;
    }

    private static MethodInfo[] FindRuntimeOverloads()
    {
        MethodInfo[] methods = typeof(RWInput).GetMethods(
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance);
        int count = 0;
        for (int i = 0; i < methods.Length; i++)
            if (string.Equals(methods[i].Name, "PlayerInputLogic", StringComparison.Ordinal)) count++;

        MethodInfo[] result = new MethodInfo[count];
        int at = 0;
        for (int i = 0; i < methods.Length; i++)
            if (string.Equals(methods[i].Name, "PlayerInputLogic", StringComparison.Ordinal)) result[at++] = methods[i];
        return result;
    }

    private static MethodInfo FindCompatible(MethodInfo[] methods, params Type[] parameterTypes)
    {
        for (int i = 0; i < methods.Length; i++)
        {
            MethodInfo method = methods[i];
            if (!method.IsStatic || method.ReturnType != typeof(Player.InputPackage)) continue;
            ParameterInfo[] parameters = method.GetParameters();
            if (parameters.Length != parameterTypes.Length) continue;
            bool match = true;
            for (int p = 0; p < parameters.Length; p++)
            {
                if (parameters[p].ParameterType == parameterTypes[p]) continue;
                match = false;
                break;
            }
            if (match) return method;
        }
        return null;
    }

    private static void LogRuntimeOverloads(MethodInfo[] overloads)
    {
        if (overloadsLogged) return;
        overloadsLogged = true;

        string assembly = typeof(RWInput).Assembly.FullName ?? typeof(RWInput).Assembly.GetName().Name;
        if (overloads.Length == 0)
        {
            logger?.LogWarning("DryCycle AI Observatory runtime RWInput assembly exposes no PlayerInputLogic overloads. Assembly=" + assembly);
            return;
        }

        var text = new StringBuilder();
        text.Append("DryCycle AI Observatory runtime PlayerInputLogic overloads (Assembly=")
            .Append(assembly).Append("): ");
        for (int i = 0; i < overloads.Length; i++)
        {
            if (i > 0) text.Append(" | ");
            text.Append(Describe(overloads[i]));
        }
        logger?.LogInfo(text.ToString());
    }

    private static string Describe(MethodInfo method)
    {
        if (method == null) return "<null>";
        var text = new StringBuilder();
        text.Append(method.IsPublic ? "public " : "nonpublic ")
            .Append(method.IsStatic ? "static " : "instance ")
            .Append(TypeName(method.ReturnType)).Append(' ')
            .Append(method.DeclaringType?.FullName ?? "RWInput").Append('.')
            .Append(method.Name).Append('(');
        ParameterInfo[] parameters = method.GetParameters();
        for (int i = 0; i < parameters.Length; i++)
        {
            if (i > 0) text.Append(',');
            text.Append(TypeName(parameters[i].ParameterType));
        }
        return text.Append(')').ToString();
    }

    private static string TypeName(Type type) => type?.FullName ?? type?.Name ?? "?";

    private static Player.InputPackage PlayerInputLogicHook2(PlayerInputOrig2 orig, int categoryID, int playerNumber) =>
        NeutralizeIfNeeded(orig(categoryID, playerNumber));

    private static Player.InputPackage PlayerInputLogicHook3(PlayerInputOrig3 orig, int categoryID, int playerNumber, RainWorld rainWorld) =>
        NeutralizeIfNeeded(orig(categoryID, playerNumber, rainWorld));

    private static Player.InputPackage NeutralizeIfNeeded(Player.InputPackage result)
    {
        if (!AIDebuggerRuntime.BlocksPlayerInput) return result;

        // Preserve controller metadata, but neutralize gameplay commands. This lets the
        // configured device resume immediately after leaving INTERACT mode.
        result.x = 0;
        result.y = 0;
        result.jmp = false;
        result.thrw = false;
        result.pckp = false;
        result.mp = false;
        result.spec = false;
        result.crouchToggle = false;
        result.analogueDir = Vector2.zero;
        result.downDiagonal = 0;
        return result;
    }
}
