using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using DevInterface;
using UnityEngine;

namespace DryCycle.Weather.Spatial;

/// <summary>
/// Adds an in-panel help page to the Hovered Room Weather diagnostic panel.
/// The panel itself remains the same DevUI node; this runtime only switches the
/// visibility of the data rows and the explanatory rows.
/// </summary>
internal static class WeatherSpatialHoverHelpRuntime
{
    private const string HoverPanelId = "DryCycle_Weather_Room_Hover_Info";
    private const string HelpToggleId = "DryCycle_Weather_Room_Hover_Help";
    private const string HelpLabelPrefix = "DryCycle_Weather_Room_Hover_Help_Line_";

    private sealed class PanelState
    {
        internal Button Toggle;
        internal readonly List<DevUILabel> HelpLabels = new();
    }

    private static ConditionalWeakTable<DevUINode, PanelState> _states = new();
    private static bool _enabled;
    private static bool _helpMode;

    internal static void Enable()
    {
        if (_enabled)
        {
            return;
        }

        On.DevInterface.MapPage.Update += MapPage_Update;
        On.DevInterface.Button.Clicked += Button_Clicked;
        _enabled = true;
    }

    internal static void Disable()
    {
        if (!_enabled)
        {
            return;
        }

        On.DevInterface.Button.Clicked -= Button_Clicked;
        On.DevInterface.MapPage.Update -= MapPage_Update;
        _states = new ConditionalWeakTable<DevUINode, PanelState>();
        _helpMode = false;
        _enabled = false;
    }

    private static void MapPage_Update(
        On.DevInterface.MapPage.orig_Update orig,
        MapPage self)
    {
        orig(self);

        DevUINode panel = FindRecursive(self, HoverPanelId);
        if (panel == null)
        {
            return;
        }

        PanelState state = _states.GetValue(panel, CreateState);
        ApplyMode(panel, state);
    }

    private static void Button_Clicked(
        On.DevInterface.Button.orig_Clicked orig,
        Button self)
    {
        if (self != null && self.IDstring == HelpToggleId)
        {
            DevUINode panel = FindAncestor(self, HoverPanelId);
            if (panel != null)
            {
                _helpMode = !_helpMode;
                PanelState state = _states.GetValue(panel, CreateState);
                ApplyMode(panel, state);
            }
            return;
        }

        orig(self);
    }

    private static PanelState CreateState(DevUINode panel)
    {
        PanelState state = new();

        // Keep the button inside the top-right corner of the existing 310x154 panel.
        // It deliberately sits beside the panel's native title-bar controls rather than
        // creating a second floating window.
        state.Toggle = new Button(
            panel.owner,
            HelpToggleId,
            panel,
            new Vector2(278f, 132f),
            24f,
            _helpMode ? "<" : "?");
        panel.subNodes.Add(state.Toggle);

        string[] lines =
        {
            "Help: how to read this panel",
            "Green = Allow   Red = Forbidden",
            "R+ / R- = explicit Room rule",
            "Z+ / Z- = Region Default rule",
            "G+ / G- = Global Default rule",
            "-- = no explicit rule / no chance",
            "FamWeather = weather family",
            "SubWeather = concrete weather",
            "DangerType = dangerous weather",
            "% = Region schedule chance, NOT room",
            "Rooms only decide Allow / Forbidden"
        };

        for (int i = 0; i < lines.Length; i++)
        {
            float y = 122f - i * 11f;
            DevUILabel label = new(
                panel.owner,
                HelpLabelPrefix + i,
                panel,
                new Vector2(8f, y),
                294f,
                lines[i]);
            label.spriteColor = Color.black;
            label.textColor = i == 0 ? Color.white : new Color(0.82f, 0.82f, 0.82f);
            panel.subNodes.Add(label);
            state.HelpLabels.Add(label);
        }

        return state;
    }

    private static void ApplyMode(DevUINode panel, PanelState state)
    {
        if (panel == null || state == null)
        {
            return;
        }

        bool panelVisible = IsPanelVisible(panel);
        SetNodeVisible(state.Toggle, panelVisible);
        if (state.Toggle != null)
        {
            state.Toggle.Text = _helpMode ? "<" : "?";
        }

        for (int i = 0; i < state.HelpLabels.Count; i++)
        {
            SetNodeVisible(state.HelpLabels[i], panelVisible && _helpMode);
        }

        if (!panelVisible)
        {
            return;
        }

        // The original panel refreshes its data rows every frame. In help mode we hide
        // only those known data nodes, leaving the Panel title/border/native controls
        // untouched. When help mode is off, the original panel owns their visibility.
        if (_helpMode && panel.subNodes != null)
        {
            for (int i = 0; i < panel.subNodes.Count; i++)
            {
                DevUINode node = panel.subNodes[i];
                if (node != null && IsDataNode(node.IDstring))
                {
                    SetNodeVisible(node, false);
                }
            }
        }
    }

    private static bool IsDataNode(string id)
    {
        if (string.IsNullOrEmpty(id))
        {
            return false;
        }

        return id == "HoverRoom" ||
               id == "HoverLegend" ||
               id == "HoverFamilyHeader" ||
               id == "HoverWeatherHeader" ||
               id == "HoverDangerHeader" ||
               id.StartsWith("HoverFamily", StringComparison.Ordinal) ||
               id.StartsWith("HoverWeather", StringComparison.Ordinal) ||
               id.StartsWith("HoverDanger", StringComparison.Ordinal);
    }

    private static DevUINode FindRecursive(DevUINode root, string id)
    {
        if (root == null)
        {
            return null;
        }
        if (root.IDstring == id)
        {
            return root;
        }
        if (root.subNodes == null)
        {
            return null;
        }

        for (int i = 0; i < root.subNodes.Count; i++)
        {
            DevUINode found = FindRecursive(root.subNodes[i], id);
            if (found != null)
            {
                return found;
            }
        }
        return null;
    }

    private static DevUINode FindAncestor(DevUINode node, string id)
    {
        DevUINode current = node;
        while (current != null)
        {
            if (current.IDstring == id)
            {
                return current;
            }
            current = current.parentNode;
        }
        return null;
    }

    private static bool IsPanelVisible(DevUINode panel)
    {
        if (panel == null)
        {
            return false;
        }

        for (int i = 0; i < panel.fSprites.Count; i++)
        {
            if (panel.fSprites[i] != null && panel.fSprites[i].isVisible)
            {
                return true;
            }
        }
        for (int i = 0; i < panel.fLabels.Count; i++)
        {
            if (panel.fLabels[i] != null && panel.fLabels[i].isVisible)
            {
                return true;
            }
        }
        return false;
    }

    private static void SetNodeVisible(DevUINode node, bool visible)
    {
        if (node == null)
        {
            return;
        }

        for (int i = 0; i < node.fSprites.Count; i++)
        {
            if (node.fSprites[i] != null)
            {
                node.fSprites[i].isVisible = visible;
            }
        }
        for (int i = 0; i < node.fLabels.Count; i++)
        {
            if (node.fLabels[i] != null)
            {
                node.fLabels[i].isVisible = visible;
            }
        }
        if (node.subNodes == null)
        {
            return;
        }
        for (int i = 0; i < node.subNodes.Count; i++)
        {
            SetNodeVisible(node.subNodes[i], visible);
        }
    }
}
