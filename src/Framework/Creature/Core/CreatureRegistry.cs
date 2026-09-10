using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace DryCycle.Framework.Creature.Core;

/// <summary>
/// 统一登记 CreatureDescriptor，并把 Rain World 的核心生物生命周期转接到对应的描述对象。
/// 本类只负责生物模板、状态、实体、抽象 AI、实际 AI 和 world.txt 名称解析这些核心注册工作。
/// 它不负责关系、沙盒、DevTools、资源、图标、生态或其他具体功能层。
///
/// Central registry for CreatureDescriptor instances and the Rain World core creature lifecycle.
/// This class only handles template, state, realized creature, abstract AI, realized AI,
/// and world-file name resolution. Feature-specific systems belong elsewhere.
/// </summary>
public static class CreatureRegistry
{
    private static readonly List<CreatureDescriptor> _descriptors = new();
    private static readonly ReadOnlyCollection<CreatureDescriptor> _readOnlyDescriptors = _descriptors.AsReadOnly();
    private static readonly HashSet<CreatureDescriptor> _descriptorSet = new();
    private static readonly Dictionary<string, CreatureDescriptor> _byTypeValue = new(StringComparer.Ordinal);
    private static readonly Dictionary<int, CreatureDescriptor> _byTypeIndex = new();
    private static readonly Dictionary<string, CreatureDescriptor> _byName = new(StringComparer.OrdinalIgnoreCase);

    private static bool _enabled;
    private static bool _registrationClosed;

    /// <summary>
    /// 按注册顺序返回所有已经登记的生物说明书。返回值只能读取，不能改 Registry 内部列表。
    ///
    /// Returns all registered creature descriptors in registration order.
    /// The returned list cannot mutate the registry.
    /// </summary>
    public static IReadOnlyList<CreatureDescriptor> Registered => _readOnlyDescriptors;

    /// <summary>
    /// 当前是否已经挂上 Rain World 的核心 Hook。
    ///
    /// True when the registry's Rain World core hooks are currently enabled.
    /// </summary>
    public static bool IsEnabled => _enabled;

    /// <summary>
    /// 当前是否还允许登记新的生物。
    /// StaticWorld 一旦开始建立生物模板数组，就会关闭登记，避免 ExtEnum 加得太晚导致数组越界。
    ///
    /// True while new creature descriptors may still be registered.
    /// Registration closes once StaticWorld starts building creature templates.
    /// </summary>
    public static bool IsRegistrationOpen => !_registrationClosed && StaticWorld.creatureTemplates == null;

    /// <summary>
    /// 登记一份生物说明书。
    /// 同一个对象重复登记属于无害操作；不同对象如果撞了 Type、Type.Index 或名称/别名，会直接报错而不是静默覆盖。
    /// 所有冲突检查通过以后才会真正写入 Registry，并冻结 Descriptor。
    ///
    /// Registers one creature descriptor.
    /// Re-registering the same instance is harmless; conflicting type, type index, canonical name,
    /// or alias registrations throw instead of silently overwriting an existing creature.
    /// </summary>
    public static CreatureDescriptor Register(CreatureDescriptor descriptor)
    {
        if (descriptor == null)
        {
            throw new ArgumentNullException(nameof(descriptor));
        }

        if (_descriptorSet.Contains(descriptor))
        {
            return descriptor;
        }

        if (_registrationClosed || StaticWorld.creatureTemplates != null)
        {
            _registrationClosed = true;
            throw new InvalidOperationException(
                $"Creature '{descriptor.Type?.value ?? "<null>"}' owned by '{descriptor.OwnerId}' was registered too late. " +
                "Register custom creatures before StaticWorld starts creating creature templates.");
        }

        if (descriptor.IsFrozen)
        {
            throw new InvalidOperationException(
                $"Creature descriptor '{descriptor.Type.value}' owned by '{descriptor.OwnerId}' is already frozen but is not registered in this registry.");
        }

        descriptor.ValidateForRegistration();
        ValidateRegistrationCollisions(descriptor);

        descriptor.Freeze();

        _descriptors.Add(descriptor);
        _descriptorSet.Add(descriptor);
        _byTypeValue.Add(descriptor.Type.value, descriptor);
        _byTypeIndex.Add(descriptor.Type.Index, descriptor);

        AddNameIndex(descriptor.Type.value, descriptor);
        for (int i = 0; i < descriptor.Aliases.Count; i++)
        {
            string alias = descriptor.Aliases[i];
            if (!StringComparer.OrdinalIgnoreCase.Equals(alias, descriptor.Type.value.Trim()))
            {
                AddNameIndex(alias, descriptor);
            }
        }

        return descriptor;
    }

    /// <summary>
    /// 按 CreatureTemplate.Type 查找已经登记的生物说明书。
    /// 内部按稳定的 Type.value 查找，不依赖调用方必须拿到完全相同的 Type 对象实例。
    ///
    /// Looks up a registered descriptor by CreatureTemplate.Type using its stable type value.
    /// </summary>
    public static bool TryGet(CreatureTemplate.Type type, out CreatureDescriptor descriptor)
    {
        descriptor = null;
        return type != null &&
               !string.IsNullOrWhiteSpace(type.value) &&
               _byTypeValue.TryGetValue(type.value, out descriptor);
    }

    /// <summary>
    /// 按标准 Type 名称或已登记别名查找生物说明书；忽略首尾空格和大小写。
    ///
    /// Looks up a descriptor by canonical type name or registered alias, ignoring case and surrounding whitespace.
    /// </summary>
    public static bool TryGet(string typeNameOrAlias, out CreatureDescriptor descriptor)
    {
        descriptor = null;
        if (string.IsNullOrWhiteSpace(typeNameOrAlias))
        {
            return false;
        }

        return _byName.TryGetValue(typeNameOrAlias.Trim(), out descriptor);
    }

    /// <summary>
    /// 按 CreatureTemplate.Type 获取生物说明书；找不到时直接抛出带明确信息的异常。
    ///
    /// Gets a descriptor by CreatureTemplate.Type and throws a descriptive exception when it is not registered.
    /// </summary>
    public static CreatureDescriptor Get(CreatureTemplate.Type type)
    {
        if (TryGet(type, out CreatureDescriptor descriptor))
        {
            return descriptor;
        }

        throw new KeyNotFoundException(
            $"No creature descriptor is registered for type '{type?.value ?? "<null>"}'.");
    }

    /// <summary>
    /// 按标准 Type 名称或别名获取生物说明书；找不到时直接抛出带明确信息的异常。
    ///
    /// Gets a descriptor by canonical type name or alias and throws a descriptive exception when it is not registered.
    /// </summary>
    public static CreatureDescriptor Get(string typeNameOrAlias)
    {
        if (TryGet(typeNameOrAlias, out CreatureDescriptor descriptor))
        {
            return descriptor;
        }

        throw new KeyNotFoundException(
            $"No creature descriptor is registered for name or alias '{typeNameOrAlias ?? "<null>"}'.");
    }

    /// <summary>
    /// 挂上 Registry 需要的 Rain World 核心 Hook。重复调用不会重复挂 Hook。
    ///
    /// Enables the Rain World core hooks required by this registry. Repeated calls are harmless.
    /// </summary>
    public static void Enable()
    {
        if (_enabled)
        {
            return;
        }

        On.StaticWorld.InitCustomTemplates += StaticWorld_InitCustomTemplates;
        On.AbstractCreature.ctor += AbstractCreature_ctor;
        On.AbstractCreature.Realize += AbstractCreature_Realize;
        On.AbstractCreature.InitiateAI += AbstractCreature_InitiateAI;
        On.WorldLoader.CreatureTypeFromString += WorldLoader_CreatureTypeFromString;
        _enabled = true;
    }

    /// <summary>
    /// 卸下 Registry 的 Rain World 核心 Hook。
    /// 已登记的 Descriptor 不会被删除，也不会重新开放晚期注册；之后可以再次 Enable。
    ///
    /// Disables the registry's Rain World core hooks.
    /// Registered descriptors are retained and late registration is not reopened.
    /// </summary>
    public static void Disable()
    {
        if (!_enabled)
        {
            return;
        }

        On.StaticWorld.InitCustomTemplates -= StaticWorld_InitCustomTemplates;
        On.AbstractCreature.ctor -= AbstractCreature_ctor;
        On.AbstractCreature.Realize -= AbstractCreature_Realize;
        On.AbstractCreature.InitiateAI -= AbstractCreature_InitiateAI;
        On.WorldLoader.CreatureTypeFromString -= WorldLoader_CreatureTypeFromString;
        _enabled = false;
    }

    private static void ValidateRegistrationCollisions(CreatureDescriptor descriptor)
    {
        if (_byTypeValue.TryGetValue(descriptor.Type.value, out CreatureDescriptor typeOwner))
        {
            throw RegistrationCollision(
                "creature type",
                descriptor.Type.value,
                typeOwner,
                descriptor);
        }

        if (_byTypeIndex.TryGetValue(descriptor.Type.Index, out CreatureDescriptor indexOwner))
        {
            throw RegistrationCollision(
                "creature type index",
                descriptor.Type.Index.ToString(),
                indexOwner,
                descriptor);
        }

        HashSet<string> incomingNames = new(StringComparer.OrdinalIgnoreCase)
        {
            descriptor.Type.value.Trim()
        };

        for (int i = 0; i < descriptor.Aliases.Count; i++)
        {
            incomingNames.Add(descriptor.Aliases[i]);
        }

        foreach (string name in incomingNames)
        {
            if (_byName.TryGetValue(name, out CreatureDescriptor nameOwner))
            {
                throw RegistrationCollision(
                    "creature name or alias",
                    name,
                    nameOwner,
                    descriptor);
            }
        }
    }

    private static InvalidOperationException RegistrationCollision(
        string kind,
        string value,
        CreatureDescriptor existing,
        CreatureDescriptor incoming)
    {
        return new InvalidOperationException(
            $"Creature registration collision for {kind} '{value}'. " +
            $"Existing owner: '{existing.OwnerId}' ({existing.Type.value}). " +
            $"Incoming owner: '{incoming.OwnerId}' ({incoming.Type.value}).");
    }

    private static void AddNameIndex(string name, CreatureDescriptor descriptor)
    {
        _byName.Add(name.Trim(), descriptor);
    }

    private static void StaticWorld_InitCustomTemplates(On.StaticWorld.orig_InitCustomTemplates orig)
    {
        _registrationClosed = true;
        orig();

        if (_descriptors.Count == 0)
        {
            return;
        }

        Dictionary<int, CreatureTemplate> previousTemplates = new();

        try
        {
            for (int i = 0; i < _descriptors.Count; i++)
            {
                CreatureDescriptor descriptor = _descriptors[i];
                CreatureTemplate template = BuildAndValidateTemplate(descriptor);
                int index = descriptor.Type.Index;

                previousTemplates.Add(index, StaticWorld.creatureTemplates[index]);
                StaticWorld.creatureTemplates[index] = template;
            }
        }
        catch
        {
            // 如果后面的模板创建失败，把前面已经写入的槽位全部还原，避免留下“注册一半”的 StaticWorld。
            // If a later template fails, restore every slot already changed so StaticWorld is not left half-registered.
            foreach (KeyValuePair<int, CreatureTemplate> pair in previousTemplates)
            {
                StaticWorld.creatureTemplates[pair.Key] = pair.Value;
            }

            throw;
        }
    }

    private static CreatureTemplate BuildAndValidateTemplate(CreatureDescriptor descriptor)
    {
        CreatureTemplate template;

        try
        {
            template = descriptor.TemplateFactory();
        }
        catch (Exception exception)
        {
            throw new InvalidOperationException(
                $"Creature template factory failed for '{descriptor.Type.value}' owned by '{descriptor.OwnerId}'.",
                exception);
        }

        if (template == null)
        {
            throw new InvalidOperationException(
                $"Creature template factory returned null for '{descriptor.Type.value}' owned by '{descriptor.OwnerId}'.");
        }

        if (template.type == null ||
            template.type.Index != descriptor.Type.Index ||
            !StringComparer.Ordinal.Equals(template.type.value, descriptor.Type.value))
        {
            throw new InvalidOperationException(
                $"Creature template factory for '{descriptor.Type.value}' owned by '{descriptor.OwnerId}' returned a template with the wrong type.");
        }

        if (template.type.Index < 0 || template.type.Index >= StaticWorld.creatureTemplates.Length)
        {
            throw new InvalidOperationException(
                $"Creature type '{descriptor.Type.value}' owned by '{descriptor.OwnerId}' was registered too late for StaticWorld. " +
                $"Type index {template.type.Index} is outside the template array length {StaticWorld.creatureTemplates.Length}.");
        }

        if (!template.AI && (descriptor.AbstractAIFactory != null || descriptor.RealizedAIFactory != null))
        {
            throw new InvalidOperationException(
                $"Creature '{descriptor.Type.value}' owned by '{descriptor.OwnerId}' declares an AI factory, but its CreatureTemplate.AI is false.");
        }

        if (template.ancestor == null && descriptor.CreatureFactory == null)
        {
            throw new InvalidOperationException(
                $"Creature '{descriptor.Type.value}' owned by '{descriptor.OwnerId}' has no ancestor and no realized-creature factory. " +
                "Rain World would have no way to create its room entity.");
        }

        if (template.AI && template.ancestor == null && descriptor.RealizedAIFactory == null)
        {
            throw new InvalidOperationException(
                $"Creature '{descriptor.Type.value}' owned by '{descriptor.OwnerId}' has AI enabled, no ancestor, and no realized-AI factory. " +
                "Rain World would have no AI implementation to create for this new top-level creature type.");
        }

        return template;
    }

    private static void AbstractCreature_ctor(
        On.AbstractCreature.orig_ctor orig,
        AbstractCreature self,
        World world,
        CreatureTemplate template,
        global::Creature realizedCreature,
        WorldCoordinate position,
        EntityID id)
    {
        orig(self, world, template, realizedCreature, position, id);

        if (world == null || template?.type == null || !TryGet(template.type, out CreatureDescriptor descriptor))
        {
            return;
        }

        if (descriptor.StateFactory != null)
        {
            CreatureState state = InvokeFactory(
                descriptor,
                "state",
                () => descriptor.StateFactory(self));

            if (state == null)
            {
                throw FactoryReturnedNull(descriptor, "state");
            }

            self.state = state;
        }

        if (descriptor.AbstractAIFactory != null)
        {
            if (!template.AI)
            {
                throw new InvalidOperationException(
                    $"Creature '{descriptor.Type.value}' owned by '{descriptor.OwnerId}' requested a custom AbstractCreatureAI while CreatureTemplate.AI is false.");
            }

            AbstractCreatureAI originalAI = self.abstractAI;
            AbstractCreatureAI customAI = InvokeFactory(
                descriptor,
                "abstract AI",
                () => descriptor.AbstractAIFactory(self));

            if (customAI == null)
            {
                throw FactoryReturnedNull(descriptor, "abstract AI");
            }

            // Rain World 会在默认 AbstractCreatureAI 构造完成后再把出生巢穴位置写进去，替换 AI 时必须把这份信息带过去。
            // Rain World may write the den position after constructing its default AbstractCreatureAI, so preserve it when replacing the AI object.
            if (originalAI != null && originalAI.privDenPos.HasValue)
            {
                customAI.privDenPos = originalAI.privDenPos;
            }

            self.abstractAI = customAI;
        }
    }

    private static void AbstractCreature_Realize(
        On.AbstractCreature.orig_Realize orig,
        AbstractCreature self)
    {
        if (!TryGet(self.creatureTemplate?.type, out CreatureDescriptor descriptor) ||
            descriptor.CreatureFactory == null)
        {
            orig(self);
            return;
        }

        if (self.Room == null || self.realizedCreature != null)
        {
            orig(self);
            return;
        }

        global::Creature creature = InvokeFactory(
            descriptor,
            "realized creature",
            () => descriptor.CreatureFactory(self));

        if (creature == null)
        {
            throw FactoryReturnedNull(descriptor, "realized creature");
        }

        self.realizedObject = creature;

        // 让原版继续执行“发现已经 Realize 后提前返回”之前的检查，例如 MSC 的 Void Sea Arena 标记。
        // Let vanilla keep the checks that run before its early return for an already-realized creature.
        orig(self);

        // MSCRealizeCustom 会先处理挑战模式标记，再发现实体已经存在并返回，因此这里调用不会覆盖我们的自定义实体。
        // MSCRealizeCustom handles challenge-mode flags before noticing the existing entity, so this preserves that vanilla side effect safely.
        if (ModManager.DLCShared)
        {
            self.MSCRealizeCustom();
        }

        // 原版 Realize 创建实体后会无条件调用 InitiateAI；这里保持同样语义。
        // Vanilla Realize calls InitiateAI unconditionally after creating the room entity; preserve that behavior here.
        self.InitiateAI();

        for (int i = 0; i < self.stuckObjects.Count; i++)
        {
            if (self.stuckObjects[i].A.realizedObject == null)
            {
                self.stuckObjects[i].A.Realize();
            }

            if (self.stuckObjects[i].B.realizedObject == null)
            {
                self.stuckObjects[i].B.Realize();
            }
        }
    }

    private static void AbstractCreature_InitiateAI(
        On.AbstractCreature.orig_InitiateAI orig,
        AbstractCreature self)
    {
        if (!TryGet(self.creatureTemplate?.type, out CreatureDescriptor descriptor) ||
            descriptor.RealizedAIFactory == null)
        {
            orig(self);
            return;
        }

        if (!self.creatureTemplate.AI)
        {
            throw new InvalidOperationException(
                $"Creature '{descriptor.Type.value}' owned by '{descriptor.OwnerId}' requested a custom realized AI while CreatureTemplate.AI is false.");
        }

        if (self.abstractAI == null)
        {
            throw new InvalidOperationException(
                $"Creature '{descriptor.Type.value}' owned by '{descriptor.OwnerId}' cannot create its realized AI because AbstractCreatureAI is null.");
        }

        // InitiateAI 可能被重复调用；已有实际 AI 时不再重复调用开发者提供的 Factory。
        // InitiateAI may be reached more than once; do not invoke the user factory again while a realized AI already exists.
        if (self.abstractAI.RealAI != null)
        {
            return;
        }

        ArtificialIntelligence ai = InvokeFactory(
            descriptor,
            "realized AI",
            () => descriptor.RealizedAIFactory(self));

        if (ai == null)
        {
            throw FactoryReturnedNull(descriptor, "realized AI");
        }

        self.abstractAI.RealAI = ai;
    }

    private static CreatureTemplate.Type WorldLoader_CreatureTypeFromString(
        On.WorldLoader.orig_CreatureTypeFromString orig,
        string value)
    {
        if (TryGet(value, out CreatureDescriptor descriptor))
        {
            return descriptor.Type;
        }

        return orig(value);
    }

    private static T InvokeFactory<T>(
        CreatureDescriptor descriptor,
        string factoryName,
        Func<T> factory)
    {
        try
        {
            return factory();
        }
        catch (Exception exception)
        {
            throw new InvalidOperationException(
                $"Creature {factoryName} factory failed for '{descriptor.Type.value}' owned by '{descriptor.OwnerId}'.",
                exception);
        }
    }

    private static InvalidOperationException FactoryReturnedNull(
        CreatureDescriptor descriptor,
        string factoryName)
    {
        return new InvalidOperationException(
            $"Creature {factoryName} factory returned null for '{descriptor.Type.value}' owned by '{descriptor.OwnerId}'.");
    }
}
