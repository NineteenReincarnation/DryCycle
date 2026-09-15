using System;
using System.Collections.Generic;
using DryCycle.DevUI.DevTool.Objects;

namespace DryCycle.DevUI.DevTool.Extensions;

/// <summary>
/// Stable capability identifiers exposed by the DevTool extension API.
/// New capabilities may be added without changing the API major version.
/// </summary>
[Flags]
public enum DevToolCapability
{
    None = 0,
    ObjectDescriptors = 1 << 0,
    ObjectInspectors = 1 << 1,
    ScopedRegistrations = 1 << 2,
    ExtensionDiscovery = 1 << 3,
    FaultIsolatedInspectors = 1 << 4
}

/// <summary>
/// Semantic version of the public DevTool extension contract. A major-version mismatch
/// is incompatible; a newer minor version only adds backwards-compatible capabilities.
/// </summary>
public readonly struct DevToolApiVersion : IEquatable<DevToolApiVersion>
{
    public DevToolApiVersion(int major, int minor)
    {
        if (major < 0) throw new ArgumentOutOfRangeException(nameof(major));
        if (minor < 0) throw new ArgumentOutOfRangeException(nameof(minor));
        Major = major;
        Minor = minor;
    }

    public int Major { get; }
    public int Minor { get; }

    public bool IsCompatibleWith(DevToolApiVersion required) =>
        Major == required.Major && Minor >= required.Minor;

    public bool Equals(DevToolApiVersion other) => Major == other.Major && Minor == other.Minor;
    public override bool Equals(object obj) => obj is DevToolApiVersion other && Equals(other);
    public override int GetHashCode() => (Major * 397) ^ Minor;
    public override string ToString() => Major + "." + Minor;

    public static bool operator ==(DevToolApiVersion left, DevToolApiVersion right) => left.Equals(right);
    public static bool operator !=(DevToolApiVersion left, DevToolApiVersion right) => !left.Equals(right);
}

/// <summary>
/// Read-only information about a currently active third-party DevTool extension.
/// </summary>
public sealed class DevToolExtensionSnapshot
{
    internal DevToolExtensionSnapshot(string id, string displayName, string version, int registrationCount)
    {
        Id = id;
        DisplayName = displayName;
        Version = version;
        RegistrationCount = registrationCount;
    }

    public string Id { get; }
    public string DisplayName { get; }
    public string Version { get; }
    public int RegistrationCount { get; }
}

/// <summary>
/// Idempotent handle for one registration owned by an extension scope.
/// Dispose it to unregister only that contribution.
/// </summary>
public sealed class DevToolRegistration : IDisposable
{
    private Action unregister;

    internal DevToolRegistration(string kind, Action unregister)
    {
        Kind = kind ?? string.Empty;
        this.unregister = unregister ?? throw new ArgumentNullException(nameof(unregister));
    }

    public string Kind { get; }
    public bool IsDisposed => unregister == null;

    public void Dispose()
    {
        Action action = unregister;
        if (action == null) return;
        unregister = null;
        try
        {
            action();
        }
        catch (Exception error)
        {
            DevToolApi.LogExtensionWarning("registration cleanup failed", error);
        }
    }
}

/// <summary>
/// Lifetime boundary for one third-party integration. Registrations made through this scope
/// are removed together when the owning mod disables or reloads, preventing stale callbacks.
/// Register and dispose scopes from the Rain World / BepInEx main thread.
/// </summary>
public sealed class DevToolExtensionScope : IDisposable
{
    private readonly List<DevToolRegistration> registrations = new();
    private bool disposed;

    internal DevToolExtensionScope(string id, string displayName, string version)
    {
        Id = id;
        DisplayName = displayName;
        Version = version;
    }

    public string Id { get; }
    public string DisplayName { get; }
    public string Version { get; }
    public bool IsDisposed => disposed;

    public int RegistrationCount
    {
        get
        {
            int count = 0;
            for (int i = 0; i < registrations.Count; i++)
                if (!registrations[i].IsDisposed) count++;
            return count;
        }
    }

    /// <summary>
    /// Registers richer library metadata for a PlacedObject type. Higher priority wins when
    /// multiple extensions describe the same type.
    /// </summary>
    public DevToolRegistration RegisterObjectDescriptor(ObjectDescriptor descriptor, int priority = 100)
    {
        ThrowIfDisposed();
        if (descriptor == null) throw new ArgumentNullException(nameof(descriptor));

        ObjectCatalog.RegisterDescriptor(descriptor, priority);
        return Track("ObjectDescriptor", () => ObjectCatalog.UnregisterDescriptor(descriptor));
    }

    /// <summary>
    /// Convenience overload that automatically uses this extension's display name as the source.
    /// </summary>
    public DevToolRegistration RegisterObjectDescriptor(
        PlacedObject.Type type,
        string displayName,
        string category,
        IEnumerable<string> tags = null,
        int priority = 100)
    {
        ThrowIfDisposed();
        ObjectDescriptor descriptor = new(type, displayName, category, DisplayName, tags);
        return RegisterObjectDescriptor(descriptor, priority);
    }

    /// <summary>
    /// Registers a custom object Inspector adapter. Third-party callbacks are invoked through
    /// the existing fault-isolated Inspector registry.
    /// </summary>
    public DevToolRegistration RegisterInspector(IObjectInspectorAdapter adapter, int priority = 100)
    {
        ThrowIfDisposed();
        if (adapter == null) throw new ArgumentNullException(nameof(adapter));

        ObjectInspectorRegistry.Register(adapter, priority);
        return Track("ObjectInspector", () => ObjectInspectorRegistry.Unregister(adapter));
    }

    /// <summary>
    /// Convenience overload for a strongly typed PlacedObject.Data inspector definition.
    /// </summary>
    public DevToolRegistration RegisterInspector<TData>(
        Action<ObjectInspectorDefinition<TData>> configure,
        int priority = 100,
        string source = null)
        where TData : PlacedObject.Data
    {
        ThrowIfDisposed();
        if (configure == null) throw new ArgumentNullException(nameof(configure));

        ObjectInspectorDefinition<TData> definition = new(source ?? DisplayName);
        configure(definition);
        return RegisterInspector(definition, priority);
    }

    public bool Supports(DevToolCapability capability) => DevToolApi.Supports(capability);

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;

        // Reverse order mirrors normal resource-stack unwinding and makes registrations that
        // depend on earlier registrations disappear first.
        for (int i = registrations.Count - 1; i >= 0; i--)
            registrations[i].Dispose();
        registrations.Clear();

        DevToolApi.Release(this);
    }

    internal DevToolExtensionSnapshot Snapshot() =>
        new(Id, DisplayName, Version, RegistrationCount);

    private DevToolRegistration Track(string kind, Action unregister)
    {
        DevToolRegistration registration = new(kind, unregister);
        registrations.Add(registration);
        return registration;
    }

    private void ThrowIfDisposed()
    {
        if (disposed) throw new ObjectDisposedException(nameof(DevToolExtensionScope),
            "DevTool extension scope '" + Id + "' has already been disposed.");
    }
}

/// <summary>
/// Public, backend-neutral entry point for optional third-party DevTool integration.
/// This contract intentionally exposes no ImGui types and does not require DevTool to know
/// the third-party mod at compile time.
/// </summary>
public static class DevToolApi
{
    private static readonly object Gate = new();
    private static readonly DevToolApiVersion currentVersion = new(1, 0);
    private const DevToolCapability CurrentCapabilities =
        DevToolCapability.ObjectDescriptors |
        DevToolCapability.ObjectInspectors |
        DevToolCapability.ScopedRegistrations |
        DevToolCapability.ExtensionDiscovery |
        DevToolCapability.FaultIsolatedInspectors;

    private static readonly Dictionary<string, DevToolExtensionScope> extensions =
        new(StringComparer.OrdinalIgnoreCase);

    public static DevToolApiVersion Version => currentVersion;
    public static DevToolCapability Capabilities => CurrentCapabilities;

    public static bool Supports(DevToolCapability capability) =>
        (CurrentCapabilities & capability) == capability;

    public static bool Supports(DevToolApiVersion requiredVersion, DevToolCapability requiredCapabilities = DevToolCapability.None) =>
        currentVersion.IsCompatibleWith(requiredVersion) && Supports(requiredCapabilities);

    /// <summary>
    /// Opens the lifetime scope for one optional integration. IDs must be stable and globally
    /// unique (normally the mod ID). Re-registering an active ID is treated as a lifecycle bug.
    /// </summary>
    public static DevToolExtensionScope RegisterExtension(
        string id,
        string displayName = null,
        string version = null,
        DevToolApiVersion? requiredApi = null,
        DevToolCapability requiredCapabilities = DevToolCapability.None)
    {
        ValidateExtensionRequest(id, requiredApi, requiredCapabilities);
        string normalizedId = id.Trim();

        lock (Gate)
        {
            if (extensions.ContainsKey(normalizedId))
                throw new InvalidOperationException("DevTool extension ID is already active: " + normalizedId);

            DevToolExtensionScope scope = new(
                normalizedId,
                string.IsNullOrWhiteSpace(displayName) ? normalizedId : displayName.Trim(),
                version?.Trim() ?? string.Empty);
            extensions.Add(normalizedId, scope);
            return scope;
        }
    }

    /// <summary>
    /// Non-throwing compatibility probe for optional integrations. False means the requested API
    /// version/capabilities are unavailable or the extension ID is already active.
    /// </summary>
    public static bool TryRegisterExtension(
        string id,
        out DevToolExtensionScope scope,
        string displayName = null,
        string version = null,
        DevToolApiVersion? requiredApi = null,
        DevToolCapability requiredCapabilities = DevToolCapability.None)
    {
        scope = null;
        if (string.IsNullOrWhiteSpace(id)) return false;

        DevToolApiVersion required = requiredApi ?? new DevToolApiVersion(currentVersion.Major, 0);
        if (!Supports(required, requiredCapabilities)) return false;

        string normalizedId = id.Trim();
        lock (Gate)
        {
            if (extensions.ContainsKey(normalizedId)) return false;
            scope = new DevToolExtensionScope(
                normalizedId,
                string.IsNullOrWhiteSpace(displayName) ? normalizedId : displayName.Trim(),
                version?.Trim() ?? string.Empty);
            extensions.Add(normalizedId, scope);
            return true;
        }
    }

    /// <summary>
    /// Returns a detached snapshot so callers cannot mutate the registry while enumerating it.
    /// </summary>
    public static IReadOnlyList<DevToolExtensionSnapshot> GetExtensions()
    {
        lock (Gate)
        {
            List<DevToolExtensionSnapshot> result = new(extensions.Count);
            foreach (DevToolExtensionScope extension in extensions.Values)
                result.Add(extension.Snapshot());
            result.Sort((a, b) => string.Compare(a.Id, b.Id, StringComparison.OrdinalIgnoreCase));
            return result;
        }
    }

    public static bool TryGetExtension(string id, out DevToolExtensionSnapshot extension)
    {
        extension = null;
        if (string.IsNullOrWhiteSpace(id)) return false;
        lock (Gate)
        {
            if (!extensions.TryGetValue(id.Trim(), out DevToolExtensionScope scope)) return false;
            extension = scope.Snapshot();
            return true;
        }
    }

    internal static void Release(DevToolExtensionScope scope)
    {
        if (scope == null) return;
        lock (Gate)
        {
            if (extensions.TryGetValue(scope.Id, out DevToolExtensionScope current) && ReferenceEquals(current, scope))
                extensions.Remove(scope.Id);
        }
    }

    internal static void LogExtensionWarning(string message, Exception error)
    {
        try
        {
            Plugin.Logger?.LogWarning("DevTool extension " + message + ": " + error?.Message);
        }
        catch
        {
            // Cleanup must remain exception-safe even if the host logger is unavailable during unload.
        }
    }

    private static void ValidateExtensionRequest(
        string id,
        DevToolApiVersion? requiredApi,
        DevToolCapability requiredCapabilities)
    {
        if (string.IsNullOrWhiteSpace(id)) throw new ArgumentException("Extension ID cannot be empty.", nameof(id));

        DevToolApiVersion required = requiredApi ?? new DevToolApiVersion(currentVersion.Major, 0);
        if (!Supports(required, requiredCapabilities))
        {
            throw new NotSupportedException(
                "DevTool API " + currentVersion + " does not satisfy extension '" + id +
                "' requirement " + required + " / " + requiredCapabilities + ".");
        }
    }
}
