# Body、Arm 与 Pose

第三阶段提供可替换身体、默认双 BodyChunk 身体、机械臂约束和姿势输入。所有坐标均为房间像素，速度与加速度按游戏物理帧计；这不是寻路或外观系统。

## 默认行为与移动

第一阶段的最小注册示例现在默认生成 `StandardIteratorBody` 和 `NoArm`。身体在初始位置悬停，使用游戏的地形、斜坡、物体碰撞与 BodyChunkConnection；不修改 Room.gravity，不创建原版 Oracle AI、机械臂或 Graphics。

```csharp
using DryCycle.Iterators;
using UnityEngine;

public sealed class MyRuntime : IteratorRuntime
{
    public MyRuntime(IteratorContext context) : base(context) { }

    protected override void OnActivate()
    {
        Context.Body.MoveTo(new Vector2(300f, 180f));
        Context.Body.LookAt(new Vector2(360f, 120f));
        Context.Body.SetPose(IteratorPose.Talk);
    }
}

// 放在所属 Mod 的注册方法中：
// Iterator.Create("MYMOD_TEST").Room("MYMOD_AI")
//     .Runtime(ctx => new MyRuntime(ctx)).Register();
```

| Body API | 约定 |
| --- | --- |
| `Context.Body` / `runtime.Body` | 当前身体；在 Runtime 的 OnCreate 开始前已初始化。创建中途失败时可能为 null。 |
| `Position / Velocity` | 默认按 Chunk 质量加权的身体中心与平均速度，派生身体可重写。 |
| `Direction` | 默认从第二 Chunk 指向第一 Chunk；单 Chunk 使用 Pose.BodyDirection。 |
| `MoveTo(target)` | 设置持续目标，以有限加速度接近并减速；受地形与 Arm 约束，不能穿墙，也不会自动绕障碍。 |
| `Stop()` | 清零 Chunk 速度，并以当前位置作为持续目标。 |
| `ReleaseMovement()` | 取消主动移动，保留惯性、姿势控制和游戏重力/碰撞。 |
| `MovementTarget` | 原始请求目标，可为 null；约束后的目标可用 `Context.Arm.ConstrainTarget(...)` 查询。 |
| `LookAt(point)` / `ClearLookTarget()` | 设置/清空注视点，仅保存坐标，不保存 Player 引用，不自动转动身体或眼睛。 |
| `SetPose(pose)` / `Pose` | 设置/读取不可变姿势；默认身体逐步调整朝向，Graphics 读取姿势和手脚目标。 |
| `LeftHandPosition / RightHandPosition / LeftFootPosition / RightFootPosition` | 当前姿势转换到房间坐标的手脚目标；第四阶段 Graphics 绘制手脚，但手脚没有独立碰撞。 |
| `Chunks` | 只读结构视图，Chunk 本身是实际游戏对象，供高级物理扩展使用。直接修改者负责遵守游戏物理约定。 |
| `IsInitialized / IsDestroyed` | 组件生命周期状态。销毁后 Chunks 清空、目标清空，控制方法抛 ObjectDisposedException。 |

控制方法可从身体 OnInitialize 或 Runtime.OnCreate 之后调用，不能从组件构造函数调用。null 姿势、NaN/Infinity 坐标及非法配置会立即抛参数异常；从受保护回调中抛出的异常由 Runtime 记录并结束该实例。

## BodyProfile 与工厂

```csharp
var profile = new BodyProfile(maxSpeed: 3f, acceleration: 0.25f);

IteratorDescriptor definition = Iterator.Create("MYMOD_CUSTOM")
    .Room("MYMOD_ROOM")
    .Body(ctx => new StandardIteratorBody(ctx, profile))
    .Arm(ctx => new FixedArm(ctx, new Vector2(300f, 200f), maxLength: 160f))
    .Register();
```

BodyProfile 创建后不可变，可由多实例共用。每次 Body/Arm 工厂必须使用传入的 Context 返回新组件，不得共享活动组件。省略工厂时使用默认值，Builder 的 `.Body(null)` / `.Arm(null)` 会报错。

| Profile 参数 | 默认值及范围 |
| --- | --- |
| `radius` / `chunkMass` / `chunkDistance` | 6 / 0.5 / 9；范围分别为 1–100、0.01–100、1–200。默认身体包含两个等质量 Chunk。 |
| `maxSpeed` / `acceleration` / `arrivalRadius` | 4 / 0.35 / 1；范围分别为 0.01–20、0.001–20、0.01–100。 |
| `gravity` | 0，范围 0–5；这是本身体的重力倍率，乘以当前房间重力。 |
| `airFriction` / `waterFriction` | 0.99 / 0.92，范围 0–1；数值为游戏使用的速度保留系数。 |
| `buoyancy` | 0.95，范围 0–5。 |
| `bounce` / `surfaceFriction` | 0.1 / 0.17，范围 0–1。 |

默认零重力使悬停可直接工作；非零重力会与移动控制力共同作用，过大的重力或阻力可能使目标无法到达。ReleaseMovement 适合让身体自然下落。标准初始位置沿用房间生成位置；修改尺寸后若身体放不下，初始化会失败并清理，不会静默把身体放进墙内。

## 机械臂

`IteratorArm` 是可替换约束组件，不要求 `Oracle.arm` 非 null。

- `NoArm`：默认，不施加锚点或长度约束。
- `FixedArm`：固定 `Anchor` 和 `MaxLength`，限制目标、惯性与物理更新后的越界。初始身体必须在可达范围内，否则生成失败。
- 自定义 Arm：继承 IteratorArm，按需重写 OnInitialize、OnConstrainTarget、OnUpdate、OnAfterPhysics、OnDestroy；通过 `.Arm(ctx => new MyArm(ctx))` 接入。

FixedArm 的 Tip 为身体中心。位置修正采用有界地形检查，不通过墙体强行满足约束；若地形和伸长限制无法同时满足，优先保留地形碰撞。它不是原版多关节机械臂的图形或关节模拟。本阶段没有 VanillaArm 适配器；原版机械臂的房间硬编码与 AI 依赖不会被默认带入。

## Pose

内置 `Idle`、`Talk`、`Look`、`Think`、`Angry`、`Inspect`、`Reach`、`Point`。这些是姿势数据，不会启动对话、AI 或动画系统。

```csharp
var wave = new IteratorPose("MyMod.Wave",
    bodyDirection: new Vector2(0.1f, 1f),
    rightHandOffset: new Vector2(14f, 20f));
runtime.Body.SetPose(wave);
```

BodyDirection 是非零、归一化后的房间方向；手脚偏移为身体局部坐标，x 向右，y 向头部。姿势对象可共享，只保存值数据，不保留游戏实体。第三阶段立即更新姿势输入，身体朝向由弹性控制逐步调整；外观过渡属于第四阶段。

## 自定义身体与生命周期

简单修改可派生 StandardIteratorBody，重写 OnUpdate 并调用 base.OnUpdate。完整替换可直接派生 IteratorBody，实现 OnInitialize 与 OnUpdate：

```csharp
public sealed class SingleBody : IteratorBody
{
    public SingleBody(IteratorContext context) : base(context) { }

    protected override void OnInitialize()
    {
        var chunk = new BodyChunk(Context.Oracle, 0,
            Context.Oracle.firstChunk.pos, 5f, 1f);
        ConfigurePhysics(new[] { chunk },
            System.Array.Empty<PhysicalObject.BodyChunkConnection>());
    }

    protected override void OnUpdate()
    {
        // 按自己的身体结构解释 MovementTarget、Pose 与 LookPoint。
        // 游戏物理随后自动更新，无须自行安装 Hook。
    }
}
```

ConfigurePhysics 只能在 OnInitialize 调用；支持 1–64 个当前 Oracle 拥有且索引正确的 Chunk，连接必须引用该身体的不同 Chunk。数组会复制；初始尺寸、位置、连接和归属均会验证。自定义身体可通过覆盖 `UseGamePhysics => false` 完全自行推进物理，但仍须配置合法宿主结构。

初始化顺序：Body 工厂 → Arm 工厂 → Body.OnInitialize → Arm.OnInitialize → Graphics 初始化 → Brain 初始化 → Conversation 初始化 → Runtime.OnCreate → 后续 Runtime 生命周期。Body 工厂可访问已绑定 Oracle，Arm 工厂可访问已创建但尚未初始化的 Body。Body.OnInitialize 中 Arm 尚未完成初始化，不应调用其 ConstrainTarget。

每帧顺序：Brain 感知与动作 → Conversation → Runtime.OnUpdate → Body.OnUpdate → Arm.OnUpdate → 游戏 PhysicalObject.Update（可选择跳过）→ Arm.OnAfterPhysics → Body.OnAfterPhysics → Graphics 更新 → Runtime.OnLateUpdate。任一步销毁实例即停止后续步骤；递归更新被 Runtime 保护拦截。

销毁顺序：Runtime.OnDestroy → Conversation 清理 → Brain 清理 → Graphics 清理 → Arm.OnDestroy → Body.OnDestroy → 宿主与索引清理 → Context 清空游戏引用。回调应能清理部分初始化的资源。Body / Arm 之后仍能查询 IsDestroyed，但不应继续持有外部复制的 Chunk 引用。身体或约束回调失败时结束当前 Runtime，因为继续运行无法保证其物理结构有效；其他实例不受影响。

物理桥仅在 Hook 安装时创建一次非虚方法调用，直接复用游戏 PhysicalObject.Update，不复制游戏物理源码，也不修改原版 Oracle.Update。独立检查覆盖托管物理路径；Unity 原生水体/天气、实际房间流式加载及其他 Mod 交互仍需游戏内验证。
