using System;
using BepInEx.Logging;

namespace DryCycle.Debugging.AI;

internal static class AIDebugSimulationControl
{
    private static bool installed;
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
        AIDebugSessionBlockWriter.Initialize(log);
        if (installed) return;

        On.RainWorldGame.Update += RainWorldGame_Update;
        installed = true;
        logger?.LogInfo("DryCycle AI Observatory world-step/recorder tick hook installed through HookGen.");
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
            if (debuggerPaused && currentGame != null)
                currentGame.paused = previousPaused;

            if (installed)
                On.RainWorldGame.Update -= RainWorldGame_Update;
        }
        catch (Exception error)
        {
            StartupDiagnostics.Failure("AIDebugSimulationControl.Uninstall", error);
            logger?.LogWarning("AI Observatory world-step hook cleanup failed: " + error.Message);
        }

        AIDebugBreakpointManager.Reset();
        AIDebugDeepProfiler.Reset();
        AIDebugOfflineSessionStore.Reset();
        AIDebugSessionBlockWriter.Shutdown();

        installed = false;
        debuggerPaused = false;
        stepRequested = false;
        currentGame = null;
    }

    private static void RainWorldGame_Update(On.RainWorldGame.orig_Update orig, RainWorldGame self)
    {
        currentGame = self;
        if (!debuggerPaused)
        {
            RunOriginalAndRecord(orig, self);
            return;
        }

        // Native pause menus own their own paused update loop. Do not try to advance gameplay
        // underneath one; a requested step remains pending until it closes.
        if (self.pauseMenu != null)
        {
            self.paused = true;
            RunOriginalAndRecord(orig, self);
            return;
        }

        if (!stepRequested)
        {
            self.paused = true;
            RunOriginalAndRecord(orig, self);
            return;
        }

        stepRequested = false;
        self.paused = false;
        try
        {
            RunOriginalAndRecord(orig, self);
        }
        finally
        {
            self.paused = true;
        }
    }

    private static void RunOriginalAndRecord(On.RainWorldGame.orig_Update orig, RainWorldGame self)
    {
        int beforeClock = self.clock;
        orig(self);

        if (self.clock == beforeClock)
            return;

        long profile = AIDebugDeepProfiler.BeginTick();
        try
        {
            AIDebugRecorder.OnSimulationTick(self);
            AIDebugRichRecorder.OnSimulationTick(self);
        }
        finally
        {
            AIDebugDeepProfiler.EndTick(profile);
        }
    }
}
