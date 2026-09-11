# Creature Core 开发进度

本文件只记录已经真正写入代码并完成的内容。
记录按照文件分类。每一项都写清楚当前代码已经能做什么，不使用“等地方”“之类”“相关功能”这类含糊说法。

## `src/Framework/Creature/Core/CreatureDescriptor.cs`

- 完成了单个生物的核心注册说明书。每一种生物都可以用一份 `CreatureDescriptor` 说明自己的 Rain World 生物类型、所属 Mod、显示名称、文本别名和核心对象创建方式。
- 完成了生物类型登记。Descriptor 保存一个确定的 `CreatureTemplate.Type`，创建 Descriptor 后不能把它换成别的生物类型。
- 完成了所属 Mod 登记。Descriptor 保存 `OwnerId`，注册冲突和工厂报错时可以明确指出是哪一个 Mod 提交的生物。
- 完成了显示名称登记。默认显示名称使用 `CreatureTemplate.Type.value`，开发者可以通过 `SetDisplayName` 或短写法 `Name` 主动修改。
- 完成了单个别名登记。开发者可以通过 `AddAlias` 或短写法 `Alias` 添加一个别名。
- 完成了批量别名登记。开发者可以通过 `AddAliases(IEnumerable<string>)` 或短写法 `WithAliases(params string[])` 一次添加多个别名。
- 完成了别名清理。开发者可以在注册前删除一个别名，也可以清空全部别名。
- 完成了别名规范化。别名会去掉首尾空白；空字符串和纯空白字符串会被拒绝。
- 完成了别名忽略大小写去重。大小写不同但文字相同的别名只保留第一次成功加入的文本。
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
- 完成了短写语法糖。`Name`、`Alias`、`WithAliases`、`Template`、`State`、`Realized`、`AbstractAI`、`AI` 都直接调用对应的完整 API，不复制第二套校验逻辑。
- 完成了中英文双语 XML 注释。公开类型、公开属性、公开配置方法和内部冻结、验证方法均采用中文说明在上、英文说明在下的格式。

## `src/Framework/Creature/Core/CreatureRegistry.cs`

- 完成了统一生物登记入口。开发者把配置好的 `CreatureDescriptor` 交给 `CreatureRegistry.Register` 后，Registry 负责保存、建立索引并冻结 Descriptor。
- 完成了按注册顺序保存 Descriptor。`Registered` 返回只读列表，外部代码不能直接增删 Registry 内部内容。
- 完成了按 `CreatureTemplate.Type.value` 查找 Descriptor。查找不要求调用方必须持有注册时的同一个 Type 对象实例。
- 完成了按 ExtEnum Index 建立内部索引，用于注册阶段检测两个不同 Descriptor 是否占用了同一个生物编号。
- 完成了按标准 Type 名称和 Alias 建立忽略大小写的名称索引。名称查找不需要逐个遍历所有 Descriptor。
- 完成了 `TryGet(CreatureTemplate.Type)`、`TryGet(string)`、`Get(CreatureTemplate.Type)`、`Get(string)` 四种查询入口。
- 完成了同一 Descriptor 重复注册的幂等处理。同一个 Descriptor 实例再次交给 Registry 时直接返回，不会重复写索引。
- 完成了 Type 名称、Type Index、标准名称和 Alias 冲突检查。不同 Descriptor 占用同一个值时直接报错，不允许后注册者静默覆盖前注册者。
- 完成了冲突报错信息。错误中会写出冲突值、已经占用该值的 OwnerId 和 Creature Type、新提交 Descriptor 的 OwnerId 和 Creature Type。
- 完成了注册名称统一预处理。冲突检查和正式提交使用同一份标准 Type 名称与 Alias 列表。
- 完成了完整注册事务。Type、Index、名称、Alias 全部预检成功后才开始写 Registry；任何列表、集合、索引写入或最终冻结失败时，本次已经写入的内容都会撤回。
- 完成了失败注册的 Descriptor 状态保护。注册提交失败时不会留下“Descriptor 已冻结但 Registry 只登记了一部分”的半完成状态。
- 完成了注册时机保护。`StaticWorld.InitCustomTemplates` 开始后关闭新增生物注册；开发者如果再提交新的 Descriptor，会收到明确的“注册太晚”错误。
- 完成了 Registry Hook 的幂等启用和卸载。`Enable` 不会重复挂 Hook；`Disable` 会移除本 Registry 安装的五个 Core Hook，保留已登记 Descriptor，并且不会重新开放已经关闭的注册阶段。
- 完成了 `On.StaticWorld.InitCustomTemplates` 接入。Registry 在原版初始化执行后，为每个已登记 Descriptor 调用一次 TemplateFactory，并把结果写入对应的 `StaticWorld.creatureTemplates` 槽位。
- 完成了模板空值、Type 一致性、模板数组边界、AI 配置、无祖先实体创建来源和无祖先 AI 创建来源检查。
- 完成了模板安装回滚。一次 StaticWorld 模板安装过程中如果后面的 Descriptor 失败，本轮已经覆盖的 `StaticWorld.creatureTemplates` 槽位会恢复成安装前内容。
- 完成了 `On.AbstractCreature.ctor` 接入。Rain World 原版构造先执行；只有 Descriptor 明确填写 StateFactory 或 AbstractAIFactory 时，Registry 才替换对应对象。
- 完成了 State 原版回退、Factory 异常包装、空值检查和归属检查。自定义 State 的 `CreatureState.creature` 必须就是当前 AbstractCreature。
- 完成了 AbstractAI 原版回退、Factory 异常包装、空值检查和归属检查。自定义 AbstractAI 的 `parent` 必须指向当前 AbstractCreature，`world` 必须指向当前 World。
- 完成了 AbstractAI 的 Den 信息保留。Rain World 原版构造阶段已经写入默认 AbstractAI 的 `privDenPos` 会在替换时迁移到自定义 AbstractAI。
- 完成了 `On.AbstractCreature.Realize` 接入。Descriptor 没有实际 Creature 工厂时完整调用原版 Realize；有实际 Creature 工厂时由 Registry 创建实体并写入 `realizedObject`。
- 完成了实际 Creature Factory 异常包装、空值检查和归属检查。返回对象的 `Creature.abstractCreature` 必须就是当前正在 Realize 的 AbstractCreature。
- 完成了自定义 Realize 后的原版前置检查、MSC 挑战模式标记、AI 初始化和 stuck object 两端实体化流程保留。
- 完成了 `On.AbstractCreature.InitiateAI` 接入。Descriptor 没有实际 AI 工厂时调用原版 AI 创建流程；声明工厂时由 Registry 创建并写入 `abstractAI.RealAI`。
- 完成了实际 AI 重复创建保护、前置条件检查、Factory 异常包装、空值检查和归属检查。返回对象的 `ArtificialIntelligence.creature` 必须就是当前 AbstractCreature。
- 完成了 Factory 错误对象提前拦截。State、AbstractAI、实际 Creature、实际 AI 的归属错误都会在对象真正写回 Rain World 状态之前被拒绝。
- 完成了 `On.WorldLoader.CreatureTypeFromString` 接入。当前实现先查询 Registry 的标准名称和 Alias；Registry 没有匹配项时再调用 Rain World 原本的名称解析。
- Registry 当前只处理 CreatureDescriptor 登记、模板创建、State 创建、实际 Creature 创建、AbstractAI 创建、实际 AI 创建和 world 文件名称解析。它没有接管 Creature relationship、资源加载、Sandbox、DevTools、图标、Expedition 和生物行为逻辑。

## `src/Framework/Creature/Core/CreatureTemplateBuilder.cs`

- 完成了公开可复用的 `CreatureTemplateBuilder`。开发者可以直接用它构造 Rain World 原生 `CreatureTemplate`，产物仍然交给 `CreatureDescriptor.Template(...)` 和 `CreatureRegistry` 使用。
- 完成了默认名称处理。创建 Builder 时只要求 `CreatureTemplate.Type`，模板名称默认使用 `Type.value`；需要玩家可读名称时可以通过 `.Name(...)` 覆盖。
- 完成了 Type 基础校验。空 Type、空 Type.value 和 Build 时仍没有有效 ExtEnum Index 的 Type 会收到明确错误。
- 完成了 `.Ancestor(...)`。开发者只声明要继承的 `CreatureTemplate.Type`，Builder 在 Build 阶段从 `StaticWorld.creatureTemplates` 解析实际 ancestor，并拒绝把自身 Type 当作 ancestor。
- 完成了 ancestor 继承保护。AI、AIMap、基础伤害抗性、基础眩晕抗性和即死阈值都使用“未设置/显式设置”两种状态；开发者没有调用对应方法时，Builder 不会用自己的默认值覆盖 Rain World 从 ancestor 继承来的值。
- 完成了 `.DefaultRelationship(...)`。它只负责提供 Rain World `CreatureTemplate` 构造函数要求的默认关系底值，不建立具体物种之间的生态关系表。
- 完成了 `.AI(...)` 和 `.RequireAIMap(...)`。开发者可以显式打开或关闭这两个模板开关；没有声明时保留 Rain World 构造结果。
- 完成了 `.OwnPreBakedPathing()`。开发者可以明确声明当前模板拥有自己的 pre-baked pathing 槽位。
- 完成了 `.ReusePreBakedPathing(...)`。开发者可以复用一个已有生物的 pre-baked pathing 槽位；Builder 会验证目标模板存在并且 `doPreBakedPathing` 为 true。
- 完成了 pre-baked pathing 冲突保护。同一个 Builder 不能同时声明自己拥有槽位和复用别人的槽位。
- 完成了 pre-baked pathing 继承修正。复用已有槽位时，Builder 会把当前模板的 `doPreBakedPathing` 强制设为 false、把 `preBakedPathingAncestor` 指向复用目标，并自动要求 AIMap，避免继承 Fly 这类 ancestor 时误把自定义 Type 当成新的烘焙槽位所有者。
- 完成了 `.Tile(...)`。开发者可以按 `AItile.Accessibility` 设置普通地形 PathCost，不需要自己创建 `TileTypeResistance` 列表和对象。
- 完成了普通 Tile 重复配置覆盖。同一个 Accessibility 再次设置时以后一次为准，不会生成两个相互竞争的 `TileTypeResistance`。
- 完成了 `.ExactTile(...)`。它会在 Rain World 原版 `CreatureTemplate` 构造和 Accessibility 归一化完成后写入最终 PathCost，支持 MossySpider 这种允许 Air 但明确禁止 Climb、Wall 的非连续可达性规则。
- 完成了 ExactTile 后的 `maxAccessibleTerrain` 重算。最终 Tile 表发生修改后，Builder 会重新计算实际最大可达地形，保持后续运行时可达性判断和最终表一致。
- 完成了 `.Connection(...)`。开发者可以按 `MovementConnection.MovementType` 设置连接 PathCost，不需要自己创建 `TileConnectionResistance` 列表和对象。
- 完成了 Connection 重复配置覆盖。同一种 MovementType 再次设置时以后一次为准。
- 完成了 `.DamageResistance(...)`、`.StunResistance(...)`、`.InstantDeathLimit(...)`。三个值只有在开发者显式填写时才覆盖模板构造结果。
- 完成了数值校验。Tile resistance、Connection resistance、基础伤害抗性、基础眩晕抗性和即死阈值拒绝负数、NaN 和 Infinity；默认关系强度拒绝 NaN 和 Infinity。
- 完成了已有模板解析保护。Builder 在解析 ancestor 或 pre-baked pathing 目标时会检查 `StaticWorld.creatureTemplates` 是否已初始化、Type Index 是否在数组范围内、目标槽位是否已经存在模板。
- 完成了 Build 独立集合。每次 `Build()` 都重新创建 `List<TileTypeResistance>` 和 `List<TileConnectionResistance>`，不同模板不会共享 Builder 内部的可变列表。
- 完成了“只包装复杂部分”的边界。`bodySize`、`grasps`、`meatPoints`、`shortcutColor`、`visualRadius`、`canSwim` 这类普通字段没有被机械包装，开发者可以在 `Build()` 后继续直接修改 Rain World 原生 `CreatureTemplate`。
- 完成了中英文双语 XML 注释。Builder 的公开入口和 pre-baked、ExactTile 这类容易误用的行为均写明中文和英文说明。

## `src/Creatures/MantleCrab/MantleCrabDefinition.cs`

- 完成了 MantleCrab 的 CreatureTemplate 创建迁移。文件不再引用 `DryCycle.Registration.CreatureTemplateBuilder`，直接使用 `DryCycle.Framework.Creature.Core.CreatureTemplateBuilder`。
- MantleCrab 当前测试模板通过 `.AI()` 显式开启 AI 生命周期，通过 `.RequireAIMap(false)` 保持“不启用 AIMap 或正式寻路”的现有测试配置。
- MantleCrab 当前仍登记 `HealthState`、实际 `MantleCrab` 实体和 `MantleCrabTestMovementAI`；本次 Builder 迁移没有改变这三项创建方式。
- MantleCrab 的身体大小、抓取数、抽象寻路开关、房间漫游概率、shortcut 限制和 dens 配置继续在 `Build()` 后直接设置原生 CreatureTemplate 字段。
- MantleCrab 实体创建时继续把 `MaintainRigidShell` 登记到 `WalkableDynamicSurfaceRuntime.RegisterPostPhysicsFinalizer`。
- MantleCrab 的 Shader 生物资源加载和 `MantleCrabMaterialCache.Enable()` 继续由 `MantleCrabDefinition.LoadResources` 自己负责，没有进入 Creature Core。

## `src/Creatures/MossySpider/MossySpiderDefinition.cs`

- 完成了 MossySpider 的 CreatureTemplate 创建迁移。文件不再引用旧 Registration Builder，直接使用 Core Builder。
- MossySpider 通过 `.AI()`、`.RequireAIMap()` 和 `.ReusePreBakedPathing(CreatureTemplate.Type.Deer)` 保留自己的 AI 生命周期并复用 Deer 的已有 pre-baked AI-map 槽位。
- MossySpider 原有的伤害抗性 8 和眩晕抗性 3 已迁移到 `.DamageResistance(8f)` 与 `.StunResistance(3f)`。
- MossySpider 的 OffScreen、Floor、CurvedFloor、Corridor、Climb、Wall、Ceiling、Air、Solid、Sand 最终可达性全部迁移到 `.ExactTile(...)`，其中 Climb、Wall 保持 IllegalTile，Solid 保持 SolidTile。
- MossySpider 的 Standard、OpenDiagonal、OutsideRoom、SideHighway、OffScreenMovement、BetweenRooms 连接代价全部迁移到 `.Connection(...)`。
- MossySpider 的自动抽象寻路、房间漫游、水中移动、体型、视野、肉量、shortcut、危险度和味道参数继续在 `Build()` 后直接设置原生 CreatureTemplate 字段。
- MossySpider 的 `MossySpiderTileAccessibilityOverride` 继续保留，用来阻止原版 swimmer fallback 把 Climb 和 Wall 当作可穿越地形。
- MossySpider 的实际实体、`MossySpiderAbstractAI` 和 `MossySpiderAI` 创建方式没有被本次 Builder 迁移改变。

## `src/Creatures/DesertBatfly/Core/DB_Definition.cs`

- 完成了 DesertBatfly 从手写 `new CreatureTemplate(...)` 到 Core Builder 的迁移，不再自己创建空的 `TileTypeResistance` 与 `TileConnectionResistance` 列表。
- DesertBatfly 通过 `.Ancestor(CreatureTemplate.Type.Fly)` 继续继承原版 Fly 模板。
- DesertBatfly 通过 `.AI(false)` 保留 Fly 系生物当前不使用 `ArtificialIntelligence` 工厂的生命周期。
- DesertBatfly 通过 `.ReusePreBakedPathing(CreatureTemplate.Type.Fly)` 继续复用 Fly 已有的 pre-baked pathing 槽位，同时不会让 DesertBatfly 自己声明新的烘焙槽位。
- DesertBatfly 原有的基础伤害抗性 0.3、基础眩晕抗性 1、即死伤害阈值 0.9 已迁移到 Builder 的三个明确接口。
- DesertBatfly 的 `quantified`、身体大小、抓取数、肉量、快速死亡和 shortcut 颜色继续在 `Build()` 后直接修改原生 CreatureTemplate 字段。
- DesertBatfly 的 `DB_State` 和 `DB_Creature` 创建方式没有被本次 Builder 迁移改变，也没有新增 AbstractAI 或实际 AI 工厂。

## `src/Creatures/DesertBatfly/Integration/DB_Relationships.cs`

- DesertBatfly 的生态关系继续由 `DB_Relationships` 单独监听 `StaticWorld.InitStaticWorld`，没有进入 CreatureTemplateBuilder 或 CreatureRegistry。
- DesertBatfly 会复制原版 Fly 的双向关系基础，再按自己的规则覆盖拾荒者、Peach Lizard、Slugcat、DesertBatfly 自身和原版 Fly 的关系。
- `Enable` 和 `Disable` 继续具有幂等保护，不会重复挂载或重复卸载关系 Hook。

## `src/Plugin.cs`

- 三个现有自定义生物继续在首次启用内容时分别调用 `MossySpiderDefinition.Register()`、`MantleCrabDefinition.Register()`、`DB_Definition.Register()`。
- `OnEnable` 继续调用 `CreatureCoreRegistry.Enable()` 安装 Creature Registry 的 Core Hook；`OnDisable` 继续调用 `CreatureCoreRegistry.Disable()` 卸载这些 Hook。
- DesertBatfly 的生态关系继续通过 `DB_Relationships.Enable()` 和 `DB_Relationships.Disable()` 单独管理。
- MantleCrab 的资源继续在 `RainWorld_OnModsInit` 中单独调用 `MantleCrabDefinition.LoadResources(self)` 加载。
- `DryCycleContent` 继续负责项目原有的物品内容和动态可行走表面运行时；三个生物不通过 `DryCycleContent` 注册。

## `src/Registration/DryCycleContent.cs`

- 当前只提供 `Register(ItemDefinition)` 给物品注册使用，不承担生物 Definition 登记职责。
- `Enable` 当前只启用 `ItemRegistry` 和 `WalkableDynamicSurfaceRuntime`。
- `Disable` 当前只关闭 `WalkableDynamicSurfaceRuntime` 和 `ItemRegistry`，并重置物品资源加载状态。
- `LoadResources` 当前只遍历 `ItemRegistry.Registered` 并调用每个 `ItemDefinition.LoadResources`。
- 文件注释已经明确说明生物注册迁移到了 `DryCycle.Framework.Creature.Core.CreatureRegistry`。

## 已移除的旧生物注册文件

- `src/Registration/CreatureDefinition.cs` 已经从当前项目树中移除，三个现有自定义生物不再依赖旧的继承式 CreatureDefinition。
- `src/Registration/CreatureRegistry.cs` 已经从当前项目树中移除，三个现有自定义生物不再通过旧 Registry 创建模板、State、实际 Creature、AbstractAI 和实际 AI。
- `src/Registration/CreatureTemplateBuilder.cs` 已经从本次 PR 分支移除。模板构建能力迁移到 `src/Framework/Creature/Core/CreatureTemplateBuilder.cs`，项目中不保留两套 CreatureTemplateBuilder 实现。
