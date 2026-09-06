using System;
using System.Collections.Generic;
using System.Linq;
using BepInEx.Logging;
using UnityEngine;

namespace DryCycle.Debugging.AI;

// Main-thread owner for the RWImGUI Observatory migration. Rain World / Unity objects are
// read only here. The Present callback consumes the detached AIDebugPresentationSnapshot
// and sends user actions back through AIDebugPresentationHub's command queue.
internal static class AIDebuggerRuntime
{
    private const string BridgeAssemblyName = "DryCycle.AIObservatory.RWImGui";
    private const string RWImGuiAssemblyName = "rain-world-imgui-api";
    private static GameObject hostObject;
    private static AIDebuggerHost host;

    internal static bool Visible => host?.Visible == true;
    internal static bool WantsMouse => AIDebugPresentationHub.WantsMouse;
    internal static bool WantsKeyboard => AIDebugPresentationHub.WantsKeyboard;
    internal static bool BlocksPlayerInput => Visible && (WantsMouse || WantsKeyboard);

    internal static void Install(RainWorld rainWorld, ManualLogSource logger)
    {
        AIDebugSettings.Load(logger);

        bool bridgeAssemblyLoaded = IsAssemblyLoaded(BridgeAssemblyName);
        bool rwimguiAssemblyLoaded = IsAssemblyLoaded(RWImGuiAssemblyName);
        logger?.LogInfo($"DryCycle AI Observatory install requested. AutoOpen={AIDebugSettings.AutoOpen}, existingHost={host != null}, " +
                        $"presentation=RWImGUI, bridgeAssemblyLoaded={bridgeAssemblyLoaded}, rwimguiApiLoaded={rwimguiAssemblyLoaded}.");

        if (!rwimguiAssemblyLoaded)
        {
            logger?.LogWarning("DryCycle AI Observatory presentation is unavailable: Rain World ImGUI API is not loaded by BepInEx. " +
                               "The official ImGUI API Workshop item (3417372413) requires Rawra's Library Loader (3326331909). " +
                               "Install/enable both mods and restart Rain World. DryCycle gameplay systems will continue normally.");
        }
        else if (!bridgeAssemblyLoaded)
        {
            logger?.LogWarning("DryCycle AI Observatory presentation is unavailable: RWImGUI is loaded, but " +
                               "DryCycle.AIObservatory.RWImGui.dll is not loaded. Rebuild DryCycle with the RWImGUI dependency available " +
                               "and verify that the bridge DLL is present in Ancient Site/newest/plugins.");
        }

        AIDebugInputGate.Install(logger);
        AIDebugSimulationControl.Install(logger);

        if (host != null)
        {
            host.Bind(rainWorld, logger);
            logger?.LogInfo("DryCycle AI Observatory rebound to the current RainWorld instance.");
            return;
        }

        AIDebugRegistry.Initialize(logger);
        AIDebugPresentationHub.Reset();
        hostObject = new GameObject("DryCycle AI Observatory Controller")
        {
            hideFlags = HideFlags.HideAndDontSave
        };
        UnityEngine.Object.DontDestroyOnLoad(hostObject);

        host = hostObject.AddComponent<AIDebuggerHost>();
        host.Bind(rainWorld, logger);
        host.SetStartupVisible(AIDebugSettings.AutoOpen);
        logger?.LogInfo($"DryCycle AI Observatory controller created. active={hostObject.activeInHierarchy}, startupVisible={AIDebugSettings.AutoOpen}, " +
                        $"legacyRenderer=disabled, overlayCamera=none, bridge={AIDebugPresentationBridgeStatus.Describe()}.");
    }

    internal static void Uninstall()
    {
        AIDebugSettings.Save();
        AIDebugRichRecorder.Reset();
        AIDebugRecorder.Reset();
        AIDebugTrace.Reset();
        AIDebugSimulationControl.Uninstall();
        AIDebugInputGate.Uninstall();
        AIDebugPresentationHub.Reset();
        if (hostObject != null) UnityEngine.Object.Destroy(hostObject);
        hostObject = null;
        host = null;
    }

    private static bool IsAssemblyLoaded(string name)
    {
        try
        {
            return AppDomain.CurrentDomain.GetAssemblies()
                .Any(a => string.Equals(a.GetName().Name, name, StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            return false;
        }
    }
}

internal sealed class AIDebuggerHost : MonoBehaviour
{
    private const float PresentationInterval = 0.10f;
    private const int EntityRefreshFrames = 15;

    private readonly List<AbstractCreature> entities = new(128);
    private readonly HashSet<int> visibleRooms = new();

    private RainWorld rainWorld;
    private ManualLogSource logger;
    private bool visible;
    private bool lifecycleLogged;
    private bool missingBridgeWarningLogged;
    private bool hasSelection;
    private bool presentationDirty = true;
    private DebugEntityKey selectedKey;
    private AIDebugViewMode viewMode = AIDebugViewMode.Live;
    private int cursorTick;
    private int nextEntityRefreshFrame;
    private float nextPresentationTime;

    internal bool Visible => visible;

    internal void Bind(RainWorld rw, ManualLogSource log)
    {
        rainWorld = rw;
        logger = log;
        presentationDirty = true;
        logger?.LogInfo($"DryCycle AI Observatory controller Bind completed. rainWorld={(rainWorld != null ? "yes" : "no")}, " +
                        $"presentationState={AIDebugPresentationBridgeStatus.Describe()}.");
    }

    internal void SetStartupVisible(bool value)
    {
        visible = value;
        AIDebugTrace.SetVisible(value);
        presentationDirty = true;
    }

    private void Update()
    {
        if (!lifecycleLogged)
        {
            lifecycleLogged = true;
            logger?.LogInfo($"DryCycle AI Observatory controller Update is running. visible={visible}, enabled={enabled}, " +
                            $"active={gameObject.activeInHierarchy}, presentationState={AIDebugPresentationBridgeStatus.Describe()}.");
        }

        RainWorldGame game = CurrentGame();
        if (game != null)
        {
            AIDebugRegistry.BindGame(game);
            AIDebugSimulationControl.Bind(game);
            if (viewMode == AIDebugViewMode.Live) cursorTick = game.clock;
        }

        DrainUiCommands(game);

        if (Input.GetKeyDown(KeyCode.F7))
        {
            bool before = visible;
            visible = !visible;
            AIDebugTrace.SetVisible(visible);
            presentationDirty = true;
            string presentation = AIDebugPresentationBridgeStatus.CallbackRegistered
                ? (AIDebugPresentationBridgeStatus.PresentSeen ? "RWImGUI-connected" : "RWImGUI-callback-waiting-for-Present")
                : "UNAVAILABLE";
            logger?.LogInfo($"DryCycle AI Observatory F7 detected. visible {before} -> {visible}. presentation={presentation}; " +
                            $"{AIDebugPresentationBridgeStatus.Describe()}.");

            if (visible && !AIDebugPresentationBridgeStatus.CallbackRegistered && !missingBridgeWarningLogged)
            {
                missingBridgeWarningLogged = true;
                logger?.LogWarning("DryCycle AI Observatory F7 state is ON, but no RWImGUI callback is registered, so no UI can appear. " +
                                   "Check that BepInEx loads 'Rain World ImGUI API' and 'DryCycle AI Observatory RWImGUI Bridge'. " +
                                   "ImGUI API also requires Rawra's Library Loader. Workshop IDs: ImGUI API=3417372413, Library Loader=3326331909.");
            }
        }

        if (Input.GetKeyDown(KeyCode.F8) &&
            (Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl)) &&
            (Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift)))
        {
            TryExportSession();
        }

        if (visible)
            PublishPresentation(game);
        else if (AIDebugPresentationHub.Current.Visible)
            PublishHidden(game);
    }

    private RainWorldGame CurrentGame()
    {
        try
        {
            return rainWorld?.processManager?.currentMainLoop as RainWorldGame;
        }
        catch
        {
            return null;
        }
    }

    private void DrainUiCommands(RainWorldGame game)
    {
        while (AIDebugPresentationHub.TryDequeue(out AIDebugUiCommand command))
        {
            switch (command.Kind)
            {
                case AIDebugUiCommandKind.SelectEntity:
                    selectedKey = command.Key;
                    hasSelection = true;
                    viewMode = AIDebugViewMode.Live;
                    cursorTick = game?.clock ?? 0;
                    if (game != null)
                    {
                        if (!AIDebugRecorder.Select(game, command.Key))
                        {
                            logger?.LogWarning("DryCycle AI Observatory recorder could not bind selected entity " + command.Key + ".");
                        }
                        else
                        {
                            AIDebugRichRecorder.Select(game, command.Key);
                        }
                    }
                    presentationDirty = true;
                    break;

                case AIDebugUiCommandKind.ClearSelection:
                    hasSelection = false;
                    selectedKey = default;
                    viewMode = AIDebugViewMode.Live;
                    cursorTick = game?.clock ?? 0;
                    AIDebugRichRecorder.ClearSelection();
                    AIDebugRecorder.ClearSelection();
                    presentationDirty = true;
                    break;

                case AIDebugUiCommandKind.TogglePause:
                    if (game != null) AIDebugSimulationControl.Toggle(game);
                    presentationDirty = true;
                    break;

                case AIDebugUiCommandKind.Step:
                    if (game != null) AIDebugSimulationControl.Step(game);
                    presentationDirty = true;
                    break;

                case AIDebugUiCommandKind.ExportSession:
                    TryExportSession();
                    break;

                case AIDebugUiCommandKind.ToggleLanguage:
                    AIDebugLocalization.Language = AIDebugLocalization.Language == AIDebugLanguage.Chinese
                        ? AIDebugLanguage.English
                        : AIDebugLanguage.Chinese;
                    AIDebugSettings.Save();
                    presentationDirty = true;
                    break;

                case AIDebugUiCommandKind.Refresh:
                    nextEntityRefreshFrame = 0;
                    nextPresentationTime = 0f;
                    presentationDirty = true;
                    break;

                case AIDebugUiCommandKind.SeekCursorTicks:
                    if (game != null && hasSelection)
                    {
                        if (viewMode == AIDebugViewMode.Live) cursorTick = game.clock;
                        long desired = (long)cursorTick + command.IntValue;
                        if (desired < 0L) desired = 0L;
                        if (desired > game.clock) desired = game.clock;
                        cursorTick = (int)desired;
                        viewMode = cursorTick >= game.clock ? AIDebugViewMode.Live : AIDebugViewMode.Historical;
                        presentationDirty = true;
                    }
                    break;

                case AIDebugUiCommandKind.ReturnLive:
                    viewMode = AIDebugViewMode.Live;
                    cursorTick = game?.clock ?? cursorTick;
                    presentationDirty = true;
                    break;
            }
        }
    }

    private void PublishPresentation(RainWorldGame game)
    {
        if (!presentationDirty && Time.unscaledTime < nextPresentationTime) return;
        nextPresentationTime = Time.unscaledTime + PresentationInterval;
        presentationDirty = false;

        if (game == null)
        {
            AIDebugPresentationHub.Publish(new AIDebugPresentationSnapshot(
                true,
                false,
                false,
                0,
                AIDebugLocalization.Language,
                Array.Empty<AIDebugPresentationEntity>(),
                null,
                "RainWorldGame is not active."));
            return;
        }

        if (Time.frameCount >= nextEntityRefreshFrame || entities.Count == 0)
        {
            AIDebugRegistry.CollectWorld(game, entities);
            nextEntityRefreshFrame = Time.frameCount + EntityRefreshFrames;
        }

        visibleRooms.Clear();
        if (game.cameras != null)
        {
            for (int i = 0; i < game.cameras.Length; i++)
            {
                AbstractRoom room = game.cameras[i]?.room?.abstractRoom;
                if (room != null) visibleRooms.Add(room.index);
            }
        }

        var presentationEntities = new AIDebugPresentationEntity[entities.Count];
        for (int i = 0; i < entities.Count; i++)
        {
            AbstractCreature creature = entities[i];
            DebugEntityKey key = DebugEntityKey.From(creature);
            string type = creature.creatureTemplate?.type?.value ?? "Creature";
            string room = creature.Room?.name ?? $"room {creature.pos.room}";
            presentationEntities[i] = new AIDebugPresentationEntity(
                key,
                $"{type} #{creature.ID.number}",
                room,
                AIDebugRegistry.EntityState(creature),
                hasSelection && key == selectedKey,
                visibleRooms.Contains(creature.pos.room));
        }

        int viewTick = viewMode == AIDebugViewMode.Historical ? Math.Min(cursorTick, game.clock) : game.clock;
        if (viewMode == AIDebugViewMode.Live) cursorTick = viewTick;

        AIDebugPresentationCreature selectedPresentation = null;
        AIDebugResolvedMotion cursorMotion = default;
        AIDebugResolvedFastState cursorFastState = default;
        if (hasSelection)
        {
            AIDebugRecorderReadApi.TryResolveMotion(selectedKey, viewTick, out cursorMotion);
            AIDebugRecorderReadApi.TryResolveFastState(selectedKey, viewTick, out cursorFastState);

            bool hasRich = viewMode == AIDebugViewMode.Historical
                ? AIDebugRichRecorder.TryResolve(viewTick, out AIDebugResolvedSnapshot resolvedHistorical)
                : AIDebugRichRecorder.TryGetLatest(out AIDebugResolvedSnapshot resolvedHistorical);
            if (hasRich && resolvedHistorical.HasValue)
                selectedPresentation = CopySnapshot(resolvedHistorical.Snapshot, resolvedHistorical.AgeTicks);
        }

        AIDebugRecorderStatus recorder = AIDebugRecorder.GetStatus();
        string recorderStatus = recorder.ActiveTracked > 0
            ? $"{presentationEntities.Length} entities · recorder {recorder.Mode} · tracked {recorder.ActiveTracked} · motion {recorder.MotionSamples} · changes {recorder.StateChanges}" +
              (recorder.DroppedRecords > 0 ? $" · LOST {recorder.DroppedRecords}" : string.Empty)
            : $"{presentationEntities.Length} entities · recorder {recorder.Mode}";

        AIDebugPresentationHub.Publish(new AIDebugPresentationSnapshot(
            true,
            true,
            AIDebugSimulationControl.Paused,
            game.clock,
            AIDebugLocalization.Language,
            presentationEntities,
            selectedPresentation,
            recorderStatus,
            viewMode,
            viewTick,
            cursorMotion,
            cursorFastState));
    }

    private void PublishHidden(RainWorldGame game)
    {
        AIDebugPresentationHub.Publish(new AIDebugPresentationSnapshot(
            false,
            game != null,
            AIDebugSimulationControl.Paused,
            game?.clock ?? 0,
            AIDebugLocalization.Language,
            Array.Empty<AIDebugPresentationEntity>(),
            null,
            string.Empty,
            viewMode,
            cursorTick));
        AIDebugPresentationHub.SetCaptureState(false, false);
    }

    private static AIDebugPresentationCreature CopySnapshot(AIDebugSnapshot source, int snapshotAgeTicks = 0)
    {
        if (source == null) return null;

        var sections = new AIDebugPresentationSection[source.Sections.Count];
        for (int s = 0; s < source.Sections.Count; s++)
        {
            AIDebugSection section = source.Sections[s];
            var values = new AIDebugPresentationValue[section.Values.Count];
            for (int i = 0; i < section.Values.Count; i++)
            {
                AIDebugValue value = section.Values[i];
                values[i] = new AIDebugPresentationValue(
                    value.LabelKey,
                    value.RawName,
                    value.Value,
                    value.AgeTicks + snapshotAgeTicks,
                    value.Source);
            }
            sections[s] = new AIDebugPresentationSection(section.TitleKey, values);
        }

        var decisions = new AIDebugPresentationDecision[source.Decisions.Count];
        for (int i = 0; i < source.Decisions.Count; i++)
        {
            AIDebugDecisionNode decision = source.Decisions[i];
            decisions[i] = new AIDebugPresentationDecision(
                decision.LabelKey,
                decision.State,
                decision.Detail,
                decision.RawName,
                decision.Depth);
        }

        return new AIDebugPresentationCreature(
            source.Key,
            source.DisplayName,
            source.EntityState,
            source.ControlOwner,
            sections,
            decisions);
    }

    private void TryExportSession()
    {
        try
        {
            string path = AIDebugSessionExporter.Export();
            logger?.LogInfo("DryCycle AI Observatory session exported: " + path);
        }
        catch (Exception error)
        {
            logger?.LogWarning("DryCycle AI Observatory session export failed: " + error);
        }
    }

    private void OnDestroy()
    {
        AIDebugSettings.Save();
        AIDebugRichRecorder.Reset();
        AIDebugRecorder.Reset();
        AIDebugTrace.Reset();
        AIDebugPresentationHub.Reset();
    }
}
