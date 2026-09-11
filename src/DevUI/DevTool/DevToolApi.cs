using System;
using System.Collections.Generic;
using DryCycle.DevUI.DevTool.Core;
using DryCycle.DevUI.DevTool.Objects;

namespace DryCycle.DevUI.DevTool;

/// <summary>
/// Public extension surface for mods that want first-class support in the DryCycle DevTool.
/// The dependency direction is intentionally one-way: an external mod may reference DryCycle
/// and register metadata/inspectors here, while DryCycle never probes or calls that mod's API.
/// </summary>
public static class DevToolApi
{
    /// <summary>
    /// Registers editor metadata for a PlacedObject type. This affects display name,
    /// category, source and search tags only; object creation remains owned by Rain World.
    /// Higher priority registrations win when several mods describe the same type.
    /// </summary>
    public static ObjectDescriptor RegisterObject(
        PlacedObject.Type type,
        string displayName = null,
        string category = null,
        string source = null,
        IEnumerable<string> tags = null,
        int priority = 0)
    {
        if (type == null) throw new ArgumentNullException(nameof(type));

        ObjectDescriptor descriptor = new(
            type,
            displayName ?? type.value,
            category ?? "Unsorted",
            source ?? "External Mod",
            tags);
        ObjectCatalog.RegisterDescriptor(descriptor, priority);
        EditorPresentationHub.InvalidateObjectLibrary();
        return descriptor;
    }

    /// <summary>
    /// Removes one exact metadata registration previously returned by RegisterObject.
    /// </summary>
    public static bool UnregisterObject(ObjectDescriptor descriptor)
    {
        bool removed = ObjectCatalog.UnregisterDescriptor(descriptor);
        if (removed) EditorPresentationHub.InvalidateObjectLibrary();
        return removed;
    }

    /// <summary>
    /// Registers a strongly typed native Inspector for a PlacedObject.Data subtype.
    /// The returned definition can later be passed to UnregisterInspector.
    /// </summary>
    public static ObjectInspectorDefinition<TData> RegisterInspector<TData>(
        Action<ObjectInspectorDefinition<TData>> configure,
        string source = null,
        int priority = 0)
        where TData : PlacedObject.Data
    {
        if (configure == null) throw new ArgumentNullException(nameof(configure));

        ObjectInspectorDefinition<TData> definition = new(source);
        configure(definition);
        ObjectInspectorRegistry.Register(definition, priority);
        return definition;
    }

    /// <summary>
    /// Registers a custom Inspector adapter when the fluent typed definition is not enough.
    /// </summary>
    public static TAdapter RegisterInspector<TAdapter>(TAdapter adapter, int priority = 0)
        where TAdapter : class, IObjectInspectorAdapter
    {
        if (adapter == null) throw new ArgumentNullException(nameof(adapter));
        ObjectInspectorRegistry.Register(adapter, priority);
        return adapter;
    }

    public static bool UnregisterInspector(IObjectInspectorAdapter adapter)
        => ObjectInspectorRegistry.Unregister(adapter);
}
