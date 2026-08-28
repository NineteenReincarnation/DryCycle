using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;

namespace DryCycle.Creatures;

/// <summary>
/// Optional Dev Console integration for Spineback Lizard.
///
/// This intentionally uses reflection so DryCycle does not require DevConsole.dll
/// at compile time or runtime. When Dev Console is installed, DryCycle registers
/// SpinebackLizard with ObjectSpawner's safe creature spawner table so the normal
/// `spawn SpinebackLizard` command and autocomplete can be used.
/// </summary>
internal static class SpinebackLizardDevConsoleSupport
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
        if (_registered || SpinebackLizardEnums.Type == null)
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
                Plugin.Logger?.LogWarning(
                    "Dev Console detected, but ObjectSpawner spawner types were not found; " +
                    "SpinebackLizard spawn integration was skipped.");
                return;
            }

            MethodInfo registerSpawner = FindCreatureRegisterSpawner(
                objectSpawnerType,
                spawnerInfoType);

            if (registerSpawner == null)
            {
                Plugin.Logger?.LogWarning(
                    "Dev Console detected, but ObjectSpawner.RegisterSpawner(CreatureTemplate.Type, SpawnerInfo) " +
                    "was not found; SpinebackLizard spawn integration was skipped.");
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
                    SpinebackLizardEnums.Type,
                    spawnerInfo
                });

            _registered = true;
            Plugin.Logger?.LogInfo(
                "Dev Console support enabled: use `spawn SpinebackLizard`.");
        }
        catch (TargetInvocationException ex)
        {
            Exception inner = ex.InnerException ?? ex;
            Plugin.Logger?.LogWarning(
                $"Failed to register SpinebackLizard with Dev Console: {inner.Message}");
        }
        catch (Exception ex)
        {
            Plugin.Logger?.LogWarning(
                $"Failed to register SpinebackLizard with Dev Console: {ex.Message}");
        }
    }

    private static MethodInfo FindCreatureRegisterSpawner(
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
                parameters[0].ParameterType == typeof(CreatureTemplate.Type) &&
                parameters[1].ParameterType == spawnerInfoType)
            {
                return method;
            }
        }

        return null;
    }

    private static IEnumerable<string> Autocomplete(
        AbstractPhysicalObject.AbstractObjectType type,
        string[] args)
    {
        // Registering the CreatureTemplate.Type itself is enough for Dev Console's
        // `spawn` autocomplete to list SpinebackLizard. Keep argument suggestions
        // empty so we do not depend on Dev Console's internal hint formatting.
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
            throw new ArgumentException("Cannot spawn SpinebackLizard without a valid room/world.");
        }

        CreatureTemplate template = StaticWorld.GetCreatureTemplate(
            SpinebackLizardEnums.Type);

        if (template == null)
        {
            throw new InvalidOperationException(
                "SpinebackLizard CreatureTemplate has not been initialized yet.");
        }

        bool validNode = pos.NodeDefined &&
                         template.mappedNodeTypes != null &&
                         pos.abstractNode >= 0 &&
                         pos.abstractNode < template.mappedNodeTypes.Length &&
                         template.mappedNodeTypes[pos.abstractNode];

        if (!validNode)
        {
            pos.abstractNode = room.RandomRelevantNode(template);
        }

        AbstractCreature creature = new AbstractCreature(
            room.world,
            template,
            null,
            pos,
            id);

        if (args != null && args.Length > 0)
        {
            string[] spawnArgs = (string[])args.Clone();

            // Match Dev Console's normal lizard convention: the first numeric
            // argument is interpreted as personality Mean. Remaining arguments are
            // passed through as normal creature spawn tags.
            if (float.TryParse(
                    spawnArgs[0],
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out float mean))
            {
                spawnArgs[0] = "Mean:" + mean.ToString(
                    CultureInfo.InvariantCulture);
            }

            creature.spawnData = "{" + string.Join(",", spawnArgs) + "}";

            try
            {
                creature.setCustomFlags();
            }
            catch
            {
                // Some custom flags only exist in specific story contexts. The
                // creature itself should still spawn even if an optional tag fails.
            }
        }

        creature.Move(pos);
        return creature;
    }
}
