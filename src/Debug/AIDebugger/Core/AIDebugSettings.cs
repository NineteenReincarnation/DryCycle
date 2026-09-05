using System;
using System.Globalization;
using System.IO;
using BepInEx;
using BepInEx.Logging;
using UnityEngine;

namespace DryCycle.Debugging.AI;

internal static class AIDebugSettings
{
    private static ManualLogSource logger;
    private static bool loaded;

    internal static float UiScale = 1f;
    internal static float FontScale = 1f;
    internal static float Opacity = 0.96f;
    internal static bool AutoOpen;
    internal static int HistorySeconds = 30;
    internal static bool ShowRawNames;
    internal static bool ShowDataAge = true;
    internal static bool ShowIds = true;
    internal static bool Overlay = true;
    internal static bool OverlayPhysics = true;
    internal static bool OverlayMovement = true;
    internal static bool OverlayPath = true;
    internal static bool OverlayAImap;
    internal static bool OverlayPerception;
    internal static bool OverlaySocial = true;
    internal static bool OverlayCombat = true;
    internal static bool OverlayLabels = true;
    internal static bool RecordFullHistory = true;
    internal static bool TriggerCapture = true;
    internal static bool DetectAnomalies = true;
    internal static bool BreakpointPausesWorld = true;

    internal static string ConfigPath => Path.Combine(Paths.ConfigPath, "DryCycle.AIObservatory.cfg");
    internal static string LayoutPath => Path.Combine(Paths.ConfigPath, "DryCycle.AIObservatory.imgui.ini");
    internal static string CaptureDirectory => Path.Combine(Paths.ConfigPath, "DryCycle.AIObservatory.Captures");

    internal static void Load(ManualLogSource log)
    {
        logger = log;
        if (loaded) return;
        loaded = true;
        ResetDefaults(save: false);
        try
        {
            if (!File.Exists(ConfigPath)) return;
            foreach (string raw in File.ReadAllLines(ConfigPath))
            {
                string line = raw?.Trim();
                if (string.IsNullOrEmpty(line) || line.StartsWith("#", StringComparison.Ordinal)) continue;
                int split = line.IndexOf('=');
                if (split <= 0) continue;
                string key = line.Substring(0, split).Trim();
                string value = line.Substring(split + 1).Trim();
                Apply(key, value);
            }
            Normalize();
        }
        catch (Exception error)
        {
            logger?.LogWarning("DryCycle AI Observatory settings could not be loaded: " + error.Message);
        }
    }

    internal static void Save()
    {
        Normalize();
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath) ?? Paths.ConfigPath);
            using StreamWriter writer = new(ConfigPath, false);
            writer.WriteLine("# DryCycle AI Observatory developer settings");
            Write(writer, "language", AIDebugLocalization.Language == AIDebugLanguage.Chinese ? "zh-CN" : "en-US");
            Write(writer, "uiScale", UiScale);
            Write(writer, "fontScale", FontScale);
            Write(writer, "opacity", Opacity);
            Write(writer, "autoOpen", AutoOpen);
            Write(writer, "historySeconds", HistorySeconds);
            Write(writer, "showRawNames", ShowRawNames);
            Write(writer, "showDataAge", ShowDataAge);
            Write(writer, "showIds", ShowIds);
            Write(writer, "overlay", Overlay);
            Write(writer, "overlayPhysics", OverlayPhysics);
            Write(writer, "overlayMovement", OverlayMovement);
            Write(writer, "overlayPath", OverlayPath);
            Write(writer, "overlayAImap", OverlayAImap);
            Write(writer, "overlayPerception", OverlayPerception);
            Write(writer, "overlaySocial", OverlaySocial);
            Write(writer, "overlayCombat", OverlayCombat);
            Write(writer, "overlayLabels", OverlayLabels);
            Write(writer, "recordFullHistory", RecordFullHistory);
            Write(writer, "triggerCapture", TriggerCapture);
            Write(writer, "detectAnomalies", DetectAnomalies);
            Write(writer, "breakpointPausesWorld", BreakpointPausesWorld);
        }
        catch (Exception error)
        {
            logger?.LogWarning("DryCycle AI Observatory settings could not be saved: " + error.Message);
        }
    }

    internal static void ResetDefaults(bool save = true)
    {
        UiScale = 1f;
        FontScale = 1f;
        Opacity = 0.96f;
        AutoOpen = false;
        HistorySeconds = 30;
        ShowRawNames = false;
        ShowDataAge = true;
        ShowIds = true;
        Overlay = true;
        OverlayPhysics = true;
        OverlayMovement = true;
        OverlayPath = true;
        OverlayAImap = false;
        OverlayPerception = false;
        OverlaySocial = true;
        OverlayCombat = true;
        OverlayLabels = true;
        RecordFullHistory = true;
        TriggerCapture = true;
        DetectAnomalies = true;
        BreakpointPausesWorld = true;
        if (save) Save();
    }

    private static void Apply(string key, string value)
    {
        switch (key)
        {
            case "language":
                AIDebugLocalization.Language = value.Equals("en-US", StringComparison.OrdinalIgnoreCase)
                    ? AIDebugLanguage.English : AIDebugLanguage.Chinese;
                break;
            case "uiScale": UiScale = Float(value, UiScale); break;
            case "fontScale": FontScale = Float(value, FontScale); break;
            case "opacity": Opacity = Float(value, Opacity); break;
            case "autoOpen": AutoOpen = Bool(value, AutoOpen); break;
            case "historySeconds": HistorySeconds = Int(value, HistorySeconds); break;
            case "showRawNames": ShowRawNames = Bool(value, ShowRawNames); break;
            case "showDataAge": ShowDataAge = Bool(value, ShowDataAge); break;
            case "showIds": ShowIds = Bool(value, ShowIds); break;
            case "overlay": Overlay = Bool(value, Overlay); break;
            case "overlayPhysics": OverlayPhysics = Bool(value, OverlayPhysics); break;
            case "overlayMovement": OverlayMovement = Bool(value, OverlayMovement); break;
            case "overlayPath": OverlayPath = Bool(value, OverlayPath); break;
            case "overlayAImap": OverlayAImap = Bool(value, OverlayAImap); break;
            case "overlayPerception": OverlayPerception = Bool(value, OverlayPerception); break;
            case "overlaySocial": OverlaySocial = Bool(value, OverlaySocial); break;
            case "overlayCombat": OverlayCombat = Bool(value, OverlayCombat); break;
            case "overlayLabels": OverlayLabels = Bool(value, OverlayLabels); break;
            case "recordFullHistory": RecordFullHistory = Bool(value, RecordFullHistory); break;
            case "triggerCapture": TriggerCapture = Bool(value, TriggerCapture); break;
            case "detectAnomalies": DetectAnomalies = Bool(value, DetectAnomalies); break;
            case "breakpointPausesWorld": BreakpointPausesWorld = Bool(value, BreakpointPausesWorld); break;
        }
    }

    private static void Normalize()
    {
        UiScale = Mathf.Clamp(UiScale, 0.75f, 1.75f);
        FontScale = Mathf.Clamp(FontScale, 0.75f, 1.75f);
        Opacity = Mathf.Clamp(Opacity, 0.55f, 1f);
        HistorySeconds = Mathf.Clamp(HistorySeconds, 5, 60);
    }

    private static float Float(string value, float fallback) =>
        float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out float parsed) ? parsed : fallback;
    private static int Int(string value, int fallback) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed) ? parsed : fallback;
    private static bool Bool(string value, bool fallback) => bool.TryParse(value, out bool parsed) ? parsed : fallback;

    private static void Write(StreamWriter writer, string key, object value)
    {
        string text = value switch
        {
            float f => f.ToString("0.###", CultureInfo.InvariantCulture),
            double d => d.ToString("0.###", CultureInfo.InvariantCulture),
            _ => Convert.ToString(value, CultureInfo.InvariantCulture)
        };
        writer.WriteLine(key + "=" + text);
    }
}
