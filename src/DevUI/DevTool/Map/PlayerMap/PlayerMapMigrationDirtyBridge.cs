using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using BepInEx.Logging;
using DevInterface;
using DryCycle.DevUI.DevTool.Core;

namespace DryCycle.DevUI.DevTool.Map.PlayerMap;

/// <summary>
/// Integrates migration-stream mutations with the Player Map's existing Dirty presentation without
/// coupling the stream runtime to PlayerMapSessionState. Every stream mutation, including history
/// restore, already crosses PlayerMapMigrationStreamRuntime.Touch; successful config commit clears the
/// flag. The normal Player Map snapshot then projects both dirty sources into one toolbar state.
/// </summary>
internal static class PlayerMapMigrationDirtyBridge
{
    private sealed class State
    {
        internal bool Dirty;
    }

    private delegate void OrigTouch(EditorSession session);
    private delegate void HookTouch(OrigTouch orig, EditorSession session);
    private delegate bool OrigSave(MapPage page, PlayerMapSessionState state, out string error);
    private delegate bool HookSave(OrigSave orig, MapPage page, PlayerMapSessionState state, out string error);
    private delegate PlayerMapPresentationSnapshot OrigGetPresentation(EditorSession session);
    private delegate PlayerMapPresentationSnapshot HookGetPresentation(OrigGetPresentation orig, EditorSession session);

    private static readonly HookTouch TouchHookDelegate = TouchHook;
    private static readonly HookSave SaveHookDelegate = SaveHook;
    private static readonly HookGetPresentation GetPresentationHookDelegate = GetPresentationHook;

    private static ConditionalWeakTable<EditorSession, State> states = new();
    private static IDisposable touchHook;
    private static IDisposable saveHook;
    private static IDisposable presentationHook;
    private static bool enabled;

    internal static void Enable(ManualLogSource logger)
    {
        if (enabled) return;
        try
        {
            const BindingFlags flags = BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;
            MethodInfo touch = typeof(PlayerMapMigrationStreamRuntime).GetMethod(
                "Touch", flags, null, new[] { typeof(EditorSession) }, null);
            MethodInfo save = typeof(PlayerMapConfigBuildPipeline).GetMethod(
                "Save", flags, null,
                new[] { typeof(MapPage), typeof(PlayerMapSessionState), typeof(string).MakeByRefType() }, null);
            MethodInfo presentation = typeof(PlayerMapWorkspaceRuntime).GetMethod(
                "GetPresentation", flags, null, new[] { typeof(EditorSession) }, null);
            if (touch == null || save == null || presentation == null)
                throw new MissingMemberException("Player Map migration dirty bridge targets were not found.");

            Type hookType = Type.GetType("MonoMod.RuntimeDetour.Hook, MonoMod.RuntimeDetour", throwOnError: false);
            ConstructorInfo constructor = hookType?.GetConstructor(new[] { typeof(MethodBase), typeof(Delegate) });
            if (constructor == null)
                throw new MissingMethodException("MonoMod.RuntimeDetour.Hook(MethodBase, Delegate) is unavailable.");

            touchHook = constructor.Invoke(new object[] { touch, TouchHookDelegate }) as IDisposable;
            saveHook = constructor.Invoke(new object[] { save, SaveHookDelegate }) as IDisposable;
            presentationHook = constructor.Invoke(new object[] { presentation, GetPresentationHookDelegate }) as IDisposable;
            if (touchHook == null || saveHook == null || presentationHook == null)
                throw new InvalidOperationException("Player Map migration dirty bridge hooks were not created.");

            enabled = true;
        }
        catch (Exception error)
        {
            Disable();
            logger?.LogWarning("Player Map migration dirty bridge could not attach: " + Unwrap(error).Message);
        }
    }

    internal static void Disable()
    {
        Dispose(ref presentationHook);
        Dispose(ref saveHook);
        Dispose(ref touchHook);
        states = new ConditionalWeakTable<EditorSession, State>();
        enabled = false;
    }

    internal static void Reset() => states = new ConditionalWeakTable<EditorSession, State>();

    private static void TouchHook(OrigTouch orig, EditorSession session)
    {
        orig(session);
        if (enabled && session != null) states.GetValue(session, _ => new State()).Dirty = true;
    }

    private static bool SaveHook(OrigSave orig, MapPage page, PlayerMapSessionState state, out string error)
    {
        bool success = orig(page, state, out error);
        if (!enabled || !success) return success;

        EditorSession session = DevToolSessionHub.Current;
        if (session != null && ReferenceEquals(session.Owner?.activePage, page))
            states.GetValue(session, _ => new State()).Dirty = false;
        return true;
    }

    private static PlayerMapPresentationSnapshot GetPresentationHook(
        OrigGetPresentation orig,
        EditorSession session)
    {
        PlayerMapPresentationSnapshot source = orig(session);
        if (!enabled || session == null || source?.Available != true ||
            !states.TryGetValue(session, out State state) || !state.Dirty || source.Dirty)
            return source;

        return new PlayerMapPresentationSnapshot
        {
            Available = source.Available,
            RegionName = source.RegionName,
            Dirty = true,
            Revision = source.Revision,
            SelectedRoomIndex = source.SelectedRoomIndex,
            Rooms = source.Rooms,
            DefaultMaterials = source.DefaultMaterials,
            RenderReport = source.RenderReport,
            Preview = source.Preview
        };
    }

    private static void Dispose(ref IDisposable hook)
    {
        try { hook?.Dispose(); }
        catch { }
        hook = null;
    }

    private static Exception Unwrap(Exception error)
    {
        while (error is TargetInvocationException invocation && invocation.InnerException != null)
            error = invocation.InnerException;
        return error;
    }
}
