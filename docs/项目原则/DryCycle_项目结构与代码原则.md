# DryCycle 项目结构与代码原则

> **Project Structure, Architecture & Code Readability Principles**  
> **Revision:** V2 / 2026-09-08  
> **Scope:** DryCycle 全项目  
> **Status:** 长期工程原则 / 后续开发默认遵循

---

## 0. 文档定位

本文件定义 DryCycle 后续长期开发所采用的项目结构、代码组织、依赖、命名、性能、测试与维护原则。

它不是某个具体 Task 的实现说明，也不规定某一功能必须使用某一种设计模式。除非具体任务书明确说明例外，后续新代码和大规模重构默认遵循本文件。

本文件的目标是：

1. 让项目在功能不断增加后仍然可读、可维护、可测试；
2. 避免历史 Task 编号、临时 Bridge、兼容补丁逐渐变成正式架构；
3. 避免为了缩短文件而机械碎片化，也避免一个模块吸收本不属于它的职责；
4. 让模块通过明确的数据、事件和接口协作，而不是依赖彼此的私有实现；
5. 让状态、行为、事件和生命周期都有明确 Owner；
6. 让性能优化有统一方向，避免多个系统重复扫描同一份游戏状态；
7. 让重构、Bug 修复和后续扩展能够被安全回归验证。

---

# 1. 核心架构原则

## 1.1 架构服务于职责，不服务于形式

目录、文件、类、接口和 Runtime 的划分首先回答：

> **这段代码负责什么？**

而不是：

- 这个文件有多少行；
- 这个文件有多少 KB；
- 这个 Task 当年编号是多少；
- 为了看起来整齐是不是应该再套一层目录；
- 为了让目录对称是不是应该强行拆成相同结构。

项目结构的目标是降低理解、修改和验证成本，而不是追求视觉上的绝对对称。

允许两个领域拥有不同的内部结构，只要它们满足：

- 职责清楚；
- 依赖方向明确；
- 状态所有权明确；
- 生命周期明确；
- 测试边界明确；
- 不产生不必要耦合。

## 1.2 Domain First / 领域优先

大型功能优先按领域组织，而不是按开发历史组织。

推荐的领域名称示例：

- `Weather`
- `Travel`
- `Colony`
- `Signals`
- `Threat`
- `Social`
- `Combat`
- `Injury`
- `Environment`
- `Graphics`
- `Hydration`
- `WorldLink`

生产代码不应长期组织成：

- `Task09/`
- `Task10/`
- `Task13/`
- `Phase2/`
- `NewSystem/`
- `Temp/`
- `OldVersion/`

Task 编号属于需求与开发历史，不属于运行时领域模型。

## 1.3 Single Responsibility / 单一职责

单一职责不是“一个类只能做一件很小的事”，而是：

> **一个模块应围绕一个稳定、可解释的修改理由变化。**

例如，一个完整的 `WorldRoutePlanner` 可以同时包含：

- 图搜索；
- cost evaluation；
- route reconstruction；
- route validation；
- route commitment。

只要这些内容都围绕“世界路线规划”这一职责共同变化，它们可以合理地留在同一个高度内聚模块中。

反过来，一个很短的文件如果同时承担彼此独立的职责，例如感知、天气判断、攻击决策、移动写入、信号传播和 Debug，也仍然属于职责过载。

## 1.4 Cohesion / 高内聚优先

同一模块内部的状态、算法和生命周期应高度相关。

重构时优先把以下代码留在一起：

- 一起变化的代码；
- 一起测试的代码；
- 一起拥有状态的代码；
- 一起创建和清理生命周期的代码；
- 围绕同一领域事实共同工作的代码。

如果修改一个行为必须同时理解多个不相关领域，说明当前模块内聚不足。

不要仅按“数据类 / Service / Manager / Helper”之类形式化分类机械拆分。

## 1.5 文件 KB 和行数不参与架构判断

这是 DryCycle 的明确长期原则：

> **文件 KB、字节数和代码行数不作为拆分、合并、高内聚、职责过载或是否需要重构的判断依据。**

因此：

- 不设置“超过多少行必须拆”的规则；
- 不设置“超过多少 KB 必须拆”的规则；
- 不因为文件很长就自动触发拆分或架构审查；
- 不因为文件很短就认为它天然高内聚；
- 不因为两个文件都很短就强行合并；
- 不把 KB 或行数作为 Code Smell、Review Trigger、评分项或验收指标。

一个文件即使很长，只要它仍然拥有单一、完整、稳定的领域职责，就可以保持原状。

一个文件即使很短，只要它拥有多个彼此独立的修改理由、生命周期或状态所有权，就应该重新审查职责边界。

真正的拆分或合并依据只看语义和职责，例如：

1. 是否存在多个彼此独立的修改理由；
2. 是否存在彼此独立的领域职责；
3. 是否存在不同的生命周期或状态 Owner；
4. 是否有部分职责能够独立测试、独立演化；
5. 是否有部分能力被其他模块作为独立领域能力消费；
6. 修改一个职责时是否被迫理解大量无关逻辑；
7. 是否出现重复 authority 或不必要的跨领域耦合；
8. API 是否暴露了本不应跨领域暴露的实现细节；
9. 是否存在不同模块争夺同一个行为结果或持久状态的问题。

**禁止用源码体积代替工程判断。**

## 1.6 文件拆分检查

准备拆一个文件前，优先回答：

1. 是否存在多个独立修改理由？
2. 是否存在可独立测试、独立演化的子职责？
3. 是否存在独立生命周期或状态所有权？
4. 是否存在被其他模块单独消费的领域能力？
5. 修改一个功能时是否必须阅读大量无关代码？
6. 拆分后每个新文件分别代表什么稳定领域职责？

如果最后一个问题无法明确回答，就不应仅为了让文件变短而拆分。

## 1.7 禁止机械碎片化

为了缩短文件而产生大量无意义碎片，同样属于低质量结构。

不推荐仅表达“这是原文件的一部分”的命名，例如：

- `Part1` / `Part2` / `Part3`
- `HelperA` / `HelperB`
- `Utils2`
- `Manager1`
- `MiscLogic`
- `CommonStuff`

复杂但高度内聚的状态机、解析器、路线规划器或协议实现可以合理地保持为一个完整实现。

拆分后的每个文件必须拥有可解释的领域职责，而不是只承担“原文件的一部分代码”。

## 1.8 目录结构服务于导航和语义

目录应帮助开发者快速定位领域，不追求理论层级的绝对完整。

避免形成没有实际导航价值的深层路径，例如：

```text
Behavior/Social/Runtime/States/Internal/...
```

一个复杂模块可以根据真实领域采用类似结构：

```text
Core/
Combat/
Injury/
Social/
Threat/
Signals/
Environment/
Colony/
Travel/
Presentation/
Integration/
Debug/
```

原则：

- 目录名称直接表达业务职责；
- 不为了形式对称强行增加层级；
- 不为尚未形成独立领域的概念预建空壳目录；
- 领域真正成长后再自然形成子目录；
- 常用职责应能够直接、稳定地定位。

### 单文件叶子目录

如果一个叶子目录只包含一个 `.cs` 文件，并且目录本身没有独立、长期稳定的领域或兼容边界，则不保留这一层目录，文件直接上移一级。

例如：

```text
Behavior/Fear/DB_FearRuntime.cs
```

如果 `Fear/` 只承担这一份源码且没有独立目录语义，应整理为：

```text
Behavior/DB_FearRuntime.cs
```

只有当目录本身确实代表需要长期保留的独立领域、资源边界、兼容边界或预计由多个同职责文件共同组成的稳定边界时，才有理由保留。

## 1.9 避免泛化垃圾目录

除非确实属于全项目共享基础设施，否则不推荐建立：

- `Utils/`
- `Helpers/`
- `Misc/`
- `Managers/`
- `Common/`

如果 helper 只服务 `Travel`，它应属于 `Travel`；如果 formatter 只服务 Observatory，它应属于对应 Debug/Observatory 领域；如果算法只服务 `Signals`，它应属于 `Signals`。

真正跨领域的设施应有明确基础职责，例如：

- Runtime
- Perception
- Events
- Serialization
- Integration

不能因为“不知道放哪里”就把代码放进 `Utils`。

## 1.10 不为“高级架构”而过度抽象

DryCycle 是 Rain World Mod，不需要照搬企业后端的所有模式。

禁止为了架构形式引入没有实际价值的层级，例如：

- 无替换需求却层层包装接口；
- 每个类都配置 Factory；
- 所有逻辑都 Event Bus 化；
- 只有一个实现却增加多层 Repository / Service；
- 为了“解耦”制造大量只转发一次调用的 Adapter。

抽象应解决真实问题，例如：

- ownership；
- testability；
- duplicate observation；
- external integration；
- lifecycle；
- future extension pressure。

---

# 2. 命名、领域身份与兼容边界

## 2.1 命名：短前缀 + 完整职责

大型子系统可以定义短且稳定的源码前缀，以降低文件名同质化和 IDE 搜索噪音。

例如 Desert Batfly 使用：

```text
DB_
```

推荐：

- `DB_Runtime`
- `DB_TravelIntent`
- `DB_ThreatMemory`
- `DB_EnvironmentRuntime`
- `DB_SignalDefinition`

不推荐二次压缩：

- `DB_EnvRt`
- `DB_TrvInt`
- `DB_BhvMgr`
- `DB_T13`

原则是：

> **前缀短，职责名完整。**

是否采用前缀由具体大型领域决定，不要求整个 DryCycle 所有类型统一带前缀。

## 2.2 开发历史名称不得污染生产架构

Task、Phase、讨论编号主要属于：

- `docs/Task/`；
- `docs/Discussion/`；
- Git history；
- 明确标注为迁移期、且会被退休的临时设施。

生产代码应使用领域名称。

例如：

```text
Task13Influence    -> EnvironmentInfluence
Task09Travel       -> TravelIntent
Task12SignalBridge -> SignalRuntime / SignalPolicy
```

历史编号不能成为永久运行时 API 或生产类型身份。

## 2.3 源码名称与外部身份分离

内部 C# 重命名不应轻易改变游戏、资源、存档或第三方可见 ID。

例如：

```text
DesertBatflyState -> DB_State
```

不意味着：

```text
"DesertBatfly" Creature ID 必须改名
DCDesertBatflyV1 save tag 必须改名
```

以下内容默认属于兼容边界：

- `CreatureTemplate.Type` / ExtEnum 字符串；
- Sandbox unlock ID；
- save tag / schema；
- `world.txt` / room / object tag；
- atlas / shader / asset key；
- JSON config key；
- 第三方 Mod 依赖的外部名称。

外部 ID 只有在明确安排迁移策略、兼容层和回归测试后才能修改。

## 2.4 Compatibility 与 Core 分离

外部 Hook、Reflection、Harmony、Watcher/Warp 等兼容逻辑应尽可能集中在：

- Integration；
- Compatibility；
- Adapter。

Core / Domain 不应依赖某个第三方 Mod 的私有字段才能正常运行。

兼容层失败应尽量 fail soft，并能通过 Debug 明确记录。

## 2.5 Persistence / 存档稳定性优先

重构生产类型名不等于重写存档格式。

修改持久 schema 前必须考虑：

- old save load；
- malformed data；
- locale；
- unknown / foreign fields；
- migration path；
- rollback。

如果没有明确玩法需求，架构重构默认不改变已有 save schema。

---

# 3. 依赖、状态与行为所有权

## 3.1 一个领域事实尽量只有一个 Authority

如果一个简单参数修改经常必须同步修改多个不相关模块，说明边界中可能存在重复 authority 或隐式耦合。

例如 Signal visual range 应拥有唯一 authoritative definition：

```text
SignalDefinition.VisualRange
    -> SignalRuntime 读取
    -> Fog / VisibilityPolicy 只提供 multiplier
```

不应形成：

```text
Signals 复制一份 range
Environment 再复制一份
Debug 再复制一份
```

原则：

> **一个领域事实尽量只有一个 authoritative definition。**

## 3.2 Explicit Dependencies / 依赖必须显式

模块不应通过以下方式形成内部协议：

- Reflection 读取兄弟模块 private field；
- RuntimeDetour 挂兄弟模块 private method；
- 字符串方法名；
- 临时修改另一个系统的持久状态；
- 读取未声明的全局 static。

内部跨模块协作优先使用：

- 明确数据结构；
- Context；
- Event；
- Policy；
- readonly state view；
- Proposal / Result；
- 显式 internal API。

Reflection / Harmony / RuntimeDetour 主要用于真正的游戏或第三方 Integration 边界。

## 3.3 依赖方向尽量单向

避免多个领域互相直接调用形成环，例如：

```text
Combat <-> Social <-> Environment <-> Threat <-> Signals
```

更健康的形式是通过稳定中间契约协作：

```text
Room / Frame Context
Event Hub
Proposal / Arbiter
Read-only Influence
```

例如：

- Environment 输出 `EnvironmentInfluence`；
- Combat 读取影响，而不是 Environment detour Combat 私有方法；
- Signals 输出 `SignalInfluence`；
- Social 读取影响，而不是 Signals 直接改 Social private state。

## 3.4 状态必须有明确 Owner

每一个重要状态都应回答：

- 谁创建？
- 谁修改？
- 谁读取？
- 是否持久化？
- 何时清理？
- Room change / death / disable 后是否还存在？

禁止多个模块把同一字段当作临时通信通道。

例如，持久 `Thirst` 不应被 Environment 临时篡改来表达 HeatAgitation；应建立明确的 decision / motivation context。

## 3.5 同一物理结果只能有一个主控制器

对移动、Camera、Weather state 等容易产生控制冲突的领域，应明确当前 Owner。

特别是生物普通 locomotion：

- 同一帧只允许一个 Primary Movement Owner；
- 其他系统提交 influence / proposal / modifier；
- 不能让多个系统长期直接写速度或目标并互相覆盖。

特殊物理状态可以显式取得所有权，例如：

- 抓附；
- 被抓；
- shortcut；
- emergence；
- 瞬时 hit impulse。

关键不是“任何地方都禁止写 velocity”，而是普通行为不能形成多套并行 locomotion controller。

## 3.6 Event Semantics / 真实事件只解释一次

同一个游戏事实不应被多个 Hook 独立重新解释。

典型事件包括：

- Damage；
- Capture；
- Release；
- Death；
- Consume；
- RoomChanged；
- Weather transition。

推荐流程：

```text
外部 Hook / Detector
    -> 统一 semantic event
    -> 多个 subscriber 消费同一事实
```

而不是：

```text
Hook A 猜一次 killer
Hook B 再猜一次 killer
Hook C 根据 grabbedBy 第三次猜
```

事件应尽量 exactly-once，并拥有 sequence / tick / source 信息。时间窗可以用于语义归因，但不能代替事件去重设计。

## 3.7 Runtime 生命周期必须显式

使用 `ConditionalWeakTable` 不等于无需设计生命周期。

所有 Room / Creature / Session scoped runtime 都应考虑：

- Enable；
- Disable；
- Reset；
- Room transition；
- Creature death；
- shortcut；
- game / session teardown；
- mod reload（可支持范围内）。

如果创建 `UpdatableAndDeletable`、Hook、reservation 或 static registry，必须存在对应清理路径。

## 3.8 API Surface 尽量小而稳定

模块对外应优先暴露稳定领域接口，而隐藏内部实现。

例如 Signals 对外更适合暴露：

- Publish；
- Update；
- GetInfluence；
- Definition。

而不是直接暴露：

- generation ring；
- merge buffer；
- urgent queue；
- receiver dictionary。

每增加一个 public 或跨模块 internal API，都应问：

> **调用者真的需要知道这个实现细节吗？**

---

# 4. Runtime 与性能原则

## 4.1 Room Runtime 按需激活

昂贵 Room 分析不应仅因为 `Room.Update` 就在所有房间启动。

例如：

- terrain shelter scan；
- creature ecology cache；
- signal packet runtime；
- specialized AI context。

优先在真实需要时建立，常见激活条件包括：

- 相关生物或对象存在；
- 相关系统明确查询；
- 相关区域配置存在；
- Debug 明确请求。

## 4.2 共享观察，避免重复扫描

当多个系统依赖同一 Room 事实时，应考虑共享低频 observation / cache。

例如：

- players；
- thrown weapons；
- active creatures；
- weather；
- shelter geometry；
- roost candidates。

应避免：

- 每只生物每帧完整扫描 `physicalObjects`；
- 每个子系统各自重复扫描同一 thrown weapon list；
- 每帧 terrain full scan；
- Debug 关闭时仍持续构造大量字符串。

优化目标不是“缓存一切”，而是消除明显重复工作，并让 cache freshness 和 invalidation 可解释。

## 4.3 性能预算按算法、频率和规模描述

相比源码体积，更有工程意义的性能指标包括：

- 是否 per-frame；
- 是否 per-creature；
- 是否 room-wide；
- 是否 world-wide；
- 最大集合规模；
- refresh cadence；
- cache invalidation；
- 上限 / cap。

适合明确约束的内容包括：

- packet cap；
- relay hop cap；
- route max hop；
- candidate limit；
- refresh interval；
- per-frame owner count。

源码 KB 和行数不属于性能预算。

## 4.4 优先复用 Rain World 原生能力

如果 Rain World 已经提供稳定能力，优先复用：

- native pathing；
- collision；
- shortcut；
- creature relationship；
- chain / roost；
- abstract movement。

只有原生能力确实不满足设计目标时才建立新的实现。

重构不应为了“代码更现代”而替换已经稳定工作的底层算法。

## 4.5 复杂方法优先表达清晰阶段

复杂逻辑可以组织成明确阶段，例如：

- Gather / Sample；
- Evaluate；
- Build；
- Resolve；
- Apply；
- Publish；
- Cleanup。

如果一个方法同时承担扫描、决策、持久状态修改、移动写入、事件发布和 Debug 等多个独立职责，即使代码很短，也应重新审查职责边界。

---

# 5. 测试、Debug 与重构纪律

## 5.1 测试保护行为契约，不保护历史技术债

测试应验证：

- 行为结果；
- priority；
- ownership；
- event exactly-once；
- persistence；
- bounded math；
- forbidden side effect；
- dependency boundary。

不应长期验证：

- 某个 `xxxBridge` 必须存在；
- 某个 private Hook 必须存在；
- 某个 RuntimeDetour 必须被调用；
- 某方法必须在特定 IL offset 上排列。

迁移阶段可以暂时保留 Architecture Lock Test，但稳定后应转化为 Behavior / Contract Test。

## 5.2 Debug / Observatory 读取真实状态

如果 runtime 已经拥有明确 Owner，Debug 不应根据多个状态变量重新“猜”当前 Owner。

应优先暴露：

- current owner；
- winning decision；
- rejected proposals；
- event source；
- cache age；
- current context；
- lifecycle state。

Debug 应帮助定位架构冲突，而不只是展示结果数值。

## 5.3 注释解释“为什么”，代码表达“做什么”

推荐注释记录：

- 为什么某个优先级必须高于另一个；
- 为什么不能读取某个输入；
- 为什么保留某个旧外部 ID；
- 为什么某个算法没有换成更复杂方案；
- 特殊兼容边界；
- 不直观的 Rain World 原版语义。

不推荐大量重复代码本身已经清楚表达的内容。

## 5.4 大规模重构保持提交可审查

大规模重构应避免一次提交同时混合：

- 大量文件移动；
- 大量 rename；
- 行为逻辑变化；
- API 改写；
- 测试删除。

推荐按责任拆分提交，例如：

1. baseline regression；
2. 新 contract / API；
3. 单一子系统迁移；
4. 删除旧 bridge；
5. 文件 rename / move；
6. cleanup；
7. performance / tuning。

目标是让 Git diff 可审查、Bug 可 bisect、阶段可回滚。

## 5.5 架构重构默认保持行为

架构重构默认采用 Behavior-Preserving Refactor。

除非任务书明确批准，不应借机：

- 改玩法；
- 改概率；
- 改 Personality 分布；
- 改存档；
- 加新机制；
- 删除玩家已依赖的行为。

Bug 修复应单独记录并具有回归依据。

## 5.6 新增大型系统前做 Architecture Check

增加较大新功能前，至少回答：

1. 它属于哪个领域？
2. 它拥有什么状态？
3. 状态是 persistent、session、room 还是 frame？
4. 它需要读取哪些已有事实？
5. 是否已有可复用 Context / Event / Perception？
6. 它是否需要成为行为或移动 Owner？
7. 如果需要，如何进入已有 arbitration？
8. 是否会重复扫描已有数据？
9. 生命周期如何清理？
10. 如何测试？

如果新功能必须先写多个 private Reflection Bridge 才能接入，通常说明旧架构应该先扩展正式 contract。

---

# 6. Architecture Review 原则

## 6.1 Review 优先级

评审代码结构时，优先检查：

1. **Correctness**：行为是否正确；
2. **Ownership**：谁负责这一事实、状态或行为；
3. **Lifecycle**：何时创建、刷新、清理；
4. **Dependency**：依赖是否明确、方向是否健康；
5. **Cohesion**：职责是否高度相关；
6. **Performance**：是否存在重复或无界昂贵工作；
7. **Testability**：是否能够稳定验证；
8. **Readability / Navigation**：开发者是否能快速定位和理解。

**文件 KB、字节数和行数不在 Architecture Review 判断项中。**

## 6.2 允许例外，但必须有工程理由

本文件不是为了禁止工程判断。

某个模块可以合理地违反一般建议，例如：

- 外部兼容边界必须使用 Reflection；
- 某个性能路径必须直接操作 Rain World 原生对象；
- 某个第三方协议要求保留特殊外部身份；
- 某个领域为了保持单一状态生命周期而不适合进一步拆分。

例外应满足：

1. 职责仍然清楚；
2. 理由可以解释和记录；
3. 边界可以测试；
4. 不把例外扩散成全项目默认方式。

注意：**“文件太长”或“文件太短”本身既不是违规理由，也不是例外理由。**

---

# 7. 文档组织原则

当前 `docs/` 的长期一级分类包括：

```text
docs/
├─ Task/
├─ Discussion/
└─ 项目原则/
```

- **Task**：正式任务书与可执行规格；
- **Discussion**：讨论、设计过程、状态和历史记录；
- **项目原则**：跨任务、跨模块长期复用的工程规范。

具体功能的规则优先写入对应 Task；只有真正能够跨任务长期复用的规则才进入“项目原则”。

项目原则文档统一优先使用 Markdown（`.md`），使用正式标题、列表、代码块和必要的强调，而不是依赖纯文本分隔线模拟结构。

---

# 8. 最终目标

DryCycle 项目结构的成功标准不是：

- 文件都很短；
- 文件 KB 都很小；
- 目录完全对称；
- 使用了很多设计模式；
- 所有东西都有接口。

真正目标是：

- 开发者能快速找到一个行为属于哪里；
- 修改一个领域时不需要理解大量无关系统；
- 同一个事实拥有清晰 authority；
- 同一个状态拥有明确 Owner；
- 同一个物理行为不会被多套控制器竞争；
- 外部兼容和核心逻辑分离；
- 性能成本有界且可解释；
- 生命周期能够正确清理；
- 测试保护行为契约而不是历史补丁；
- 项目持续增加功能后仍然能够安全扩展。

---

## 核心判断一句话

> **先看职责、Owner、生命周期、依赖和行为契约；不要看文件有多少 KB，也不要看有多少行。**
