using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;

namespace DryCycle.Items.ScavengerLance;

internal static class ScavengerLanceDevConsoleSupport
{
    private static object _spawner;
    private static IDictionary _spawners;
    private static AbstractPhysicalObject.AbstractObjectType _registeredType;
    internal static void TryRegister()
    {
        if (_spawner != null || ScavengerLanceHooks.ObjectType == null) return;
        Type type = Type.GetType("DevConsole.ObjectSpawner, DevConsole", false);
        if (type == null) return;
        try
        {
            Type info = type.GetNestedType("SpawnerInfo", BindingFlags.Public);
            Type simple = type.GetNestedType("SimpleSpawnerInfo", BindingFlags.Public);
            MethodInfo register = type.GetMethod("RegisterSpawner", new[] { typeof(AbstractPhysicalObject.AbstractObjectType), info });
            // Current DevConsole has no unregister API. Retain only our dictionary entry
            // and remove it by identity, preserving a replacement installed by another mod.
            _spawners = type.GetField("safeObjSpawners", BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null) as IDictionary;
            if (register == null || simple == null || _spawners == null)
                throw new MissingMemberException("DevConsole object-spawner lifecycle API changed.");
            Func<AbstractPhysicalObject.AbstractObjectType, string[], IEnumerable<string>> complete = (_, _) =>
                new[] { "length=75", "length=80", "length=90" };
            Func<AbstractPhysicalObject.AbstractObjectType, string[], EntityID, AbstractRoom, WorldCoordinate, AbstractPhysicalObject> spawn =
                (_, args, id, room, pos) => new AbstractScavengerLance(room.world, pos, id, ParseLength(args));
            object candidate = Activator.CreateInstance(simple, complete, spawn);
            register.Invoke(null, new[] { (object)ScavengerLanceHooks.ObjectType, candidate });
            _spawner = candidate;
            _registeredType = ScavengerLanceHooks.ObjectType;
            Plugin.Logger?.LogInfo("Dev Console: spawn ScavengerLance [length=75..90] (default 80).");
        }
        catch (Exception ex)
        { Plugin.Logger?.LogWarning("ScavengerLance DevConsole registration failed: " + ex); }
    }
    internal static void ResetRegistration()
    {
        if (_spawners != null && _registeredType != null && ReferenceEquals(_spawners[_registeredType], _spawner))
            _spawners.Remove(_registeredType);
        _spawner = null; _spawners = null; _registeredType = null;
    }
    internal static float ParseLength(string[] args)
    {
        float length = LanceCombatMath.DefaultLength;
        foreach (string arg in args ?? Array.Empty<string>())
        {
            if (!arg.StartsWith("length=", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Use spawn ScavengerLance [length=75..90].");
            if (!float.TryParse(arg.Substring(7), NumberStyles.Float, CultureInfo.InvariantCulture, out float parsed) ||
                float.IsInfinity(parsed) || float.IsNaN(parsed) || parsed < 75f || parsed > 90f)
                throw new ArgumentException("Lance length must be between 75 and 90.");
            length = parsed;
        }
        return length;
    }
}
