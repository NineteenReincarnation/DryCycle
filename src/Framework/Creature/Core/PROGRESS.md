# Creature Core 开发进度

本文件只记录已经真正写入代码并完成的内容。
记录按照文件分类。每一项都写清楚当前代码已经能做什么，不使用“等地方”“之类”“相关功能”这类含糊说法。

## `src/Framework/Creature/Core/CreatureDescriptor.cs`

- 完成了单个生物的核心注册说明书。每一种生物都可以用一份 `CreatureDescriptor` 说明自己的 Rain World 生物类型、所属 Mod、显示名称、文本别名和核心对象创建方式。
- 完成了生物类型登记。Descriptor 保存一个确定的 `CreatureTemplate.Type`，创建 Descriptor 后不能把它换成别的生物类型。
- 完成了所属 Mod 登记。Descriptor 保存 `OwnerId`，注册冲突和工厂报错时可以明确指出是哪一个 Mod 提交的生物。
- 完成了显示名称登记。默认显示名称使用 `CreatureTemplate.Type.value`，开发者可以通过 `SetDisplayName` 或短写法 `Name` 主动修改。
- 完成了单个别名登记。开发者可以通过 `AddAlias` 或短写法 `Alias` 添加一个别名。
- 完成了批量别名登记。开发者可以通过 `AddAliases(IEnumerable<string>)` 或短写法 `WithAliases(params string[])` 一次添加多个别名，避免与只读属性 `Aliases` 重名。
- 完成了别名清理。开发者可以在注册前删除一个别名，也可以清空全部别名。
- 完成了别名规范化。别名会去掉首尾空白；空字符串和纯空白字符串会被拒绝。
- 完成了别名忽略大小写去重。`MossySpider`、`mossyspider` 如果作为别名重复加入，只保留第一次成功加入的文本。
- 完成了别名声明顺序保留。对外读取别名时，顺序与第一次成功登记时一致。
- 完成了别名只读保护。外部代码拿到 `Aliases` 后不能直接修改 Descriptor 内部保存的别名集合。
- 完成了 `CreatureTemplate` 创建方式登记。`TemplateFactory` 是 Descriptor 唯一强制要求的工厂；没有模板创建方式的 Descriptor 不能正式注册。
- 完成了 `CreatureState` 创建方式登记。开发者可以通过 `SetStateFactory` 或 `State` 提供自定义 State；没有填写时由 Registry 保留 Rain World 原本创建的 State。
- 完成了实际 `Creature` 创建方式登记。开发者可以通过 `SetCreatureFactory` 或 `Realized` 提供房间内实际生物的创建方式；没有填写时由 Registry 继续走 Rain World 原本的 Realize 流程。
- 完成了 `AbstractCreatureAI` 创建方式登记。开发者可以通过 `SetAbstractAIFactory` 或 `AbstractAI` 提供自定义抽象 AI；没有填写时保留 Rain World 原本的 AbstractAI。
- 完成了实际 `ArtificialIntelligence` 创建方式登记。开发者可以通过 `SetRealizedAIFactory` 或 `AI` 提供实际 AI；没有填写时由 Registry 继续走 Rain World 原本的 AI 创建流程。
- 完成了四个可选工厂的清除接口。State、实际 Creature、AbstractAI、实际 AI 都可以在注册前撤销自定义工厂并恢复“交给原版处理”的配置状态。
- 完成了注册前验证。生物 Type 必须已经拥有有效 ExtEnum Index，Descriptor 必须已经填写 TemplateFactory。
- 完成了注册后冻结。Descriptor 被 Registry 成功接收后，显示名称、别名和所有工厂都不能继续修改，避免 Registry 已建立索引后注册信息再次变化。
- 完成了短写语法糖。`Name`、`Alias`、`Aliases`、`Template`、`State`、`Realized`、`AbstractAI`、`AI` 都直接调用对应的完整 API，不复制第二套校验逻辑。
- 完成了中英文双语 XML 注释。公开类型、公开属性、公开配置方法和内部冻结、验证方法均采用中文说明在上、英文说明在下的格式。

## `src/Framework/Creature/Core/CreatureRegistry.cs`

- 完成了统一生物登记入口。开发者把配置好的 `CreatureDescriptor` 交给 `CreatureRegistry.Register` 后，Registry 负责保存、建立索引并冻结 Descriptor。
- 完成了按注册顺序保存 Descriptor。`Registered` 返回只读列表，外部代码不能直接增删 Registry 内部内容。
- 完成了按 `CreatureTemplate.Type.value` 查找 Descriptor。查找不要求调用方必须持有注册时的同一个 Type 对象实例。
- 完成了按 ExtEnum Index 建立内部索引，用于注册阶段检测两个不同 Descriptor 是否占用了同一个生物编号。
- 完成了按标准 Type 名称和 Alias 建立忽略大小写的名称索引。名称查找不再逐个遍历所有生物和所有别名。
- 完成了 `TryGet(CreatureTemplate.Type)`、`TryGet(string)`、`Get(CreatureTemplate.Type)`、`Get(string)` 四种查询入口。
- 完成了同一 Descriptor 重复注册的幂等处理。同一个 Descriptor 实例再次交给 Registry 时直接返回，不会重复写索引。
- 完成了 Type 名称冲突检查。两个不同 Descriptor 使用相同 `CreatureTemplate.Type.value` 时直接报错，不允许后注册者静默覆盖前注册者。
- 完成了 Type Index 冲突检查。两个不同 Descriptor 占用同一个 ExtEnum Index 时直接报错。
- 完成了标准名称与 Alias 冲突检查。一个生物的 Type 名称或 Alias 如果已经被另一个 Descriptor 占用，注册直接失败。
- 完成了冲突报错信息。错误中会写出冲突值、已经占用该值的 OwnerId 和 Creature Type、新提交 Descriptor 的 OwnerId 和 Creature Type。
- 完成了注册名称统一预处理。一次注册实际要写入的标准 Type 名称和 Alias 会先整理成同一份名称列表，冲突检查和正式写入使用完全相同的数据，不再各自重新计算一遍。
- 完成了完整注册事务。Type、Index、名称、Alias 全部预检成功后才开始写 Registry；任何列表或索引写入失败时，本次已经写入的内容都会撤回。
- 完成了冻结失败回滚。Descriptor 的 `Freeze()` 被放在本次 Registry 写入的最后一步；如果最终冻结失败，本次写入的 Descriptor 列表、Descriptor 集合、Type 名称索引、Type Index 索引和名称索引都会撤回。
- 完成了失败注册的 Descriptor 状态保护。注册提交失败时不会留下“Descriptor 已经被冻结，但 Registry 只登记了一部分”的半完成状态。
- 完成了注册时机保护。`StaticWorld.InitCustomTemplates` 开始后关闭新增生物注册；开发者如果再提交新的 Descriptor，会收到明确的“注册太晚”错误。
- 完成了 Registry Hook 的幂等启用。`Enable` 重复调用不会重复挂 Hook。
- 完成了 Registry Hook 的卸载。`Disable` 会移除本 Registry 安装的五个 Core Hook，但保留已经登记的 Descriptor，也不会重新开放已经关闭的注册阶段。
- 完成了 `On.StaticWorld.InitCustomTemplates` 接入。Registry 在原版初始化执行后，为每个已登记 Descriptor 调用一次 TemplateFactory，并把结果写入对应的 `StaticWorld.creatureTemplates` 槽位。
- 完成了模板空值检查。TemplateFactory 返回 `null` 时立即报出具体 Creature Type 和 OwnerId。
- 完成了模板 Type 一致性检查。工厂返回的模板必须和 Descriptor 登记的 Type 名称、Type Index 一致。
- 完成了模板数组边界检查。自定义 Type Index 超出 `StaticWorld.creatureTemplates` 长度时直接报告该生物注册过晚以及实际 Index、数组长度。
- 完成了模板 AI 配置检查。模板的 `AI` 为 `false` 时，不允许 Descriptor 同时声明 AbstractAIFactory 或 RealizedAIFactory。
- 完成了无祖先实体创建检查。模板没有 ancestor 时，Descriptor 必须提供实际 Creature 工厂，否则 Registry 会拒绝这个没有任何实际实体创建来源的注册结果。
- 完成了无祖先 AI 创建检查。模板启用 AI、没有 ancestor 时，Descriptor 必须提供实际 AI 工厂，否则 Registry 会拒绝这个没有 AI 实现来源的注册结果。
- 完成了模板安装回滚。一次 StaticWorld 模板安装过程中如果后面的 Descriptor 失败，Registry 会把本轮已经覆盖的 `StaticWorld.creatureTemplates` 槽位恢复成安装前的内容，避免留下半套模板。
- 完成了 `On.AbstractCreature.ctor` 接入。Rain World 原版构造先执行；只有 Descriptor 明确填写 StateFactory 或 AbstractAIFactory 时，Registry 才替换对应对象。
- 完成了 State 原版回退。Descriptor 没有 StateFactory 时，Registry 不覆盖 `AbstractCreature.state`。
- 完成了自定义 State 创建失败包装。StateFactory 抛出的异常会被包装成包含 Creature Type、OwnerId 和 factory 阶段的信息；返回 `null` 会直接报错。
- 完成了 State 归属检查。StateFactory 返回的 `CreatureState.creature` 必须就是当前正在构造的 `AbstractCreature`；返回绑定到其他生物的 State 会在写入 `self.state` 之前直接报错。
- 完成了 AbstractAI 原版回退。Descriptor 没有 AbstractAIFactory 时，Registry 不覆盖原版 `AbstractCreature.abstractAI`。
- 完成了 AbstractAI 归属检查。AbstractAIFactory 返回对象的 `parent` 必须就是当前 `AbstractCreature`，`world` 必须就是当前生物所在的 `World`；任意一项错误都会在替换原版 AbstractAI 之前报错。
- 完成了 AbstractAI 的 Den 信息保留。Rain World 原版构造阶段如果已经给默认 AbstractAI 写入 `privDenPos`，替换成自定义 AbstractAI 时会把这个巢穴位置迁过去。
- 完成了 `On.AbstractCreature.Realize` 接入。Descriptor 没有实际 Creature 工厂时完整调用原版 Realize；有实际 Creature 工厂时由 Registry 创建并赋给 `realizedObject`。
- 完成了实际 Creature 工厂空值和异常检查。返回 `null` 或抛异常时会明确指出 Creature Type、OwnerId 和 realized creature factory 阶段。
- 完成了实际 Creature 归属检查。CreatureFactory 返回对象的 `Creature.abstractCreature` 必须就是当前正在 Realize 的 `AbstractCreature`；如果工厂误用了另一只生物，Registry 会在写入 `realizedObject` 之前报错。
- 完成了自定义 Realize 后的原版流程保留。Registry 会继续执行原版针对已经存在 realized object 的检查，并保留 MSC 自定义 Realize 产生的挑战模式标记处理。
- 完成了自定义 Realize 后的 AI 初始化。生物模板启用 AI 且存在 AbstractAI 时，Registry 会调用 `InitiateAI`。
- 完成了自定义 Realize 后的 stuck object 实体化。与该 AbstractCreature 连接的 stuck object 两端如果尚未实体化，Registry 会继续调用它们的 `Realize`，保留 Rain World 原本的连接对象行为。
- 完成了 `On.AbstractCreature.InitiateAI` 接入。Descriptor 没有实际 AI 工厂时调用原版 AI 创建流程；声明实际 AI 工厂时由 Registry 创建并写入 `abstractAI.RealAI`。
- 完成了实际 AI 重复创建保护。`abstractAI.RealAI` 已经存在时不会再次调用 RealizedAIFactory。
- 完成了实际 AI 前置条件检查。声明实际 AI 工厂但当前 `AbstractCreature` 没有 AbstractAI 时直接报错。
- 完成了实际 AI 工厂空值和异常检查。返回 `null` 或抛异常时会明确指出 Creature Type、OwnerId 和 realized AI factory 阶段。
- 完成了实际 AI 归属检查。RealizedAIFactory 返回对象的 `ArtificialIntelligence.creature` 必须就是当前正在初始化 AI 的 `AbstractCreature`；绑定到其他生物的 AI 不会被写入 `abstractAI.RealAI`。
- 完成了 Factory 错误对象提前拦截。State、AbstractAI、实际 Creature、实际 AI 的归属错误都会在对象真正写回 Rain World 状态之前被拒绝，避免错误对象进入后续游戏更新流程后才暴露问题。
- 完成了 `On.WorldLoader.CreatureTypeFromString` 接入。当前实现会先查询 Registry 的标准名称和 Alias；如果 Registry 没有匹配项，再调用 Rain World 原本的名称解析。
- 完成了自定义名称解析。标准 Type 名称和 Alias 都通过已经建立的名称索引查找，不需要逐个扫描 Descriptor。
- 完成了中英文双语 XML 注释。公开 Registry API 和本次新增的内部事务、Factory 归属说明采用中文说明在上、英文说明在下的格式。
- Registry 当前只处理 CreatureDescriptor 登记、模板创建、State 创建、实际 Creature 创建、AbstractAI 创建、实际 AI 创建和 world 文件名称解析。它没有接管 Creature relationship、资源加载、Sandbox、DevTools、图标、Expedition 和生物行为逻辑。

## `src/Creatures/MantleCrab/MantleCrabDefinition.cs`

- 完成了 MantleCrab 从旧生物 Definition 注册方式到 `CreatureDescriptor + CreatureRegistry` 的迁移。
- MantleCrab 使用 `MantleCrabEnums.Type` 作为 Type，使用 `DryCycle.Plugin.ModId` 作为 OwnerId，显示名称登记为 `Mantle Crab`。
- MantleCrab 的 CreatureTemplate 创建方式已经通过 `.Template(CreateTemplate)` 交给新 Descriptor。
- MantleCrab 原有的 `HealthState` 创建行为已经通过 `.State(CreateState)` 保留下来，迁移后仍为每个 MantleCrab AbstractCreature 创建 `HealthState`。
- MantleCrab 的实际实体创建已经通过 `.Realized(CreateRealizedCreature)` 交给新 Descriptor。
- MantleCrab 实体创建时仍会把 `MaintainRigidShell` 登记到 `WalkableDynamicSurfaceRuntime.RegisterPostPhysicsFinalizer`，保持硬壳与动态可行走表面的原有衔接。
- MantleCrab 当前 `CreatureTemplate.AI` 为 `false`，没有向 Core Registry 登记 AbstractAIFactory 或 RealizedAIFactory。
- MantleCrab 的 Shader 生物资源加载和 `MantleCrabMaterialCache.Enable()` 仍由 `MantleCrabDefinition.LoadResources` 自己负责，没有塞进 Creature Core Registry。

## `src/Creatures/MossySpider/MossySpiderDefinition.cs`

- 完成了 MossySpider 从旧生物 Definition 注册方式到 `CreatureDescriptor + CreatureRegistry` 的迁移。
- MossySpider 使用 `MossySpiderEnums.Type` 作为 Type，使用 `DryCycle.Plugin.ModId` 作为 OwnerId，显示名称登记为 `Mossy Spider`。
- MossySpider 保留文本别名 `mossy spider`，该别名已经通过 `.Alias("mossy spider")` 登记到新 Registry。
- MossySpider 的 CreatureTemplate 创建方式已经通过 `.Template(CreateTemplate)` 交给新 Descriptor，原有 Tile Accessibility、MovementConnection、AI-map、抗伤、抗眩晕、房间迁移、水中移动、体型、肉量和 shortcut 参数仍在原模板构建函数中保留。
- MossySpider 的实际实体创建已经通过 `.Realized(CreateRealizedCreature)` 交给新 Descriptor。
- MossySpider 的 `MossySpiderAbstractAI` 创建已经通过 `.AbstractAI(CreateAbstractAI)` 交给新 Descriptor。
- MossySpider 的 `MossySpiderAI` 创建已经通过 `.AI(CreateRealizedAI)` 交给新 Descriptor。
- MossySpider 没有声明自定义 StateFactory，因此 Registry 保留 Rain World 根据模板构造出来的原始 State，不额外覆盖。

## `src/Creatures/DesertBatfly/Core/DB_Definition.cs`

- 完成了 DesertBatfly 从旧生物 Definition 注册方式到 `CreatureDescriptor + CreatureRegistry` 的迁移。
- DesertBatfly 继续使用字符串 `DesertBatfly` 创建自己的 `CreatureTemplate.Type`，使用 `DryCycle.Plugin.ModId` 作为 OwnerId，显示名称登记为 `Desert Batfly`。
- DesertBatfly 的 CreatureTemplate 创建方式已经通过 `.Template(CreateTemplate)` 交给新 Descriptor。
- DesertBatfly 的 `DB_State` 创建已经通过 `.State(CreateState)` 交给新 Descriptor。
- DesertBatfly 的 `DB_Creature` 创建已经通过 `.Realized(CreateRealizedCreature)` 交给新 Descriptor。
- DesertBatfly 继续以原版 `Fly` 作为模板 ancestor，并保留 `preBakedPathingAncestor`、身体大小、抓取数、伤害抗性、眩晕抗性、快速死亡阈值和 shortcut 颜色设置。
- DesertBatfly 的模板继续设置 `AI = false`。它没有向 Core Registry 登记 AbstractAIFactory 或 RealizedAIFactory，因此不会把 Fly 使用的非 `ArtificialIntelligence` 控制方式强行改造成 Framework AI。

## `src/Creatures/DesertBatfly/Integration/DB_Relationships.cs`

- 完成了 DesertBatfly 生态关系从旧 Definition 生命周期中的拆分。关系初始化没有进入 Creature Core Registry，而是由 `DB_Relationships` 单独监听 `StaticWorld.InitStaticWorld`。
- DesertBatfly 会先复制原版 Fly 对其他生物的关系，并把其他生物针对 Fly 的关系复制到 DesertBatfly。
- 拾荒者 TopAncestor 会把 DesertBatfly 视为可攻击目标，关系强度使用 `DB_Tuning.ScavengerHostility`。
- Watcher 启用且 Peach Lizard 可用时，Peach Lizard 对 DesertBatfly 设置 `Eats, 0.32f`，DesertBatfly 对 Peach Lizard 设置 `Afraid, 0.90f`。
- DesertBatfly 对 Slugcat、DesertBatfly 自身和原版 Fly 的关系明确设置为 `Ignores, 0f`。
- `Enable` 和 `Disable` 都具有幂等保护，不会重复挂载或重复卸载 `StaticWorld.InitStaticWorld` Hook。

## `src/Plugin.cs`

- 完成了三个现有自定义生物的项目入口迁移。首次启用内容时分别调用 `MossySpiderDefinition.Register()`、`MantleCrabDefinition.Register()`、`DB_Definition.Register()`，不再实例化旧 `CreatureDefinition` 子类。
- 完成了新 Creature Core Registry 的运行时启用。`OnEnable` 调用 `CreatureCoreRegistry.Enable()` 安装新 Registry 的五个 Core Hook。
- 完成了新 Creature Core Registry 的运行时卸载。`OnDisable` 调用 `CreatureCoreRegistry.Disable()` 移除新 Registry 的五个 Core Hook。
- DesertBatfly 的生态关系通过 `DB_Relationships.Enable()` 和 `DB_Relationships.Disable()` 单独管理，不让 Creature Core Registry 承担生态关系职责。
- MantleCrab 的资源加载在 `RainWorld_OnModsInit` 中单独调用 `MantleCrabDefinition.LoadResources(self)`，不让 Creature Core Registry 承担资源加载职责。
- `DryCycleContent` 仍然负责项目里原有的物品内容和动态可行走表面运行时；三个生物不再通过 `DryCycleContent.Register(CreatureDefinition)` 注册。

## `src/Registration/DryCycleContent.cs`

- 已经移除生物 Definition 的登记职责。
- 当前只提供 `Register(ItemDefinition)` 给物品注册使用。
- `Enable` 当前只启用 `ItemRegistry` 和 `WalkableDynamicSurfaceRuntime`。
- `Disable` 当前只关闭 `WalkableDynamicSurfaceRuntime` 和 `ItemRegistry`，并重置物品资源加载状态。
- `LoadResources` 当前只遍历 `ItemRegistry.Registered` 并调用每个 `ItemDefinition.LoadResources`。
- 文件注释已经明确说明生物注册迁移到了 `DryCycle.Framework.Creature.Core.CreatureRegistry`。

## 已移除的旧生物注册文件

- `src/Registration/CreatureDefinition.cs` 已经从当前项目树中移除，三个现有自定义生物不再依赖旧的继承式 CreatureDefinition。
- `src/Registration/CreatureRegistry.cs` 已经从当前项目树中移除，三个现有自定义生物不再通过旧 Registry 创建模板、State、实际 Creature、AbstractAI 和实际 AI。
