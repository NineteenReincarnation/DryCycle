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
   - `DryCycle.DevTool.RWImGui.dll` 可以读取公开/内部 Presentation Snapshot 并发送 Command，反向依赖禁止。

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

### 已开始

本阶段第一批清债已经移除：

- `PomManagedDataInspectorAdapter`
- `RegionKitAdvancedShaderInspectorAdapter`
- `ObjectCatalog` 中对 POM Inspector 的静态自动注册

原因不是取消第三方兼容，而是恢复正确的依赖方向：

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

删除这些专用反射桥之后，核心不再通过第三方私有类型名读取 ManagedData / AdvancedShader 私有字段。第三方如果希望获得 Level 1 原生 Inspector，应通过 Phase 5 API 主动注册；否则仍保留 Level 2～4 的基础兼容路径。

### 后续检查

- 扫描 DevTool 核心目录中残留的第三方私有类型名、程序集名和专用反射分支。
- 区分“诊断来源分类”和“运行时特殊适配”：前者可用于兼容统计，后者必须清除或泛化为协议能力。
- 确保删除专用适配后 Generic DevInterface / Vanilla fallback 路径仍保持可达。

## 工作流 B — Runtime / Lifecycle 收口

当前 `DevToolRuntime` 同时承担：

- Hook 安装 / 卸载
- 子系统 Enable / Disable
- Command Queue 清理
- Presentation Hub 清理
- State Hub / Revision / Session Reset
- 更新帧编排
- Presentation 发布调度

第六阶段将把这些职责收口为更明确的生命周期组件，使 `DevToolRuntime` 最终只负责运行时 Hook 和每帧编排。

目标：

- Queue / Hub / State 的释放顺序只有一个定义位置。
- 新增模块时不需要去多个 Disable / Reset 路径补同一份逻辑。
- Frontend retained-state 生命周期与 backend runtime 生命周期继续分离。
- 不改变 Phase 2 已验证的稳定帧调用次数与 Dirty 行为。

## 工作流 C — Presentation / Command 边界复核

检查重点：

- Draw 阶段是否仍存在直接修改 Rain World 数据的遗留入口。
- 是否有 Command 在绕过 History / Revision hint 后直接写业务状态。
- 是否存在同一操作在 Generic Bridge 和 Native Workspace 中各维护一套提交逻辑。
- Presentation Hub 是否存在重复缓存、重复版本键或可合并的简单桥接类。

清理原则：优先合并“重复所有权”，不为了减少文件数量而制造大类。

## 工作流 D — Compatibility / Diagnostics 收口

兼容审计工具需要保留，因为它们用于确认未知 Mod 的公共 DevInterface 协议是否仍被覆盖；但第六阶段会区分：

- **允许**：按公共协议分类、来源统计、未知控件审计、语义一致性检查。
- **禁止**：通过具体第三方类型名直接实现业务编辑语义。

最终要求：Compatibility 层可以告诉开发者“这里有一个未知协议缺口”，但不应该变成“给每个 Mod 再写一个 Adapter”的长期入口。

## 工作流 E — 代码清债

逐项处理：

- 已无调用的类、静态注册器和旧兼容入口。
- 重复的 Reset / Clear / Invalidate 组合。
- 已过期注释、阶段性 workaround 和与当前架构不一致的 README 描述。
- 可以安全合并的桥接类、命名不一致和临时目录结构。
- 过宽 public API：能保持兼容的前提下收紧新代码的可见性；Phase 5 已发布 API 不破坏。
- 只为调试阶段存在、但正式架构不再需要的统计或探针。

不以“代码行数更少”为目标；以所有权清晰、依赖方向稳定和未来修改成本更低为目标。

## 工作流 F — 最终守卫与验证

第六阶段结束前至少需要：

1. 架构静态守卫：
   - 后端不依赖 RWImGui / ImGuiNET。
   - Extension API 不依赖第三方框架。
   - Objects/Core 等核心模块不重新出现专用第三方反射 Inspector。
2. PR 静态差异审查。
3. 可用环境下的 `DryCycle.dll` + `DryCycle.DevTool.RWImGui.dll` 完整联编。
4. Rain World 内基本回归：
   - New UI / Vanilla 切换
   - Save / Undo / Redo
   - Objects / Room / Sound / Triggers / Map / Dialog / Relationships
   - Generic DevInterface 第三方控件回退
   - Extension API 注册 / Dispose
5. 性能回归：确认稳定帧没有重新出现整表扫描和重复 Snapshot rebuild。

仓库当前没有覆盖真实 Rain World 安装引用、HookGen、RuntimeDetour 与 RWImGui 的完整通用 CI，因此在拿到实际运行结果前，不把“能静态审查”写成“进游戏验证通过”。

## 当前进度

第六阶段已启动，当前处于第一轮架构债清理。

已完成：

- 从最新 `main` 建立独立 Phase 6 分支。
- 明确最终封板原则与验收范围。
- 移除 POM 专用反射 Inspector 自动注入。
- 移除 RegionKit AdvancedShader 专用反射 Inspector。
- 恢复 Object Catalog 对第三方框架零认知的依赖方向。

接下来优先处理：

1. 扫描并清除剩余第三方专用运行时适配分支。
2. 收口 `DevToolRuntime` 生命周期与 Reset/Clear 所有权。
3. 复核 Presentation / Command / History 边界。
4. 增加 Phase 6 最终架构守卫。
5. 清理死代码与过期文档，进行最终验证。
