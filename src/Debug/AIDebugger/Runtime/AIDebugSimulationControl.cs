using System;
using System.Reflection;
using BepInEx.Logging;
using MonoMod.RuntimeDetour;

namespace DryCycle.Debugging.AI;

internal static class AIDebugSimulationControl
{
    private delegate void GameUpdateOrig(RainWorldGame self);
    private delegate void GameUpdateDetour(GameUpdateOrig orig, RainWorldGame self);

    private static Hook updateHook;
    private static ManualLogSource logger;
    private static bool debuggerPaused;
    private static bool previousPaused;
    private static bool stepRequested;
    private static RainWorldGame currentGame;

    internal static bool Paused => debuggerPaused;
    internal static bool StepPending => stepRequested;

    internal static void Install(ManualLogSource log)
    {
        logger = log;
        if (updateHook != null) return;
        try
        {
            MethodInfo method = typeof(RainWorldGame).GetMethod(
                nameof(RainWorldGame.Update),
                BindingFlags.Public | BindingFlags.Instance,
                null,
                Type.EmptyTypes,
                null);
            if (method == null) throw new MissingMethodException(typeof(RainWorldGame).FullName, "Update()");
            updateHook = new Hook(method, (GameUpdateDetour)GameUpdateHook);
            logger?.LogInfo("DryCycle AI Observatory world-step/recorder tick hook installed.");
        }
        catch (Exception error)
        {
            logger?.LogWarning("DryCycle AI Observatory world-step hook unavailable: " + error);
            updateHook?.Dispose();
            updateHook = null;
        }
    }

    internal static void Bind(RainWorldGame game)
    {
        currentGame = game;
    }

    internal static void SetPaused(RainWorldGame game, bool paused)
    {
        if (game == null) return;
        currentGame = game;
        if (paused == debuggerPaused) return;
        if (paused)
        {
            previousPaused = game.paused;
            debuggerPaused = true;
            stepRequested = false;
            game.paused = true;
        }
        else
        {
            debuggerPaused = false;
            stepRequested = false;
            game.paused = previousPaused;
        }
    }

    internal static void Toggle(RainWorldGame game) => SetPaused(game, !debuggerPaused);

    internal static void Step(RainWorldGame game)
    {
        if (game == null || game.pauseMenu != null) return;
        currentGame = game;
        if (!debuggerPaused)
        {
            previousPaused = game.paused;
            debuggerPaused = true;
        }
        game.paused = true;
        stepRequested = true;
    }

    internal static void PauseForBreakpoint()
    {
        if (currentGame != null) SetPaused(currentGame, true);
    }

    internal static void Uninstall()
    {
        try
        {
            if (debuggerPaused && currentGame != null) currentGame.paused = previousPaused;
            updateHook?.Dispose();
        }
        catch (Exception error)
        {
            logger?.LogWarning("AI Observatory world-step hook dispose failed: " + error.Message);
        }
        updateHook = null;
        debuggerPaused = false;
        stepRequested = false;
        currentGame = null;
    }

    private static void GameUpdateHook(GameUpdateOrig orig, RainWorldGame self)
    {
        currentGame = self;
        if (!debuggerPaused)
        {
            RunOriginalAndRecord(orig, self);
            return;
        }

        // Native pause menus own their own paused update loop. Do not try to advance
        // gameplay underneath one; a requested step remains pending until it closes.
        if (self.pauseMenu != null)
        {
            self.paused = true;
            RunOriginalAndRecord(orig, self);
            return;
        }

        if (!stepRequested)
        {
            self.paused = true;
            RunOriginalAndRecord(orig, self); // PausedUpdate/HUD path; clock does not advance.
            return;
        }

        stepRequested = false;
        self.paused = false;
        try
        {
            RunOriginalAndRecord(orig, self); // Exactly one complete simulation tick.
        }
        finally
        {
            self.paused = true;
        }
    }

    private static void RunOriginalAndRecord(GameUpdateOrig orig, RainWorldGame self)
    {
        int beforeClock = self.clock;
        orig(self);

        // RainWorldGame.clock increments exactly when the normal simulation branch runs.
        // This keeps the recorder on simulation time rather than MonoBehaviour/Present
        // frames, and naturally ignores native paused updates while preserving Step 1 Tick.
        if (self.clock != beforeClock)
            AIDebugRecorder.OnSimulationTick(self);
    }
}
