# DevTool Phase 6 — 游戏内最终验收规程

这份文档只覆盖 Phase 6 剩余的真实 Rain World 运行时验证。架构静态守卫、PR diff 终审、真实 `net48` 联编和离线 Extension API / reset smoke 已在游戏外完成。

**只有本文件的功能回归与性能回归全部 PASS，Phase 6 才能从 99% 记为 100%。**

## 1. 验证前提

- 使用 PR #47 当前 head 编译出的 `DryCycle.dll` 与 `DryCycle.DevTool.RWImGui.dll`。
- RWImGui 使用 Workshop `3417372413` 的插件目录：
  `D:/Application/Steam/steamapps/workshop/content/312520/3417372413/plugins`
- `rain-world-imgui-api.dll` 与配套 `ImGui.NET.dll` 必须来自同一套实际安装。
- 测试前保留一份房间/World 文件备份，Save 验收不得拿唯一工作副本做破坏性测试。
- 先关闭与 DevTool 无关的临时 debug overlay；需要验证 Generic DevInterface fallback 时再开启目标第三方 Mod。

## 2. New UI / Vanilla 生命周期

### 操作

1. 打开 Rain World DevTools。
2. 在 Control Center 中切到 **New UI**。
3. New UI / Vanilla 连续往返切换至少 10 次。
4. 关闭 DevTools，再重新打开，重复一次 New UI / Vanilla 切换。
5. 在 Objects / Sound / Triggers / Map 至少各做一次切换。

### PASS 条件

- Vanilla 控件在 New UI 下不会残留不可见点击区或挡住场景输入。
- 切回 Vanilla 后，被隐藏的 Vanilla 控件位置、可见性和 world-space handles 能正确恢复。
- Map 页面不能因为隐藏旧 UI 而修改/污染 `RoomPanel.pos`、`devPos` 或保存坐标。
- 关闭 DevTools 后重新打开，不保留上一次 session 的 stale Selection、Command、Presentation 或隐藏状态。
- 不出现重复 Hook、一次输入执行两次、窗口重复实例或关闭后仍继续处理 DevTool 命令的现象。

任意一项失败即记为 FAIL。

## 3. Workspace 功能回归

每个 Workspace 至少执行一项真实修改，并验证 Undo / Redo / Save。

### Objects

- 选中现有对象。
- 修改一个普通属性。
- 移动一个带 world-space handle 的对象/控制点。
- 新建并删除一个对象。
- Undo / Redo。
- Save 后切换页面/重开 DevTools，确认值与位置一致。

### Room

- 修改一个 RoomSetting 普通值。
- 修改 Effect / palette / override 中至少一类当前房间实际支持的值。
- Undo / Redo。
- Save 后重新进入该 Room 页面验证。

### Sound

- 修改或移动一个现有 Sound 条目。
- 若当前房间存在 Spot/Directional handle，拖动一次。
- Undo / Redo。
- Save 后重开验证。

### Triggers

- 修改一个 Trigger 参数或 handle。
- Undo / Redo。
- Save 后重开验证。

### Map

- 进行一次不会破坏地图工程的可逆编辑。
- Undo / Redo。
- SaveMapConfig 前后确认地图坐标没有因为 UI suppression 发生漂移。

### Dialog

- 修改一个当前 Dialog 页面可编辑值。
- Undo / Redo。
- 切页/重开后确认 Presentation 与实际模型一致。

### Relationships

- 修改一对关系值。
- Undo / Redo。
- 切换对象/页面后确认关系没有被旧 Snapshot 覆盖。

### PASS 条件

- 每个修改只产生一次语义结果，不出现一次点击执行两次。
- Undo 回到准确的前值；Redo 回到准确的后值。
- Save 后重新读取的数据与 UI 展示一致。
- Selection 不指向已经删除的对象。
- 页面切换后不显示上一页面的 stale Snapshot。

## 4. Generic DevInterface / 第三方兼容回归

选择至少一个**没有主动接入 DevTool Extension API**、但会创建自定义 DevInterface 控件/PlacedObject 的第三方对象进行验证。

推荐优先使用当前安装中实际存在的 POM / RegionKit 对象；这里验证的是公共协议 fallback，不是恢复已经删除的专用私有反射适配器。

### PASS 条件

- 已知 Button / Slider / Cycler / Select / Text / Handle 等公共协议能通过 Generic Bridge 编辑时，应正常工作。
- 无法安全识别的特殊交互必须明确回退 Vanilla，而不是显示一个“看似可编辑但实际不生效”的假控件。
- 不因第三方类型名、程序集名或私有字段差异发生异常。
- New UI / Vanilla 往返后，第三方对象仍可继续编辑。

## 5. Extension API 生命周期

离线 smoke 已验证：

- API 版本 `1.0`。
- Capability mask 保持 Phase 5 冻结语义。
- 注册可发现。
- ID 大小写不敏感。
- 重复 ID 拒绝。
- Dispose 幂等并从 discovery 中移除。
- Dispose 后相同 ID 可重新注册。
- 不兼容 Major 版本被拒绝。

游戏内只补以下两项：

1. 如果当前有真实第三方扩展使用 `DevToolApi`，执行一次 Mod disable/reload 或等价生命周期，确认其 Scope 能释放并重新注册。
2. 关闭/重新打开 DevTools，确认 Extension Scope **不会**被 UI runtime reset 错误 Dispose。

没有真实 Extension API 使用者时，第 1 项记为 N/A，不作为阻塞；第 2 项仍需确认 DevTool 本身不会清空外部 scope。

## 6. Dormant / Reset 回归

### 操作

1. New UI 中选中对象并打开一个 detail workspace。
2. 执行一次编辑，使 History / Revision / Presentation 都发生变化。
3. 关闭 DevTools。
4. 在游戏中停留数秒。
5. 重新打开 DevTools。

### PASS 条件

- 没有旧 Command 在重开时突然执行。
- 没有旧 Selection 指向已经失效的对象。
- 没有上一 session 的旧 Presentation 闪现一帧以上。
- Performance Monitor 默认回到关闭状态。
- Vanilla 隐藏节点全部恢复；再次进入 New UI 后能够重新正确 suppression。

## 7. 稳定帧性能回归

现有性能窗口已经提供：

- `DevUI total`
- `Session sync`
- `Workspace restore`
- `Vanilla DevUI`
- `Post-legacy sync`
- `Command processing`
- `Legacy presentation`
- `Object gizmo`
- 各 Workspace Presentation time
- Core / Room / Sound / Triggers / Map / Dialog / Relationships 的 `Hit / Partial / Full / Hit %`
- 最近 240 样本的 Last / Avg / P95 / Max

### 基本稳定帧测试

对 Objects/Room/Sound/Triggers/Map/Dialog/Relationships 逐个执行：

1. 切到目标 Workspace。
2. Control Center → **性能 / Profiling → 开启**。
3. 点击 **重置样本**。
4. 完全停止编辑和鼠标拖动，保持至少 240 个 DevUI 样本。
5. 记录该 Workspace 的 Hit / Partial / Full，以及 `DevUI total`、`Session sync`、对应 Presentation 的 P95。

### 稳定帧 PASS 条件

- 初次进入 Workspace 允许一次 Full rebuild。
- 进入稳定状态后，**Hit 必须持续增加**。
- 没有 Revision/Selection/Document/第三方结构变化时，**Full 不得按帧持续增加**。
- 仅发生轻量语义变化时允许 Partial；不应因为普通稳定帧不断 Partial。
- `Session sync` 不应表现出每帧全量扫描型的持续高成本；120 帧 safety audit 可以出现孤立样本，但不能把整个稳定窗口变成周期性 full rebuild。
- 未启用 Compatibility Diagnostics 时，不应观察到诊断反射扫描造成的持续命令/表现层成本。
- 关闭 Profiling 后，不应出现功能行为变化。

这里不设固定毫秒阈值，因为 CPU、房间规模、Mod 数量和 VSync 环境差异很大。Phase 6 的硬性判定是**是否重新引入与数据规模无关的每帧全量工作**，而不是用某一台机器的绝对 ms 作为产品门槛。

## 8. 大数据压力回归

使用当前项目中对象/连接/声音/触发器数量较多的房间或 World：

- 快速滚动 Objects / Sound / Trigger 长列表。
- 在 Map 中平移/缩放，并观察大量房间/连接时的交互。
- 连续切换 Selection。
- 执行 Undo / Redo 连续操作。

### PASS 条件

- 列表滚动不会因为恢复全量绘制而出现明显随总行数线性恶化。
- Map 不重新绘制/处理明显离开 viewport 的全部元素。
- Selection 改变不会触发无关 Workspace 的持续 Full rebuild。
- Undo / Redo 后 Snapshot 正确，并在下一稳定阶段重新回到 Hit 主导。

## 9. 日志与失败记录

若任何项失败，记录以下最小信息：

```text
Workspace:
操作:
New UI / Vanilla:
是否开启 Profiling:
Hit / Partial / Full:
DevUI total P95:
Session sync P95:
目标 Presentation P95:
预期:
实际:
是否可稳定复现:
BepInEx LogOutput.log 相关异常:
```

性能问题优先附 Performance 窗口截图；功能问题优先附最短复现步骤。

## 10. Phase 6 最终完成条件

以下全部成立才把 Phase 6 标为 100%：

- New UI / Vanilla 生命周期 PASS。
- 七个 Workspace 基本编辑 + Undo / Redo / Save PASS。
- Generic DevInterface / Vanilla fallback PASS。
- Dormant / reopen 生命周期 PASS。
- 稳定帧性能回归 PASS。
- 大数据 UI/Map 压力回归无 Phase 2/4 明显退化。
- 没有新的高严重度异常写入 BepInEx 日志。

通过后：

1. 将 `PHASE6.md` 更新为 100%。
2. 将 PR #47 从 Draft 转为 Ready for Review（仅在明确决定进入评审时）。
3. **不要自动合并 PR；合并仍需要明确指示。**
