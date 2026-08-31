using System;
using System.Collections.Generic;
using System.Reflection;

namespace DryCycle.Registration;

/// <summary>
/// Optional Dev Console integration for every creature registered through
/// DryCycleContent. Reflection keeps DevConsole a soft dependency while custom
/// creatures automatically gain `spawn <CreatureTemplate.Type>` support.
/// </summary>
internal static class CreatureDevConsoleSupport
{
    private const string ObjectSpawnerAssemblyQualifiedName =
        "DevConsole.ObjectSpawner, DevConsole";

    private static readonly HashSet<CreatureTemplate.Type> RegisteredTypes = new();

    internal static void ResetRegistration()
    {
        RegisteredTypes.Clear();
    }

    internal static void TryRegisterAll()
    {
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
                    "DryCycle custom-creature spawn integration was skipped.");
                return;
            }

            MethodInfo registerSpawner = FindCreatureRegisterSpawner(
                objectSpawnerType,
                spawnerInfoType);

            if (registerSpawner == null)
            {
                Plugin.Logger?.LogWarning(
                    "Dev Console detected, but ObjectSpawner.RegisterSpawner(CreatureTemplate.Type, SpawnerInfo) " +
                    "was not found; DryCycle custom-creature spawn integration was skipped.");
                return;
            }

            foreach (CreatureDefinition definition in CreatureRegistry.Registered)
            {
                if (definition?.Type == null || RegisteredTypes.Contains(definition.Type))
                {
                    continue;
                }

                RegisterDefinition(
                    definition,
                    simpleSpawnerInfoType,
                    registerSpawner);
            }
        }
        catch (TargetInvocationException ex)
        {
            Exception inner = ex.InnerException ?? ex;
            Plugin.Logger?.LogWarning(
                $"Failed to register DryCycle creatures with Dev Console: {inner.Message}");
        }
        catch (Exception ex)
        {
            Plugin.Logger?.LogWarning(
                $"Failed to register DryCycle creatures with Dev Console: {ex.Message}");
        }
    }

    private static void RegisterDefinition(
        CreatureDefinition definition,
        Type simpleSpawnerInfoType,
        MethodInfo registerSpawner)
    {
        Func<AbstractPhysicalObject.AbstractObjectType, string[], IEnumerable<string>> autocomplete =
            Autocomplete;

        Func<AbstractPhysicalObject.AbstractObjectType, string[], EntityID, AbstractRoom, WorldCoordinate, AbstractPhysicalObject> spawn =
            (ignoredObjectType, args, id, room, pos) => Spawn(
                definition,
                args,
                id,
                room,
                pos);

        object spawnerInfo = Activator.CreateInstance(
            simpleSpawnerInfoType,
            autocomplete,
            spawn);

        registerSpawner.Invoke(
            null,
            new object[]
            {
                definition.Type,
                spawnerInfo
            });

        RegisteredTypes.Add(definition.Type);
        Plugin.Logger?.LogInfo(
            $"Dev Console support enabled: use `spawn {definition.Type.value}`.");
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
        AbstractPhysicalObject.AbstractObjectType ignoredObjectType,
        string[] args)
    {
        // The CreatureTemplate.Type registration itself supplies the spawn command
        // autocomplete entry. Creature-specific spawn arguments can be added later
        // without changing this central registration path.
        return null;
    }

    private static AbstractPhysicalObject Spawn(
        CreatureDefinition definition,
        string[] args,
        EntityID id,
        AbstractRoom room,
        WorldCoordinate pos)
    {
        if (room?.world == null)
        {
            throw new ArgumentException(
                $"Cannot spawn {definition.Type.value} without a valid room/world.");
        }

        CreatureTemplate template = StaticWorld.GetCreatureTemplate(definition.Type);
        if (template == null)
        {
            throw new InvalidOperationException(
                $"{definition.Type.value} CreatureTemplate has not been initialized yet.");
        }

        bool validNode = pos.NodeDefined &&
                         template.mappedNodeTypes != null &&
                         pos.abstractNode >= 0 &&
                         pos.abstractNode < room.nodes.Length &&
                         room.nodes[pos.abstractNode].type.Index >= 0 &&
                         room.nodes[pos.abstractNode].type.Index < template.mappedNodeTypes.Length &&
                         template.mappedNodeTypes[room.nodes[pos.abstractNode].type.Index];

        if (!validNode)
        {
            // During staged creature development MovementConnection rules may not
            // have been authored yet, leaving no mapped room node. -1 is valid for
            // a tile-defined Dev Console spawn and avoids inventing pathing rules.
            pos.abstractNode = room.RandomRelevantNode(template);
        }

        AbstractCreature creature = new(
            room.world,
            template,
            null,
            pos,
            id);

        if (args != null && args.Length > 0)
        {
            creature.spawnData = "{" + string.Join(",", args) + "}";

            try
            {
                creature.setCustomFlags();
            }
            catch
            {
                // Optional story-only flags should not prevent a development spawn.
            }
        }

        creature.Move(pos);
        return creature;
    }
}
