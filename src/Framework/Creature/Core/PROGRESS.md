# Creature Core 开发进度

这个文件记录 Creature Core 框架已经真正完成并进入仓库的功能，以及现有生物迁移到这套框架后的实际改动。

记录规则：

- 按文件分别记录，不把多个文件的职责混在一条里。
- 只写已经实现并提交的内容，不把计划中的功能写成已完成。
- 每一条都说明实际完成了什么，不使用“等地方”“之类”“相关功能”这类无法判断具体范围的说法。
- 后续每完成一个 Creature Core 任务，都同步更新这个文件。

---

## `src/Framework/Creature/Core/CreatureDescriptor.cs`

这个文件负责描述“一种生物要怎样接入 Creature Core Framework”，本身不负责执行 Rain World Hook。

### 已完成

- 完成了生物类型登记：每份描述必须保存一个有效的 `CreatureTemplate.Type`，注册前会检查该类型是否已经取得有效的 ExtEnum 编号。
- 完成了所属 Mod 登记：每份描述必须填写 `OwnerId`，用于注册冲突和 Factory 报错时明确指出是哪一个 Mod 提供了这只生物。
- 完成了显示名称登记：`DisplayName` 默认使用 `CreatureTemplate.Type.value`，开发者可以在注册前修改显示名称。
- 完成了别名登记：可以给同一只生物添加一个或多个文本别名。
- 完成了别名清理：别名会去掉首尾空格，空字符串和纯空白字符串会直接被拒绝。
- 完成了别名去重：同一份描述中的别名使用不区分大小写的比较方式去重，`Crab` 和 `crab` 不会被保存成两个别名。
- 完成了别名顺序保留：多个别名按照第一次成功添加的顺序保存。
- 完成了别名只读保护：外部代码取得 `Aliases` 后只能读取，不能通过取得集合引用绕过 Descriptor 修改内部别名。
- 完成了单个别名删除：注册前可以通过 `RemoveAlias` 删除指定别名，删除时不区分大小写。
- 完成了全部别名清空：注册前可以通过 `ClearAliases` 清除这份描述中的全部额外别名。
- 完成了 `CreatureTemplate` 创建方式登记：`TemplateFactory` 是唯一必须提供的 Factory，Registry 会通过它取得最终写入 `StaticWorld.creatureTemplates` 的模板。
- 完成了 `CreatureState` 创建方式登记：开发者可以选择提供 `StateFactory`；没有提供时，不替换 Rain World 原本创建的 State。
- 完成了实际 `Creature` 创建方式登记：开发者可以选择提供 `CreatureFactory`；没有提供时，不接管 Rain World 原本的 Realize 实体创建路径。
- 完成了 `AbstractCreatureAI` 创建方式登记：开发者可以选择提供 `AbstractAIFactory`；没有提供时，不替换 Rain World 原本创建的 AbstractAI。
- 完成了实际 `ArtificialIntelligence` 创建方式登记：开发者可以选择提供 `RealizedAIFactory`；没有提供时，`InitiateAI` 继续使用 Rain World 原本的 AI 分派。
- 完成了四个可选 Factory 的清除入口：State、实际 Creature、AbstractAI、实际 AI 的 Factory 都可以在正式注册前恢复为空，让该部分重新交还 Rain World 原版流程。
- 完成了注册前修改、注册后冻结：Descriptor 在交给 Registry 成功登记之前可以配置；成功登记后 `IsFrozen` 变为 true，后续修改名称、别名和 Factory 都会被拒绝。
- 完成了注册前完整性检查：无效的 Creature Type 和缺少 `TemplateFactory` 的 Descriptor 不能进入 Registry。
- 完成了简写语法 `Name`：功能与 `SetDisplayName` 相同。
- 完成了简写语法 `Alias`：功能与 `AddAlias` 相同。
- 完成了简写语法 `Aliases(params string[])`：可以一次写入多个别名，并继续使用同一套去重、清理和冻结规则。
- 完成了简写语法 `Template`：功能与 `SetTemplateFactory` 相同。
- 完成了简写语法 `State`：功能与 `SetStateFactory` 相同。
- 完成了简写语法 `Realized`：功能与 `SetCreatureFactory` 相同，用名称明确区分 `AbstractCreature` 和房间中实际存在的 `Creature`。
- 完成了简写语法 `AbstractAI`：功能与 `SetAbstractAIFactory` 相同。
- 完成了简写语法 `AI`：功能与 `SetRealizedAIFactory` 相同。
- 所有公开说明和关键内部说明已经使用“中文在上、英文在下”的双语注释格式。

---

## `src/Framework/Creature/Core/CreatureRegistry.cs`

这个文件负责保存 `CreatureDescriptor`，并把 Rain World 的核心生物创建流程转接到对应 Descriptor 提供的 Factory。

### 已完成

- 完成了统一生物登记入口：开发者通过 `CreatureRegistry.Register(CreatureDescriptor)` 把一份生物描述正式加入框架。
- 完成了注册顺序保存：Registry 使用有序列表保存 Descriptor，`Registered` 按实际登记顺序返回结果。
- 完成了注册列表只读保护：外部代码可以遍历 `Registered`，但不能直接修改 Registry 内部列表。
- 完成了同一 Descriptor 重复注册的幂等处理：同一个 Descriptor 对象再次调用 `Register` 时直接返回原对象，不重复写入索引，也不制造重复注册错误。
- 完成了 Creature Type 标准名称索引：Registry 按 `CreatureTemplate.Type.value` 保存 Descriptor，可以通过 Type 查回对应描述。
- 完成了 Creature Type 编号索引：Registry 保存 `CreatureTemplate.Type.Index` 对应的 Descriptor，用于检查两个不同注册项是否占用了同一个 ExtEnum 编号。
- 完成了文本名称索引：标准 Type 名称和 Descriptor 中声明的 Alias 会提前进入不区分大小写的字典，不需要在解析一次名称时遍历所有生物和所有别名。
- 完成了 `TryGet(CreatureTemplate.Type, out CreatureDescriptor)`：按 Type 查询已经登记的 Descriptor。
- 完成了 `TryGet(string, out CreatureDescriptor)`：按标准 Type 名称或 Alias 查询 Descriptor，查询时会去除输入首尾空格并忽略大小写。
- 完成了 `Get(CreatureTemplate.Type)`：找不到时抛出包含目标 Type 的明确异常。
- 完成了 `Get(string)`：找不到时抛出包含输入名称的明确异常。
- 完成了 Type 名称冲突检查：两个不同 Descriptor 使用相同 `CreatureTemplate.Type.value` 时拒绝后注册者。
- 完成了 Type 编号冲突检查：两个不同 Descriptor 使用相同 `CreatureTemplate.Type.Index` 时拒绝后注册者。
- 完成了标准名称与 Alias 冲突检查：一个生物的标准 Type 名称不能占用另一只生物已经登记的 Alias。
- 完成了 Alias 与 Alias 冲突检查：两只不同生物不能登记同一个不区分大小写的 Alias。
- 完成了冲突来源说明：注册冲突异常会同时写出已有注册项的 `OwnerId`、已有生物 Type、新注册项的 `OwnerId` 和新生物 Type。
- 完成了先检查、后写入的注册流程：Type、编号、标准名称和全部 Alias 都通过冲突检查后才会真正写入 Registry。
- 完成了晚期注册保护：`StaticWorld.creatureTemplates` 已经建立，或者 Registry 已经进入模板初始化阶段以后，不再允许登记新的 Creature Type。
- 完成了 Registry 启用状态查询：`IsEnabled` 可以判断核心 Hook 当前是否已经挂上。
- 完成了注册窗口状态查询：`IsRegistrationOpen` 可以判断当前是否仍允许登记新的 Descriptor。
- 完成了幂等 `Enable`：重复调用不会重复挂同一批 Rain World Hook。
- 完成了幂等 `Disable`：重复调用不会重复卸载 Hook；Disable 不删除已经登记的 Descriptor，也不会重新开放已经关闭的晚期注册窗口。
- 完成了 `StaticWorld.InitCustomTemplates` 接入：在 Rain World 建立自定义模板时，Registry 会调用每一份 Descriptor 的 `TemplateFactory`。
- 完成了模板 Factory 异常包装：`TemplateFactory` 自己抛出异常时，外层异常会补充生物 Type 和 `OwnerId`。
- 完成了空模板检查：`TemplateFactory` 返回 null 时立即报告是哪只生物和哪个 Owner 返回了空模板。
- 完成了模板 Type 一致性检查：Factory 返回模板的 Type value 和 Type index 必须与 Descriptor 登记的 Type 一致。
- 完成了模板数组范围检查：模板 Type index 必须落在当前 `StaticWorld.creatureTemplates` 数组范围内。
- 完成了 AI 配置一致性检查：模板 `AI == false` 时不允许 Descriptor 同时声明 AbstractAI Factory 或实际 AI Factory。
- 完成了无祖先实体创建检查：模板没有 ancestor 时必须提供实际 Creature Factory，否则 Registry 会在模板初始化阶段直接报告这只生物没有实体创建来源。
- 完成了无祖先 AI 创建检查：模板启用 AI、没有 ancestor 且没有实际 AI Factory 时，Registry 会在模板初始化阶段直接报告没有 AI 创建来源。
- 完成了模板安装回滚：一批 Descriptor 安装模板时，如果后面的模板创建或验证失败，Registry 会把这一批过程中已经改写的 `StaticWorld.creatureTemplates` 槽位恢复成进入安装前的内容。
- 完成了 `AbstractCreature` 构造接入：先运行 Rain World 原构造函数，再根据 Descriptor 是否提供 Factory 决定是否替换 State 和 AbstractAI。
- 完成了自定义 State 创建：存在 `StateFactory` 时调用一次 Factory，并把结果写入 `AbstractCreature.state`。
- 完成了 State Factory 空结果检查和异常包装：返回 null 或内部抛错都会指出生物 Type、Owner 和出错的是 State Factory。
- 完成了自定义 AbstractAI 创建：存在 `AbstractAIFactory` 时调用 Factory 并替换 `AbstractCreature.abstractAI`。
- 完成了 AbstractAI Factory 空结果检查和异常包装。
- 完成了出生巢穴位置保留：Rain World 原构造阶段已经写入默认 AbstractAI 的 `privDenPos` 会在替换成自定义 AbstractAI 时复制过去，避免自定义 AI 丢失出生巢穴位置。
- 完成了 `AbstractCreature.Realize` 接入：只有 Descriptor 明确提供 `CreatureFactory` 时才接管实际 Creature 创建；没有提供时完整调用 Rain World 原版 Realize。
- 完成了自定义实际 Creature Factory 空结果检查和异常包装。
- 完成了自定义实体创建后的 Rain World 检查保留：设置自定义 `realizedObject` 后仍调用原版 Realize，让原版在“实体已经存在”提前返回之前执行它自己的检查。
- 完成了 MSC 挑战模式自定义标志保留：DLCShared 开启时调用 `MSCRealizeCustom()`，保留其在已存在实体检查之前执行的挑战模式 `setCustomFlags()` 行为，同时不会让它覆盖已经创建的自定义实体。
- 完成了实体创建后的 AI 启动：自定义实际 Creature 建立后调用 `InitiateAI()`，保持 Rain World 创建实体后启动 AI 的生命周期语义。
- 完成了 stuck object Realize：自定义实体建立以后会继续 Realize `stuckObjects` 两端尚未 Realize 的对象，保持原版同阶段处理。
- 完成了 `AbstractCreature.InitiateAI` 接入：只有 Descriptor 提供 `RealizedAIFactory` 时才接管实际 AI 创建；没有提供时继续调用 Rain World 原版 AI 分派。
- 完成了实际 AI 前置检查：模板必须启用 AI，并且当前 `AbstractCreature.abstractAI` 不能为空。
- 完成了实际 AI 重复创建保护：`abstractAI.RealAI` 已经存在时不再重复调用开发者提供的 `RealizedAIFactory`。
- 完成了实际 AI Factory 空结果检查和异常包装。
- 完成了 `WorldLoader.CreatureTypeFromString` 接入：先用 Registry 已建立的标准名称/Alias 索引查询自定义生物，查不到时交回 Rain World 原解析函数。
- Core Registry 当前只挂 `StaticWorld.InitCustomTemplates`、`AbstractCreature.ctor`、`AbstractCreature.Realize`、`AbstractCreature.InitiateAI`、`WorldLoader.CreatureTypeFromString` 五条核心 Hook，没有把食物链关系、资源加载、Sandbox、DevTools、图标或生态规则塞进这个文件。
- 关键实现说明已经使用“中文在上、英文在下”的双语注释格式。

---

## `src/Creatures/MantleCrab/MantleCrabDefinition.cs`

这个文件已经从旧 `CreatureDefinition` 继承模式迁移到新的 `CreatureDescriptor + CreatureRegistry`。

### 已完成

- 删除了对旧 `CreatureDefinition` 基类的继承，改成物种自己的静态注册入口。
- 使用 `MantleCrabEnums.Type` 登记 MantleCrab 的 Creature Type。
- 使用 `DryCycle.Plugin.ModId` 登记这只生物的 Owner。
- 登记显示名称 `Mantle Crab`。
- 登记 MantleCrab 的 `CreatureTemplate` 创建函数。
- 登记 MantleCrab 的实际 Creature 创建函数。
- 没有登记 State Factory，因此 MantleCrab 保留 Rain World 原本的 State 创建结果。
- 没有登记 AbstractAI Factory 和实际 AI Factory，因为当前 MantleCrab 外观/物理原型的模板明确设置 `AI = false`。
- 保留了原模板配置：`grasps = 0`、`bodySize = 6f`、`canAutoAbstractPath = false`、房间内自动游荡概率为 0、跨房间自动游荡概率为 0、禁止标准 Shortcut 入口、`doesNotUseDens = true`。
- 保留了实际 MantleCrab 创建后的硬壳平台收尾注册：仍然把 `crab.MaintainRigidShell` 交给 `WalkableDynamicSurfaceRuntime.RegisterPostPhysicsFinalizer`。
- 保留了 MantleCrab 自己的资源加载入口：仍然调用 `DryCycleShaderAssets.EnsureCreatureAssets` 和 `MantleCrabMaterialCache.Enable`。
- MantleCrab 的资源加载没有进入 Creature Core Registry。

---

## `src/Creatures/MossySpider/MossySpiderDefinition.cs`

这个文件已经从旧 `CreatureDefinition` 继承模式迁移到新的 `CreatureDescriptor + CreatureRegistry`。

### 已完成

- 删除了对旧 `CreatureDefinition` 基类的继承，改成物种自己的静态注册入口。
- 使用 `MossySpiderEnums.Type` 登记 MossySpider 的 Creature Type。
- 使用 `DryCycle.Plugin.ModId` 登记 Owner。
- 登记显示名称 `Mossy Spider`。
- 登记带空格的 Alias `mossy spider`；标准名称 `MossySpider` 已经由 Registry 的标准 Type 名称索引负责，并且文本查询不区分大小写，因此不再重复登记 `MossySpider` 和 `mossyspider` 两个同义 Alias。
- 登记 MossySpider 的 `CreatureTemplate` 创建函数。
- 登记 MossySpider 的实际 Creature 创建函数。
- 登记 MossySpider 的 `MossySpiderAbstractAI` 创建函数。
- 登记 MossySpider 的 `MossySpiderAI` 创建函数。
- 没有登记自定义 State Factory，因此继续保留 Rain World 原本创建的 State。
- 保留了原模板的 Deer pre-baked pathing ancestor 配置。
- 保留了原模板对 OffScreen、Floor、CurvedFloor、Corridor、Climb、Wall、Ceiling、Air、Solid、Sand 十种 tile accessibility 的精确通行设置。
- 保留了原模板对 Standard、OpenDiagonal、OutsideRoom、SideHighway、OffScreenMovement、BetweenRooms 六种 MovementConnection 的通行设置。
- 保留了 `canAutoAbstractPath = false`、房间内自动游荡概率 0、跨房间自动游荡概率 0、`offScreenSpeed = 0.55f`、`abstractedLaziness = 60`、`doesNotUseDens = true`、`hibernateOffScreen = false`、禁止标准 Shortcut 入口。
- 保留了 `bodySize = 12f`、`grasps = 0`、`visualRadius = 700f`、`movementBasedVision = 0f`、`dangerousToPlayer = 0f`、`communityInfluence = 0f`。
- 保留了两栖、水中可游泳、水路代价 1、不飞行的水体和移动配置。
- 保留了 `MossySpiderTileAccessibilityOverride`，继续明确排除 Wall 和 Climb。
- 保留了 `meatPoints = 12`、`countsAsAKill = 1`、shortcut 颜色、`shortcutSegments = 8`、`scaryness = 0.8f`、`deliciousness = 0.1f`。

---

## `src/Creatures/DesertBatfly/Core/DB_Definition.cs`

这个文件已经从旧 `CreatureDefinition` 继承模式迁移到新的 `CreatureDescriptor + CreatureRegistry`。

### 已完成

- 保留 `CreatureType = new("DesertBatfly", true)` 作为 DesertBatfly 的稳定 Creature Type 定义。
- 删除了对旧 `CreatureDefinition` 基类的继承，改成物种自己的静态注册入口。
- 使用 `DryCycle.Plugin.ModId` 登记 Owner。
- 登记显示名称 `Desert Batfly`。
- 登记 DesertBatfly 的 `CreatureTemplate` 创建函数。
- 登记 `DB_State` 创建函数。
- 登记 `DB_Creature` 实际实体创建函数。
- 没有登记 AbstractAI Factory 和实际 AI Factory，因为 DesertBatfly 的模板保持 `AI = false`，继续复用 Fly 的非 `ArtificialIntelligence` 控制生命周期。
- 保留 Fly 作为模板 ancestor。
- 保留 `quantified = false`、`AI = false`、Fly pre-baked pathing ancestor、`doPreBakedPathing = false`。
- 保留 `bodySize = 0.18f`、`grasps = 1`、`meatPoints = 0`。
- 保留基础伤害抗性 `0.3f`、基础眩晕抗性 `1f`、即时死亡伤害阈值 `0.9f`、`quickDeath = true`。
- 保留原来的 shortcut 颜色 `(0.65f, 0.48f, 0.29f)`。
- 原先混在这个 Definition 里的食物链关系已经移出 Core 定义，不再要求通用 CreatureRegistry 管理 DesertBatfly 的生态规则。

---

## `src/Creatures/DesertBatfly/Integration/DB_Relationships.cs`

这个文件是迁移时新增的 DesertBatfly 生态接入文件，用来接住旧 Definition 中的关系初始化职责。

### 已完成

- 完成了独立的 `Enable` 和 `Disable`，重复调用不会重复挂载或重复卸载 `StaticWorld.InitStaticWorld` Hook。
- 在 `StaticWorld.InitStaticWorld` 原逻辑完成后建立 DesertBatfly 食物链关系，保持旧注册系统原本的初始化时机。
- 继续以 Fly 的关系表作为 DesertBatfly 的基础关系模板。
- 对 StaticWorld 中每一个有效的其他生物，继续把 Fly 对该生物的关系复制给 DesertBatfly。
- 对 StaticWorld 中每一个有效的其他生物，继续把该生物对 Fly 的关系复制成它对 DesertBatfly 的基础关系。
- 继续让所有 TopAncestor 为 Scavenger 的生物以 `DB_Tuning.ScavengerHostility` 强度攻击 DesertBatfly。
- Watcher 可用且 Peach Lizard Type 有效时，继续让 Peach Lizard 以 `0.32f` 强度把 DesertBatfly 视为食物。
- Watcher 可用且 Peach Lizard Type 有效时，继续让 DesertBatfly 以 `0.90f` 强度害怕 Peach Lizard。
- 继续让 DesertBatfly 忽略 Slugcat。
- 继续让 DesertBatfly 忽略同种 DesertBatfly。
- 继续让 DesertBatfly 忽略普通 Fly。
- 增加了 DesertBatfly 模板或 Fly 模板没有正确初始化时的空值保护。
- 这套生态关系不进入 `Framework/Creature/Core/CreatureRegistry.cs`，保持 Core Registry 只负责核心注册生命周期。

---

## `src/Registration/CreatureDevConsoleSupport.cs`

这个文件保留 DevConsole 软依赖能力，但已经不再依赖旧的 `CreatureDefinition` 和旧 Creature Registry。

### 已完成

- `TryRegisterAll` 改为读取新的 `DryCycle.Framework.Creature.Core.CreatureRegistry.Registered`。
- DevConsole 注册对象从旧 `CreatureDefinition` 改为新的 `CreatureDescriptor`。
- 继续通过反射查找 `DevConsole.ObjectSpawner`，没有安装 DevConsole 时不会产生硬依赖。
- 继续通过反射查找 `ObjectSpawner.SpawnerInfo` 和 `ObjectSpawner.SimpleSpawnerInfo`。
- 继续查找参数签名为 `CreatureTemplate.Type + SpawnerInfo` 的 `RegisterSpawner`。
- 每个通过新 Registry 登记的 Descriptor 都可以继续获得 `spawn <CreatureTemplate.Type>` 的 DevConsole 生成入口。
- 同一 Creature Type 在一次 DevConsole 注册周期中不会重复登记。
- 生成时直接使用 Descriptor 的 `Type` 从 `StaticWorld` 取得已经初始化的 `CreatureTemplate`。
- 生成位置已有合法生物节点时保留该节点；没有合法节点时继续使用 `room.RandomRelevantNode(template)` 处理开发阶段尚未完成寻路规则的生物。
- DevConsole 带额外参数生成时继续把参数写入 `AbstractCreature.spawnData`。
- 写入 `spawnData` 后继续尝试调用 `setCustomFlags()`；故事模式专用标志处理失败不会阻止开发阶段生成生物。
- 最终继续通过 `AbstractCreature.Move(pos)` 把新建的 AbstractCreature 放到指定位置。

---

## `src/Registration/DryCycleContent.cs`

这个文件已经退出生物注册职责，只保留它仍然实际负责的旧内容系统。

### 已完成

- 删除了 `Register(CreatureDefinition)` 生物注册入口。
- 删除了对旧 Creature Registry 的 Enable 调用。
- 删除了对旧 Creature Registry 的 Disable 调用。
- 删除了遍历旧 CreatureDefinition 加载生物资源的逻辑。
- 继续保留 `ItemDefinition` 注册入口。
- 继续负责 `ItemRegistry.Enable()` 和 `ItemRegistry.Disable()`。
- 继续负责 `WalkableDynamicSurfaceRuntime.Enable()` 和 `WalkableDynamicSurfaceRuntime.Disable()`。
- `LoadResources` 现在只遍历 `ItemRegistry.Registered` 并调用物品自己的资源加载函数。

---

## `src/Plugin.cs`

这个文件已经把 DryCycle 当前通过统一注册系统管理的三只生物切换到 Creature Core Framework。

### 已完成

- MossySpider 改为调用 `MossySpiderDefinition.Register()`，不再创建旧 `CreatureDefinition` 对象交给 `DryCycleContent`。
- MantleCrab 改为调用 `MantleCrabDefinition.Register()`。
- DesertBatfly 改为调用 `DB_Definition.Register()`。
- 在 `OnEnable` 中启用新的 `DryCycle.Framework.Creature.Core.CreatureRegistry`。
- 在 `OnDisable` 中卸载新的 Creature Core Registry Hook。
- 在 `OnEnable` 中启用 `DB_Relationships`，让 DesertBatfly 的生态关系继续在 StaticWorld 初始化阶段写入。
- 在 `OnDisable` 中卸载 `DB_Relationships` Hook。
- `DryCycleContent` 继续启用，因为它仍负责 ItemRegistry 和 `WalkableDynamicSurfaceRuntime`，但不再负责三只生物的核心注册。
- `RainWorld_OnModsInit` 中继续调用 `DryCycleContent.LoadResources(self)` 处理物品资源。
- `RainWorld_OnModsInit` 中新增 `MantleCrabDefinition.LoadResources(self)`，保留 MantleCrab 原先由旧 CreatureDefinition 触发的 Creature Asset 和 Material Cache 初始化。
- DesertBatfly 原有 `DB_RainWorldHooks` 继续独立启用和卸载，它负责 Fly 非虚方法适配与 DesertBatfly 自己的运行时逻辑，没有塞进 Creature Core Registry。
- MossySpider 原有 `MossySpiderBackPlatform` 继续独立启用和卸载，没有塞进 Creature Core Registry。

---

## 已删除：`src/Registration/CreatureDefinition.cs`

### 已完成

- 三只现有生物已经不再继承这个旧基类。
- DevConsole 生物生成支持已经改用 `CreatureDescriptor`。
- `DryCycleContent` 已经不再接收 `CreatureDefinition`。
- 旧基类已经从仓库删除，避免后续新生物继续误用旧注册方式。

---

## 已删除：`src/Registration/CreatureRegistry.cs`

### 已完成

- 三只现有生物的核心模板、State、实际 Creature、AbstractAI、实际 AI 和文本名称解析已经交给新的 `Framework/Creature/Core/CreatureRegistry.cs`。
- `DryCycleContent` 已经不再启用或卸载旧 Registry。
- `CreatureDevConsoleSupport` 已经不再读取旧 Registry 的 Registered 列表。
- 旧 Registry 已经从仓库删除，避免同一批 Creature Hook 同时存在两套实现。
