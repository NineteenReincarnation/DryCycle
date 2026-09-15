# Runtime 与生命周期

第二阶段提供实例创建、Context、Oracle / Room 绑定与销毁；第三阶段接入 [Body / Arm / Pose](BODY.md)，第四阶段接入 [Graphics](GRAPHICS.md)，第五阶段接入 [Brain 与行为](BEHAVIOR.md)，第六阶段接入 [Conversation](CONVERSATION.md)。所有操作和回调在 Unity 主线程同步执行；不提供后台写入或异步生命周期保证。

## 实例工厂

```csharp
using DryCycle.Iterators;

public sealed class ChamberRuntime : IteratorRuntime
{
    public ChamberRuntime(IteratorContext context) : base(context) { }

    protected override void OnActivate()
    {
        Context.Logger.Info("进入房间 " + Context.Room.abstractRoom.name);
    }

    protected override void OnDestroy()
    {
        // 释放本实例自行订阅的事件与资源；框架随后释放宿主和游戏引用。
        Context.Logger.Info("销毁原因：" + DestroyReason);
    }
}
```

在所属 Mod 的注册方法中：

```csharp
IteratorDescriptor definition = Iterator.Create("MYMOD_CHAMBER")
    .Name("Chamber Iterator")
    .Room("MYMOD_AI")
    .Runtime(context => new ChamberRuntime(context))
    .Register();
```

工厂在生成时才调用。每次必须使用传入的 Context 构造全新 Runtime，不能缓存并复用旧实例或替换 Context。不配置工厂时使用默认 `IteratorRuntime`，其回调为空。

工厂或构造函数执行时，Context 的定义、Room、World、Game 可用，Runtime 与 Oracle 尚未绑定，玩家列表尚未刷新。把需要宿主或玩家的初始化放到 `OnCreate` 或之后的回调。构造函数抛异常时，框架拿不到返回实例，不能替外部代码调用 `OnDestroy`，因此资源申请宜放到受保护的初始化回调。

## 状态与回调

| `State` | 框架行为与回调 |
| --- | --- |
| `RuntimeCreated` | 绑定 Runtime、Context、Oracle，将宿主加入 Room，依次初始化 Body / Arm、Graphics、Brain、Conversation，然后调用 `OnCreate`。 |
| `Initializing` | 调用 `OnInitialize`。 |
| `Initialized` | 调用 `OnRoomReady`。 |
| `Active` | 调用一次 `OnActivate`；之后每次宿主 Update 调用 Brain、Conversation、OnUpdate、Body / Arm 控制与物理、Graphics 更新、OnLateUpdate。 |
| `Destroying` | 记录原因，调用一次 `OnDestroy`，随后执行框架清理。 |
| `Destroyed` | 游戏引用已释放，不再执行更新和初始化。 |

`Registered` 与 `SpawnRequested` 发生在实例存在之前，通过注册与生成日志表达，不是 `IteratorLifecycle` 的值。`OnLateUpdate` 在本实例的身体物理更新之后，不等于 Unity 的全局 LateUpdate。

框架统一保护重复初始化、重复销毁和递归更新。在任一回调内调用 `Destroy()` 后，不再调用后续初始化或更新回调。初始化期间即使已经进入 Active，也不会提前执行更新。

| Runtime 属性或方法 | 约定 |
| --- | --- |
| `Context / Descriptor / ID` | 当前实例的上下文与静态定义。 |
| `Body / Arm / Graphics / Brain / Conversation` | 当前各组件，在正常 OnCreate 前已初始化（可选组件整体失败时可能停用）；销毁后保留已释放组件供状态查询。 |
| `State / IsActive` | 当前生命周期状态；只有 Active 为活动实例。 |
| `IsInitialized` | 仅在 Initialized 或 Active 为 true；销毁后为 false。 |
| `UpdateCount` | 完整完成 OnUpdate 与 OnLateUpdate 的次数；失败、重入或中途销毁不计数。 |
| `DestroyReason` | 销毁开始前为 null，之后保留结束原因。 |
| `Destroy()` | 立即以 Requested 原因销毁本实例，幂等；不注销定义。 |

生命周期回调为 `protected virtual`。状态转换与初始化/更新调度由框架控制，外部代码无需也不能直接调用它们。

## Context

| 属性 | 内容与有效期 |
| --- | --- |
| `ID / Descriptor / Runtime` | 当前定义和绑定的 Runtime，销毁后仍可查询。Runtime 在工厂返回后绑定。 |
| `Body / Arm / Graphics / Brain / Conversation` | 与 Runtime 的组件属性相同；部分创建失败时可能为空，销毁后组件 IsDestroyed 为 true。 |
| `Oracle / Room / World / Game` | 本实例的游戏引用；正常 OnCreate 开始时全部可用，OnDestroy 结束后清空。 |
| `StorySession` | 当前 Game 的 StoryGameSession；Arena 等其他模式返回 null，销毁后返回 null。 |
| `Players` | 本房间内已实体化的 Session 玩家，只读视图；在 OnCreate 与每次 OnUpdate 之前刷新，销毁后清空。 |
| `PrimaryPlayer` | 优先存活且不在捷径中的玩家，其次存活玩家，最后仍在本房间的玩家；同级取 Session 中靠前者，无玩家时为 null。 |
| `Logger` | 自动携带当前 Iterator ID、Runtime 模块与回调阶段；销毁后仍可使用。 |

Players 是复用的实时视图，不是历史快照。Context 不缓存外部 Mod 的全局玩家状态，也不把其他房间的玩家当成本实例玩家。

创建中途失败也会对已接管的 Runtime 调用 OnDestroy；这时 Oracle 可能尚未创建。清理代码必须能处理部分初始化，并只释放自己已取得的资源。

## 房间生成与查询

DryCycle 启用后安装集中 Hook。在原版 `Room.ReadyForAI` 执行完毕后，框架查找房间定义，验证 Room / World / Game / AbstractRoom 属于同一有效实例，再运行工厂与初始化。

```csharp
IteratorRuntimes.TryGet(room, out IteratorRuntime byRoom);
IteratorRuntimes.TryGet(oracle, out IteratorRuntime byOracle);
IteratorRuntimes.TrySpawn(room, out IteratorRuntime spawned);
```

| API | 约定 |
| --- | --- |
| `IsEnabled` | Runtime Hook 是否已成功启用。失败时定义注册仍可用，但不能生成实例。 |
| `Active` | 当前所有 Active 实例的只读集合快照；每次查询分配，不宜每帧用它搜索单个实例。 |
| `TryGet(Room, out runtime)` | 按具体 Room 对象查询，包含已绑定且正在初始化的实例；Destroying / Destroyed 返回 false/null。 |
| `TryGet(Oracle, out runtime)` | 仅查询框架实际绑定的 Oracle，不根据 Oracle ID 接管其他实体。 |
| `TrySpawn(Room, out runtime)` | 在已就绪、定义有效且未关闭的房间显式生成；已有 Active 实例时返回同一对象，其余失败返回 false/null。 |

一个 Room 对象最多有一个实例。同名房间在不同 Session 中各有独立实例；同一抽象房间卸载后重新实体化，会得到新 Runtime。定义不自动扫描已加载房间，晚注册时应显式 TrySpawn。

每个 Room 的自动生成对同一 Descriptor 只尝试一次，重复 ReadyForAI 不会重复生成或无限重试。主动 Destroy、初始化失败或需要在已加载房间替换定义后，可显式 TrySpawn。正在生成、已经卸载的 Room 和已经结束的 Game 会拒绝请求；不要从失败回调递归重试。

默认宿主在房间中心附近寻找具有竖直空隙的位置，只在生成时扫描一次，不消耗游戏随机数。没有合适空间时记录失败并清理实例。

## 销毁与异常

| `IteratorDestroyReason` | 触发条件 |
| --- | --- |
| `Requested` | 外部调用 Runtime.Destroy。 |
| `RoomUnloaded` | 所属 Room 开始卸载。 |
| `OracleRemoved` | 宿主 Destroy 或 RemoveFromRoom。 |
| `SessionEnded` | 所属 RainWorldGame 开始 ShutDownProcess。 |
| `DefinitionUnregistered` | 所属定义注销，或创建期间定义被撤销。 |
| `FrameworkDisabled` | DryCycle 停用，或生成期间 Hook 被停用/重新启用。 |
| `InitializationFailed` | 宿主创建或初始化回调失败。 |
| `UpdateFailed` | OnUpdate 或 OnLateUpdate 抛异常。 |
| `HostUnavailable` | 更新前发现宿主已标记删除、Room 转移或游戏绑定失效。 |

清理顺序：设置 Destroying 与原因 → Runtime.OnDestroy → Conversation 及所有播放清理 → Brain / 动作 / 行为模块清理 → Graphics 清理 → Arm.OnDestroy → Body.OnDestroy → 销毁并移除宿主 → 移除 Room / Oracle 索引和活动列表 → 清空 Context 的游戏引用 → Destroyed。

OnDestroy 抛异常仍会继续清理。Runtime、Body 或 Arm 的初始化与更新回调抛异常只结束当前实例，错误带有 ID 和阶段日志。工厂返回 null、外来 Context 或复用实例会被拒绝；框架不会销毁工厂错误返回的其他实例。插件内部清理先于原版房间卸载和 Session 关闭，原版流程仍继续执行。

注销定义会先关闭其生成入口，销毁所有关联实例，再释放房间索引和 Oracle ID。OnDestroy 不能重新生成或重注册正在注销的同一 ID；操作其他定义不会丢失。房间卸载、Session 结束和框架停用本身不注销定义，外部 Mod 仍须管理自己的注册所有权。

## 游戏适配与阶段边界

所有 Hook 集中于内部 IteratorHooks，覆盖 Oracle 构造、房间就绪/卸载、Session 结束及实体移除。安装失败会回滚，停用时先销毁实例再卸载 Hook。

内部 IteratorHost 仅作 Oracle 适配。构造补丁在 PhysicalObject 基类初始化后识别框架专属宿主，再初始化基础 BodyChunk，跳过原版 Oracle 的行为、机械臂、神经元与房间副作用。其他 Oracle 继续原有构造路径；若找不到预期基类构造位置则拒绝安装补丁。

Body / Arm / Pose 和 Graphics 已接入，宿主使用游戏物理与自定义绘制，不运行原版 Oracle AI，也不持久化进存档。可选图形错误只停用绘制或失败部件，不结束 Runtime；Brain 的基本 Idle、感知、观察、动作及模块，以及 Conversation 的脚本、分支和打断已实现；交互、环境和存档属于后续阶段。当前托管检查调用实际游戏程序集及 Hook，但不能替代 Unity 内的房间加载、原版迭代器共存和其他 Mod 兼容验收。
