# Iterator Framework 快速开始

## 引用

外部 Mod 引用编译后的 `DryCycle.dll` 和自身使用的 Rain World 引用程序集，添加 `using DryCycle.Iterators;`。

当前框架随 DryCycle 提供，尚未拆分为独立插件。依赖 DryCycle 的外部 BepInEx 插件使用插件 GUID `Anno` 声明依赖；不要把另一份 DryCycle 或游戏 DLL 随外部 Mod 重复发布。

```csharp
[BepInDependency("Anno")]
```

## 最小注册与注销

注册和注销在 Unity 主线程执行。对接外部 Mod 自己的启用、停用或 Mod 初始化生命周期，并保留返回的 Descriptor：

```csharp
using DryCycle.Iterators;

private IteratorDescriptor _definition;

private void RegisterIterator()
{
    if (_definition != null)
    {
        IteratorRegistry.Register(_definition); // 同一对象重复登记幂等
        return;
    }

    _definition = Iterator.Create("MYMOD_TEST")
        .Name("我的迭代器")
        .Room("MYMOD_CHAMBER")
        .Register();
}

private void UnregisterIterator()
{
    if (_definition == null)
        return;

    IteratorRegistry.Unregister(_definition);
    _definition = null;
}
```

房间不需要 `_AI` 后缀。未填名称时使用 ID；至少需要一个房间。请在目标房间完成加载前注册：DryCycle 启用时安装集中 Hook，`Room.ReadyForAI` 完成后自动为已注册房间创建默认 Runtime 和 Oracle 宿主。

当前默认身体在初始位置悬停，具备移动和游戏碰撞，默认使用 NoArm；外观和对话由后续阶段实现。使用 `Context.Body.MoveTo(...)`、`SetPose(...)` 或替换 `.Body(...)` / `.Arm(...)` 工厂，见 [Body 文档](BODY.md)。

房间卸载和普通 Session 重启只销毁实例，不注销定义；玩家离开但房间仍保持加载时，实例仍存在。所属 Mod 停用、移除或重新加载定义时，使用保留的 Descriptor 显式注销，框架会先销毁它的全部实例。DryCycle 停用会释放所有实例、Hook 和日志后端引用，但不会推断外部 Mod 的定义所有权。

## 自定义 Runtime 与延迟注册

```csharp
public sealed class MyIteratorRuntime : IteratorRuntime
{
    public MyIteratorRuntime(IteratorContext context) : base(context) { }

    protected override void OnActivate()
    {
        Context.Logger.Info("已绑定房间 " + Context.Room.abstractRoom.name);
    }

    protected override void OnDestroy()
    {
        Context.Logger.Info("实例结束：" + DestroyReason);
    }
}
```

在注册链中添加 `.Runtime(context => new MyIteratorRuntime(context))`。每次生成都必须返回新实例，并把框架提供的 Context 原样传入构造函数。

若定义在房间已经就绪后才注册，或需要主动重试失败的生成，可在 Unity 主线程用当前 Room 请求：

```csharp
if (IteratorRuntimes.TrySpawn(room, out IteratorRuntime runtime))
{
    Oracle host = runtime.Context.Oracle;
    IteratorRuntimes.TryGet(host, out IteratorRuntime sameRuntime);
}
```

已有 Active 实例时返回该实例；未启用、房间未就绪、无匹配定义或生成失败时返回 false/null。不要每帧请求生成。完整回调时序与清理语义见 [Runtime 文档](RUNTIME.md)。

## 多房间与元数据

```csharp
IteratorDescriptor definition = Iterator.Create("MYMOD_NXR")
    .Name("Nineteenth Reincarnation")
    .Rooms("NXR_AI", "NXR_OBSERVATORY")
    .WithMetadata("MyMod.Author", "Example author")
    .WithMetadata("MyMod.DialogueSet", "nxr-first-meeting")
    .Register();
```

元数据是只读字符串字典；上述对话集名称只是外部 Mod 的数据，框架当前不会读取或运行对话资源。

## 查询与游戏 ID

```csharp
IteratorRegistry.TryGet("MYMOD_NXR", out IteratorDescriptor byID);
IteratorRegistry.TryGetByRoom("nxr_ai", out IteratorDescriptor byRoom);

IteratorID.TryGet("MYMOD_NXR", out IteratorID id);
IteratorRegistry.TryGetOracleID(id, out Oracle.OracleID gameID);
IteratorRegistry.TryGetByOracleID(gameID, out IteratorDescriptor byOracleID);
```

ID 区分大小写，房间匹配忽略大小写。没有找到时 `TryGet` 返回 `false` 并将输出设为 `null`。不要对取得的游戏值对象自行调用 `Unregister()`；由 `IteratorRegistry.Unregister(definition)` 统一释放索引和游戏 ID。

## 自定义扩展方法

```csharp
public static class MyIteratorExtensions
{
    public static IteratorBuilder WithMyCredits(this IteratorBuilder builder)
    {
        return builder.WithMetadata("MyMod.Credits", "My team");
    }
}

IteratorDescriptor definition = Iterator.Create("MYMOD_EXTRA")
    .WithMyCredits()
    .Room("EXTRA_CHAMBER")
    .Register();
```

不需要继承框架内部类型或操作任何 Hook。Graphics、Behavior 和 Conversation 的配置方法尚未实现。
