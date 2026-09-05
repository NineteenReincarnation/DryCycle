using System;
using System.Linq;
using System.Reflection;

namespace DryCycle.Creatures.DesertBatfly;

// Optional integrations should not become hard compile/runtime dependencies.
// BepInEx already ships Harmony; resolve it reflectively so DryCycle continues to
// load even in unusual installs where that assembly or the target mod is absent.
internal static class DesertBatflyRuntimePatch
{
    private static Type HarmonyType => FindType("HarmonyLib.Harmony");
    private static Type HarmonyMethodType => FindType("HarmonyLib.HarmonyMethod");

    internal static object Create(string id)
    {
        Type type = HarmonyType;
        if (type == null) return null;
        try { return Activator.CreateInstance(type, new object[] { id }); }
        catch { return null; }
    }

    internal static bool Patch(object harmony, MethodBase original, MethodInfo prefix = null, MethodInfo postfix = null)
    {
        if (harmony == null || original == null) return false;
        Type harmonyMethodType = HarmonyMethodType;
        if (harmonyMethodType == null) return false;

        try
        {
            object prefixMethod = prefix == null ? null : Activator.CreateInstance(harmonyMethodType, new object[] { prefix });
            object postfixMethod = postfix == null ? null : Activator.CreateInstance(harmonyMethodType, new object[] { postfix });
            MethodInfo patch = harmony.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public)
                .FirstOrDefault(method =>
                {
                    if (method.Name != "Patch") return false;
                    ParameterInfo[] parameters = method.GetParameters();
                    return parameters.Length == 5 && typeof(MethodBase).IsAssignableFrom(parameters[0].ParameterType);
                });
            if (patch == null) return false;
            patch.Invoke(harmony, new[] { original, prefixMethod, postfixMethod, null, null });
            return true;
        }
        catch
        {
            return false;
        }
    }

    internal static void UnpatchSelf(object harmony)
    {
        if (harmony == null) return;
        try
        {
            harmony.GetType().GetMethod("UnpatchSelf", BindingFlags.Instance | BindingFlags.Public)?.Invoke(harmony, null);
        }
        catch
        {
        }
    }

    internal static Type FindType(string fullName)
    {
        foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            try
            {
                Type type = assembly.GetType(fullName, false);
                if (type != null) return type;
            }
            catch
            {
            }
        }
        return null;
    }
}
