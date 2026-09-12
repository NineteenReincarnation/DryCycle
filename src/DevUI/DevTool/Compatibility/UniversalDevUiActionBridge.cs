using System;
using System.Reflection;
using DevInterface;
using UnityEngine;

namespace DryCycle.DevUI.DevTool.Compatibility;

/// <summary>
/// Generic action bridge for any DevInterface page.
///
/// The existing object compatibility layer originally resolved controls through a selected
/// PlacedObjectRepresentation. This bridge removes that object-specific assumption: an action is
/// addressed only by its path in the active Page tree and by the structural protocol published in
/// <see cref="LegacyControlSnapshot"/>. No vanilla/RK/DryCycle type table is used.
/// </summary>
public static class UniversalDevUiActionBridge
{
    public static bool CanExecute(DevUINode node) =>
        node != null && LegacyDevInterfaceBridge.CanAdaptNode(node);

    public static bool Click(global::DevInterface.DevUI owner, string path)
    {
        if (!TryResolve(owner, path, out DevUINode node, out LegacyControlSnapshot snapshot) ||
            snapshot.Kind != LegacyControlKind.Button || node is not Button button)
            return false;

        try
        {
            button.Clicked();
            button.Refresh();
            return true;
        }
        catch (Exception error)
        {
            Warn("button", node, error);
            return false;
        }
    }

    public static bool SetBoolean(global::DevInterface.DevUI owner, string path, bool desired)
    {
        if (!TryResolve(owner, path, out DevUINode node, out LegacyControlSnapshot snapshot) ||
            snapshot.Kind != LegacyControlKind.Boolean || node is not Button button)
            return false;

        try
        {
            if (TryReadMember(node, "actualValue", out object raw) && raw is bool current && current == desired)
                return true;

            // Prefer the control's own semantic boundary first. This covers ordinary toggle
            // buttons and preserves every signal/event the legacy control emits itself.
            button.Clicked();
            button.Refresh();
            if (TryReadMember(node, "actualValue", out raw) && raw is bool after && after == desired)
                return true;

            // Value-backed buttons that use Clicked only to open a helper panel still have a
            // writable final value. Commit it and emit the standard parent ButtonClick signal.
            if (!TryWriteMember(node, "actualValue", desired)) return false;
            PropagateSignal(button, DevUISignalType.ButtonClick, string.Empty);
            button.Refresh();
            return TryReadMember(node, "actualValue", out raw) && raw is bool verified && verified == desired;
        }
        catch (Exception error)
        {
            Warn("boolean", node, error);
            return false;
        }
    }

    public static bool SetSlider(global::DevInterface.DevUI owner, string path, float factor)
    {
        if (!TryResolve(owner, path, out DevUINode node, out LegacyControlSnapshot snapshot) ||
            snapshot.Kind != LegacyControlKind.Slider || node is not Slider slider)
            return false;

        try
        {
            factor = Mathf.Clamp01(factor);
            MethodInfo semantic = FindMethod(slider.GetType(), "NubDragged2", new[] { typeof(float) });
            if (semantic != null && semantic.ReturnType == typeof(void))
                semantic.Invoke(slider, new object[] { factor });
            else
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
            Warn("slider", node, error);
            return false;
        }
    }

    public static bool ResetSlider(global::DevInterface.DevUI owner, string path)
    {
        if (!TryResolve(owner, path, out DevUINode node, out LegacyControlSnapshot snapshot) ||
            snapshot.Kind != LegacyControlKind.Slider || node is not Slider slider || !slider.inheritButton)
            return false;

        try
        {
            slider.ClickedResetToInherent();
            slider.Refresh();
            SynchronizePollingParent(owner, slider);
            return true;
        }
        catch (Exception error)
        {
            Warn("slider reset", node, error);
            return false;
        }
    }

    public static bool SetChoice(global::DevInterface.DevUI owner, string path, int selectedIndex)
    {
        if (!TryResolve(owner, path, out DevUINode node, out LegacyControlSnapshot snapshot)) return false;
        string[] options = snapshot.Options ?? Array.Empty<string>();
        if (selectedIndex < 0 || selectedIndex >= options.Length) return false;

        try
        {
            string selected = options[selectedIndex] ?? string.Empty;
            switch (snapshot.Kind)
            {
                case LegacyControlKind.Cycler:
                    if (node is not Cycler cycler || cycler.alternatives == null ||
                        selectedIndex >= cycler.alternatives.Count)
                        return false;
                    cycler.currentAlternative = selectedIndex;
                    cycler.Text = (cycler.baseName ?? string.Empty) + (cycler.alternatives[selectedIndex] ?? string.Empty);
                    PropagateSignal(cycler, DevUISignalType.ButtonClick, selected);
                    SynchronizePollingParent(owner, cycler);
                    cycler.Refresh();
                    return true;

                case LegacyControlKind.ExtEnum:
                    return SetExtEnum(node, selected, selectedIndex, owner);

                case LegacyControlKind.Select:
                    if (node is not ButtonWithSelectPanel select) return false;
                    CloseSelectPanel(select);
                    select.OnValueChange(selected);
                    select.Refresh();
                    return true;

                case LegacyControlKind.PanelSelect:
                    if (node is not Button panelButton || !TryWriteMember(node, "actualValue", selected)) return false;
                    panelButton.Text = selected;
                    PropagateSignal(panelButton, DevUISignalType.ButtonClick, selected);
                    panelButton.Refresh();
                    return true;

                default:
                    return false;
            }
        }
        catch (Exception error)
        {
            Warn("choice", node, error);
            return false;
        }
    }

    public static bool IncrementInteger(global::DevInterface.DevUI owner, string path, int change)
    {
        if (change == 0) return false;
        if (!TryResolve(owner, path, out DevUINode node, out LegacyControlSnapshot snapshot) ||
            snapshot.Kind != LegacyControlKind.Integer || node is not IntegerControl integer)
            return false;

        try
        {
            integer.Increment(change);
            integer.Refresh();
            SynchronizePollingParent(owner, integer);
            return true;
        }
        catch (Exception error)
        {
            Warn("integer", node, error);
            return false;
        }
    }

    public static bool SetText(global::DevInterface.DevUI owner, string path, string value)
    {
        if (!TryResolve(owner, path, out DevUINode node, out LegacyControlSnapshot snapshot) ||
            snapshot.Kind != LegacyControlKind.Text)
            return false;

        MethodInfo commit = FindMethod(node.GetType(), "TrySetValue", new[] { typeof(string), typeof(bool) });
        if (commit == null || commit.ReturnType != typeof(void)) return false;

        try
        {
            commit.Invoke(node, new object[] { value ?? string.Empty, true });
            node.Refresh();
            return true;
        }
        catch (Exception error)
        {
            Warn("text", node, error);
            return false;
        }
    }

    public static bool SetDirection(global::DevInterface.DevUI owner, string path, float x, float y)
    {
        if (!TryResolve(owner, path, out DevUINode node, out LegacyControlSnapshot snapshot) ||
            snapshot.Kind != LegacyControlKind.Direction)
            return false;

        try
        {
            Vector2 direction = new(x, y);
            direction = direction.sqrMagnitude > 0.000001f ? direction.normalized : Vector2.up;
            if (!TryWriteVector2(node, "Dir", direction))
            {
                object handle = ReadMember(node, "handle");
                if (handle == null || !TryWriteVector2(handle, "Dir", direction)) return false;
            }
            node.Refresh();
            SynchronizePollingParent(owner, node);
            return true;
        }
        catch (Exception error)
        {
            Warn("direction", node, error);
            return false;
        }
    }

    public static bool SetColor(
        global::DevInterface.DevUI owner,
        string path,
        float r,
        float g,
        float b,
        float a)
    {
        if (!TryResolve(owner, path, out DevUINode node, out LegacyControlSnapshot snapshot) ||
            snapshot.Kind != LegacyControlKind.Color || node is not Button button)
            return false;

        try
        {
            Color color = new(Mathf.Clamp01(r), Mathf.Clamp01(g), Mathf.Clamp01(b), Mathf.Clamp01(a));
            if (!TryWriteMember(node, "actualValue", color)) return false;
            button.Text = ColorUtility.ToHtmlStringRGB(color);
            PropagateSignal(button, DevUISignalType.ButtonClick, string.Empty);
            button.Refresh();
            return true;
        }
        catch (Exception error)
        {
            Warn("color", node, error);
            return false;
        }
    }

    /// <summary>
    /// Non-mutating structural verification used by the full audit. This verifies that a published
    /// snapshot has a corresponding generic action route, without clicking or changing user data.
    /// </summary>
    internal static bool CanExecute(Page root, LegacyControlSnapshot snapshot)
    {
        if (root == null || snapshot == null || string.IsNullOrWhiteSpace(snapshot.Path)) return false;
        DevUINode node = ResolveNode(root, snapshot.Path);
        if (node == null) return false;

        return snapshot.Kind switch
        {
            LegacyControlKind.Button => node is Button,
            LegacyControlKind.Boolean => node is Button && IsWritableMember(node, "actualValue", typeof(bool)),
            LegacyControlKind.Slider => node is Slider,
            LegacyControlKind.Cycler => node is Cycler,
            LegacyControlKind.ExtEnum => FindExtEnumMember(node, out _, out _),
            LegacyControlKind.Integer => node is IntegerControl,
            LegacyControlKind.Select => node is ButtonWithSelectPanel,
            LegacyControlKind.PanelSelect => node is Button && IsWritableMember(node, "actualValue", typeof(string)),
            LegacyControlKind.Text => FindMethod(node.GetType(), "TrySetValue", new[] { typeof(string), typeof(bool) }) != null,
            LegacyControlKind.Direction => CanWriteVector2(node, "Dir") ||
                                           (ReadMember(node, "handle") is object handle && CanWriteVector2(handle, "Dir")),
            LegacyControlKind.Color => node is Button && IsWritableMember(node, "actualValue", typeof(Color)),
            _ => false
        };
    }

    private static bool TryResolve(
        global::DevInterface.DevUI owner,
        string path,
        out DevUINode node,
        out LegacyControlSnapshot snapshot)
    {
        node = null;
        snapshot = null;
        Page root = owner?.activePage;
        if (root == null || string.IsNullOrWhiteSpace(path)) return false;

        node = ResolveNode(root, path);
        if (node == null) return false;

        LegacyControlSnapshot[] controls = LegacyDevInterfaceBridge.CaptureRoot(root);
        for (int i = 0; i < controls.Length; i++)
        {
            LegacyControlSnapshot candidate = controls[i];
            if (candidate != null && string.Equals(candidate.Path, path, StringComparison.Ordinal))
            {
                snapshot = candidate;
                break;
            }
        }
        return snapshot != null && CanExecute(root, snapshot);
    }

    private static bool SetExtEnum(
        DevUINode node,
        string selected,
        int selectedIndex,
        global::DevInterface.DevUI owner)
    {
        if (!FindExtEnumMember(node, out string memberName, out Type enumType)) return false;
        object parsed = ExtEnumBase.Parse(enumType, selected, false);
        if (!TryWriteMember(node, memberName, parsed)) return false;
        node.Refresh();
        if (node is Button button)
            PropagateSignal(button, DevUISignalType.ButtonClick, selected);
        SynchronizePollingParent(owner, node);

        object current = ReadMember(node, memberName);
        if (current is not ExtEnumBase extEnum) return false;
        return string.Equals(extEnum.value, selected, StringComparison.Ordinal) || extEnum.Index == selectedIndex;
    }

    private static bool FindExtEnumMember(DevUINode node, out string memberName, out Type enumType)
    {
        memberName = string.Empty;
        enumType = null;
        object value = ReadMember(node, "Type");
        if (value is ExtEnumBase ext)
        {
            memberName = "Type";
            enumType = ext.GetType();
            return IsWritableMember(node, memberName, enumType);
        }

        value = ReadMember(node, "actualValue");
        if (value is ExtEnumBase actual)
        {
            memberName = "actualValue";
            enumType = actual.GetType();
            return IsWritableMember(node, memberName, enumType);
        }
        return false;
    }

    private static void CloseSelectPanel(ButtonWithSelectPanel button)
    {
        SelectPanel panel = button?.selectPanel;
        if (button == null || panel == null) return;
        button.subNodes.Remove(panel);
        panel.ClearSprites();
        button.selectPanel = null;
    }

    private static DevUINode ResolveNode(DevUINode root, string path)
    {
        if (root == null || string.IsNullOrWhiteSpace(path)) return null;
        string[] parts = path.Split('.');
        DevUINode current = root;
        for (int i = 0; i < parts.Length; i++)
        {
            if (!int.TryParse(parts[i], out int index) || index < 0 || current.subNodes == null ||
                index >= current.subNodes.Count)
                return null;
            current = current.subNodes[index];
            if (current == null) return null;
        }
        return current;
    }

    private static object ReadMember(object instance, string name)
    {
        TryReadMember(instance, name, out object value);
        return value;
    }

    private static bool TryReadMember(object instance, string name, out object value)
    {
        value = null;
        if (instance == null || string.IsNullOrEmpty(name)) return false;
        FieldInfo field = FindField(instance.GetType(), name);
        if (field != null)
        {
            value = field.GetValue(instance);
            return true;
        }
        PropertyInfo property = FindProperty(instance.GetType(), name);
        if (property == null || !property.CanRead || property.GetIndexParameters().Length != 0) return false;
        value = property.GetValue(instance, null);
        return true;
    }

    private static bool TryWriteMember(object instance, string name, object value)
    {
        if (instance == null || string.IsNullOrEmpty(name)) return false;
        FieldInfo field = FindField(instance.GetType(), name);
        if (field != null && !field.IsInitOnly && (value == null || field.FieldType.IsInstanceOfType(value)))
        {
            field.SetValue(instance, value);
            return true;
        }
        PropertyInfo property = FindProperty(instance.GetType(), name);
        if (property != null && property.CanWrite && property.GetIndexParameters().Length == 0 &&
            (value == null || property.PropertyType.IsInstanceOfType(value)))
        {
            property.SetValue(instance, value, null);
            return true;
        }
        return false;
    }

    private static bool IsWritableMember(object instance, string name, Type expected)
    {
        if (instance == null || expected == null) return false;
        FieldInfo field = FindField(instance.GetType(), name);
        if (field != null && !field.IsInitOnly && expected.IsAssignableFrom(field.FieldType)) return true;
        PropertyInfo property = FindProperty(instance.GetType(), name);
        return property != null && property.CanWrite && property.GetIndexParameters().Length == 0 &&
               expected.IsAssignableFrom(property.PropertyType);
    }

    private static bool CanWriteVector2(object instance, string name) =>
        IsWritableMember(instance, name, typeof(Vector2));

    private static bool TryWriteVector2(object instance, string name, Vector2 value) =>
        TryWriteMember(instance, name, value);

    private static FieldInfo FindField(Type type, string name)
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

    private static PropertyInfo FindProperty(Type type, string name)
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

    private static MethodInfo FindMethod(Type type, string name, Type[] parameters)
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

    private static void Warn(string operation, DevUINode node, Exception error)
    {
        Plugin.Logger?.LogWarning(
            "DevTool universal " + operation + " action failed for " +
            (node?.GetType().FullName ?? "<unknown>") + ": " + error.Message);
    }
}
