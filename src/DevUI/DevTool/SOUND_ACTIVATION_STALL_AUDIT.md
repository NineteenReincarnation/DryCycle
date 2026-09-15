# DevTool Sound 首次激活卡顿审计

> 本文只做诊断与方案设计，不修改 Sound 行为、数据格式或运行时代码。
>
> 审计基线：`main` @ `6cda4e43d4a31f23760a64f2c6653365ae265adc`。

## 1. 结论

新 UI 点击 **Sound** 时的明显卡顿，核心不是某一个 ImGui 控件画得慢，也不是列表虚拟化失效，而是 **Sound 的冷启动工作没有独立生命周期，多个“一次性初始化”在第一次进入 Sound 的同一帧串行爆发**。

当前架构实际近似：

```text
点击 Sound
  ↓
ToolMode 切到 Sound
  ↓
同一帧立即要求完整 Sound Presentation + 完整 Sound UI
  ↓
首次访问 SoundSampleCatalog
  ├─ 枚举声音资源/目录
  ├─ 解析候选路径
  ├─ AssetManager.ResolveFilePath / File.Exists
  ├─ 规范化路径
  └─ 判定 Vanilla / DLC / Mod 来源
  ↓
首次访问 SoundGroupLibrary
  ├─ 枚举各来源 Group 配置
  ├─ XDocument.Load
  ├─ 解析所有 Group / Sample 引用
  └─ 再调用 SoundSampleCatalog.Resolve
  ↓
Sound backend snapshot / revision 首次建立
  ↓
SoundLibraryGroupsView 首次建立 retained UI projection
  ├─ Group presentation
  ├─ Sample presentation
  ├─ 搜索/来源/问题状态字符串
  └─ 列表 run / visible projection
  ↓
最后才提交 ImGui UI
```

这些工作绝大部分之后都有缓存，所以 **Sound 打开以后可以很顺，但第一次点击仍会卡**。

这就是此前几轮优化一直无法彻底解决现象的根本原因：之前主要优化的是 **steady-state cost（稳定帧成本）**，而用户看到的是 **cold activation spike（冷启动尖峰）**。

---

## 2. 当前代码中冷启动工作集中在哪里

### 2.1 `SoundSampleCatalog`：第一层同步资源索引

文件：

`src/DevUI/DevTool/Sound/SoundSampleCatalog.cs`

该类已经做了缓存，但缓存第一次建立仍然需要同步完成资源发现与解析。

冷启动路径包含：

- 声音目录/候选资源枚举；
- Vanilla / DLC / active mods 的来源判断；
- `AssetManager` 路径解析；
- `File.Exists` 等文件系统访问；
- 路径 normalize / compare；
- sample 名称、路径、来源 metadata 建立。

其中最需要注意的不是某一次调用有多慢，而是它们会随着 sample 数量与 mod/source 数量放大。

可近似理解为：

```text
候选 Sample 数量
×
路径解析 / 来源判断 / Mod root 比较
```

即使每个操作本身不大，一次性集中处理整个声音库，也足够制造明显主线程长帧。

### 2.2 `SoundGroupLibrary`：第二层同步 XML 加载，而且会回调 Sample Catalog

文件：

`src/DevUI/DevTool/Sound/SoundGroupLibrary.cs`

第一次需要 Group 数据时，会同步完成：

- 找 Group 配置文件；
- 遍历 active mod / 标准配置来源；
- `XDocument.Load`；
- 解析 Group；
- 解析 Group 中引用的声音；
- 对引用声音再次进入 `SoundSampleCatalog.Resolve`。

因此它不是一个与 Sample Catalog 完全独立的成本。

冷启动时形成了：

```text
Sample 资源索引
       ↑
       │ Resolve
Group XML 解析
```

也就是说第一次 Sound 激活既做“大资源表”，又做“Group XML”，两者还存在交叉解析。

### 2.3 `SoundEditorRuntime`：首次 Sound Presentation 仍要求数据现在就可用

文件：

`src/DevUI/DevTool/Sound/SoundEditorRuntime.cs`

当前 Revision / Snapshot 架构已经明显减少后续重复刷新，但它解决的是：

> 数据没变时，不要重新生成整个 Sound presentation。

它没有解决：

> 第一次进入 Sound 时，数据还不存在，能不能不在这一帧全部生成。

所以第一次 cache miss 仍然必须支付完整成本。

### 2.4 `SoundWorkspaceState`：首次进入时同步工作集

文件：

`src/DevUI/DevTool/RWImGui/SoundWorkspaceState.cs`

它负责 Sound UI 工作状态与 Group/selection 等同步。

本身不是最重的磁盘 I/O 源，但它属于第一次进入 Sound 时必走的初始化链，进一步说明目前系统没有“Sound 正在准备”的中间态：UI 第一次出现时就默认所有工作集必须准备完毕。

### 2.5 `SoundLibraryGroupsView`：已有 retained cache，但首次 projection 仍然集中发生

文件：

`src/DevUI/DevTool/RWImGui/SoundLibraryGroupsView.cs`

这里已经有：

- retained presentation；
- sample projection cache；
- list clipper / visible-run virtualization；
- stable-frame allocation 优化。

这些都是正确优化。

问题在于：

> retained cache 只能让第二帧以后便宜，无法让第一次建立 cache 免费。

首次打开 Sound，仍要生成：

- Group presentation；
- Sample presentation；
- 搜索/分类/来源相关派生数据；
- UI row labels / problem state；
- run/projection 索引。

所以 UI 层自身又给冷启动尖峰叠加了一段 CPU / allocation 成本。

### 2.6 Sound 点击入口没有 Activation Boundary

`DevToolOverlay` 在 ToolMode 变成 Sound 后，同一 Render 链立刻进入 Sound draw。

目前缺少类似：

```text
Sound requested
→ Sound preparing
→ Sound ready
→ Sound UI consumes ready snapshot
```

当前实际只有：

```text
Not Sound
→ Sound
```

因此所有 lazy initialization 的“债”都由第一次 Sound Draw/Presentation 来偿还。

---

## 3. 为什么前几次修复没有解决

### 3.1 `Optimize DevTool sound sample discovery`

Commit：`e0102ac624d88da209d1fd97a8942d9bbb4bff8a`

它优化了声音资源发现方式，减少重复目录/路径工作。

这是有效优化，但本质仍是：

```text
以前：第一次点击做一大坨较低效工作
后来：第一次点击做一大坨更高效工作
```

没有改变“第一次点击必须同步完成整个索引”的调度模型。

所以总量下降了，**尖峰仍存在**。

### 3.2 `Keep sound resource resolution on game thread`

Commit：`b1ec94e28fba98a7f75b8f0f9b542e70b572af41`

这个改动是正确的安全收口：Sound 资源解析涉及 Rain World / `AssetManager` 等运行时边界，不能为了性能直接粗暴扔到任意 worker thread。

但它也暴露了真正矛盾：

> “必须在 game thread” 不等于 “必须在同一个 game frame 全做完”。

此前把问题理解成“主线程还是后台线程”，实际上缺少第三种方案：

> **主线程、可中断、按帧预算执行。**

这才是本问题最重要的突破口。

### 3.3 `Reduce sound group panel allocations...`

Commit：`8807f4c88148168d116b4d491e89ad3d5e381f31`

它降低 Group 面板持续使用时的 allocation。

解决的是：

- 每帧 GC；
- 重复临时对象；
- UI steady-state cost。

它不会消除第一次加载 XML / Sample Catalog / projection 的总成本。

### 3.4 `DevTool: retain sound library row presentation`

Commit：`336dc9639b2e431a2df216325f3a627a0746972e`

这是典型 retained-mode 优化：数据 revision 不变时复用已有 presentation。

但第一次没有 presentation 可以命中，因此仍然是 Full Build。

### 3.5 `Virtualize sound library sample runs`

Commit：`1a3b1457e955daf4c4f821fbd762f27fff7898cf`

虚拟化解决的是：

> 已经有完整数据集之后，不要给不可见行提交 ImGui widgets。

它不解决：

> 在虚拟化之前，完整数据集本身是怎样被发现、解析、分类和投影出来的。

因此它能显著改善超长列表滚动/稳定绘制，却很难改变第一次点 Sound 的卡顿。

### 3.6 Phase 2 / Phase 4 / PR #48 为什么也治不到这里

已有 DevTool 性能工作分别重点解决：

- Revision / Dirty：避免无变化时 backend 重建；
- Snapshot retained：避免重复 snapshot；
- UI virtualization：减少不可见 row；
- Derived cache：减少重复派生；
- Work Gate：隐藏 UI 不工作；
- Stable-frame GC：减少稳定帧 allocation。

它们共同改善的是：

```text
Sound 已经打开并稳定后的帧
```

本问题发生在：

```text
Sound 从未初始化 → 第一次激活的那一帧
```

两者不是同一个性能问题。

---

## 4. 根本架构问题

当前 Sound 有多个 lazy cache，但没有一个统一的 **cold-start owner**。

每个子系统都在做局部正确的事：

- Sample Catalog：没建就现在建；
- Group Library：没加载就现在加载；
- Runtime Snapshot：没 snapshot 就现在生成；
- UI Projection：没 projection 就现在生成。

单独看都合理。

组合起来就变成：

> **每一层都把“第一次调用者”当成可以承担完整初始化成本的人。**

而第一次调用者恰好就是“用户点击 Sound 后的这一帧”。

所以真正需要修的不是某一个函数，而是建立统一的 **Sound Activation Lifecycle**。

---

# 5. 彻底方案：Sound Activation Pipeline

建议新增一个明确的协调器：

`SoundBootstrapCoordinator` / `SoundActivationPipeline`

名字可再定，但职责必须唯一：

> Sound 的昂贵冷启动工作只能由它调度，任何 Draw / Presentation getter 都不得再隐式触发整库初始化。

## 5.1 状态机

建议状态：

```text
Dormant
  ↓
Primed
  ↓
IndexingSamples
  ↓
DiscoveringGroups
  ↓
ParsingGroups
  ↓
ResolvingGroupSamples
  ↓
BuildingPresentation
  ↓
BuildingUiProjection
  ↓
Ready
```

异常可以进入：

```text
PartialReady
Failed
```

每个状态必须允许跨帧存在。

**禁止**：进入某一个状态后用 while 一口气跑到 Ready。

---

## 5.2 核心原则：主线程预算化，而不是粗暴异步化

涉及以下内容的步骤继续留在 game thread：

- `AssetManager`；
- Rain World runtime data；
- active mod runtime metadata，如果其线程安全性没有正式保证；
- 任何依赖游戏当前状态的解析。

但改为：

```text
每帧最多执行 X ms
超过预算立即 yield 到下一帧
```

建议初始预算：

```text
Sound bootstrap budget = 0.75 ms / frame
```

允许配置/诊断模式调整到 0.5–1.5 ms。

实现不要只用固定“每帧 N 个条目”，应同时有 `Stopwatch` 时间预算，因为不同磁盘/Mod/路径的单项成本差异很大。

伪代码：

```csharp
while (HasPendingWork)
{
    ProcessOneUnit();

    if (elapsed >= frameBudget)
        break;
}
```

这样仍然遵守“game thread only”，但彻底消除“同一帧全做完”的要求。

---

## 5.3 `SoundSampleCatalog` 改成可步进构建

不要再让：

```text
Resolve / GetAll / EnsureLoaded
```

第一次调用时同步建完整索引。

目标 API 形态：

```text
BeginRefresh()
StepRefresh(frameBudget)
CurrentSnapshot
Revision
IsReady
Progress
```

内部拆阶段：

1. CaptureSourceRoots
2. EnumerateCandidates
3. ResolvePaths
4. ClassifyProvenance
5. FinalizeImmutableIndex

每一步都可以在条目边界暂停。

最终一次性 publish：

```text
Mutable Builder
      ↓ 完成
Immutable SoundCatalogSnapshot
      ↓ atomic/reference swap
Consumers
```

消费者只读最后一个完整 snapshot，不读半构建集合。

---

## 5.4 `SoundGroupLibrary` 改成分阶段加载

不要一次 `EnsureLoaded()` 做完所有 XML。

拆成：

### Phase A — Discover

只找到配置文件列表。

### Phase B — Parse

每帧解析一个或少量 XML，受时间预算限制。

### Phase C — Resolve Samples

Group 的 sample reference 逐批与 `SoundCatalogSnapshot` 对接。

### Phase D — Publish

建立完整 immutable Group snapshot 后一次 swap。

Group 解析不要再通过一个“可能隐式触发整库初始化”的 `SoundSampleCatalog.Resolve`。

它只能消费：

```text
已经 Ready 的 catalog snapshot
```

或者明确登记 unresolved reference，等待 Catalog phase 完成后再 resolve。

这样可以切断当前最危险的递归式冷启动耦合：

```text
Load Groups
→ Resolve Sample
→ Ensure Sample Catalog
→ 扫完整声音库
→ 返回继续 Load Groups
```

---

## 5.5 打开 Sound 时先画 Shell，不等待 Ready

点击 Sound 后的下一帧应该立即显示 UI，而不是等待资源库完成。

例如：

```text
Sound
────────────────────────────
Scene Sounds     可用

Library
正在建立声音资源索引…
██████████░░░░░  68%
当前：Resolving samples
```

原则：

- 已有 room scene data 可以先显示；
- 尚未 Ready 的 library/group 区域显示 loading shell；
- 不让 Draw 调用任何重初始化入口；
- Ready 后无缝替换为正常列表。

用户体验从：

```text
点击 → 游戏冻结一下 → Sound 出现
```

变成：

```text
点击 → Sound 立即出现 → 资源列表渐进 Ready
```

总初始化 wall-clock 时间甚至可以不减少，交互卡顿也会消失。

---

## 5.6 UI Derived Projection 也不能在 Ready 的一瞬间再制造第二个尖峰

如果 Catalog / Groups 分帧完成，但 Ready 后同一帧又一次性构建几千条 UI projection，就只是把卡顿从第 1 帧移到了第 N 帧。

所以 `SoundLibraryGroupsView` 的冷 projection 也要进入 pipeline。

可选两种方式：

### 方案 A — 主线程预算构建

最稳妥。

对 row/group projection 逐批生成，仍受 0.75 ms 左右预算。

### 方案 B — detached managed data 后台构建

只有在输入已经完全 detached，且工作只包含：

- string normalize；
- sort/filter；
- 纯 managed DTO 生成；

时才允许 worker thread。

不得把 `AssetManager`、Rain World live object、ImGui、DevInterface 对象传到 worker。

第一版建议先做 A，确认有必要后再引入 B。

---

## 5.7 Prewarm：让多数玩家甚至看不到 Loading

在 DevTool session 已经稳定、用户尚未点击 Sound 时，可以低优先级预热。

推荐时机：

```text
DevTool 打开
→ 其它 workspace 已稳定若干帧
→ 没有输入/拖拽/保存等高优先级工作
→ 用很低预算开始 Sound Priming
```

例如：

```text
后台主线程预算：0.20–0.35 ms/frame
```

注意这里的“后台”是**逻辑后台**，仍是 Unity 主线程低预算执行，不是 worker thread。

这样正常情况下用户第一次点 Sound 时，Catalog/Groups 很可能已经 Ready。

如果用户很快点击 Sound，则提升到 0.75–1.0 ms/frame 交互预算继续完成。

---

## 5.8 Cache 生命周期必须高于 Sound 窗口生命周期

Sound catalog/group cache 不应该因为：

- 切去 Objects；
- 切去 Map；
- 关闭 Browser；
- UI 暂时隐藏；

就失效。

建议生命周期：

```text
Rain World mod set / DevTool session lifetime
```

只有这些事件才 invalidation：

- active mod set 改变；
- Sound 资源显式 Refresh；
- Group 配置实际变化；
- 游戏 session / mod reload 需要重建；
- 明确的开发者强制刷新。

搜索词、分类、sort 等只 invalid UI projection，不 invalid 磁盘 Catalog。

这样第二次进入 Sound 应接近 O(1)。

---

# 6. 必须补的性能测量

现有 Sound / frontend P95 还不足以证明冷启动，因为 240 帧平均/P95 很容易把一个巨大 first-frame Max 稀释掉。

需要单独增加 activation metrics：

```text
SoundActivation.ClickToShellMs
SoundActivation.ClickToReadyMs
SoundActivation.MaxBootstrapFrameMs
SoundActivation.BootstrapWorkMsPerFrame

SoundActivation.SampleCandidateDiscoveryMs
SoundActivation.SamplePathResolveMs
SoundActivation.SampleProvenanceMs
SoundActivation.GroupDiscoveryMs
SoundActivation.GroupXmlParseMs
SoundActivation.GroupSampleResolveMs
SoundActivation.BackendPresentationBuildMs
SoundActivation.UiProjectionBuildMs
```

并记录：

```text
Samples processed / total
Groups processed / total
Pending units
Cache hit / cold build
```

这有两个目的：

1. 实机确定当前长帧各阶段的真实占比；
2. 防止未来有人又把某个阶段改回“一次性跑完”。

---

# 7. 验收标准

彻底解决不能只看“总耗时更短”，必须看“最坏单帧不再爆”。

建议验收：

## Cold Open

在清空 Sound runtime cache 后第一次点击 Sound：

- Sound Shell 下一帧可见；
- Sound bootstrap 自身单帧工作不超过配置预算 + 容许误差；
- 不允许一次完整 catalog/group rebuild 阻塞一帧；
- `MaxBootstrapFrameMs` 不出现当前这种肉眼可感知尖峰。

目标建议：

```text
正常 budget：≤ 1.0 ms/frame
硬上限：≤ 2.0 ms/frame attributable to bootstrap
```

如果某一个不可切分的外部调用单次就超过上限，应单独记录出来，而不是隐藏到总计时里。

## Warm Open

切走 Sound 再切回来：

- Catalog Hit；
- Group snapshot Hit；
- UI projection Hit；
- 不扫描磁盘；
- 不重新 parse XML；
- 不做 full sample provenance pass；
- 激活接近普通工具切换成本。

## Explicit Refresh

点击 Refresh 也必须走同一个 pipeline，不能重新引入同步全量 rebuild。

## Correctness

必须回归：

- Vanilla / DLC / Mod sample source attribution；
- Sound group 读取；
- Group 编辑；
- Scene sound 编辑；
- Save；
- Undo / Redo；
- 搜索；
- Preview；
- Vanilla / New UI 切换；
- DevTool dormant → reopen；
- Mod reload / session reset。

---

# 8. 不应该采用的“修复”

## 8.1 再加一层普通 Dictionary cache

不能解决第一次 cache miss。

## 8.2 再做一次 ImGui ListClipper

数据集构建发生在 clipper 能发挥作用之前。

## 8.3 把 `EnsureLoaded()` 换个名字

如果仍在 Draw/Getter 中同步跑完，架构没有变化。

## 8.4 把整个 Sound 加载扔到 `Task.Run`

风险过大。

Rain World / `AssetManager` / Mod runtime metadata 没有正式线程安全契约。之前将资源 resolution 保留在 game thread 是正确边界。

## 8.5 点击 Sound 时提前一帧偷偷加载

只是把冻结提前一帧，不是解决。

## 8.6 只优化总 CPU 时间

哪怕总初始化从 80 ms 降到 35 ms，只要 35 ms 仍集中在一帧，用户仍会感到明显卡顿。

本问题的主要 KPI 是 **max frame spike**，不是单纯 total work。

---

# 9. 推荐实施顺序

真正开始修复时建议按以下顺序，不要再次从 UI 微优化开始：

### Step 1 — Instrument cold activation

先把各 phase 的真实耗时和 Max Frame 量出来。

### Step 2 — 建 `SoundBootstrapCoordinator`

先建立生命周期与时间预算，不急着改所有内部实现。

### Step 3 — 拆 `SoundSampleCatalog`

把 full synchronous initialization 改成 resumable builder。

### Step 4 — 拆 `SoundGroupLibrary`

切断 Group load → Sample catalog cold initialization 的隐式级联。

### Step 5 — UI Shell / Progressive Ready

确保 Sound 点击本身永远不等待 catalog 完成。

### Step 6 — 分帧 UI projection

防止资源层不卡了，projection 又制造第二个 spike。

### Step 7 — Low-budget prewarm

让绝大多数实际使用场景第一次点击已经是 warm open。

### Step 8 — Runtime validation

以 `Max activation frame`、P95、GC、功能回归收口。

---

# 10. 最终判断

前几次性能修复不是“完全没效果”，而是解决了另一个层面的问题：它们已经把 Sound 的 **重复刷新、长列表绘制、稳定帧 allocation** 压下去了。

现在残留的明显卡顿反而更容易被看见，因为它已经集中成为一个纯粹的 **cold activation architecture problem**。

真正的终局方案不是继续在 `SoundLibraryGroupsView` 里挤 1–2 ms，而是：

```text
取消“第一次点击 Sound = 必须立即得到完整 Sound 世界”的假设。
```

最终理想路径应为：

```text
点击 Sound
→ 下一帧立即显示 Sound Shell
→ 已准备数据立即显示
→ 未准备资源由统一 Bootstrap Pipeline 按主线程预算逐步完成
→ phase 完成后 immutable snapshot swap
→ UI projection 同样受预算控制
→ Ready
→ 后续再次进入全部 cache hit
```

这会从架构上消灭第一次 Sound 点击的同步长帧，而不是继续围绕症状做局部补丁。

## 11. 本审计的证据边界

以上结论来自当前 `main` 的源码调用链与历史优化提交审计，足以确认“首次激活同步工作级联”这一架构问题。

但本文不伪造各阶段的实机毫秒占比。`SoundSampleCatalog`、Group XML、provenance 与 UI projection 在用户实际 Mod 集合、磁盘和 Rain World 运行环境中的具体占比，必须通过第 6 节 activation instrumentation 实测后才能给出。

因此下一次实现 PR 的第一批代码应该是 **cold activation instrumentation + bootstrap 生命周期骨架**，而不是继续猜哪个 for-loop 最慢。
