using System;
using System.Collections.Generic;
using DevInterface;
using UnityEngine;

namespace DryCycle.DevUI.DevTool.Compatibility;

public enum LegacyControlKind
{
    Button,
    Slider
}

/// <summary>
/// Detached description of a standard Rain World DevInterface control belonging to the
/// selected PlacedObjectRepresentation. The bridge knows only vanilla DevInterface types;
/// it never checks the owning mod or a third-party framework.
/// </summary>
public sealed class LegacyControlSnapshot
{
    public string Path { get; init; } = string.Empty;
    public string Id { get; init; } = string.Empty;
    public string Label { get; init; } = string.Empty;
    public LegacyControlKind Kind { get; init; }
    public string ValueText { get; init; } = string.Empty;
    public float Factor { get; init; }
    public bool CanReset { get; init; }
}

internal static class LegacyDevInterfaceBridge
{
    internal static LegacyControlSnapshot[] Capture(DevUI owner, PlacedObject target)
    {
        if (owner?.activePage is not ObjectsPage page || target == null)
            return Array.Empty<LegacyControlSnapshot>();

        PlacedObjectRepresentation representation = FindRepresentation(page, target);
        if (representation == null) return Array.Empty<LegacyControlSnapshot>();

        List<LegacyControlSnapshot> result = new();
        CaptureChildren(representation, string.Empty, result);
        return result.ToArray();
    }

    internal static bool ClickButton(DevUI owner, PlacedObject target, string path)
    {
        PlacedObjectRepresentation representation = FindRepresentation(owner?.activePage as ObjectsPage, target);
        if (representation == null) return false;
        if (ResolveNode(representation, path) is not Button button) return false;

        try
        {
            button.Clicked();
            owner.activePage?.Refresh();
            return true;
        }
        catch (Exception error)
        {
            Plugin.Logger?.LogWarning("DevTool legacy button invocation failed: " + error.Message);
            return false;
        }
    }

    internal static bool SetSlider(DevUI owner, PlacedObject target, string path, float factor)
    {
        PlacedObjectRepresentation representation = FindRepresentation(owner?.activePage as ObjectsPage, target);
        if (representation == null) return false;
        if (ResolveNode(representation, path) is not Slider slider) return false;

        try
        {
            factor = Mathf.Clamp01(factor);
            slider.NubDragged(factor);
            slider.RefreshNubPos(factor);
            slider.Refresh();
            return true;
        }
        catch (Exception error)
        {
            Plugin.Logger?.LogWarning("DevTool legacy slider mutation failed: " + error.Message);
            return false;
        }
    }

    internal static bool ResetSlider(DevUI owner, PlacedObject target, string path)
    {
        PlacedObjectRepresentation representation = FindRepresentation(owner?.activePage as ObjectsPage, target);
        if (representation == null) return false;
        if (ResolveNode(representation, path) is not Slider slider || !slider.inheritButton) return false;

        try
        {
            slider.ClickedResetToInherent();
            slider.Refresh();
            return true;
        }
        catch (Exception error)
        {
            Plugin.Logger?.LogWarning("DevTool legacy slider reset failed: " + error.Message);
            return false;
        }
    }

    private static void CaptureChildren(DevUINode parent, string parentPath, List<LegacyControlSnapshot> output)
    {
        if (parent?.subNodes == null) return;

        for (int i = 0; i < parent.subNodes.Count; i++)
        {
            DevUINode node = parent.subNodes[i];
            if (node == null) continue;
            string path = string.IsNullOrEmpty(parentPath) ? i.ToString() : parentPath + "." + i;

            // A Slider is a composite vanilla control. Capture it once and do not expose
            // its internal labels/nub as separate inspector rows.
            if (node is Slider slider)
            {
                output.Add(new LegacyControlSnapshot
                {
                    Path = path,
                    Id = slider.IDstring ?? string.Empty,
                    Label = SliderTitle(slider),
                    Kind = LegacyControlKind.Slider,
                    ValueText = SafeNumberText(slider),
                    Factor = SliderFactor(slider),
                    CanReset = slider.inheritButton
                });
                continue;
            }

            if (node is Button button && !IsInfrastructureButton(button))
            {
                output.Add(new LegacyControlSnapshot
                {
                    Path = path,
                    Id = button.IDstring ?? string.Empty,
                    Label = string.IsNullOrWhiteSpace(button.Text) ? button.IDstring ?? "Button" : button.Text,
                    Kind = LegacyControlKind.Button
                });
            }

            CaptureChildren(node, path, output);
        }
    }

    private static bool IsInfrastructureButton(Button button)
    {
        string id = button?.IDstring ?? string.Empty;
        return id == "Save_Settings" || id == "Save_Specific" || id == "Collapse" ||
               id == "Prev_Button" || id == "Next_Button" || id == "Export_Sandbox";
    }

    private static string SliderTitle(Slider slider)
    {
        try
        {
            if (slider.subNodes.Count > 0 && slider.subNodes[0] is DevUILabel title && !string.IsNullOrWhiteSpace(title.Text))
                return title.Text;
        }
        catch { }
        return slider.IDstring ?? "Slider";
    }

    private static string SafeNumberText(Slider slider)
    {
        try { return slider.NumberText ?? string.Empty; }
        catch { return string.Empty; }
    }

    private static float SliderFactor(Slider slider)
    {
        try
        {
            int nubIndex = slider.inheritButton ? 3 : 2;
            if (nubIndex < 0 || nubIndex >= slider.subNodes.Count || slider.subNodes[nubIndex] is not Slider.SliderNub nub)
                return 0f;
            return Mathf.Clamp01((nub.pos.x - slider.SliderStartCoord) / 92f);
        }
        catch
        {
            return 0f;
        }
    }

    private static PlacedObjectRepresentation FindRepresentation(ObjectsPage page, PlacedObject target)
    {
        if (page == null || target == null) return null;
        return FindRepresentationRecursive(page, target);
    }

    private static PlacedObjectRepresentation FindRepresentationRecursive(DevUINode node, PlacedObject target)
    {
        if (node == null) return null;
        if (node is PlacedObjectRepresentation representation && ReferenceEquals(representation.pObj, target))
            return representation;

        for (int i = 0; i < node.subNodes.Count; i++)
        {
            PlacedObjectRepresentation found = FindRepresentationRecursive(node.subNodes[i], target);
            if (found != null) return found;
        }
        return null;
    }

    private static DevUINode ResolveNode(DevUINode root, string path)
    {
        if (root == null || string.IsNullOrWhiteSpace(path)) return null;
        string[] parts = path.Split('.');
        DevUINode current = root;
        for (int i = 0; i < parts.Length; i++)
        {
            if (!int.TryParse(parts[i], out int index) || index < 0 || index >= current.subNodes.Count)
                return null;
            current = current.subNodes[index];
            if (current == null) return null;
        }
        return current;
    }
}
