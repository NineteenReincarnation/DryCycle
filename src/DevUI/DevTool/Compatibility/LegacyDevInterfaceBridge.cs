using System;
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
    Integer,
    Select,
    PanelSelect,
    Text,
    Direction,
    Color
}

/// <summary>
/// Detached description of a DevInterface control belonging to the selected
/// PlacedObjectRepresentation. Optional third-party controls are recognized structurally or by
/// runtime type name so the core assembly never acquires a hard RegionKit dependency.
/// </summary>
public sealed class LegacyControlSnapshot
{
    public string Path { get; init; } = string.Empty;
    public string Id { get; init; } = string.Empty;
    public string Label { get; init; } = string.Empty;
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

public static class LegacyDevInterfaceBridge
{
    private sealed class SelectOptionCache
    {
        internal string DisplayText = string.Empty;
        internal string[] Options = Array.Empty<string>();
    }

    private const string CyclerActionPrefix = "@cycler|";
    private const string IntegerActionPrefix = "@integer|";
    private const string SelectActionPrefix = "@select|";
    private const string PanelSelectActionPrefix = "@panel-select|";
    private const string RegionKitBoolButtonType = "RegionKit.Modules.DevUIMisc.GenericNodes.BoolButton";
    private const string RegionKitPanelSelectType = "RegionKit.Modules.DevUIMisc.GenericNodes.PanelSelectButton";
    private const string RegionKitColorSelectType = "RegionKit.Modules.DevUIMisc.GenericNodes.RGBSelectButton";
    private const string RegionKitExtEnumCyclerDefinition = "RegionKit.Modules.DevUIMisc.GenericNodes.ExtEnumCycler`1";
    private static readonly ConditionalWeakTable<ButtonWithSelectPanel, SelectOptionCache> SelectOptions = new();

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

    public static string CyclerAction(string path, int selectedIndex) =>
        CyclerActionPrefix + selectedIndex + "|" + (path ?? string.Empty);

    public static string IntegerAction(string path, int change) =>
        IntegerActionPrefix + change + "|" + (path ?? string.Empty);

    public static string SelectAction(string path, int selectedIndex) =>
        SelectActionPrefix + selectedIndex + "|" + (path ?? string.Empty);

    public static string PanelSelectAction(string path, int selectedIndex) =>
        PanelSelectActionPrefix + selectedIndex + "|" + (path ?? string.Empty);

    internal static bool CanAdaptBoolean(DevUINode node) =>
        IsExactType(node, RegionKitBoolButtonType) && node is Button &&
        TryReadBoolMember(node, "actualValue", out _);

    internal static bool IsTerminalSemanticButton(DevUINode node)
    {
        if (CanAdaptBoolean(node)) return true;
        Type type = node?.GetType();
        return type != null && type.IsGenericType &&
               string.Equals(type.GetGenericTypeDefinition().FullName,
                   RegionKitExtEnumCyclerDefinition,
                   StringComparison.Ordinal);
    }

    internal static bool CanAdaptSelect(ButtonWithSelectPanel button) =>
        button != null && ReadSelectOptions(button).Length > 0;

    internal static bool CanAdaptPanelSelect(DevUINode node) =>
        IsExactType(node, RegionKitPanelSelectType) && node is Button &&
        TryReadStringArrayMember(node, "values", out string[] options) && options.Length > 0 &&
        TryReadStringMember(node, "actualValue", out _);

    internal static bool CanAdaptColorSelect(DevUINode node) =>
        IsExactType(node, RegionKitColorSelectType) && node is Button && TryReadColor(node, out _);

    internal static bool CanAdaptText(DevUINode node) =>
        node != null && FindTextCommitMethod(node.GetType()) != null && TryReadTextValue(node, out _);

    internal static bool CanAdaptDirection(DevUINode node) =>
        node != null && TryReadDirection(node, out _) && FindDirectionSetter(node, out _, out _);

    internal static bool ClickButton(global::DevInterface.DevUI owner, PlacedObject target, string path)
    {
        if (TryParseCompositeAction(path, CyclerActionPrefix, out int cyclerIndex, out string cyclerPath))
            return SetCycler(owner, target, cyclerPath, cyclerIndex);
        if (TryParseCompositeAction(path, IntegerActionPrefix, out int integerChange, out string integerPath))
            return IncrementInteger(owner, target, integerPath, integerChange);
        if (TryParseCompositeAction(path, SelectActionPrefix, out int selectedIndex, out string selectPath))
            return SetSelect(owner, target, selectPath, selectedIndex);
        if (TryParseCompositeAction(path, PanelSelectActionPrefix, out int panelIndex, out string panelPath))
            return SetPanelSelect(owner, target, panelPath, panelIndex);

        PlacedObjectRepresentation representation = FindRepresentation(owner?.activePage as ObjectsPage, target);
        if (representation == null) return false;
        if (ResolveNode(representation, path) is not Button button || button is ButtonWithSelectPanel) return false;
        if (CanAdaptPanelSelect(button) || CanAdaptColorSelect(button)) return false;

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

            // RegionKit GenericSlider intentionally overrides vanilla NubDragged with a no-op and
            // exposes NubDragged2 as its semantic mutation boundary. Prefer that boundary when it
            // exists; this keeps RegionKit optional while preserving its events/signals.
            if (TryInvokeSemanticSliderDrag(slider, factor))
            {
                slider.Refresh();
                owner.activePage?.Refresh();
                return true;
            }

            slider.NubDragged(factor);
            slider.RefreshNubPos(factor);
            slider.Refresh();
            owner.activePage?.Refresh();
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
            owner.activePage?.Refresh();
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

    internal static bool SetSelect(global::DevInterface.DevUI owner, PlacedObject target, string path, int selectedIndex)
    {
        PlacedObjectRepresentation representation = FindRepresentation(owner?.activePage as ObjectsPage, target);
        if (representation == null) return false;
        if (ResolveNode(representation, path) is not ButtonWithSelectPanel button) return false;

        string[] options = ReadSelectOptions(button);
        if (selectedIndex < 0 || selectedIndex >= options.Length) return false;

        try
        {
            CloseSelectPanel(button);
            button.OnValueChange(options[selectedIndex]);
            SelectOptions.Remove(button);
            owner.activePage?.Refresh();
            return true;
        }
        catch (Exception error)
        {
            Plugin.Logger?.LogWarning("DevTool legacy select mutation failed: " + error.Message);
            return false;
        }
    }

    internal static bool SetPanelSelect(global::DevInterface.DevUI owner, PlacedObject target, string path, int selectedIndex)
    {
        PlacedObjectRepresentation representation = FindRepresentation(owner?.activePage as ObjectsPage, target);
        if (representation == null) return false;
        DevUINode node = ResolveNode(representation, path);
        if (!CanAdaptPanelSelect(node) || node is not Button button) return false;
        if (!TryReadStringArrayMember(node, "values", out string[] options) ||
            selectedIndex < 0 || selectedIndex >= options.Length)
            return false;

        try
        {
            string selected = options[selectedIndex] ?? string.Empty;
            if (!TryWriteMember(node, "actualValue", selected)) return false;
            button.Text = selected;

            // RegionKit PanelSelectButton normally receives a child-panel signal and forwards one
            // ButtonClick to the nearest IDevUISignals parent. Reproduce that final semantic
            // boundary without creating the hidden ItemSelectPanel.
            PropagateSignal(button, DevUISignalType.ButtonClick, selected);
            owner.activePage?.Refresh();
            return TryReadStringMember(node, "actualValue", out string actual) &&
                   string.Equals(actual, selected, StringComparison.Ordinal);
        }
        catch (Exception error)
        {
            Plugin.Logger?.LogWarning("DevTool RegionKit panel-select mutation failed: " + error.Message);
            return false;
        }
    }

    internal static bool SetText(global::DevInterface.DevUI owner, PlacedObject target, string path, string value)
    {
        PlacedObjectRepresentation representation = FindRepresentation(owner?.activePage as ObjectsPage, target);
        if (representation == null) return false;
        DevUINode node = ResolveNode(representation, path);
        if (node == null) return false;

        MethodInfo commit = FindTextCommitMethod(node.GetType());
        if (commit == null || !TryReadTextValue(node, out _)) return false;

        try
        {
            commit.Invoke(node, new object[] { value ?? string.Empty, true });
            owner.activePage?.Refresh();
            return TryReadTextValue(node, out string actual) &&
                   string.Equals(actual, value ?? string.Empty, StringComparison.Ordinal);
        }
        catch (Exception error)
        {
            Plugin.Logger?.LogWarning("DevTool legacy text mutation failed: " + error.Message);
            return false;
        }
    }

    internal static bool SetDirection(global::DevInterface.DevUI owner, PlacedObject target, string path, float x, float y)
    {
        PlacedObjectRepresentation representation = FindRepresentation(owner?.activePage as ObjectsPage, target);
        if (representation == null) return false;
        DevUINode node = ResolveNode(representation, path);
        if (node == null || !FindDirectionSetter(node, out object directionTarget, out PropertyInfo directionProperty))
            return false;

        try
        {
            Vector2 direction = new Vector2(x, y);
            direction = direction.sqrMagnitude > 0.000001f ? direction.normalized : Vector2.up;
            directionProperty.SetValue(directionTarget, direction, null);
            node.Refresh();
            SynchronizePollingParent(owner, node);
            owner.activePage?.Refresh();
            return TryReadDirection(node, out Vector2 actual) && Vector2.Dot(actual, direction) > 0.999f;
        }
        catch (Exception error)
        {
            Plugin.Logger?.LogWarning("DevTool legacy direction mutation failed: " + error.Message);
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
            Color color = new Color(Mathf.Clamp01(r), Mathf.Clamp01(g), Mathf.Clamp01(b), Mathf.Clamp01(a));
            if (!TryWriteMember(node, "actualValue", color)) return false;
            button.Text = ColorUtility.ToHtmlStringRGB(color);

            // RGBSelectButton forwards ButtonClick only after its RGBSelectPanel commits a color.
            // Emit that same final signal directly so the hidden panel is no longer required.
            PropagateSignal(button, DevUISignalType.ButtonClick, string.Empty);
            owner.activePage?.Refresh();
            return TryReadColor(node, out Color actual) && ColorDistanceSquared(actual, color) < 0.000001f;
        }
        catch (Exception error)
        {
            Plugin.Logger?.LogWarning("DevTool RegionKit color-select mutation failed: " + error.Message);
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

            if (node is ButtonWithSelectPanel select)
            {
                string[] options = ReadSelectOptions(select);
                if (options.Length > 0)
                {
                    string current = select.Text ?? string.Empty;
                    output.Add(new LegacyControlSnapshot
                    {
                        Path = path,
                        Id = select.IDstring ?? string.Empty,
                        Label = SelectTitle(select),
                        Kind = LegacyControlKind.Select,
                        ValueText = current,
                        SelectedIndex = FindOption(options, current),
                        Options = options
                    });
                }
                continue;
            }

            if (CanAdaptBoolean(node) && TryReadBoolMember(node, "actualValue", out bool booleanValue))
            {
                output.Add(new LegacyControlSnapshot
                {
                    Path = path,
                    Id = node.IDstring ?? string.Empty,
                    Label = SemanticTitle(node, "Enabled"),
                    Kind = LegacyControlKind.Boolean,
                    BooleanValue = booleanValue,
                    ValueText = node is Button boolButton ? boolButton.Text ?? string.Empty : string.Empty
                });
                continue;
            }

            if (CanAdaptPanelSelect(node) && TryReadStringArrayMember(node, "values", out string[] panelOptions) &&
                TryReadStringMember(node, "actualValue", out string panelValue))
            {
                output.Add(new LegacyControlSnapshot
                {
                    Path = path,
                    Id = node.IDstring ?? string.Empty,
                    Label = SemanticTitle(node, "Select"),
                    Kind = LegacyControlKind.PanelSelect,
                    ValueText = panelValue,
                    SelectedIndex = FindOption(panelOptions, panelValue),
                    Options = panelOptions
                });
                continue;
            }

            if (CanAdaptColorSelect(node) && TryReadColor(node, out Color color))
            {
                output.Add(new LegacyControlSnapshot
                {
                    Path = path,
                    Id = node.IDstring ?? string.Empty,
                    Label = SemanticTitle(node, "Color"),
                    Kind = LegacyControlKind.Color,
                    X = color.r,
                    Y = color.g,
                    Z = color.b,
                    W = color.a
                });
                continue;
            }

            if (CanAdaptDirection(node) && TryReadDirection(node, out Vector2 direction))
            {
                output.Add(new LegacyControlSnapshot
                {
                    Path = path,
                    Id = node.IDstring ?? string.Empty,
                    Label = SemanticTitle(node, "Direction"),
                    Kind = LegacyControlKind.Direction,
                    X = direction.x,
                    Y = direction.y
                });
                continue;
            }

            if (CanAdaptText(node) && TryReadTextValue(node, out string textValue))
            {
                output.Add(new LegacyControlSnapshot
                {
                    Path = path,
                    Id = node.IDstring ?? string.Empty,
                    Label = SemanticTitle(node, "Text"),
                    Kind = LegacyControlKind.Text,
                    ValueText = textValue
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
        catch
        {
            return 0f;
        }
    }

    private static bool TryInvokeSemanticSliderDrag(Slider slider, float factor)
    {
        if (slider == null) return false;
        MethodInfo method = slider.GetType().GetMethod(
            "NubDragged2",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            null,
            new[] { typeof(float) },
            null);
        if (method == null || method.ReturnType != typeof(void)) return false;
        method.Invoke(slider, new object[] { factor });
        return true;
    }

    private static bool TryReadNumericMember(object instance, string name, out float value)
    {
        value = 0f;
        if (instance == null || string.IsNullOrEmpty(name)) return false;
        object raw = ReadMember(instance, name);
        return TryConvertFloat(raw, out value);
    }

    private static bool TryConvertFloat(object raw, out float value)
    {
        value = 0f;
        if (raw == null) return false;
        try
        {
            value = Convert.ToSingle(raw);
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }
        catch
        {
            return false;
        }
    }

    private static MethodInfo FindTextCommitMethod(Type type)
    {
        Type current = type;
        while (current != null)
        {
            MethodInfo method = current.GetMethod(
                "TrySetValue",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly,
                null,
                new[] { typeof(string), typeof(bool) },
                null);
            if (method != null && method.ReturnType == typeof(void)) return method;
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

    private static bool TryReadStringArrayMember(object instance, string name, out string[] value)
    {
        value = Array.Empty<string>();
        object raw = ReadMember(instance, name);
        if (raw is not string[] strings) return false;
        value = CloneOptions(strings);
        return true;
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

        PropertyInfo property = FindPropertyInHierarchy(instance.GetType(), "Dir");
        if (property != null && property.PropertyType == typeof(Vector2) && property.CanRead &&
            property.GetValue(instance, null) is Vector2 vector)
        {
            direction = vector.sqrMagnitude > 0.000001f ? vector.normalized : Vector2.up;
            return true;
        }

        FieldInfo field = FindFieldInHierarchy(instance.GetType(), "Dir");
        if (field != null && field.FieldType == typeof(Vector2) && field.GetValue(instance) is Vector2 fieldVector)
        {
            direction = fieldVector.sqrMagnitude > 0.000001f ? fieldVector.normalized : Vector2.up;
            return true;
        }

        return false;
    }

    private static bool FindDirectionSetter(DevUINode node, out object target, out PropertyInfo property)
    {
        target = null;
        property = null;
        if (node == null) return false;

        PropertyInfo direct = FindPropertyInHierarchy(node.GetType(), "Dir");
        if (direct != null && direct.PropertyType == typeof(Vector2) && direct.CanWrite)
        {
            target = node;
            property = direct;
            return true;
        }

        FieldInfo handleField = FindFieldInHierarchy(node.GetType(), "handle");
        object handle = handleField?.GetValue(node);
        if (handle == null) return false;

        PropertyInfo handleDirection = FindPropertyInHierarchy(handle.GetType(), "Dir");
        if (handleDirection == null || handleDirection.PropertyType != typeof(Vector2) || !handleDirection.CanWrite)
            return false;

        target = handle;
        property = handleDirection;
        return true;
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

    private static bool IsExactType(object instance, string fullTypeName) =>
        instance != null && string.Equals(instance.GetType().FullName, fullTypeName, StringComparison.Ordinal);

    private static string SemanticTitle(DevUINode node, string fallback)
    {
        string id = node?.IDstring ?? string.Empty;
        if (string.IsNullOrWhiteSpace(id)) return fallback;
        return id.Replace('_', ' ').Trim();
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
            Plugin.Logger?.LogWarning("DevTool legacy select option discovery failed: " + error.Message);
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
        if (separator < 0) return false;
        if (!int.TryParse(encoded.Substring(prefix.Length, separator - prefix.Length), out argument))
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
        finally
        {
            owner.mouseClick = oldMouseClick;
        }
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
