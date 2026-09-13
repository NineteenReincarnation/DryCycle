# Iterator Framework

Rain World 自定义迭代器框架，位于 DryCycle 的 `src/Iterators`，公共命名空间为 `DryCycle.Iterators`。

目前完成任务书第 81 节规定的**第一阶段：定义与注册核心**。可以定义、验证、注册、查询和注销迭代器；本阶段不会生成 Oracle 实体，也不会挂载房间或 Oracle Hook。

```csharp
using DryCycle.Iterators;

IteratorDescriptor definition = Iterator.Create("TEST")
    .Name("Test Iterator")
    .Room("TEST_AI")
    .Register();

IteratorRegistry.TryGet("TEST", out IteratorDescriptor byID);
IteratorRegistry.TryGetByRoom("TEST_AI", out IteratorDescriptor byRoom);

// 所属 Mod 卸载时，用原先保存的定义注销。
IteratorRegistry.Unregister(definition);
```

## 文档入口

- [快速开始](QUICKSTART.md)：程序集引用、注册时机、示例、卸载。
- [第一阶段 API](API.md)：输入规则、冲突、不可变性、ID 映射、日志和错误语义。
- [开发进度与验证](PROGRESS.md)：本次交付、测试结果、后续阶段和未验证内容。

## 当前结构

```text
Iterators/
├─ Core/
│  ├─ Iterator.cs              公开入口
│  ├─ IteratorID.cs            不可变 ID 值对象
│  ├─ IteratorDescriptor.cs    不可变定义快照
│  ├─ IteratorBuilder.cs       独立配置收集器
│  ├─ IteratorRegistry.cs      定义与房间索引
│  ├─ IteratorValidation.cs    内部输入校验
│  ├─ IteratorOracleIds.cs     内部游戏 ID 适配
│  └─ IteratorLogger.cs        日志上下文
├─ Logging/
│  └─ IteratorLogBridge.cs     内部 BepInEx 日志适配
└─ Documentation/
```

第一阶段的数据流为 `Builder → Descriptor → Registry`。Builder 不作为注册表数据源；所有集合在 Descriptor 构造时复制并封装为只读集合，Registry 在完整预检后一次发布新索引快照。

Core 不依赖 Graphics、Behavior、Conversation、BepInEx 或具体模块。游戏 ID 适配只使用本机 Rain World 实际提供的 `Oracle.OracleID`，不保留实体或 Session 引用。后续 Runtime、Context 与模块工厂会在对应阶段以有实际类型、有测试的 API 加入。

当前 API 为第一阶段开发接口。后续优先以新增接口演进；正式发布后，已有 public API 的改名或移除必须提供弃用与迁移期。`FrameworkVersion`、`ApiVersion` 和能力查询尚未实现，不应据此猜测后续系统已可用。
