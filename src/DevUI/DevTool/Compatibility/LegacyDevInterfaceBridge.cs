using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using DevInterface;
using UnityEngine;

namespace DryCycle.DevUI.DevTool.Compatibility;

public enum LegacyControlKind
{
    Button,
    Boolean,
    Slider,
    Cycler,
    ExtEnum,
    Integer,
    Select,
    PanelSelect,
    Text,
    Direction,
    Color
}

/// <summary>
/// Detached semantic description of a legacy DevInterface control.
///
/// The bridge intentionally describes capabilities instead of concrete mod control classes.
/// Vanilla, RegionKit, DryCycle and third-party controls are handled by the same rules whenever
/// they expose the same DevInterface/base-class/reflection contract.
/// </summary>
public sealed class LegacyControlSnapshot
{
    public string Path { get; init; } = string.Empty;
    public string Id { get; init; } = string.Empty;
    public string Label { get; init; } = string.Empty;
    public string RuntimeType { get; init; } = string.Empty;
    public LegacyControlKind Kind { get; init; }
    public string ValueText { get; init; } = string.Empty;
    public float Factor { get; init; }
    public float X { get; init; }
    public float Y { get; init; }
    public float Z { get; init; }
    public float W { get; init; }
    public bool BooleanValue { get; init; }
    public bool CanReset { get; init; }
    public int SelectedIndex { get; init; } = -1;
    public string[] Options { get; init; } = Array.Empty<string>();
}

/// <summary>
/// Generic DevInterface semantic bridge.
///
/// This is deliberately not a RegionKit adapter table. It recognizes control protocols:
/// Slider/Cycler/IntegerControl, ButtonWithSelectPanel, actualValue-backed bool/string/color
/// buttons, ExtEnum-valued buttons, text committers, direction pickers and finally arbitrary
/// Buttons through their original Clicked() boundary. A button that opens a custom legacy panel
/// remains visible in ImGui; after it is clicked the newly-created child controls are recursively
/// mirrored on the next publication, so custom panels do not require one adapter per mod/type.
/// </summary>
public static class LegacyDevInterfaceBridge
{
    private sealed class SelectOptionCache
    {
        internal string DisplayText = string.Empty;
        internal string[] Options = Array.Empty<string>();
    }

    private const string CyclerActionPrefix = "@cycler|";
    private const string ExtEnumActionPrefix = "@ext-enum|";
    private const string IntegerActionPrefix = "@integer|";
    private const string SelectActionPrefix = "@select|";
    private const string PanelSelectActionPrefix = "@panel-select|";

    private static readonly ConditionalWeakTable<ButtonWithSelectPanel, SelectOptionCache> SelectOptions = new();

    internal static LegacyControlSnapshot[] Capture(global::DevInterface.DevUI owner, PlacedObject target)
    {
        if (owner?.activePage is not ObjectsPage page || target == null)
            return Array.Empty<LegacyControlSnapshot>();

        PlacedObjectRepresentation representation = FindRepresentation(page, target);
        return representation == null ? Array.Empty<LegacyControlSnapshot>() : CaptureRoot(representation);
    }

    /// <summary>
    /// Generic entry point used by coverage/tests and by future page-level mirrors. The root itself
    /// is treated as a container; paths are relative to its subNodes collection.
    /// </summary>
    internal static LegacyControlSnapshot[] CaptureRoot(DevUINode root)
    {
        if (root == null) return Array.Empty<LegacyControlSnapshot>();
        List<LegacyControlSnapshot> result = new();
        CaptureChildren(root, string.Empty, result);
        return result.ToArray();
    }

    public static string CyclerAction(string path, int selectedIndex) =>
        CyclerActionPrefix + selectedIndex + "|" + (path ?? string.Empty);

    public static string ExtEnumAction(string path, int selectedIndex) =>
        ExtEnumActionPrefix + selectedIndex + "|" + (path ?? string.Empty);

    public static string IntegerAction(string path, int change) =>
        IntegerActionPrefix + change + "|" + (path ?? string.Empty);

    public static string SelectAction(string path, int selectedIndex) =>
        SelectActionPrefix + selectedIndex + "|" + (path ?? string.Empty);

    public static string PanelSelectAction(string path, int selectedIndex) =>
        PanelSelectActionPrefix + selectedIndex + "|" + (path ?? string.Empty);

    /// <summary>
    /// Structural capability probe. No namespace or concrete third-party type is involved.
    /// </summary>
    internal static bool CanAdaptNode(DevUINode node)
    {
        if (node == null) return false;
        if (node is Slider || node is Cycler || node is IntegerControl) return true;
        if (node is ButtonWithSelectPanel) return true; // combo when discoverable, generic button otherwise
        if (CanAdaptBoolean(node) || CanAdaptExtEnum(node) || CanAdaptPanelSelect(node) ||
            CanAdaptColorSelect(node) || CanAdaptText(node) || CanAdaptDirection(node))
            return true;
        return node is Button button && !IsInfrastructureButton(button);
    }

    internal static bool IsAtomicAdaptedControl(DevUINode node)
    {
        if (node is Slider || node is Cycler || node is IntegerControl) return true;
        if (node is ButtonWithSelectPanel select && CanAdaptSelect(select)) return true;
        return CanAdaptBoolean(node) || CanAdaptExtEnum(node) || CanAdaptPanelSelect(node) ||
               CanAdaptColorSelect(node) || CanAdaptText(node) || CanAdaptDirection(node);
    }

    internal static bool CanAdaptBoolean(DevUINode node) =>
        node is Button && TryReadBoolMember(node, "actualValue", out _);

    internal static bool CanAdaptExtEnum(DevUINode node) =>
        TryReadExtEnum(node, out _, out _, out _, out _);

    /// <summary>
    /// Kept for the existing coverage API. Any ordinary Button is a valid semantic boundary:
    /// Clicked() is the legacy contract. Composite buttons are not assumed to be leaf controls;
    /// their newly-created child panels are recursively scanned as well.
    /// </summary>
    internal static bool IsTerminalSemanticButton(DevUINode node) =>
        node is Button button && !IsInfrastructureButton(button);

    internal static bool CanAdaptSelect(ButtonWithSelectPanel button) =>
        button != null && ReadSelectOptions(button).Length > 0;

    internal static bool CanAdaptPanelSelect(DevUINode node) =>
        node is Button &&
        TryReadStringOptions(node, "values", out string[] options) && options.Length > 0 &&
        TryReadStringMember(node, "actualValue", out _);

    internal static bool CanAdaptColorSelect(DevUINode node) =>
        node is Button && TryReadColor(node, out _);

    internal static bool CanAdaptText(DevUINode node) =>
        node != null && FindTextCommitMethod(node.GetType()) != null && TryReadTextValue(node, out _);

    internal static bool CanAdaptDirection(DevUINode node) =>
        node != null && TryReadDirection(node, out _) && CanWriteDirection(node);

    internal static bool ClickButton(global::DevInterface.DevUI owner, PlacedObject target, string path)
    {
        if (TryParseCompositeAction(path, CyclerActionPrefix, out int cyclerIndex, out string cyclerPath))
            return SetCycler(owner, target, cyclerPath, cyclerIndex);
        if (TryParseCompositeAction(path, ExtEnumActionPrefix, out int extEnumIndex, out string extEnumPath))
            return SetExtEnum(owner, target, extEnumPath, extEnumIndex);
        if (TryParseCompositeAction(path, IntegerActionPrefix, out int integerChange, out string integerPath))
            return IncrementInteger(owner, target, integerPath, integerChange);
        if (TryParseCompositeAction(path, SelectActionPrefix, out int selectedIndex, out string selectPath))
            return SetSelect(owner, target, selectPath, selectedIndex);
        if (TryParseCompositeAction(path, PanelSelectActionPrefix, out int panelIndex, out string panelPath))
            return SetPanelSelect(owner, target, panelPath, panelIndex);

        PlacedObjectRepresentation representation = FindRepresentation(owner?.activePage as ObjectsPage, target);
        if (representation == null) return false;
        if (ResolveNode(representation, path) is not Button button || IsInfrastructureButton(button)) return false;

        try
        {
            // Do not reproduce the implementation of custom buttons. Clicked() is exactly the
            // semantic boundary the original UI itself uses. If it opens a panel, CaptureChildren
            // will mirror that panel recursively on the following frame.
            button.Clicked();
            button.Refresh();
            return true;
        }
        catch (Exception error)
        {
            Plugin.Logger?.LogWarning("DevTool generic button invocation failed: " + error.Message);
            return false;
        }
    }

    internal static bool SetSlider(global::DevInterface.DevUI owner, PlacedObject target, string path, float factor)
    {
        PlacedObjectRepresentation representation = FindRepresentation(owner?.activePage as ObjectsPage, target);
        if (representation == null || ResolveNode(representation, path) is not Slider slider) return false;

        try
        {
            factor = Mathf.Clamp01(factor);

            // Some custom sliders expose a second semantic drag method while intentionally making
            // vanilla NubDragged a no-op. Prefer any compatible NubDragged2(float) contract, then
            // fall back to the vanilla virtual method. This is structural, not RegionKit-specific.
            if (!TryInvokeSemanticSliderDrag(slider, factor))
            {
                slider.NubDragged(factor);
                slider.RefreshNubPos(factor);
            }

            slider.Refresh();
            SynchronizePollingParent(owner, slider);
            return true;
        }
        catch (Exception error)
        {
            Plugin.Logger?.LogWarning("DevTool generic slider mutation failed: " + error.Message);
            return false;
        }
    }

    internal static bool ResetLegacyBoolean(global::DevInterface.DevUI owner, PlacedObject target, string path, bool desired)
    {
        PlacedObjectRepresentation representation = FindRepresentation(owner?.activePage as ObjectsPage, target);
        if (representation == null) return false;
        DevUINode node = ResolveNode(representation, path);
        if (!CanAdaptBoolean(node) || node is not Button button) return false;

        try
        {
            if (TryReadBoolMember(node, "actualValue", out bool current) && current == desired) return true;
            button.Clicked();
            button.Refresh();
            if (TryReadBoolMember(node, "actualValue", out bool after) && after == desired) return true;

            // Fallback for value-backed buttons whose Clicked implementation only opens a helper
            // panel. Preserve the same final parent signal used by generic value buttons.
            if (!TryWriteMember(node, "actualValue", desired)) return false;
            PropagateSignal(button, DevUISignalType.ButtonClick, string.Empty);
            button.Refresh();
            return TryReadBoolMember(node, "actualValue", out after) && after == desired;
        }
        catch (Exception error)
        {
            Plugin.Logger?.LogWarning("DevTool generic boolean mutation failed: " + error.Message);
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
            SynchronizePollingParent(owner, slider);
            return true;
        }
        catch (Exception error)
        {
            Plugin.Logger?.LogWarning("DevTool generic slider reset failed: " + error.Message);
            return false;
        }
    }

    internal static bool SetCycler(global::DevInterface.DevUI owner, PlacedObject target, string path, int selectedIndex)
    {
        PlacedObjectRepresentation representation = FindRepresentation(owner?.activePage as ObjectsPage, target);
        if (representation == null || ResolveNode(representation, path) is not Cycler cycler) return false;
        if (cycler.alternatives == null || selectedIndex < 0 || selectedIndex >= cycler.alternatives.Count) return false;

        try
        {
            cycler.currentAlternative = selectedIndex;
            string selected = cycler.alternatives[selectedIndex] ?? string.Empty;
            cycler.Text = (cycler.baseName ?? string.Empty) + selected;
            PropagateSignal(cycler, DevUISignalType.ButtonClick, selected);
            SynchronizePollingParent(owner, cycler);
            cycler.Refresh();
            return true;
        }
        catch (Exception error)
        {
            Plugin.Logger?.LogWarning("DevTool generic cycler mutation failed: " + error.Message);
            return false;
        }
    }

    internal static bool SetExtEnum(global::DevInterface.DevUI owner, PlacedObject target, string path, int selectedIndex)
    {
        PlacedObjectRepresentation representation = FindRepresentation(owner?.activePage as ObjectsPage, target);
        if (representation == null) return false;
        DevUINode node = ResolveNode(representation, path);
        if (!TryReadExtEnum(node, out Type enumType, out string memberName, out string[] options, out _)) return false;
        if (selectedIndex < 0 || selectedIndex >= options.Length) return false;

        try
        {
            object parsed = ExtEnumBase.Parse(enumType, options[selectedIndex], false);
            if (!TryWriteMember(node, memberName, parsed)) return false;
            node.Refresh();
            if (node is Button button)
                PropagateSignal(button, DevUISignalType.ButtonClick, options[selectedIndex]);
            SynchronizePollingParent(owner, node);
            return TryReadExtEnum(node, out _, out _, out _, out int actualIndex) && actualIndex == selectedIndex;
        }
        catch (Exception error)
        {
            Plugin.Logger?.LogWarning("DevTool generic ExtEnum mutation failed: " + error.Message);
            return false;
        }
    }

    internal static bool IncrementInteger(global::DevInterface.DevUI owner, PlacedObject target, string path, int change)
    {
        if (change == 0) return false;
        PlacedObjectRepresentation representation = FindRepresentation(owner?.activePage as ObjectsPage, target);
        if (representation == null || ResolveNode(representation, path) is not IntegerControl control) return false;

        try
        {
            control.Increment(change);
            control.Refresh();
            SynchronizePollingParent(owner, control);
            return true;
        }
        catch (Exception error)
        {
            Plugin.Logger?.LogWarning("DevTool generic integer mutation failed: " + error.Message);
            return false;
        }
    }

    internal static bool SetSelect(global::DevInterface.DevUI owner, PlacedObject target, string path, int selectedIndex)
    {
        PlacedObjectRepresentation representation = FindRepresentation(owner?.activePage as ObjectsPage, target);
        if (representation == null || ResolveNode(representation, path) is not ButtonWithSelectPanel button) return false;

        string[] options = ReadSelectOptions(button);
        if (selectedIndex < 0 || selectedIndex >= options.Length) return false;

        try
        {
            CloseSelectPanel(button);
            button.OnValueChange(options[selectedIndex]);
            SelectOptions.Remove(button);
            button.Refresh();
            return true;
        }
        catch (Exception error)
        {
            Plugin.Logger?.LogWarning("DevTool generic select mutation failed: " + error.Message);
            return false;
        }
    }

    internal static bool SetPanelSelect(global::DevInterface.DevUI owner, PlacedObject target, string path, int selectedIndex)
    {
        PlacedObjectRepresentation representation = FindRepresentation(owner?.activePage as ObjectsPage, target);
        if (representation == null) return false;
        DevUINode node = ResolveNode(representation, path);
        if (!CanAdaptPanelSelect(node) || node is not Button button) return false;
        if (!TryReadStringOptions(node, "values", out string[] options) ||
            selectedIndex < 0 || selectedIndex >= options.Length)
            return false;

        try
        {
            string selected = options[selectedIndex] ?? string.Empty;
            if (!TryWriteMember(node, "actualValue", selected)) return false;
            button.Text = selected;
            PropagateSignal(button, DevUISignalType.ButtonClick, selected);
            button.Refresh();
            return TryReadStringMember(node, "actualValue", out string actual) &&
                   string.Equals(actual, selected, StringComparison.Ordinal);
        }
        catch (Exception error)
        {
            Plugin.Logger?.LogWarning("DevTool generic option mutation failed: " + error.Message);
            return false;
        }
    }

    internal static bool SetText(global::DevInterface.DevUI owner, PlacedObject target, string path, string value)
    {
        PlacedObjectRepresentation representation = FindRepresentation(owner?.activePage as ObjectsPage, target);
        if (representation == null) return false;
        DevUINode node = ResolveNode(representation, path);
        MethodInfo commit = FindTextCommitMethod(node?.GetType());
        if (commit == null || !TryReadTextValue(node, out _)) return false;

        try
        {
            commit.Invoke(node, new object[] { value ?? string.Empty, true });
            node.Refresh();
            return TryReadTextValue(node, out string actual) &&
                   string.Equals(actual, value ?? string.Empty, StringComparison.Ordinal);
        }
        catch (Exception error)
        {
            Plugin.Logger?.LogWarning("DevTool generic text mutation failed: " + error.Message);
            return false;
        }
    }

    internal static bool SetDirection(global::DevInterface.DevUI owner, PlacedObject target, string path, float x, float y)
    {
        PlacedObjectRepresentation representation = FindRepresentation(owner?.activePage as ObjectsPage, target);
        if (representation == null) return false;
        DevUINode node = ResolveNode(representation, path);
        if (!CanAdaptDirection(node)) return false;

        try
        {
            Vector2 direction = new Vector2(x, y);
            direction = direction.sqrMagnitude > 0.000001f ? direction.normalized : Vector2.up;
            if (!TryWriteDirection(node, direction)) return false;
            node.Refresh();
            SynchronizePollingParent(owner, node);
            return TryReadDirection(node, out Vector2 actual) && Vector2.Dot(actual, direction) > 0.999f;
        }
        catch (Exception error)
        {
            Plugin.Logger?.LogWarning("DevTool generic direction mutation failed: " + error.Message);
            return false;
        }
    }

    internal static bool SetColor(
        global::DevInterface.DevUI owner,
        PlacedObject target,
        string path,
        float r,
        float g,
        float b,
        float a)
    {
        PlacedObjectRepresentation representation = FindRepresentation(owner?.activePage as ObjectsPage, target);
        if (representation == null) return false;
        DevUINode node = ResolveNode(representation, path);
        if (!CanAdaptColorSelect(node) || node is not Button button) return false;

        try
        {
            Color color = new(Mathf.Clamp01(r), Mathf.Clamp01(g), Mathf.Clamp01(b), Mathf.Clamp01(a));
            if (!TryWriteMember(node, "actualValue", color)) return false;
            button.Text = ColorUtility.ToHtmlStringRGB(color);
            PropagateSignal(button, DevUISignalType.ButtonClick, string.Empty);
            button.Refresh();
            return TryReadColor(node, out Color actual) && ColorDistanceSquared(actual, color) < 0.000001f;
        }
        catch (Exception error)
        {
            Plugin.Logger?.LogWarning("DevTool generic color mutation failed: " + error.Message);
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
            bool atomic = false;

            if (node is Slider slider)
            {
                output.Add(Snapshot(path, node, SliderTitle(slider), LegacyControlKind.Slider,
                    valueText: SafeNumberText(slider), factor: SliderFactor(slider), canReset: slider.inheritButton));
                atomic = true;
            }
            else if (node is Cycler cycler)
            {
                output.Add(Snapshot(path, node, CyclerTitle(cycler), LegacyControlKind.Cycler,
                    valueText: CyclerValue(cycler), selectedIndex: cycler.currentAlternative, options: CyclerOptions(cycler)));
                atomic = true;
            }
            else if (TryReadExtEnum(node, out _, out _, out string[] extEnumOptions, out int extEnumIndex))
            {
                output.Add(Snapshot(path, node, ChildLabelOrSemanticTitle(node, "Type"), LegacyControlKind.ExtEnum,
                    valueText: extEnumIndex >= 0 && extEnumIndex < extEnumOptions.Length ? extEnumOptions[extEnumIndex] : string.Empty,
                    selectedIndex: extEnumIndex, options: extEnumOptions));
                atomic = true;
            }
            else if (node is IntegerControl integerControl)
            {
                output.Add(Snapshot(path, node, IntegerTitle(integerControl), LegacyControlKind.Integer,
                    valueText: SafeIntegerValue(integerControl)));
                atomic = true;
            }
            else if (node is ButtonWithSelectPanel select && CanAdaptSelect(select))
            {
                string[] options = ReadSelectOptions(select);
                string current = select.Text ?? string.Empty;
                output.Add(Snapshot(path, node, SelectTitle(select), LegacyControlKind.Select,
                    valueText: current, selectedIndex: FindOption(options, current), options: options));
                atomic = true;
            }
            else if (CanAdaptBoolean(node) && TryReadBoolMember(node, "actualValue", out bool booleanValue))
            {
                output.Add(Snapshot(path, node, SemanticTitle(node, "Enabled"), LegacyControlKind.Boolean,
                    valueText: node is Button boolButton ? boolButton.Text ?? string.Empty : string.Empty,
                    booleanValue: booleanValue));
                atomic = true;
            }
            else if (CanAdaptPanelSelect(node) && TryReadStringOptions(node, "values", out string[] panelOptions) &&
                     TryReadStringMember(node, "actualValue", out string panelValue))
            {
                output.Add(Snapshot(path, node, SemanticTitle(node, "Select"), LegacyControlKind.PanelSelect,
                    valueText: panelValue, selectedIndex: FindOption(panelOptions, panelValue), options: panelOptions));
                atomic = true;
            }
            else if (CanAdaptColorSelect(node) && TryReadColor(node, out Color color))
            {
                output.Add(Snapshot(path, node, SemanticTitle(node, "Color"), LegacyControlKind.Color,
                    x: color.r, y: color.g, z: color.b, w: color.a));
                atomic = true;
            }
            else if (CanAdaptDirection(node) && TryReadDirection(node, out Vector2 direction))
            {
                output.Add(Snapshot(path, node, SemanticTitle(node, "Direction"), LegacyControlKind.Direction,
                    x: direction.x, y: direction.y));
                atomic = true;
            }
            else if (CanAdaptText(node) && TryReadTextValue(node, out string textValue))
            {
                output.Add(Snapshot(path, node, SemanticTitle(node, "Text"), LegacyControlKind.Text,
                    valueText: textValue));
                atomic = true;
            }
            else if (node is Button button && !IsInfrastructureButton(button))
            {
                output.Add(Snapshot(path, node,
                    string.IsNullOrWhiteSpace(button.Text) ? button.IDstring ?? "Button" : button.Text,
                    LegacyControlKind.Button));
                // Not atomic: arbitrary Buttons may create a custom child panel. If such a panel
                // is already open, recurse into it and mirror its controls too.
            }

            if (!atomic)
                CaptureChildren(node, path, output);
        }
    }

    private static LegacyControlSnapshot Snapshot(
        string path,
        DevUINode node,
        string label,
        LegacyControlKind kind,
        string valueText = "",
        float factor = 0f,
        float x = 0f,
        float y = 0f,
        float z = 0f,
        float w = 0f,
        bool booleanValue = false,
        bool canReset = false,
        int selectedIndex = -1,
        string[] options = null)
    {
        return new LegacyControlSnapshot
        {
            Path = path ?? string.Empty,
            Id = node?.IDstring ?? string.Empty,
            Label = string.IsNullOrWhiteSpace(label) ? node?.IDstring ?? "Control" : label.Trim(),
            RuntimeType = node?.GetType().FullName ?? string.Empty,
            Kind = kind,
            ValueText = valueText ?? string.Empty,
            Factor = factor,
            X = x,
            Y = y,
            Z = z,
            W = w,
            BooleanValue = booleanValue,
            CanReset = canReset,
            SelectedIndex = selectedIndex,
            Options = options == null ? Array.Empty<string>() : CloneOptions(options)
        };
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
            if (TryReadNumericMember(slider, "actualValue", out float actual) &&
                TryReadNumericMember(slider, "minValue", out float min) &&
                TryReadNumericMember(slider, "maxValue", out float max) &&
                Math.Abs(max - min) > 0.000001f)
                return Mathf.InverseLerp(min, max, actual);

            int nubIndex = slider.inheritButton ? 3 : 2;
            if (nubIndex < 0 || nubIndex >= slider.subNodes.Count || slider.subNodes[nubIndex] is not Slider.SliderNub nub)
                return 0f;
            return Mathf.Clamp01((nub.pos.x - slider.SliderStartCoord) / 92f);
        }
        catch { return 0f; }
    }

    private static bool TryInvokeSemanticSliderDrag(Slider slider, float factor)
    {
        if (slider == null) return false;
        MethodInfo method = FindMethodInHierarchy(slider.GetType(), "NubDragged2", new[] { typeof(float) });
        if (method == null || method.ReturnType != typeof(void)) return false;
        method.Invoke(slider, new object[] { factor });
        return true;
    }

    private static bool TryReadExtEnum(
        DevUINode node,
        out Type enumType,
        out string memberName,
        out string[] options,
        out int selectedIndex)
    {
        enumType = null;
        memberName = string.Empty;
        options = Array.Empty<string>();
        selectedIndex = -1;
        if (node is not Button) return false;

        object current = ReadMember(node, "Type");
        if (current is ExtEnumBase)
            memberName = "Type";
        else
        {
            current = ReadMember(node, "actualValue");
            if (current is ExtEnumBase)
                memberName = "actualValue";
        }

        if (current is not ExtEnumBase extEnum) return false;
        enumType = extEnum.GetType();
        try
        {
            options = ExtEnumBase.GetNames(enumType) ?? Array.Empty<string>();
            selectedIndex = FindOption(options, extEnum.value);
            if (selectedIndex < 0) selectedIndex = extEnum.Index;
            return options.Length > 0;
        }
        catch
        {
            enumType = null;
            memberName = string.Empty;
            options = Array.Empty<string>();
            selectedIndex = -1;
            return false;
        }
    }

    private static bool TryReadNumericMember(object instance, string name, out float value)
    {
        value = 0f;
        object raw = ReadMember(instance, name);
        if (raw == null) return false;
        try
        {
            value = Convert.ToSingle(raw);
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }
        catch { return false; }
    }

    private static MethodInfo FindTextCommitMethod(Type type) =>
        FindMethodInHierarchy(type, "TrySetValue", new[] { typeof(string), typeof(bool) });

    private static MethodInfo FindMethodInHierarchy(Type type, string name, Type[] parameters)
    {
        Type current = type;
        while (current != null)
        {
            MethodInfo method = current.GetMethod(
                name,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly,
                null,
                parameters,
                null);
            if (method != null) return method;
            current = current.BaseType;
        }
        return null;
    }

    private static bool TryReadTextValue(object instance, out string value) =>
        TryReadStringMember(instance, "actualValue", out value);

    private static bool TryReadStringMember(object instance, string name, out string value)
    {
        value = string.Empty;
        object raw = ReadMember(instance, name);
        if (raw is not string text) return false;
        value = text;
        return true;
    }

    private static bool TryReadStringOptions(object instance, string name, out string[] value)
    {
        value = Array.Empty<string>();
        object raw = ReadMember(instance, name);
        if (raw is string[] strings)
        {
            value = CloneOptions(strings);
            return true;
        }

        if (raw is not IEnumerable enumerable || raw is string) return false;
        List<string> result = new();
        foreach (object item in enumerable)
        {
            if (item == null) continue;
            result.Add(item.ToString() ?? string.Empty);
        }
        value = result.ToArray();
        return value.Length > 0;
    }

    private static bool TryReadBoolMember(object instance, string name, out bool value)
    {
        value = false;
        object raw = ReadMember(instance, name);
        if (raw is not bool boolean) return false;
        value = boolean;
        return true;
    }

    private static bool TryReadColor(object instance, out Color color)
    {
        color = Color.white;
        object raw = ReadMember(instance, "actualValue");
        if (raw is not Color value) return false;
        color = value;
        return true;
    }

    private static object ReadMember(object instance, string name)
    {
        if (instance == null || string.IsNullOrEmpty(name)) return null;
        FieldInfo field = FindFieldInHierarchy(instance.GetType(), name);
        if (field != null) return field.GetValue(instance);
        PropertyInfo property = FindPropertyInHierarchy(instance.GetType(), name);
        if (property != null && property.GetIndexParameters().Length == 0 && property.CanRead)
            return property.GetValue(instance, null);
        return null;
    }

    private static bool TryWriteMember(object instance, string name, object value)
    {
        if (instance == null || string.IsNullOrEmpty(name)) return false;
        FieldInfo field = FindFieldInHierarchy(instance.GetType(), name);
        if (field != null && !field.IsInitOnly && (value == null || field.FieldType.IsInstanceOfType(value)))
        {
            field.SetValue(instance, value);
            return true;
        }

        PropertyInfo property = FindPropertyInHierarchy(instance.GetType(), name);
        if (property != null && property.CanWrite && property.GetIndexParameters().Length == 0 &&
            (value == null || property.PropertyType.IsInstanceOfType(value)))
        {
            property.SetValue(instance, value, null);
            return true;
        }
        return false;
    }

    private static bool TryReadDirection(object instance, out Vector2 direction)
    {
        direction = Vector2.up;
        if (instance == null) return false;

        object direct = ReadMember(instance, "Dir");
        if (direct is Vector2 vector)
        {
            direction = vector.sqrMagnitude > 0.000001f ? vector.normalized : Vector2.up;
            return true;
        }

        object handle = ReadMember(instance, "handle");
        object nested = ReadMember(handle, "Dir");
        if (nested is not Vector2 handleVector) return false;
        direction = handleVector.sqrMagnitude > 0.000001f ? handleVector.normalized : Vector2.up;
        return true;
    }

    private static bool CanWriteDirection(DevUINode node)
    {
        if (node == null) return false;
        if (CanWriteVector2Member(node, "Dir")) return true;
        object handle = ReadMember(node, "handle");
        return handle != null && CanWriteVector2Member(handle, "Dir");
    }

    private static bool TryWriteDirection(DevUINode node, Vector2 value)
    {
        if (TryWriteVector2Member(node, "Dir", value)) return true;
        object handle = ReadMember(node, "handle");
        return handle != null && TryWriteVector2Member(handle, "Dir", value);
    }

    private static bool CanWriteVector2Member(object instance, string name)
    {
        if (instance == null) return false;
        FieldInfo field = FindFieldInHierarchy(instance.GetType(), name);
        if (field != null && !field.IsInitOnly && field.FieldType == typeof(Vector2)) return true;
        PropertyInfo property = FindPropertyInHierarchy(instance.GetType(), name);
        return property != null && property.PropertyType == typeof(Vector2) && property.CanWrite &&
               property.GetIndexParameters().Length == 0;
    }

    private static bool TryWriteVector2Member(object instance, string name, Vector2 value)
    {
        if (instance == null) return false;
        FieldInfo field = FindFieldInHierarchy(instance.GetType(), name);
        if (field != null && !field.IsInitOnly && field.FieldType == typeof(Vector2))
        {
            field.SetValue(instance, value);
            return true;
        }
        PropertyInfo property = FindPropertyInHierarchy(instance.GetType(), name);
        if (property != null && property.PropertyType == typeof(Vector2) && property.CanWrite &&
            property.GetIndexParameters().Length == 0)
        {
            property.SetValue(instance, value, null);
            return true;
        }
        return false;
    }

    private static PropertyInfo FindPropertyInHierarchy(Type type, string name)
    {
        Type current = type;
        while (current != null)
        {
            PropertyInfo property = current.GetProperty(
                name,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
            if (property != null) return property;
            current = current.BaseType;
        }
        return null;
    }

    private static FieldInfo FindFieldInHierarchy(Type type, string name)
    {
        Type current = type;
        while (current != null)
        {
            FieldInfo field = current.GetField(
                name,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
            if (field != null) return field;
            current = current.BaseType;
        }
        return null;
    }

    private static string SemanticTitle(DevUINode node, string fallback)
    {
        string child = ChildLabel(node);
        if (!string.IsNullOrEmpty(child)) return child;
        string id = node?.IDstring ?? string.Empty;
        if (string.IsNullOrWhiteSpace(id)) return fallback;
        return id.Replace('_', ' ').Trim();
    }

    private static string ChildLabelOrSemanticTitle(DevUINode node, string fallback)
    {
        string child = ChildLabel(node);
        return string.IsNullOrEmpty(child) ? SemanticTitle(node, fallback) : child;
    }

    private static string ChildLabel(DevUINode node)
    {
        if (node?.subNodes == null) return string.Empty;
        for (int i = 0; i < node.subNodes.Count; i++)
        {
            if (node.subNodes[i] is DevUILabel label && !string.IsNullOrWhiteSpace(label.Text))
                return label.Text.Trim().TrimEnd(':').Trim();
        }
        return string.Empty;
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
        if (cycler?.alternatives == null || cycler.alternatives.Count == 0) return Array.Empty<string>();
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

    private static string SelectTitle(ButtonWithSelectPanel button) => SemanticTitle(button, "Select");

    private static string[] ReadSelectOptions(ButtonWithSelectPanel button)
    {
        if (button == null) return Array.Empty<string>();
        if (button.selectPanel?.items != null && button.selectPanel.items.Length > 0)
            return CloneOptions(button.selectPanel.items);

        string displayText = button.Text ?? string.Empty;
        if (SelectOptions.TryGetValue(button, out SelectOptionCache cached) &&
            string.Equals(cached.DisplayText, displayText, StringComparison.Ordinal))
            return CloneOptions(cached.Options);

        string[] discovered = DiscoverSelectOptions(button);
        SelectOptions.Remove(button);
        SelectOptions.Add(button, new SelectOptionCache
        {
            DisplayText = displayText,
            Options = CloneOptions(discovered)
        });
        return discovered;
    }

    private static string[] DiscoverSelectOptions(ButtonWithSelectPanel button)
    {
        if (button?.makeSelectPanel == null) return Array.Empty<string>();
        SelectPanel temporary = null;
        try
        {
            temporary = button.makeSelectPanel(button);
            return temporary?.items == null ? Array.Empty<string>() : CloneOptions(temporary.items);
        }
        catch (Exception error)
        {
            Plugin.Logger?.LogWarning("DevTool generic select option discovery failed: " + error.Message);
            return Array.Empty<string>();
        }
        finally
        {
            if (temporary != null && !ReferenceEquals(temporary, button.selectPanel))
            {
                try { temporary.ClearSprites(); }
                catch { }
            }
        }
    }

    private static void CloseSelectPanel(ButtonWithSelectPanel button)
    {
        SelectPanel panel = button?.selectPanel;
        if (button == null || panel == null) return;
        button.subNodes.Remove(panel);
        panel.ClearSprites();
        button.selectPanel = null;
    }

    private static string[] CloneOptions(string[] source)
    {
        if (source == null || source.Length == 0) return Array.Empty<string>();
        string[] copy = new string[source.Length];
        for (int i = 0; i < copy.Length; i++) copy[i] = source[i] ?? string.Empty;
        return copy;
    }

    private static int FindOption(string[] options, string value)
    {
        if (options == null) return -1;
        for (int i = 0; i < options.Length; i++)
            if (string.Equals(options[i], value, StringComparison.Ordinal)) return i;
        return -1;
    }

    private static bool TryParseCompositeAction(string encoded, string prefix, out int argument, out string path)
    {
        argument = 0;
        path = string.Empty;
        if (string.IsNullOrEmpty(encoded) || string.IsNullOrEmpty(prefix) ||
            !encoded.StartsWith(prefix, StringComparison.Ordinal))
            return false;

        int separator = encoded.IndexOf('|', prefix.Length);
        if (separator < 0 || !int.TryParse(encoded.Substring(prefix.Length, separator - prefix.Length), out argument))
            return false;
        path = separator + 1 < encoded.Length ? encoded.Substring(separator + 1) : string.Empty;
        return !string.IsNullOrWhiteSpace(path);
    }

    private static void PropagateSignal(DevUINode source, DevUISignalType type, string message)
    {
        DevUINode current = source;
        while (current != null)
        {
            current = current.parentNode;
            if (current is not IDevUISignals signals) continue;
            signals.Signal(type, source, message ?? string.Empty);
            return;
        }
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
        finally { owner.mouseClick = oldMouseClick; }
    }

    private static float ColorDistanceSquared(Color a, Color b)
    {
        float dr = a.r - b.r;
        float dg = a.g - b.g;
        float db = a.b - b.b;
        float da = a.a - b.a;
        return dr * dr + dg * dg + db * db + da * da;
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
        if (node.subNodes == null) return null;

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
            if (!int.TryParse(parts[i], out int index) || index < 0 || current.subNodes == null || index >= current.subNodes.Count)
                return null;
            current = current.subNodes[index];
            if (current == null) return null;
        }
        return current;
    }
}
