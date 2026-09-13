# 第一阶段 API 约定

公共类型统一位于 `DryCycle.Iterators`。未列出的内部类型不是外部扩展接口。

## ID 与输入

| API | 约定 |
| --- | --- |
| `new IteratorID(value)` | 创建不可变 ID，不登记游戏 ExtEnum；非法参数抛出 `ArgumentException`，null 抛出 `ArgumentNullException`。 |
| `IteratorID.Value` / `ToString()` | 返回原始字符串，不自动裁剪或改变大小写。 |
| `IteratorID.Parse(value)` | 解析；非法格式抛出 `FormatException`，null 抛出 `ArgumentNullException`。 |
| `IteratorID.TryParse(value, out id)` | 不抛出格式异常；失败返回 false/null，不查询注册表。 |
| `IteratorID.TryGet(value, out id)` | 仅查询已注册定义的 ID；失败返回 false/null。 |
| `IteratorID.IsRegistered(value)` | 查询框架是否拥有此 ID。 |
| `Equals` / `==` / `!=` / `GetHashCode` | 使用 Ordinal 字符串值，支持不同实例之间比较与 null，不使用 ExtEnum Index。 |

ID 和房间名使用 `[A-Za-z0-9_][A-Za-z0-9_.-]{0,127}`：1–128 个 ASCII 字符，点和连字符不能位于首位。空白、路径分隔符、控制字符以及自动裁剪后的名称不会被静默接受。

显示名称和元数据键支持中文等 Unicode 文本；不能为空、带首尾空白或包含控制字符。元数据值是非 null 字符串，允许空字符串。省略名称或直接构造 Descriptor 时给名称传入 null，默认使用 ID；`Builder.Name(null)` 被视为显式错误输入。

## Builder 与 Descriptor

| API | 约定 |
| --- | --- |
| `Iterator.Create(string)` / `Iterator.Create(IteratorID)` | 返回新的 Builder，不注册、不生成实体。 |
| `new IteratorBuilder(id)` | 非 Fluent 入口，可供扩展方法组合。 |
| `.Name(name)` | 设置 Builder 的显示名称。 |
| `.Room(name)` / `.Rooms(params names)` | 添加精确房间名；至少一个、忽略大小写去重；批次内有错误时整批不写入。 |
| `.WithMetadata(key, value)` | 设置区分大小写的扩展键；同一键在 Builder 中后写覆盖前写。 |
| `.Build()` | 返回新的、已验证的不可变 Descriptor；无全局状态副作用。 |
| `.Register()` | 等价于 `IteratorRegistry.Register(Build())`。每次调用都会先创建新 Descriptor。 |
| `new IteratorDescriptor(id, rooms, displayName = null, metadata = null)` | 直接构造，输入列表和字典均复制。 |
| `descriptor.ID / DisplayName / Rooms / Metadata` | 只读定义数据，集合不能被外部修改。 |
| `descriptor.Validate()` | 验证定义，不修改数据、不查询游戏或全局冲突。 |

重复调用同一个 Builder 的 `Register()` 会遇到重复 ID 错误，因为每次 Build 都创建不同定义。需要幂等调用时保留返回的 Descriptor，再将同一对象传入 Registry。

第一阶段只提供精确房间绑定，尚无自定义谓词 `RoomRule`。Descriptor 也尚无 Body、Graphics、Brain、Conversation、Environment 或 Module 工厂；这些工厂随对应实现加入，不提前暴露 `object` 工厂或无功能占位接口。

## Registry

| API | 约定 |
| --- | --- |
| `IteratorRegistry.Register(descriptor)` | 返回该定义。同一实例重复注册幂等；不同定义占用相同 ID 或任一房间时抛出 `InvalidOperationException`，不覆盖。 |
| `IteratorRegistry.Unregister(descriptor)` | 仅移除当前仍由这个 Descriptor 实例拥有的注册。已注销或已被新定义替代时返回 false。null 抛出参数异常。 |
| `IteratorRegistry.Registered` | 注册顺序的不可变集合快照；之后增删不会改变旧快照。 |
| `TryGet(id, out descriptor)` | 接受 `IteratorID` 或 string，按 ID 字符串 Ordinal 查询。传入 null 字面量时需要明确重载类型。 |
| `TryGetByRoom(name, out descriptor)` | 完整房间名，OrdinalIgnoreCase，无后缀推断或模糊匹配。 |
| `TryGetByOracleID(gameID, out descriptor)` | 按 `Oracle.OracleID.value` 查询，只返回框架已注册定义。 |
| `TryGetOracleID(id, out gameID)` | 返回独立的 `Oracle.OracleID` 值对象；未注册时返回 false/null。 |

所有查询在缺失或 null 时返回 false/null。注册和注销必须在 Unity 主线程进行；注册表不提供后台注册、跨线程写入或异步生命周期保证。

注册顺序为：验证定义 → 检查 ID 和全部房间冲突 → 检查游戏 ID 占用 → 构建完整索引快照 → 登记游戏 ID → 发布快照。普通输入与冲突失败不会写入部分房间索引或遗留新游戏 ID。

游戏中的真实类型名为 `Oracle.OracleID`。保留原版 ID `SS`、`SL` 和 MSC 的 `SS_Cutscene`、`SL_Cutscene`、`ST_Cutscene`、`DM`、`ST`、`CL`；即使对应 DLC 当前未启用也禁止占用。其他已登记的 Oracle ID 同样视为冲突，不能由框架接管。

框架按照字符串查找，正常 ExtEnum 注销导致其他 Index 变化时，已存在的映射仍有效。其他 Mod 不应绕过 Registry 修改或注销框架拥有的 ExtEnum；公共游戏值对象本身仍具有游戏原有的可变接口。

定义不引用 Session 或 Room 实体，可在多次游戏 Session 之间保留。第一阶段的“重载”指显式注销旧 Descriptor，再注册新 Descriptor；没有自动文件重载、活动 Runtime 热替换或卸载 Hook。

## Logger

```csharp
var logger = new IteratorLogger(definition.ID)
    .ForModule("ExampleModule")
    .ForPhase("Initialize");

logger.Info("Initialized.");
logger.Warn("Optional resource was absent.");
logger.Error("Operation failed.", exception);
```

| API | 约定 |
| --- | --- |
| `new IteratorLogger(id)` | 默认作用域 `Core` / `Registration`。 |
| `new IteratorLogger(id, Action<IteratorLogLevel, string> sink)` | 实例专属后端；null 使用默认后端，不修改其他 Logger。 |
| `.ForModule(name)` / `.ForPhase(name)` | 返回新的作用域对象，不改变原 Logger。 |
| `.Info(message)` / `.Warn(message)` / `.Error(message, exception = null)` | 输出 `Info`、`Warning`、`Error`，自动携带 Iterator、Module、Phase；null 消息显示为 `<null>`。 |

格式示例：

```text
[IteratorFramework][Iterator:TEST][Module:Registry][Phase:Register] Registered 'Test Iterator' in 1 room(s).
```

DryCycle 启用时默认输出到其 BepInEx 日志源；停用或独立调用时回退到 `System.Diagnostics.Trace`。Trace 在没有接收器的进程中不保证落盘。自定义接收器只接收最终等级与格式化文本，不需要引用 BepInEx。

日志接收器抛异常时先尝试 Trace 回退，Trace 或扩展异常对象的格式化再失败时也不会向调用者传播。日志回调内再次调用日志会被重入保护跳过。此处隔离的是**日志后端**；Graphics、Dialogue 与其他运行时模块的错误隔离尚待各阶段实现。

## 扩展和 API 演进

- 本阶段 public 接口仅用于外部定义、查询、注销及日志；ExtEnum 适配、索引快照和 BepInEx 桥保持 internal/private。
- 外部 Mod 可以使用 Builder 扩展方法或直接构造 Descriptor，无须访问 Hook。
- 未来增加 Runtime/Context 时继续保持 Descriptor 静态、Runtime 实例化以及 Module 组合的分工。
- 进入有活动实例的阶段时，必须在接入游戏前补齐注销与 Runtime 销毁协调；当前没有活动实例，因此注销立即释放定义。
- 正式发布后的破坏性 API 变更需提供 `[Obsolete]` 与迁移说明，不依靠临时宽类型接口维持兼容。
