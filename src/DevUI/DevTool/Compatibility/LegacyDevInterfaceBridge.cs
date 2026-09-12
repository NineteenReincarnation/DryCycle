using System;
using System.Collections.Generic;
using DevInterface;
using UnityEngine;

namespace DryCycle.DevUI.DevTool.Compatibility;

public enum LegacyControlKind
{
    Button,
    Slider,
    Cycler,
    Integer
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
    public int SelectedIndex { get; init; } = -1;
    public string[] Options { get; init; } = Array.Empty<string>();
}

internal static class LegacyDevInterfaceBridge
{
    internal static LegacyControlSnapshot[] Capture(global::DevInterface.DevUI owner, PlacedObject target)
    {
        if (owner?.activePage is not ObjectsPage page || target == null)
            return Array.Empty<LegacyControlSnapshot>();

        PlacedObjectRepresentation representation = FindRepresentation(page, target);
        if (representation == null) return Array.Empty<LegacyControlSnapshot>();

        List<LegacyControlSnapshot> result = new();
        CaptureChildren(representation, string.Empty, result);
        return result.ToArray();
    }

    internal static bool ClickButton(global::DevInterface.DevUI owner, PlacedObject target, string path)
    {
        PlacedObjectRepresentation representation = FindRepresentation(owner?.activePage as ObjectsPage, target);
        if (representation == null) return false;
        if (ResolveNode(representation, path) is not Button button || button is ButtonWithSelectPanel) return false;

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

    internal static bool SetSlider(global::DevInterface.DevUI owner, PlacedObject target, string path, float factor)
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

    internal static bool ResetSlider(global::DevInterface.DevUI owner, PlacedObject target, string path)
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

    internal static bool SetCycler(global::DevInterface.DevUI owner, PlacedObject target, string path, int selectedIndex)
    {
        PlacedObjectRepresentation representation = FindRepresentation(owner?.activePage as ObjectsPage, target);
        if (representation == null) return false;
        if (ResolveNode(representation, path) is not Cycler cycler) return false;
        if (cycler.alternatives == null || selectedIndex < 0 || selectedIndex >= cycler.alternatives.Count) return false;

        try
        {
            cycler.currentAlternative = selectedIndex;
            cycler.Text = (cycler.baseName ?? string.Empty) + (cycler.alternatives[selectedIndex] ?? string.Empty);

            // Vanilla Cycler is intentionally polling-based: parent panels generally copy
            // currentAlternative into their data from Update(), rather than receiving a signal.
            // Run that parent update immediately so the history snapshot taken by the new editor
            // observes the same data mutation in this command, not one frame later. Suppress the
            // legacy click edge while doing so, otherwise Cycler.Update could advance twice.
            SynchronizePollingParent(owner, cycler);
            owner.activePage?.Refresh();
            return true;
        }
        catch (Exception error)
        {
            Plugin.Logger?.LogWarning("DevTool legacy cycler mutation failed: " + error.Message);
            return false;
        }
    }

    internal static bool IncrementInteger(global::DevInterface.DevUI owner, PlacedObject target, string path, int change)
    {
        if (change == 0) return false;
        PlacedObjectRepresentation representation = FindRepresentation(owner?.activePage as ObjectsPage, target);
        if (representation == null) return false;
        if (ResolveNode(representation, path) is not IntegerControl control) return false;

        try
        {
            // Increment is the virtual behavior boundary used by vanilla IntegerControl itself.
            // Calling it preserves subclass side effects such as palette/application refreshes.
            control.Increment(change);
            control.Refresh();
            owner.activePage?.Refresh();
            return true;
        }
        catch (Exception error)
        {
            Plugin.Logger?.LogWarning("DevTool legacy integer mutation failed: " + error.Message);
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

            // These are composite controls. Capture each as one semantic control and never leak
            // their implementation labels, arrow buttons or slider nub into the new inspector.
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

            if (node is Cycler cycler)
            {
                output.Add(new LegacyControlSnapshot
                {
                    Path = path,
                    Id = cycler.IDstring ?? string.Empty,
                    Label = CyclerTitle(cycler),
                    Kind = LegacyControlKind.Cycler,
                    ValueText = CyclerValue(cycler),
                    SelectedIndex = cycler.currentAlternative,
                    Options = CyclerOptions(cycler)
                });
                continue;
            }

            if (node is IntegerControl integerControl)
            {
                output.Add(new LegacyControlSnapshot
                {
                    Path = path,
                    Id = integerControl.IDstring ?? string.Empty,
                    Label = IntegerTitle(integerControl),
                    Kind = LegacyControlKind.Integer,
                    ValueText = SafeIntegerValue(integerControl)
                });
                continue;
            }

            // ButtonWithSelectPanel is not a plain action button: choosing an item creates a
            // SelectPanel and routes the chosen ID through OnValueChange/IDevUISignals. Until its
            // option model is migrated explicitly, do not misrepresent it as a one-shot Button.
            if (node is ButtonWithSelectPanel)
                continue;

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

    private static string CyclerTitle(Cycler cycler)
    {
        if (!string.IsNullOrWhiteSpace(cycler?.baseName))
            return cycler.baseName.Trim().TrimEnd(':').Trim();
        return cycler?.IDstring ?? "Cycler";
    }

    private static string CyclerValue(Cycler cycler)
    {
        if (cycler?.alternatives == null || cycler.currentAlternative < 0 || cycler.currentAlternative >= cycler.alternatives.Count)
            return string.Empty;
        return cycler.alternatives[cycler.currentAlternative] ?? string.Empty;
    }

    private static string[] CyclerOptions(Cycler cycler)
    {
        if (cycler?.alternatives == null || cycler.alternatives.Count == 0)
            return Array.Empty<string>();
        string[] options = new string[cycler.alternatives.Count];
        for (int i = 0; i < options.Length; i++) options[i] = cycler.alternatives[i] ?? string.Empty;
        return options;
    }

    private static string IntegerTitle(IntegerControl control)
    {
        try
        {
            if (control.subNodes.Count > 0 && control.subNodes[0] is DevUILabel title && !string.IsNullOrWhiteSpace(title.Text))
                return title.Text;
        }
        catch { }
        return control?.IDstring ?? "Integer";
    }

    private static string SafeIntegerValue(IntegerControl control)
    {
        try { return control.NumberLabelText ?? string.Empty; }
        catch { return string.Empty; }
    }

    private static void SynchronizePollingParent(global::DevInterface.DevUI owner, DevUINode node)
    {
        if (owner == null || node?.parentNode == null) return;
        bool oldMouseClick = owner.mouseClick;
        try
        {
            owner.mouseClick = false;
            node.parentNode.Update();
        }
        finally
        {
            owner.mouseClick = oldMouseClick;
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
