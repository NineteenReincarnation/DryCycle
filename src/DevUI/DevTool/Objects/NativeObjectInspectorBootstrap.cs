using System.Runtime.CompilerServices;

namespace DryCycle.DevUI.DevTool.Objects;

/// <summary>
/// Registers the low-priority reflected model inspector before DevTool sessions are created.
/// Strongly typed extension adapters still win by priority; SafeDataFallbackInspector remains the
/// final read-only safety net.
/// </summary>
internal static class NativeObjectInspectorBootstrap
{
    [ModuleInitializer]
    internal static void Initialize()
    {
        ObjectInspectorRegistry.Register(NativeDataReflectionInspector.Instance, -1000);
    }
}