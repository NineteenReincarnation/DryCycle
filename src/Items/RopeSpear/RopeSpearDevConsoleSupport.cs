using System;
using System.Collections.Generic;
using System.Reflection;

namespace DryCycle.Items.RopeSpear;

internal static class RopeSpearDevConsoleSupport
{
    private const string ObjectSpawnerAssemblyQualifiedName =
        "DevConsole.ObjectSpawner, DevConsole";

    private static bool _registered;

    internal static void ResetRegistration()
    {
        _registered = false;
    }

    internal static void TryRegister()
    {
        if (_registered || RopeSpearHooks.ObjectType == null)
        {
            return;
        }

        Type objectSpawnerType = Type.GetType(
            ObjectSpawnerAssemblyQualifiedName,
            throwOnError: false);
        if (objectSpawnerType == null)
        {
            return;
        }

        try
        {
            Type spawnerInfoType = objectSpawnerType.GetNestedType(
                "SpawnerInfo",
                BindingFlags.Public);
            Type simpleSpawnerInfoType = objectSpawnerType.GetNestedType(
                "SimpleSpawnerInfo",
                BindingFlags.Public);
            if (spawnerInfoType == null || simpleSpawnerInfoType == null)
            {
                return;
            }

            MethodInfo registerSpawner = FindRegisterSpawner(
                objectSpawnerType,
                spawnerInfoType);
            if (registerSpawner == null)
            {
                return;
            }

            Func<AbstractPhysicalObject.AbstractObjectType, string[], IEnumerable<string>> autocomplete =
                Autocomplete;
            Func<AbstractPhysicalObject.AbstractObjectType, string[], EntityID, AbstractRoom, WorldCoordinate, AbstractPhysicalObject> spawn =
                Spawn;

            object spawnerInfo = Activator.CreateInstance(
                simpleSpawnerInfoType,
                autocomplete,
                spawn);

            registerSpawner.Invoke(
                null,
                new object[]
                {
                    RopeSpearHooks.ObjectType,
                    spawnerInfo
                });

            _registered = true;
            Plugin.Logger?.LogInfo("Dev Console support enabled: use `spawn RopeSpear`.");
        }
        catch (Exception ex)
        {
            Plugin.Logger?.LogWarning(
                $"Failed to register RopeSpear with Dev Console: {ex.Message}");
        }
    }

    private static MethodInfo FindRegisterSpawner(
        Type objectSpawnerType,
        Type spawnerInfoType)
    {
        MethodInfo[] methods = objectSpawnerType.GetMethods(
            BindingFlags.Public | BindingFlags.Static);

        for (int i = 0; i < methods.Length; i++)
        {
            MethodInfo method = methods[i];
            if (method.Name != "RegisterSpawner")
            {
                continue;
            }

            ParameterInfo[] parameters = method.GetParameters();
            if (parameters.Length == 2 &&
                parameters[0].ParameterType == typeof(AbstractPhysicalObject.AbstractObjectType) &&
                parameters[1].ParameterType == spawnerInfoType)
            {
                return method;
            }
        }

        return null;
    }

    private static IEnumerable<string> Autocomplete(
        AbstractPhysicalObject.AbstractObjectType ignoredObjectType,
        string[] args)
    {
        return null;
    }

    private static AbstractPhysicalObject Spawn(
        AbstractPhysicalObject.AbstractObjectType ignoredObjectType,
        string[] args,
        EntityID id,
        AbstractRoom room,
        WorldCoordinate pos)
    {
        if (room?.world == null)
        {
            throw new ArgumentException("Cannot spawn RopeSpear without a valid room/world.");
        }

        AbstractRopeSpear spear = new(
            room.world,
            pos,
            id,
            AbstractRopeSpear.DefaultRopeLength,
            ropeBroken: false);
        spear.Move(pos);
        return spear;
    }
}
