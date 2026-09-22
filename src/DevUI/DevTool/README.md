# DevTool 重构

本目录承载 Rain World DevTools 的新一代编辑器实现。目标不是给原版 `DevInterface` 换皮，而是建立一套以场景为中心、可扩展、可撤销、可搜索、可渐进迁移的编辑器框架。

## 硬性设计原则

1. **完整房间画面优先**：Rain World 继续按完整屏幕渲染。Room / Objects / Sound / Triggers 默认采用覆盖式工具面板，不通过左右 Dock 永久压缩游戏 Camera。
2. **场景优先而不是页面优先**：Objects、Sound、Triggers、Room Settings 是同一个 Scene Editor 的工具模式，不因为切换工具就清空历史。Map、Dialog、Relationships 在数据本身成为主工作区时使用中央编辑画布。
3. **UI 与业务解耦**：ImGui 只读取 Presentation Snapshot、发出 Command；保存、Undo/Redo、Selection、对象创建和数据修改不写进 Draw 方法。
4. **核心完全自有**：ObjectDescriptor、PropertyDescriptor、Inspector、Gizmo、History、Input、Save、Selection 全部由 DryCycle 自己实现。POM、Fisobs、M4r 等第三方 Mod 只能作为设计经验参考，不建立编译依赖、软依赖、反射探测或运行时 API 调用。
5. **兼容别人，但不依赖别人**：任何 Mod 只要最终把对象和编辑行为暴露为 Rain World 公共的 `PlacedObject.Type`、`PlacedObject.Data`、`PlacedObjectRepresentation`、`DevInterface` 控件或相关 Hook，新 DevTool 就尽量从这些公共行为层兼容。不会读取第三方私有注册表，也不会因为某个第三方框架损坏而让 DevTool 自身失效。
6. **第三方可以主动获得原生体验**：其他 Mod 可以选择引用 DryCycle 的公开 DevTool API，注册对象元数据和 Inspector。依赖方向只能是“外部 Mod → DevTool API”，不能反过来。
7. **输入必须集中仲裁**：文本输入、ImGui 控件、Gizmo、编辑器快捷键和玩家输入只允许经过统一 Input Router 决定所有权。
8. **Undo/Redo 是基础设施**：新原生编辑行为优先使用明确的编辑事务；无法理解的原版/外部 DevInterface 行为使用通用状态快照兜底。
9. **不依赖 UI 控件进行保存**：`Ctrl+S`、工具栏和后续命令面板必须调用同一个保存入口，不再模拟点击 `Save_Settings`。
10. **原版永远可退回**：左上角常驻 `New UI / Vanilla` 切换。Vanilla 模式完整恢复原版 DevUI，新版只保留切换按钮，不破坏当前 Document、Selection 或 History。

## 当前目录

```text
DevTool/
├── Core/              编辑器生命周期、上下文、Document、Selection、UI 模式
├── Commands/          保存、对象编辑和统一命令入口
├── History/           Document History、原版兼容快照、事务记录
├── Input/             键鼠所有权、世界 Handle 与游戏输入隔离
├── Preview/           临时运行时预览、回滚、所有权传播和安全探测
├── Objects/           Object Catalog、Inspector、属性描述、多选
├── Extensions/        第三方公开 API、版本/能力协商、注册生命周期
├── Room/              RoomSettings 与 RoomEffect 编辑
├── Sound/             环境音浏览、放置和参数编辑
├── Triggers/          Trigger 与 TriggeredEvent 编辑
├── Map/               区域节点图、房间位置/层级/子区域
├── Dialog/            会话文件浏览和结构化预览
├── Relationships/     生物双向关系矩阵
├── Compatibility/     只基于 Rain World 公共 DevInterface 的兼容桥
├── RWImGui/           RWImGUI 覆盖式前端
└── README.md
```

## 已经完成

### 基础设施

- 已建立覆盖式 Editor Shell，Rain World 房间 Camera 不因为左右面板缩小。
- 已实现左上角 `New UI / Vanilla` 全局切换；Vanilla 模式完整恢复原版界面。
- 已把原有 `Ctrl+S / Ctrl+Z / Ctrl+Y / Ctrl+Shift+Z` 能力迁入新版 Command / History / Input 基础设施，旧 `VanillaDevUIShortcutRuntime` 已删除。
- 已删除旧 `DevUIShortcutInputGuard`，文本输入、ImGui、世界 Handle、快捷键和玩家输入统一交给 `EditorInputRouter`。
- 已把历史从“跟 Page 走”改成“跟 Document 走”：Room、RegionMap、Relationships 各有独立历史上下文。
- 已迁移保持 `PlacedObject` / `PlacedObject.Data` 对象身份的快照恢复逻辑。
- ImGui Present 生命周期只读取 Snapshot 和入队 Command，不直接修改 Rain World 对象。
- 主 `DryCycle.dll` 不引用 ImGui；RWImGui 前端使用独立 `DryCycle.DevTool.RWImGui.dll`。

### Objects

- Object Library、分类/标签/来源元数据、模糊搜索和 Scene 对象列表。
- 点击 Library 进入放置模式；精确使用房间坐标放置，Shift 连续放置，Esc/右键取消。
- Ctrl/Shift 多选、批量删除、`Ctrl+D` 批量复制。
- 多选 Inspector：共同属性、Mixed 状态、组锚点移动、批量属性写入，一次操作只生成一条 Undo。
- 自有 PropertyDescriptor / Inspector 注册体系。
- 标准 `DevInterface.Button / Slider / PlacedObjectRepresentation / Handle` 自动兼容。
- 原版/其他 Mod 的世界 Handle 保留；无法翻译的自定义控件可回退原始 DevUI。

### 第三方扩展 API

- `DryCycle.DevUI.DevTool.Extensions.DevToolApi` 作为统一公共入口，当前契约版本为 `1.0`。
- `DevToolApiVersion` + `DevToolCapability` 提供显式 Major/Minor 与能力协商，外部 Mod 不必靠版本字符串猜功能。
- `DevToolExtensionScope` 把一个 Mod 的全部 DevTool 注册收进同一生命周期；Disable / Reload 时一次 `Dispose()` 即可逆序清理。
- `DevToolRegistration` 支持单项提前释放且重复 Dispose 安全；Discovery 中只统计仍有效的注册。
- Scope 直接接入 Object Descriptor 与强类型/自定义 Inspector，且不向第三方暴露 RWImGui 类型。
- `GetExtensions()` / `TryGetExtension(...)` 只返回只读快照，不把内部 Registry 或 Scope 暴露给其他扩展。
- 原有 `DevToolObjectApi`、`ObjectCatalog`、`ObjectInspectorRegistry` 保留，避免为了新 API 强迫已有接入立即迁移。
- 完整契约、示例和兼容保证见 [`PHASE5.md`](PHASE5.md)。

### Room

- Environment / Visual / Gameplay / Effects 分组。
- 常用 RoomSettings 数值、布尔、Palette、Danger 等直接编辑。
- RoomEffect 搜索、添加、参数编辑、删除。
- 防止沿用原版 Create 信号的 toggle 语义导致“再次添加反而删除”。
- Effect 浏览器已接入悬停实时预览：约 180ms 后把一个 `save=false` 的临时 `RoomEffect` 放到当前 `RoomSettings.effects` 最前端，移开、切换页面或关闭 DevTools 时按对象身份精确回滚。
- Effect 预览不识别 Mod 名称、程序集或私有 API；任何通过正常 `RoomSettings.effects`、`GetEffect`、`GetEffectAmount` 读取效果状态的未知 Mod 都可以自动看到同一份预览状态。
- 对只在加载阶段创建视觉对象的 Effect，已加入第二层通用 bootstrap：优先从本体 `Room.Loaded` 和已注册 HookGen `Room.Loaded` 回调的 IL 中寻找“Effect 判断 → 构造 UAD → `Room.AddObject`”关系，只执行可证明安全的构造器，不重跑整个房间加载流程。
- 当基础 Recipe 因额外标量参数而拒绝构造器时，会进入更保守的扩展 Recipe：当前可从同一 IL block 推导单个 `float amount`、末尾 `bool` 常量/双 Effect 选择器、单个 `int` 常量和单个 enum 常量，并补齐 `RoomSettings`、`RoomEffect.Type`、`RainWorldGame`、`World`、`AbstractRoom` 等上下文参数；找不到明确证据时仍然 fail closed。像本体 `Lightning / BkgOnlyLightning` 这种共用构造器的模式不需要写 Effect 名特判。
- 扩展 Recipe 还要求目标 UAD 构造器后方在很近的 IL 范围内确实进入 `Room.AddObject`，并拒绝 PhysicalObject、多同类标量参数和不明确的自定义可选参数，避免仅仅因为同一代码块里出现了一个构造器就误实例化。
- IL 无法直接确定构造器时，只允许经过安全扫描的 HookGen 回调进入 A/B Probe；明显涉及静态写入、AbstractEntity、文件、AssetBundle、存档等持久副作用的回调直接 fail closed，不做高级预览。
- 高级预览的 Runtime Object、Drawable、Camera SpriteLeaser 和安全 Room 字段由独立 Ownership Transaction 持有；结束 Preview 时按对象身份逆序回滚，不按类型名猜测删除对象。
- 预览对象运行后继续通过 `Room.AddObject` 生成的非物理子对象可以继承同一份 Preview ownership。正常 `Room.Update` 内优先使用 `Room.updateIndex` 精确确认当前生成者身份；仅在 Update 循环之外才使用“该调用类型的所有房间实例都属于 Preview”的保守栈回退。
- Preview 如果生成 `PhysicalObject`、出现强回滚泄漏或高级 bootstrap 抛出异常，会只按 `RoomEffect.Type` 在当前插件会话中标记为 Unsafe；之后该 Effect 仍保留第一层 RoomEffect 状态预览，但不再反复执行高级 bootstrap。这里同样不记录 Mod 名或程序集。
- 回滚后会检查 Preview-owned Object、Drawable、Camera SpriteLeaser、Room 字段以及房间运行时 baseline；强所有权泄漏会触发 Unsafe，普通房间自身的粒子/滴水等 baseline 波动只记诊断，不误判为 Mod 污染。
- 临时 Effect 不进入 Inspector Snapshot、History 或保存数据；Save / Undo / Redo / 真正点击添加都会先结束预览，并处理了从左侧预览直接点击右侧 Inspector 时的一帧索引竞态。
- 所有正式修改进入 Room Document History。

### Sound

- Library / Scene 双视图和音频文件搜索。
- Omni / Directional / Spot 环境音创建。
- Room drone 音量、Volume、Pitch、Doppler、Spot Position/Radius/Taper、Directional Direction 编辑。
- inherited 音源保护。
- Spot / Directional 场景 Handle 保留，旧参数 Panel 隐藏。
- 添加行为与删除行为分离，同时继续通过本体公共创建路径保持 Hook 兼容。

### Triggers

- Trigger 类型创建、选择、删除。
- cycle 范围、delay、fire chance、multi-use、Karma、entrance、Slugcat 过滤、SeeCreature 类型。
- Spot Trigger 位置/半径和原版场景 Gizmo。
- TriggeredEvent 第二层编辑：MusicEvent、StopMusicEvent、ShowProjectedImageEvent 有语义化字段。
- Mod 新增的未知 EventType 保留类型并允许切 Vanilla 编辑其自定义控件，不猜测第三方私有数据。

### Map

- 新增内置 [制图工作区](Map/Cartography/README.md)：参考 Cornifer 的制图流程，使用自有作者文档、房间/标注排布、可编辑图层、独立撤销历史及 PNG/SVG/分层图片导出。入口是 Map 内的“制图 / Cartography”，制图修改不回写游戏地图布局。

- 独立中央节点画布，而不是压在房间场景上。
- 鼠标滚轮缩放、中键平移、Fit Map、层级过滤、房间搜索。
- 房间连接线直接读取 `AbstractRoom.connections`，不维护第二份世界拓扑。
- 房间节点拖动只在前端做实时预览，松手后主线程一次提交，因此一次拖动只有一条 Undo。
- 房间 devPos、Layer、Subregion Inspector。
- 保存继续使用本体 `MapPage.SaveMapConfig()`；Undo/Redo 使用 Map Document Snapshot。

### Dialog

- 保持原版能力边界：这是会话资源浏览/预览工具，不擅自新增文本文件写入功能。
- 当前语言会话文件搜索。
- 中央结构化预览 Text / Wait / Special / Chatlog，不再把所有内容堆到游戏 HUD。
- `<PLAYERNAME>` 等原版替换规则继续通过 DialogPage 处理。

### Relationships

- 主生物搜索/选择。
- 中央双向关系矩阵，同一行同时显示 `Primary → Other` 与 `Other → Primary`。
- 关系类型、Intensity 编辑和 direct override 标记。
- `Reset Override` 只删除显式修改并露出本体/继承关系。
- CreatureType 和 Relationship.Type 都从本体 ExtEnum 获取，因此 Mod 正常注册到本体的数据会自然进入面板，不需要依赖 Mod API。
- 修改直接写入 `RelationshipPage.changedRelationships`，保存继续使用本体日志输出路径。

## Phase 6 最终架构收口

第六阶段不再扩张编辑功能，而是冻结前五阶段形成的长期边界：

- `DevToolRuntime` 只负责 Hook 和帧顺序；跨 Workspace 的 Queue / Presentation / State / Revision / Session 生命周期统一由 `DevToolSubsystemCoordinator` 收口。
- RWImGui 只读 detached Snapshot 并入队 Command；兼容诊断同样由后端 `DevUiDiagnosticsPublisher` 发布，Draw 不执行 live DevUI 扫描、审计或写操作。
- POM / RegionKit 专用 Inspector 已从核心删除；第三方原生增强只通过 `DevToolApi 1.x`，未接 API 的内容继续走 Generic DevInterface / Rain World Data Model / Vanilla fallback。
- 已删除未接线的 Universal Protocol Augmenter 与重复 Generic Protocol Bootstrap；未知协议必须明确显示为缺口并回退，而不是用未验证的特殊适配伪装兼容。
- Extension Scope 属于外部 Mod 生命周期，不会因为 New UI / Vanilla 切换、房间切换或 DevUI runtime reset 被 DevTool 擅自释放。
- `DevTool Final Architecture Guard` 持续检查后端/前端依赖方向、第三方私有类型、生命周期所有权和诊断纯读边界。

完整封板契约与验证清单见 [`PHASE6.md`](PHASE6.md)。静态守卫通过不等价于实际 Rain World 联编或游戏内回归通过。

## 兼容策略

```text
Level 1  DevTool Extension API 1.x
         原生 Metadata / Inspector / Scope 生命周期 / Capability 协商

Level 2  Rain World 标准 DevInterface
         自动投影常见 Button / Slider / Representation / Handle

Level 3  通用本体数据模型
         PlacedObject / RoomSettings / World / ExtEnum 等基础行为可工作

Level 4  Vanilla fallback
         无法理解的特殊控件完整退回原版 DevUI
```

Level 1 是可选增强而不是兼容前提。第三方完全不引用 DryCycle 时，Level 2～4 仍然工作；主动接入 API 失败也不能破坏通用兼容或 Vanilla fallback。

Effect Hover Preview 额外遵守一条规则：兼容对象是 Rain World 的运行时行为，而不是具体 Mod。代码中不建立 `RegionKitAdapter`、`POMAdapter` 或按程序集名称分支的 Effect 兼容表。

## 继续审查 / 完善的重点

- 真正使用游戏安装中的 `PUBLIC-Assembly-CSharp.dll`、`HOOKS-Assembly-CSharp.dll`、RuntimeDetour 和 RWImGui 进行完整联编、进游戏运行测试与错误清理；当前仓库没有覆盖这套环境的编译 CI。
- 用最小外部测试 Mod 对 `DevToolApi 1.x` 做 Enable / Disable / Reload 回归，确认 Scope 清理、Catalog 回落和 Inspector fallback。
- Effect Preview 的 IL Recipe 后续只扩展“能从 IL 明确证明来源”的参数表达式，例如多个标量参数、局部变量回传或 helper 返回值；复杂条件仍然保持 fail closed，不通过默认值猜测第三方语义。
- Effect Preview 继续审查 Camera / Futile / 全局 shader 等不经过 `Room.AddObject` 的运行时副作用；只有能建立可证明所有权和可逆性的通用 Journal 后才扩大自动预览范围。
- Objects 框选、吸附、网格、对齐/分布等高效场景编辑工具。
- 更多本体常用 PlacedObject 的语义化 Inspector 与我们自己的 Gizmo。
- 自定义 Gizmo 的公共 ABI 仍未冻结；在 Input Router、History Transaction、scene-space snapshot 与前端绘制协议稳定前不向第三方发布半成品接口。
- Map 的连接端口编辑、房间 attractiveness/default material 等高级数据编辑。
- Command Palette、History 面板、Problems/Console 等编辑器级工具。
- Overlay 自动避让、拖动时弱化、窄屏自动折叠和布局持久化。
- 更完整的视觉主题、图标和交互细节打磨。
