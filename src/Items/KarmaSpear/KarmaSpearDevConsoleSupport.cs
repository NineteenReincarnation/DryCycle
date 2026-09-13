using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;

namespace DryCycle.Items.KarmaSpear;

/// <summary>
/// DevConsole integration for Karma Spear. This deliberately creates the same
/// AbstractKarmaSpear used by normal gameplay so console testing exercises the
/// real realization, persistence and interaction path.
/// </summary>
internal static class KarmaSpearDevConsoleSupport
{
    private const string ObjectSpawnerAssemblyQualifiedName =
        "DevConsole.ObjectSpawner, DevConsole";
    private const int DefaultKarmaLevel = 5;

    private static bool _registered;

    internal static void ResetRegistration()
    {
        _registered = false;
    }

    internal static void TryRegister()
    {
        if (_registered || KarmaSpearHooks.ObjectType == null)
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
                    KarmaSpearHooks.ObjectType,
                    spawnerInfo
                });

            _registered = true;
            Plugin.Logger?.LogInfo(
                "Dev Console support enabled: `spawn KarmaSpear`, " +
                "`spawn KarmaSpear karma=10`, `spawn KarmaSpear spent=true`.");
        }
        catch (Exception ex)
        {
            Plugin.Logger?.LogWarning(
                $"Failed to register KarmaSpear with Dev Console: {ex.Message}");
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
        if (args == null || args.Length == 0)
        {
            return new[]
            {
                "karma=1",
                "karma=5",
                "karma=10",
                "spent=true",
                "spent=false"
            };
        }

        string current = args[args.Length - 1] ?? string.Empty;
        if (current.StartsWith("karma=", StringComparison.OrdinalIgnoreCase) ||
            current.StartsWith("level=", StringComparison.OrdinalIgnoreCase))
        {
            return new[] { "karma=1", "karma=5", "karma=10" };
        }

        if (current.StartsWith("spent", StringComparison.OrdinalIgnoreCase))
        {
            return new[] { "spent=true", "spent=false" };
        }

        return new[]
        {
            "karma=5",
            "karma=10",
            "spent=true",
            "spent=false"
        };
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
            throw new ArgumentException("Cannot spawn KarmaSpear without a valid room/world.");
        }

        ParseArguments(args, out int karmaLevel, out bool spent);

        AbstractKarmaSpear spear = new(
            room.world,
            pos,
            id,
            karmaLevel,
            spent);
        spear.Move(pos);
        return spear;
    }

    private static void ParseArguments(
        string[] args,
        out int karmaLevel,
        out bool spent)
    {
        karmaLevel = DefaultKarmaLevel;
        spent = false;

        if (args == null)
        {
            return;
        }

        bool positionalLevelUsed = false;
        for (int i = 0; i < args.Length; i++)
        {
            string raw = args[i]?.Trim();
            if (string.IsNullOrEmpty(raw))
            {
                continue;
            }

            if (TryReadValue(raw, "karma", out string karmaValue) ||
                TryReadValue(raw, "level", out karmaValue))
            {
                karmaLevel = ParseKarmaLevel(karmaValue);
                continue;
            }

            if (TryReadValue(raw, "spent", out string spentValue))
            {
                spent = ParseBoolean(spentValue, "spent");
                continue;
            }

            if (raw.Equals("spent", StringComparison.OrdinalIgnoreCase))
            {
                spent = true;
                continue;
            }

            if (raw.Equals("active", StringComparison.OrdinalIgnoreCase))
            {
                spent = false;
                continue;
            }

            if (!positionalLevelUsed &&
                int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int positionalLevel))
            {
                karmaLevel = ValidateKarmaLevel(positionalLevel);
                positionalLevelUsed = true;
                continue;
            }

            throw new ArgumentException(
                $"Unknown KarmaSpear argument `{raw}`. " +
                "Use `karma=1..10` (or `level=1..10`) and `spent=true|false`. " +
                "Examples: `spawn KarmaSpear`, `spawn KarmaSpear karma=10`, " +
                "`spawn KarmaSpear karma=5 spent=true`.");
        }
    }

    private static bool TryReadValue(
        string argument,
        string key,
        out string value)
    {
        string prefix = key + "=";
        if (argument.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            value = argument.Substring(prefix.Length).Trim();
            return true;
        }

        value = null;
        return false;
    }

    private static int ParseKarmaLevel(string value)
    {
        if (!int.TryParse(
                value,
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out int parsed))
        {
            throw new ArgumentException(
                $"Invalid KarmaSpear karma level `{value}`. Expected an integer from 1 to 10.");
        }

        return ValidateKarmaLevel(parsed);
    }

    private static int ValidateKarmaLevel(int value)
    {
        if (value < 1 || value > 10)
        {
            throw new ArgumentOutOfRangeException(
                nameof(value),
                value,
                "KarmaSpear karma level must be between 1 and 10.");
        }

        return value;
    }

    private static bool ParseBoolean(string value, string name)
    {
        if (bool.TryParse(value, out bool parsed))
        {
            return parsed;
        }

        if (value == "1" || value.Equals("yes", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("on", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (value == "0" || value.Equals("no", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("off", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        throw new ArgumentException(
            $"Invalid KarmaSpear {name} value `{value}`. Expected true/false, 1/0, yes/no or on/off.");
    }
}
