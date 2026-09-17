# DevTool Page / View 内部架构

本文档描述 **DryCycle 自己维护的 RWImGui 页面组合层**。它不是第三方 Mod API，也不是 `DevToolApi 1.x` 的一部分。

## 1. 边界

页面体系分成三层：

```text
DryCycle.dll
    EditorSession / Command / Revision / Presentation Snapshot
        ↓ detached snapshot
DryCycle.DevTool.RWImGui.dll
    IDevToolPage + IDevToolPageView
        ↓ one composite page object
    DevToolPageViewRegistry
        ↓
    DevToolOverlay / SceneWorkspaceWindow / ControlCenterWindow
```

硬规则：

- `IDevToolPage`、`IDevToolPageView`、`IDevToolFrontendPage`、`DevToolPageViewRegistry` 都是 **internal**。
- 这些类型可以依赖 `InternalsVisibleTo("DryCycle.DevTool.RWImGui")` 访问 backend internal contract，但不得转化成 public ABI。
- 第三方扩展继续使用 `DryCycle.DevUI.DevTool.Extensions.DevToolApi 1.x`。
- 1.x Extension API 不提供 ImGui 绘制回调、页面注册、窗口注入或 `IDevToolPageView` 实现入口。
- 如果未来确实需要第三方页面扩展，应作为单独设计的新 capability / 新 ABI 评审，不得直接把当前 internal 接口改成 public。

## 2. 一个页面只有一个权威对象

每个 `EditorToolMode` 在 `DevToolPageViewRegistry` 中只注册一个 `IDevToolFrontendPage` 实例。

该对象同时拥有：

- 稳定 `Id`
- `EditorToolMode`
- `Activate / Deactivate / Reset`
- Activity Bar 导航信息
- Browser / Inspector / Workspace / Scene 绘制入口
- 页面能力声明
- 页面状态文本投影

禁止再建立第二份：

- ToolMode → View 字典
- ToolMode → 导航标题表
- ToolMode → Reset 回调表
- ToolMode → Scene 支持表
- ToolMode → Control Center 状态 switch

页面差异必须优先通过页面对象上的 metadata / capability / virtual method 表达。

## 3. 页面生命周期

正常页面切换：

```text
EditorSession.ToolMode changes
    ↓
DevToolRetainedViewLifecyclePlugin.LateUpdate
    ↓
DevToolPageViewRegistry.SynchronizeActive(mode)
    ↓
previous.Deactivate()
next.Activate()
```

`Activate / Deactivate` 必须幂等。`Reset` 只释放前端 retained projection，不修改：

- Editor document
- Selection
- History
- Revision
- RoomSettings / World model

独立 Debug workspace 是替代工作区，不是覆盖层。`DevToolOverlay.SuppressesSharedPageSurfaces == true` 时必须保持普通 page lifecycle deactivated。

## 4. Draw 只能消费 Snapshot、发送 Command

页面 View 不拥有业务模型。

允许：

- 读取 `EditorPresentationSnapshot` 或 feature PresentationHub 的 detached snapshot。
- 维护纯前端搜索字符串、折叠状态、布局缓存、格式化文本缓存等 retained projection。
- 向对应 Command Queue enqueue 用户操作。

禁止：

- Draw 中直接调用 feature `Actions` 修改模型。
- Draw 中执行 Command Queue。
- Draw 中推进 History / Revision。
- Draw 中扫描 live DevInterface tree。
- Draw 中执行 Compatibility Audit。
- 为了刷新 UI 主动重建整个 backend snapshot。

数据方向固定为：

```text
Draw
    ↓ enqueue
Command Queue
    ↓ main thread
Actions / model mutation
    ↓
History / Revision / semantic hint
    ↓
Presentation Hub
    ↓ detached snapshot
Draw
```

## 5. 页面能力

共享 chrome 不应该知道具体页面类型。

当前通用能力包括：

- `UsesDedicatedWorkspace`
- `SupportsSceneSurface`
- `SupportsPlacementInput`
- `SuppressInspector(snapshot)`
- `LegacyFallbackTooltip`

例如 Objects 声明 `SupportsPlacementInput = true`，因此 `DevToolOverlay` 只检查 capability，不允许重新写 `ToolMode == Objects` 的共享分支。

如果以后出现新的共享行为差异，应先判断它是不是可复用的页面能力；不要在 Overlay / ControlCenter / SceneWindow 中添加七页面 switch。

## 6. Scene ownership

`SceneWorkspaceWindow` 只是共享壳：

```text
SceneWorkspaceWindow
    ↓
page.SupportsSceneSurface
    ↓
page.DrawSceneWorkspace(snapshot)
```

Objects 的中心 Scene 逻辑属于 `ObjectSceneWorkspaceView`；Sound / Triggers 的 scene workspace 属于各自页面 View。

`SceneWorkspaceWindow` 不保存 feature retained state，不应拥有 `ResetRetainedState()`。

`ScenePlacementWindow` 自己确实拥有窗口投影缓存，因此由 frontend lifetime 单独 Reset。

## 7. Browser / Inspector / Workspace ownership

页面组合根位于 `BuiltinDevToolPages.cs`。

这是允许具体 feature View 与通用 page contract 相遇的位置，例如：

```text
ObjectsDevToolPage
    Browser   -> ObjectExplorerView
    Inspector -> ObjectInspectorView
    Scene     -> ObjectSceneWorkspaceView
```

共享 chrome 不允许直接引用：

- `ObjectExplorerView`
- `SoundEditorView`
- `TriggerEditorView`
- `MapEditorView`
- `DialogEditorView`
- `RelationshipEditorView`
- 各 feature PresentationHub

## 8. Session status

Control Center 不维护页面状态 switch。

每个 page 通过：

```text
BuildSessionStatusState(snapshot)
    ↓ semantic state
FormatSessionStatus(state)
    ↓ cached string
GetSessionStatus(snapshot)
```

基础类按 semantic state + language 缓存结果。不要在稳定帧每次重新拼接字符串。

## 9. 新增内部页面的步骤

如果 DryCycle 新增一个正式 `EditorToolMode`：

1. 在 backend 定义 ToolMode、Revision channel、Presentation/Command 语义。
2. 在 RWImGui 中新增一个 `DevToolFrontendPageBase` 子类。
3. 提供唯一 `Id`、`Mode`、导航 metadata。
4. 仅声明真正需要的 capability。
5. 在 `DevToolPageViewRegistry` 注册一次。
6. 页面自己的 retained state 在 `OnReset()` 中释放。
7. 不修改共享 chrome 来添加页面专用 switch。
8. 如果有 Scene，走 `DrawSceneWorkspace`；如果有独立中央工作区，走 `DrawWorkspace`。
9. 更新相应 architecture guard。
10. 使用 `scripts/validate-devtool-phase6-local.ps1` 在真实 Rain World + RWImGui 依赖环境做 backend/frontend 联编。

## 10. 构建边界

`DryCycle.dll` 明确排除 `DevUI/DevTool/RWImGui/**/*.cs`。

`DryCycle.DevTool.RWImGui.csproj`：

- 使用 `Microsoft.NET.Sdk`
- 使用 SDK 默认 Compile glob
- AssemblyName 固定为 `DryCycle.DevTool.RWImGui`
- 引用当前构建输出中的 `DryCycle.dll`
- backend 通过 `InternalsVisibleTo("DryCycle.DevTool.RWImGui")` 开放 internal contract

因此不要维护手写 `<Compile Include>` 文件清单，也不要把 RWImGui 源码重新编进 `DryCycle.dll`。

## 11. Public Extension API 与内部 Page API 的关系

两者是两条不同的扩展轴：

```text
第三方 Mod
    ↓
DevToolApi 1.x
    ↓
Object descriptors / inspectors / scoped registrations

DryCycle 自身 frontend
    ↓
internal Page / View composition
    ↓
RWImGui windows
```

当前禁止交叉：第三方 extension scope 不持有 frontend page 生命周期；frontend page registry 也不发现或加载第三方 extension 类型。

这样可以保证没有安装 RWImGui 时 `DryCycle.dll` 和第三方 backend integration 仍然成立，同时避免把 ImGui ABI、窗口生命周期和 retained-state 规则永久冻结进 `DevToolApi 1.x`。
