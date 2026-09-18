namespace DryCycle.DevUI.DevTool.Objects;

/// <summary>
/// Registers the low-priority reflected model inspector when the DevTool runtime is enabled.
/// Strongly typed extension adapters still win by priority; SafeDataFallbackInspector remains the
/// final read-only safety net.
/// </summary>
internal static class NativeObjectInspectorBootstrap
{
    internal static void Enable()
    {
        BuiltinStructuredInspectorAdapters.Enable();
        ObjectInspectorRegistry.Register(NativeDataReflectionInspector.Instance, -1000);
    }
}