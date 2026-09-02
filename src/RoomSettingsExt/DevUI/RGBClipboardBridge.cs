using System;
using System.Reflection;

namespace DryCycle.RoomSettingsExt.DevUI;

/// <summary>
/// Compile-time independent bridge to Unity's clipboard. DryCycle intentionally does
/// not reference UnityEngine.IMGUIModule directly, so the RGB editor resolves the
/// systemCopyBuffer property at runtime just like the shared text-field framework.
/// </summary>
internal static class GUIUtility
{
    private static readonly PropertyInfo CopyBufferProperty = ResolveCopyBufferProperty();

    internal static string systemCopyBuffer
    {
        get
        {
            try
            {
                return CopyBufferProperty?.GetValue(null, null) as string ?? string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }
        set
        {
            try
            {
                CopyBufferProperty?.SetValue(null, value ?? string.Empty, null);
            }
            catch
            {
                // Clipboard integration is optional and must never break DevUI.
            }
        }
    }

    private static PropertyInfo ResolveCopyBufferProperty()
    {
        Type type = Type.GetType("UnityEngine.GUIUtility, UnityEngine.IMGUIModule", throwOnError: false)
            ?? Type.GetType("UnityEngine.GUIUtility, UnityEngine", throwOnError: false);
        return type?.GetProperty("systemCopyBuffer", BindingFlags.Public | BindingFlags.Static);
    }
}
