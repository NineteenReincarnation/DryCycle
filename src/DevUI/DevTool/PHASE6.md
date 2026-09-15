# DevTool Phase 6 — 架构合并 / 清债 / 最终代码封板

第六阶段是本轮 DevTool 重构的最后一个架构阶段。目标不是继续堆新功能，而是把前五个阶段留下的临时边界、重复实现、专用兼容桥和生命周期分散点收口，最终得到一套可以长期维护、可以公开给第三方依赖、并且不再依靠“先别动这块”的稳定架构。

本阶段完成前，DevTool 仍视为重构期代码；本阶段完成后，除明确的新功能需求外，不再允许以“兼容某个 Mod”“先临时接一下”“前端方便”为理由打破已经冻结的层级边界。

## 最终封板原则

1. **核心不认识具体第三方 Mod**
   - Core / Objects / Room / Sound / Triggers / Map / Dialog / Relationships 不允许出现按第三方框架名称编写的运行时适配器。
   - 不允许为了获得“原生体验”在核心中反射 POM、RegionKit、Fisobs、M4r 等私有类型。
   - 第三方原生增强统一走 Phase 5 的 `DevToolApi`；未接 API 的第三方走 Generic DevInterface / Rain World Data Model / Vanilla fallback。

2. **前端依赖只能单向**
   - `DryCycle.dll` 的 DevTool 后端不引用 ImGui / RWImGui。
   - `DryCycle.DevTool.RWImGui.dll` 只读取 detached Presentation Snapshot、维护纯前端 retained projection、发送 Command。
   - Draw 阶段不得执行 Command Queue、History、Revision、Compatibility Audit 或 live DevInterface 扫描。

3. **状态只能有一个权威拥有者**
   - Document / History / Selection / Revision / Command Queue / Presentation Hub 不允许在不同模块维护第二份可变真相。
   - 前端 retained projection 只能缓存派生数据，不拥有业务状态。

4. **生命周期必须成对且集中**
   - Enable / Disable、Hook 安装 / 卸载、Queue Clear、Hub Reset、retained-state release 必须能从有限的生命周期入口追踪。
   - 禁止新增“某个页面关闭时顺便清某个全局状态”的隐式所有权。

5. **兼容层只描述协议，不复制第三方实现**
   - Compatibility 允许识别 Rain World / DevInterface 的公共行为协议。
   - 不允许继续扩展按 Mod / 类型名维护的大型特殊兼容表。
   - 特殊语义无法安全推导时，回退 Vanilla，而不是猜测第三方私有数据。

6. **公共 Extension API 进入 1.x 冻结期**
   - Phase 5 已发布的 public 类型和核心语义不在第六阶段随意破坏。
   - 新能力必须通过 Capability 增量暴露；破坏性调整必须有明确 Major 升级理由。

7. **稳定帧不能重新退化**
   - Phase 2 的 Revision / Dirty、Phase 4 的虚拟化 / 可见区计算继续作为硬约束。
   - 最终清债不得为了“代码看起来更统一”重新引入整表扫描、整 Snapshot 重建或重复 Synchronize。

8. **封板优先于功能扩张**
   - 自定义 Gizmo ABI、Command Palette、更多高级编辑功能等不因为“顺便”进入本阶段。
   - 只有为了消除架构债、保持行为一致或完成验证所需的改动才进入 Phase 6。

## 工作流 A — 清除专用第三方适配器

已完成首轮封板：

- 移除 `PomManagedDataInspectorAdapter`。
- 移除 `RegionKitAdvancedShaderInspectorAdapter`。
- 移除 `ObjectCatalog` 中对 POM Inspector 的静态自动注册。
- 清理 RWImGui 中删除适配器后残留的 RegionKit 专用前端判断。
- 增加全目录静态守卫，阻止 Objects/Core 等功能模块重新出现 POM / RegionKit / Fisobs / M4r 私有运行时类型依赖。

正确的依赖方向固定为：

```text
第三方主动接入
    ↓
DevTool Extension API
    ↓
Native Metadata / Inspector

未主动接入
    ↓
Generic DevInterface Bridge
    ↓
Rain World Data Model
    ↓
Vanilla fallback
```

Compatibility 中允许保留 `RegionKit` 等来源名称用于**诊断分类**；来源统计不等于运行时适配。业务编辑语义不得依赖这些名称。

## 工作流 B — Runtime / Lifecycle 收口

已完成主要收口：

- 新增 `DevToolSubsystemCoordinator`，统一拥有跨功能模块的 Command Queue processing、Queue Clear、Presentation Clear、State Reset、Revision/Session Reset。
- `DevToolRuntime` 回归到 Hook、帧顺序和 Presentation 调度职责，不再直接维护七套 Queue/Clear 列表。
- DevTools 从 live → dormant 时不再由 Compatibility 复制一套 Queue / Hub 清理；统一委托 `DevToolSubsystemCoordinator.ResetRuntimeState()`。
- Extension Scope 生命周期明确不属于 UI Runtime Reset；外部 Mod 注册的 scope 仍由外部 Mod 自己 Dispose。
- Frontend retained state 继续由 RWImGui 自己释放，避免后端反向引用前端程序集。

当前生命周期所有权：

```text
DevToolRuntime
    Hook + frame orchestration
            ↓
DevToolSubsystemCoordinator
    backend queues / presentations / workspace state / revision / session
            ↓
Feature modules
    local model + local snapshot cache

Extension Scope
    external mod owns lifetime

RWImGui retained projection
    frontend owns lifetime
```

## 工作流 C — Presentation / Command / History / Revision 边界

已完成第一轮完整复核：

- RWImGui 未发现直接调用 `EditorActions` / `RoomEditorActions` / `SoundEditorActions` / `TriggerEditorActions` / `MapEditorActions` / `DialogEditorActions` / `RelationshipEditorActions` 的写模型旁路。
- Universal DevUI Command Queue 已并入统一 backend Command phase，不再从 Presentation getter / Draw 阶段执行。
- `UniversalDevUiPresentationHub.Current` 已变成 O(1) detached snapshot getter；live DevInterface capture 由后端显式发布。
- History Service 保持模型修改后的权威 revision 发布点；Command Queue 仅为没有进入 History 的可见变化补 revision。
- History 的 retained key 与 Shell/workspace revision 分离：History.Revision 保持每次栈变化精确递增，而同帧 Presentation dirty 可以合并，避免重复 rebuild。
- 已复核 Room / Sound 的 batch 语义：进入 History 的正常模型修改不再由 Queue 重复 bump workspace revision；非 History 的目录/模板等可见变化保留直接 invalidation 兜底。

当前约束：

```text
RWImGui Draw
    ↓ only enqueue
Command Queue
    ↓ main-thread ordered execution
Actions / Generic Action Bridge
    ↓ model mutation + History
History / explicit non-history invalidation
    ↓ Revision + semantic hint
Presentation Hub
    ↓ detached snapshot
RWImGui Draw
```

## 工作流 D — Compatibility / Diagnostics 收口

已完成核心收口。

兼容诊断现在也遵守 Presentation 单向边界：

```text
Backend diagnostics phase
    DevUiGenericProtocolBootstrap.Ensure
    UniversalDevUiPresentationHub.Publish
    DevUiPageCoverageTracker.Observe
    DevUiProtocolInventory.ObserveLoadedTypes
    DevUiSemanticConformanceAudit.Evaluate
    DevUiCompatibilityGate.Evaluate
            ↓
    detached diagnostic snapshots
            ↓
RWImGui diagnostics views
    read only
```

具体改动：

- 新增 `DevUiDiagnosticsPublisher` 作为后端兼容诊断发布入口。
- `DevUiPageCoverageTracker.Current` 改成纯 snapshot getter。
- `DevUiProtocolInventory.Current` 改成纯 snapshot getter。
- Semantic Conformance 与 Compatibility Gate 不再从 ImGui Draw 中执行 `Evaluate()`。
- Generic protocol bootstrap 不再从 ImGui Draw 中执行。
- Universal mirror capture、loaded-type inventory、page coverage、semantic audit 和 gate evaluation 只在 `DevUiDiagnosticsPolicy.Enabled` 时运行；正常编辑帧不承担这些反射扫描成本。
- Final Architecture Guard 已禁止 RWImGui 调用这些诊断 side-effect 入口。

## 工作流 E — 代码清债

正在进行。

已处理：

- 专用第三方 Inspector / 前端残留引用。
- Runtime / dormant 生命周期重复 Clear/Reset。
- Universal Presentation getter 的隐式写操作和隐式扫描。
- Compatibility diagnostics 从 Draw 驱动改为 backend publication。
- 若干与当前 ownership 不一致的阶段性注释。

仍需继续：

- 扫描无调用类、旧 bridge 和只为早期迁移存在的兼容入口。
- 清理剩余过期注释和 README 中与最终架构不一致的描述。
- 审查过宽 internal/public surface；Phase 5 已发布 API 不做破坏性收缩。
- 检查重复 Reset / Invalidate / Refresh 边界是否还有小规模残留。

## 工作流 F — 最终守卫与验证

已建立并持续扩展 `DevTool Final Architecture Guard`，当前覆盖：

- 后端禁止依赖 ImGuiNET / RWImGui。
- RWImGui 禁止直接调用各 Workspace Actions。
- Universal Presentation / PageCoverage / ProtocolInventory 的 `Current` 必须保持纯 detached snapshot getter。
- RWImGui 禁止执行 Universal Command Queue、Universal Publish、Generic Protocol Bootstrap、Page Coverage Observe、Protocol Inventory Observe、Semantic Evaluate、Compatibility Gate Evaluate。
- DevTool Runtime 与 dormant cleanup 必须走统一 `DevToolSubsystemCoordinator`。
- Extension API scope 不得被 UI/runtime reset 接管。
- 删除的 POM / RegionKit 专用 Inspector 不得重新出现或留下悬空引用。
- Compatibility 以外的功能模块禁止重新依赖第三方私有运行时类型。

仍需完成的验证：

1. PR 最终静态差异审查。
2. 可用环境下的 `DryCycle.dll` + `DryCycle.DevTool.RWImGui.dll` 完整联编。
3. Rain World 内基本回归：
   - New UI / Vanilla 切换
   - Save / Undo / Redo
   - Objects / Room / Sound / Triggers / Map / Dialog / Relationships
   - Generic DevInterface 第三方控件回退
   - Extension API 注册 / Dispose
4. 性能回归：确认稳定帧没有重新出现整表扫描和重复 Snapshot rebuild。

仓库当前没有覆盖真实 Rain World 安装引用、HookGen、RuntimeDetour 与 RWImGui 的完整通用 CI，因此在拿到实际运行结果前，不把“静态守卫通过”写成“进游戏验证通过”。

## 当前进度

**第六阶段：约 65%。**

已经完成的主体架构清债：

- 第三方专用适配器移除与依赖方向冻结。
- Runtime / dormant backend lifecycle 收口。
- Command Queue fan-out 和 Reset/Clear 所有权统一。
- Universal DevUI Command / Presentation 边界收口。
- RWImGui 原生 Workspace 写模型边界复核。
- History / Revision / semantic hint 第一轮复核。
- Compatibility diagnostics 全链路后端化。
- Final Architecture Guard 已覆盖主要冻结边界。

剩余工作主要集中在：

1. 死代码、旧 bridge、旧 workaround 和过期文档的第二轮清债。
2. PR 全量 diff 的最终架构审查。
3. 完整联编环境验证。
4. Rain World 游戏内功能/兼容/性能回归。
5. 所有验证完成后更新 README、把 PR 从 Draft 转为 Ready for Review；未经明确指示不合并。
