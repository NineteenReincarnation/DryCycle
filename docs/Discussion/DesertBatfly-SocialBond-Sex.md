# Desert Batfly 5A：Sex / SocialBond / Grief

2026-09-06：已实现并通过托管回归测试；尚未进入 Unity 游戏循环验证动画、寻路和实际捕食场景。
**5B reproduction remains deferred**：没有实现 Courtship、MateBond、交配、怀孕、产卵、幼体、亲缘或人口增长。

## 实现与参数

- Sex 使用 `System.Random(seed ^ 0x147AD639)` 独立派生，Male 概率 0.48。没有向旧人格随机流插入调用，性别不决定 Temperament / Nerve / Conformity / Vengeance / SandSpit。
- 只修改 Graphics：Male 身体 0.98、翅长 1.055、花纹/刺突出度 1.10；Female 身体 1.035、翅宽 1.04。沿用原版动画和 atlas。物理半径、质量、速度不改；质量最大 0.0625，低于 Peach small-object 的 0.2 阈值。
- Conformity 是对群体的敏感度；SocialBond 是对具体同伴的重视。关系不按性别筛选，每只只有一个完整 EntityID 和一个强度，可以非对称。
- `StrengthenBond`：空槽建立、同目标累加至 1；不同目标只有本次候选增益达到当前强度 +0.12 才替换。不保存挑战者历史，也不自动镜像关系。
- 成功救援：被救者向救援者 +0.30，救援者向被救者 +0.12，Conformity 仅提供 0.96–1.04 倍微调。接在真正释放舌头/嘴部之后，沿用 RescueAttempted 防重入；后续参与者发现已释放便不会再次记功。
- 真实 Fly Chain 每 180 次活体 update 取一个直接邻居 +0.004；末端可以使用直接抓住的上邻。单次增益很小，没有每帧群落配对。
- 共同逃生复用 Death/Capture 的 tier 数组，在复仇招募结束后，仅 Tier 0/1 且未参加复仇的可行动个体向前一个合格、120 距离内的逃生者 +0.01。每个观察者每事件最多一个候选，新增处理为 O(n)。
- 本阶段没有自然 Bond 衰减。跨房间保留身份，解析只访问当前 realized colony；没有跨房间坐标查询、跟随或导航。观察到死亡会清理关系；未观察到的远处死亡不会即时清理或产生 Grief。
- Roost 复用原有 8 tick scan 和当前合法挂点：同伴在 100 距离内、可见且正在 Chain 时，关系 0.3 以下无加成，强度 1 时增加 35% willingness。没有强制进入 Chain、改变挂点或相互追逐。
- 捕获事件对已有 True Avenger 排序和 Follower 分数提供最多 0.18 动机加成；Grief 对已知 killer 提供强度 × Temperament × Nerve ×0.15 动机，并用于已有 Rage。资格、PTSD、失败不补员、同目标 1+2 上限继续由原系统掌管。

## Grief 与生存优先级

只有确认 live → dead 后，受支持死亡广播中的同链/直接目击/Tier 1 观察者才处理同伴死亡；未知死因使用同链或 180 距离内直接可见的本地观察者，不制造新的恐惧波。

有效关系阈值 0.30，Grief 1200–4000 tick，固定保存在 CreatureState。Player / Peach 死亡在现有 Trauma 上单次额外增加 0.08–0.30，先应用增幅再运行原恐惧/PTSD collapse 检查。未知 killer 可以产生 Grief，但不凭空制造 Player/Peach Trauma。

Grief 每次活体 update 衰减一次，跨房间保留，尸体不更新。普通攻击有效动机降低最多 25%–55%；高 Temperament 且高 Nerve 抑制较小，低值者 Roost 加成较大（15%–40%）。同一稳定条件同时用于目标扫描、CanHarass 与普通攻击分支，避免每帧随机反复选目标。Attach 的主动渴求也使用这一缩放。

严格保持：**即时生存 / severe PTSD > Grief > 普通骚扰**。Grief 不授予 True Avenger，不解除创伤，不直接设置攻击 Target。开始 Grief 时释放旧攻击槽并清掉旧 attacker/retaliation，正常生存移动继续工作。

## 存档

继续使用 `DCDesertBatflyV1`。原先索引 0–14（Personality / Thirst / GrabMemory / Trauma 等）完全保留，追加：

| 索引 | 内容 |
| --- | --- |
| 15 | SocialBondTarget，`spawner,number` 或空字符串 |
| 16 | SocialBondStrength |
| 17 | GriefStrength |
| 18 | GriefTicks |
| 19 | GriefThreatIdentity，`spawner,number` 或空字符串 |

数值使用 invariant culture；NaN/Infinity 清零，强度限制 0–1、GriefTicks 限制 0–4000；不合法身份清除，缺字段的旧 V1 默认无关系/Grief。外部 mod 的 unrecognizedSaveStrings 保留。Sex 从既有稳定 seed 派生，不新增随机存档字段。

## 整体复盘与修复

1. 救援后验证两种捕获途径均已释放，避免仅发出释放调用就错误记功；原有 attempt 标记和捕获状态阻止重复救援增益。
2. 观察到死亡后立即清除该 BondTarget，重复事件不再次增加专属 Trauma/Grief；尸体提醒不调用关系死亡入口。
3. 同伴额外 Trauma 在原 ReceiveFear 之前应用，原 severe collapse 与招募门槛可立即看见它；不重复完整恐惧广播。
4. 共同逃生增长移到招募之后，避免即将参加复仇的个体被错误记为共同逃生。
5. 新候选筛选补充 held / unconscious / shortcut / deletion 检查，防止不可行动个体占复仇组名额。
6. Grief 对普通攻击的取消保留已经处于 Roost 的状态，避免每帧 CancelAttack 将 Roost 重置为 Flight；开始 Grief 清除旧槽位和反击次数。
7. Chain 采样仅处理活体；helper 没有 ConditionalWeakTable、每只字典、历史关系图或跨房间引用缓存。死亡对象不会创建社会 runtime state。

## 验证

- `dotnet build src/DryCycle.csproj`：0 警告、0 错误，输出到项目配置的游戏 plugins 目录。
- `dotnet build tests/DesertBatfly/DesertBatfly.Tests.csproj`：0 警告、0 错误。
- 使用本机 Rain World PUBLIC/HOOKS 程序集和编译后的 DryCycle.dll 运行测试：**137620 assertions PASS**。
- 10,000 seeds：Male **4800 (48%)**、Female **5200 (52%)**；True Avenger **494 (4.94%)**；SandSpit **3729 (37.29%)**。
- 验证旧随机流水线与喷沙计算、质量边界、完整身份/文化独立存档、旧 V1、异常值、foreign fields、单槽替换、救援非对称、真实直接 Chain 邻居采样、跨房间/死亡增长阻断、死亡去重、Grief 到期、PTSD 保留、旧槽位和反击清理。
- 原 Rock 100 次命中、营养、Peach 尸体可食性、AttackSlots=2、被抓 Attach 取消、流沙和真实 TerrainCurve emergence 回归继续通过。

实机仍需验收任务书的 10 个场景，特别是画面上的轻度性二型、长期 Chain、真实 Peach 舌头→嘴部转移后的 Rescue、同伴死亡的个性化 Grief、PTSD 冲突及跨房间重现。托管测试未模拟 Unity 动画、完整恐惧传播或实时寻路，不等于这些场景已经实机通过。

## 文件清单

修改 DesertBatflyState.cs、DesertBatfly.cs、DesertBatflyAI.cs、DesertBatflyIntimidation.cs、DesertBatflyGraphics.cs、tests/DesertBatfly/Program.cs、DiscussionStatus.txt；新增 DesertBatflySocialBond.cs 和本文档。Peach 捕食 AI 与 DesertSwarmRoom 无需修改。
