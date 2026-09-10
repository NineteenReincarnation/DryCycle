using System;
using System.Collections.Generic;
using System.Reflection;
using DryCycle.Framework.Creature.Core;
using CreatureCoreRegistry = DryCycle.Framework.Creature.Core.CreatureRegistry;

namespace DryCycle.Registration;

/// <summary>
/// 为通过 Creature Core Framework 登记的生物提供可选的 Dev Console 生成支持。
/// 这里通过反射接入 DevConsole，因此 DevConsole 仍然只是软依赖；已经登记的生物会自动获得 `spawn <CreatureTemplate.Type>` 入口。
///
/// Optional Dev Console spawn integration for creatures registered through the Creature Core Framework.
/// Reflection keeps DevConsole a soft dependency while registered creatures automatically gain `spawn <CreatureTemplate.Type>` support.
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

            foreach (CreatureDescriptor descriptor in CreatureCoreRegistry.Registered)
            {
                if (descriptor?.Type == null || RegisteredTypes.Contains(descriptor.Type))
                {
                    continue;
                }

                RegisterDescriptor(
                    descriptor,
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

    private static void RegisterDescriptor(
        CreatureDescriptor descriptor,
        Type simpleSpawnerInfoType,
        MethodInfo registerSpawner)
    {
        Func<AbstractPhysicalObject.AbstractObjectType, string[], IEnumerable<string>> autocomplete =
            Autocomplete;

        Func<AbstractPhysicalObject.AbstractObjectType, string[], EntityID, AbstractRoom, WorldCoordinate, AbstractPhysicalObject> spawn =
            (ignoredObjectType, args, id, room, pos) => Spawn(
                descriptor,
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
                descriptor.Type,
                spawnerInfo
            });

        RegisteredTypes.Add(descriptor.Type);
        Plugin.Logger?.LogInfo(
            $"Dev Console support enabled: use `spawn {descriptor.Type.value}`.");
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
        // CreatureTemplate.Type 本身已经提供 spawn 命令的生物名称补全。
        // 以后如果某个生物需要额外生成参数，可以在不改这条统一注册路径的前提下单独扩展。
        // CreatureTemplate.Type itself supplies the creature-name autocomplete entry for the spawn command.
        // Creature-specific spawn arguments can be extended later without changing this central registration path.
        return null;
    }

    private static AbstractPhysicalObject Spawn(
        CreatureDescriptor descriptor,
        string[] args,
        EntityID id,
        AbstractRoom room,
        WorldCoordinate pos)
    {
        if (room?.world == null)
        {
            throw new ArgumentException(
                $"Cannot spawn {descriptor.Type.value} without a valid room/world.");
        }

        CreatureTemplate template = StaticWorld.GetCreatureTemplate(descriptor.Type);
        if (template == null)
        {
            throw new InvalidOperationException(
                $"{descriptor.Type.value} CreatureTemplate has not been initialized yet.");
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
            // 分阶段开发生物时，MovementConnection 规则可能还没写完整，导致没有可映射的房间节点。
            // 对一个已经给出 tile 的 Dev Console 生成位置来说，-1 是合法值，也避免在这里凭空发明寻路规则。
            // During staged creature development, MovementConnection rules may not yet provide a mapped room node.
            // -1 is valid for a tile-defined Dev Console spawn and avoids inventing pathing rules here.
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
                // 可选的故事模式自定义标志不应该阻止开发阶段通过 Dev Console 生成生物。
                // Optional story-only custom flags should not prevent a development spawn.
            }
        }

        creature.Move(pos);
        return creature;
    }
}
