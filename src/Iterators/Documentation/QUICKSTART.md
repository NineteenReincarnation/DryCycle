# 第一阶段快速开始

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

房间不需要 `_AI` 后缀。未填名称时使用 ID；本阶段至少需要一个房间。该注册示例只建立定义和游戏 ID 映射，进入房间自动生成实体将在第二阶段接入。

房间退出和普通 Session 重启不应注销定义。定义不保存当前游戏对象；所属 Mod 停用、移除或重新加载定义时，使用保留的 Descriptor 显式注销。DryCycle 日志桥会随插件启停释放日志后端引用；注册表目前不会自动推断或清理外部 Mod 的定义所有权。

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

不需要继承框架内部类型或操作任何 Hook。Body、Graphics、Behavior 和 Conversation 的配置方法尚未进入第一阶段 API。
