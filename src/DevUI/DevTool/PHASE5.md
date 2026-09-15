# DevTool Phase 5 — 第三方兼容层 / 扩展接口

第五阶段的目标是把 DevTool 从“内部已经能兼容第三方”推进到“外部开发者可以稳定接入，而且 DevTool 不反向依赖第三方”。本阶段不引入新的按 Mod 名称硬编码适配表，也不把 ImGui、RWImGui 或内部 Presentation 类型暴露为公共 ABI。

## 核心原则

1. **依赖方向单向**：外部 Mod 可以选择引用 `DryCycle.dll` 的 DevTool API；DryCycle 不引用外部 Mod 的程序集、注册表或私有 API。
2. **不接 API 也能工作**：标准 `PlacedObject.Type` / `PlacedObject.Data` / `DevInterface` 行为继续走现有通用兼容层和 Vanilla fallback。
3. **接 API 只用于升级体验**：原生对象名称、分类、标签、强类型 Inspector 等增强能力通过公开 API 注册，不成为基础兼容前提。
4. **公共 ABI 不绑定前端**：扩展接口只使用 Rain World 数据类型和 DevTool 自己的中立数据契约，不暴露 ImGui/RWImGui 类型。
5. **显式版本协商**：扩展可以声明最低 API 版本和所需 Capability；不满足时明确拒绝，而不是在运行时半失效。
6. **注册必须有生命周期**：推荐所有第三方注册都挂在 `DevToolExtensionScope` 上；外部 Mod Disable / Reload 时 `Dispose()` 一次即可清理其全部贡献。
7. **单个注册也可释放**：`DevToolRegistration` 是幂等句柄，可提前撤销单项贡献；Scope 最终仍会安全清理剩余注册。
8. **第三方异常不能击穿 DevTool**：Inspector 调用继续经过现有异常边界；扩展清理本身也必须异常安全。
9. **旧入口不强拆**：已有 `DevToolObjectApi`、`ObjectCatalog`、`ObjectInspectorRegistry` 保持可用；新 API 是推荐入口，不在第五阶段制造源码级破坏。
10. **UI 生命周期不等于 Mod 生命周期**：切换 New UI / Vanilla、重建 ImGui retained state、换房间都不能自动注销第三方扩展。扩展只由拥有它的 Mod 明确释放。

## 已完成的公共扩展层

入口位于：

```text
DryCycle.DevUI.DevTool.Extensions
└── DevToolApi
```

### API 版本与 Capability

当前公共契约版本：

```text
1.0
```

`DevToolApiVersion` 使用 Major / Minor 语义：

- Major 不同：不兼容。
- Major 相同、宿主 Minor 大于等于扩展要求：兼容。
- 新增向后兼容能力时只增加 Minor。
- 破坏现有公共契约时才增加 Major。

当前 Capability：

- `ObjectDescriptors`
- `ObjectInspectors`
- `ScopedRegistrations`
- `ExtensionDiscovery`
- `FaultIsolatedInspectors`

扩展可以先调用：

```csharp
DevToolApi.Supports(
    new DevToolApiVersion(1, 0),
    DevToolCapability.ObjectDescriptors |
    DevToolCapability.ObjectInspectors |
    DevToolCapability.ScopedRegistrations);
```

也可以直接使用 `TryRegisterExtension(...)` 做非抛异常协商。

## 推荐接入方式

外部 Mod 应把 Scope 保存到自己的插件生命周期中：

```csharp
using DryCycle.DevUI.DevTool.Extensions;

private DevToolExtensionScope devTool;

public void OnEnable()
{
    if (!DevToolApi.TryRegisterExtension(
            "example.my-mod",
            out devTool,
            displayName: "My Mod",
            version: "1.0.0",
            requiredApi: new DevToolApiVersion(1, 0),
            requiredCapabilities:
                DevToolCapability.ObjectDescriptors |
                DevToolCapability.ObjectInspectors |
                DevToolCapability.ScopedRegistrations))
        return;

    devTool.RegisterObjectDescriptor(
        MyPlacedObjectType,
        "My Object",
        "My Mod",
        new[] { "example", "custom" });

    devTool.RegisterInspector<MyPlacedObjectData>(inspector => inspector
        .Float("strength", "Strength", d => d.strength, (d, v) => d.strength = v, 0f, 1f)
        .Boolean("enabled", "Enabled", d => d.enabled, (d, v) => d.enabled = v));
}

public void OnDisable()
{
    devTool?.Dispose();
    devTool = null;
}
```

### 为什么必须保存 Scope

过去直接调用静态注册表的模式容易留下两个问题：

- Mod 热重载 / Disable 后，Registry 里还握着旧程序集对象或委托。
- 开发者必须记住每一个注册项，再逐个调用对应的 Unregister。

Scope 把这些注册变成一个资源栈。`Dispose()` 会按注册逆序撤销全部剩余贡献；单项 `DevToolRegistration.Dispose()` 也可以提前释放，而且重复 Dispose 不会重复注销。

## Object Descriptor

`DevToolExtensionScope.RegisterObjectDescriptor(...)` 有两种形式：

1. 直接注册完整 `ObjectDescriptor`。
2. 传入 `PlacedObject.Type / displayName / category / tags`，Source 自动使用扩展的 `DisplayName`。

优先级仍由 `ObjectCatalog` 统一裁决：高 Priority 覆盖同一 `PlacedObject.Type` 的低 Priority 元数据。释放注册后，Catalog 会失效缓存并重新落回下一层描述或 Rain World 默认推导。

这只影响编辑器呈现，不接管对象创建、序列化或保存。

## Object Inspector

第三方可以：

- 直接实现 `IObjectInspectorAdapter`；
- 或使用 `RegisterInspector<TData>(...)` 构建强类型 Inspector。

强类型定义继续支持：

- ReadOnly
- Float
- Integer
- Boolean
- String
- Vector2
- Color
- Enum
- Action

Inspector Snapshot 是脱离 ImGui 的中立数据。外部 Mod 不需要引用 `DryCycle.DevTool.RWImGui.dll`。

异常仍在 `ObjectInspectorRegistry` 的调用边界内捕获并记录，不允许第三方 Inspector 异常直接打断 DevTool Present / Command 链路。

## Extension Discovery

`DevToolApi.GetExtensions()` 返回脱离内部 Registry 的只读快照，包括：

- `Id`
- `DisplayName`
- `Version`
- 当前仍有效的 `RegistrationCount`

`TryGetExtension(id, out snapshot)` 可查询单个扩展。返回对象不能用于修改 Registry，也不暴露 Scope 本身。

这里主要用于诊断、兼容面板和后续扩展管理，不应该被用于建立“Mod A 依赖 Mod B 的 DevTool 注册状态”这种新的耦合。

## 与通用第三方兼容层的关系

第五阶段没有替换既有 Level 2 / Level 3 兼容路径。

```text
Level 1  DevTool Extension API
         第三方主动注册原生 Metadata / Inspector

Level 2  Generic DevInterface Bridge
         标准 Button / Slider / Cycler / Select / Text / Handle 等按行为协议镜像

Level 3  Rain World Data Model
         PlacedObject / RoomSettings / World / ExtEnum 等本体数据继续自然进入编辑器

Level 4  Vanilla fallback
         无法安全理解的特殊交互退回原版 DevUI
```

因此：

- 第三方不引用 DryCycle：仍然有 Level 2～4。
- 第三方引用 API：在基础兼容之上获得 Level 1 原生体验。
- 第三方 API 注册失败：不能破坏 Level 2～4。

## 不在第五阶段公开的接口

### ImGui / RWImGui 绘制回调

不公开。让第三方直接向 Draw 阶段注入 ImGui 会破坏 Snapshot / Command 边界、输入仲裁和主 DLL 不依赖 ImGui 的架构。

### 任意内部 Backend Command

不公开。第三方不能通过扩展 API 绕过 Document History、Input Router、Save 和 Revision / Dirty 语义。

### 按 Mod 名称的“兼容声明”

不公开，也不新增。兼容判断继续基于公共 Rain World 行为协议；不能让一个扩展通过声明字符串把实际无法镜像的控件伪装成“已兼容”。

### 自定义 Gizmo ABI

本阶段不仓促公开。当前世界 Handle 仍由通用 `DevInterface.Handle` 路径保留；一个真正稳定的自定义 Gizmo ABI 需要同时冻结输入所有权、命中测试、History transaction、scene-space snapshot 和前端绘制协议。第五阶段宁可不发布半成品 ABI，也不把后续第六阶段清债锁死。

## 兼容保证

在 `1.x` 内：

- 不删除已有 public 类型、成员或 Capability 位。
- 不改变已有方法的核心语义。
- 新能力通过新的 Capability 位暴露。
- 扩展必须只依赖自己实际声明过的 Capability。
- Scope Dispose 保持幂等。
- Registration Dispose 保持幂等。
- UI retained-state Reset 不会隐式释放扩展 Scope。

## 第五阶段验收结论

- 已建立统一公开入口，而不是让外部开发者到多个内部 Registry 自行拼接生命周期。
- 已建立 Major / Minor 版本协商与 Capability 协商。
- 已建立 extension-level 生命周期、registration-level 生命周期和只读 discovery。
- 已把 Object Descriptor / Inspector 接到同一 Scope 下。
- 已保留旧 `DevToolObjectApi`，避免第五阶段自身制造破坏性迁移。
- 已明确 Level 1 主动接入与 Level 2～4 被动兼容的责任边界。
- 已明确禁止前端注入、内部 Command 绕过和伪兼容声明。
- 未合并 PR；第五阶段完成后由维护者单独决定是否进入 `main`。

## 尚需由游戏环境执行的验证

仓库当前没有覆盖 Rain World 安装环境、HookGen、RuntimeDetour、RWImGui 的完整 CI，因此以下验证仍属于合并前运行时检查，而不是第五阶段继续扩展架构的理由：

1. 使用实际游戏安装引用联编 `DryCycle.dll` 与 `DryCycle.DevTool.RWImGui.dll`。
2. 用一个最小外部测试 Mod 注册 Descriptor + Inspector，确认 Enable / Disable / Reload 后无重复项和旧委托残留。
3. 在 New UI / Vanilla 多次切换、换房间后确认 Scope 注册仍存在。
4. Dispose 单个 Registration 后确认 `RegistrationCount` 只统计仍有效注册。
5. Dispose Scope 后确认对象 Catalog 回落、Inspector 回落到通用/安全 fallback。
