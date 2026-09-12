using System;
using System.Collections.Generic;
using System.Reflection;
using DevInterface;

namespace DryCycle.DevUI.DevTool.Compatibility;

/// <summary>
/// Adds generic protocols that are intentionally broader than Rain World's stock control base
/// classes. This remains capability-driven: no vanilla/RK/DryCycle type names are referenced.
/// </summary>
public static class UniversalDevUiProtocolAugmenter
{
    public static LegacyControlSnapshot[] Augment(DevUINode root, LegacyControlSnapshot[] captured)
    {
        if (root == null) return captured ?? Array.Empty<LegacyControlSnapshot>();

        List<LegacyControlSnapshot> result = new(captured ?? Array.Empty<LegacyControlSnapshot>());
        HashSet<string> occupied = new(StringComparer.Ordinal);
        for (int i = 0; i < result.Count; i++)
        {
            string path = result[i]?.Path;
            if (!string.IsNullOrWhiteSpace(path)) occupied.Add(path);
        }

        AddGenericChildren(root, string.Empty, occupied, result);
        return result.ToArray();
    }

    /// <summary>
    /// Generic action contract for controls that expose a parameterless void Clicked() but do not
    /// derive from Button. Containers/gizmos are excluded because their Clicked-like helper methods
    /// describe child handling rather than a standalone widget action.
    /// </summary>
    public static bool CanAdaptClick(DevUINode node) => node != null && CanAdaptClickType(node.GetType());

    public static bool CanAdaptClickType(Type type)
    {
        if (type == null || type.IsAbstract || !typeof(DevUINode).IsAssignableFrom(type)) return false;
        if (typeof(Page).IsAssignableFrom(type) ||
            typeof(Panel).IsAssignableFrom(type) ||
            typeof(Handle).IsAssignableFrom(type) ||
            typeof(DevUILabel).IsAssignableFrom(type) ||
            typeof(Slider).IsAssignableFrom(type) ||
            typeof(Cycler).IsAssignableFrom(type) ||
            typeof(IntegerControl).IsAssignableFrom(type) ||
            typeof(Button).IsAssignableFrom(type))
            return false;

        MethodInfo method = FindClicked(type);
        return method != null && method.ReturnType == typeof(void);
    }

    internal static bool InvokeClick(DevUINode node)
    {
        if (!CanAdaptClick(node)) return false;
        MethodInfo method = FindClicked(node.GetType());
        if (method == null || method.ReturnType != typeof(void)) return false;
        method.Invoke(node, Array.Empty<object>());
        node.Refresh();
        return true;
    }

    private static void AddGenericChildren(
        DevUINode parent,
        string parentPath,
        HashSet<string> occupied,
        List<LegacyControlSnapshot> output)
    {
        if (parent?.subNodes == null) return;

        for (int i = 0; i < parent.subNodes.Count; i++)
        {
            DevUINode node = parent.subNodes[i];
            if (node == null) continue;
            string path = string.IsNullOrEmpty(parentPath) ? i.ToString() : parentPath + "." + i;

            if (!occupied.Contains(path) && CanAdaptClick(node))
            {
                output.Add(new LegacyControlSnapshot
                {
                    Path = path,
                    Id = node.IDstring ?? string.Empty,
                    Label = SemanticLabel(node),
                    RuntimeType = node.GetType().FullName ?? node.GetType().Name,
                    Kind = LegacyControlKind.Button
                });
                occupied.Add(path);
            }

            // A captured atomic control already owns its presentation-only descendants. Do not
            // expose internal arrow/nub components as duplicate actions. Composite controls remain
            // recursive so a dynamically opened child panel is still discovered.
            if (LegacyDevInterfaceBridge.IsAtomicAdaptedControl(node)) continue;
            AddGenericChildren(node, path, occupied, output);
        }
    }

    private static string SemanticLabel(DevUINode node)
    {
        if (node?.subNodes != null)
        {
            for (int i = 0; i < node.subNodes.Count; i++)
            {
                if (node.subNodes[i] is DevUILabel label && !string.IsNullOrWhiteSpace(label.Text))
                    return label.Text.Trim().TrimEnd(':').Trim();
            }
        }

        string id = node?.IDstring ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(id)) return id.Replace('_', ' ').Trim();
        return node?.GetType().Name ?? "Action";
    }

    private static MethodInfo FindClicked(Type type)
    {
        Type current = type;
        while (current != null && current != typeof(DevUINode))
        {
            MethodInfo method = current.GetMethod(
                "Clicked",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly,
                null,
                Type.EmptyTypes,
                null);
            if (method != null) return method;
            current = current.BaseType;
        }
        return null;
    }
}
