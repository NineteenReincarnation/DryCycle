using System;
using System.Collections.Generic;

namespace DryCycle.DevUI.DevTool.Objects;

/// <summary>
/// Framework-owned fallback inspector. It deliberately understands only PlacedObject's
/// public base state, so completely unknown mod objects still have a useful panel without
/// DryCycle reflecting into or depending on their implementation.
/// </summary>
internal static class BuiltinInspectors
{
    private static readonly IObjectInspectorAdapter Fallback = new FallbackObjectInspector();
    private static bool registered;

    internal static void Enable()
    {
        if (registered) return;
        ObjectInspectorRegistry.Register(Fallback, int.MinValue);
        registered = true;
    }

    private sealed class FallbackObjectInspector : IObjectInspectorAdapter
    {
        public bool CanInspect(PlacedObject target) => target != null;

        public IReadOnlyList<EditorPropertySnapshot> Capture(PlacedObject target)
        {
            if (target == null) return Array.Empty<EditorPropertySnapshot>();

            return new EditorPropertySnapshot[]
            {
                new()
                {
                    Key = "$active",
                    DisplayName = "Active",
                    Group = "Object",
                    Source = "Rain World PlacedObject",
                    Kind = EditorPropertyKind.Boolean,
                    BooleanValue = target.active
                },
                new()
                {
                    Key = "$save",
                    DisplayName = "Save",
                    Group = "Object",
                    Source = "Rain World PlacedObject",
                    Kind = EditorPropertyKind.Boolean,
                    BooleanValue = target.save
                },
                new()
                {
                    Key = "$data",
                    DisplayName = "Serialized Data",
                    Group = "Fallback",
                    Source = target.data?.GetType().FullName ?? "PlacedObject.Data",
                    Kind = EditorPropertyKind.ReadOnly,
                    StringValue = SafeDataString(target)
                }
            };
        }

        public bool TrySetValue(PlacedObject target, string key, EditorPropertyValue value)
        {
            if (target == null || value.Kind != EditorPropertyKind.Boolean) return false;

            switch (key)
            {
                case "$active":
                    target.active = value.Boolean;
                    return true;
                case "$save":
                    target.save = value.Boolean;
                    return true;
                default:
                    return false;
            }
        }

        private static string SafeDataString(PlacedObject target)
        {
            try { return target?.data?.ToString() ?? string.Empty; }
            catch (Exception error) { return "<serialization failed: " + error.GetType().Name + ">"; }
        }
    }
}
