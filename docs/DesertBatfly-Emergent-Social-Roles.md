# Desert Batfly Task 02：稀疏涌现式社会角色

2026-09-06：**已实现 / 待实机验收**。托管编译、统计和集成测试通过；没有运行完整 Rain World / Unity 游戏循环。

## 目标和边界

绝大多数个体仍然是 None / Ordinary。只有鲜明倾向在安全环境中暂时表达为 Sentinel、Bully、Opportunist；没有职业人口配额，没有保证每群拥有三种角色，也没有永久角色存档。

None 完整保留原有人格、Sex、SocialBond、Grief、GrabMemory、Fear/PTSD、Roost、攻击与复仇。Follower-like 仅为 Conformity 的只读派生；Loner-like 为 `(1-Conformity)*(1-RoostAffinity)`，不是角色 enum，也不拒绝强 Bond。本次没有为离群倾向额外接管移动。

Sex、Bond 不参与角色评分。没有更改 Personality 随机流、伤害、速度参数、质量、True Avenger 资格、AttackSlots=2 或 SocialVengeanceGroupCap=3。

## 评分、表达、软预算

记 T=Temperament、N=Nerve、C=Conformity、R=RoostAffinity；三个 J 是从既有 VisualSeed 用独立 salt 派生的稳定 System.Random 值。所有最终评分限制在 0–1，并清理非有限输入。

- Sentinel：`1.15 * (0.35N + 0.20C + 0.15*(1-2*abs(T-0.5)) + 0.10R + 0.20Js)`。
- Bully：`1.15 * (0.50T + 0.30N + 0.20Jb)`。
- Opportunist：`1.15 * (0.30N + 0.25*(1-abs(T-0.38)/0.62) + 0.20*(1-C) + 0.25Jo)`。
- 三者均减去 `0.18*PanicRatio + 0.30*Trauma`；Grief 另分别减去 `0.20/0.40/0.30 * GriefStrength`。经过安全确认的 Opportunity 给 Opportunist 最多 +0.04。
- Js/Jb/Jo 的 salts：`0x1937AC51 / 0x35AC1497 / 0x271DA653`。不调用 UnityEngine.Random 来重滚角色。

进入需要最高分 ≥ entryThreshold，且领先第二高分 ≥0.12。角色接近时保持 None。

软预算为 `max(1, round(activeCount*0.24))`。进入阈值：

```text
min(0.96, 0.79 + max(0, roleCount-softBudget+1)*0.035)
```

14 只时预算参考值为 3；有 3 个表达者后门槛从 0.79 升为 0.825。预算不空降角色，不保留职位，不硬阻止极端高分者继续进入。计数来自最多 30 tick 旧的快照，允许短时软超额。

每只每 120 tick 重评估，首相位由 unsigned seed %120 错峰。被抑制时仍保持原相位，避免危险结束后全群同帧重新评分。Commitment=800–1100 tick；到期后在下一次重评估结束。存续退出分数为 0.72，形成进入/退出迟滞。正常结束或危险打断只在状态转换时设置 Cooldown=240–360 tick，不能逐帧重置冷却。

这些是 runtime expression。NewRoom/ResetRoom、死亡、Destroy 清除临时状态；unrealize 后随 realized AI 丢弃。没有新增 save schema 字段。

## FlockSnapshot 与性能

DesertSwarmRoom 每 30 tick 扫描一次已有 Hive.flies，缓存值类型：Center、AverageVelocity、ActiveCount、PanicRatio、PreviousPanicRatio、RoostRatio、ExpressedRoleCount。SnapshotAge 可供调试查看。

过滤 dead、deleting、shortcut、other-room、InHive，以及无效 BodyChunk / 非有限位置速度。空群落返回零均值，不缓存任何 realized 引用列表；死亡和离房会在下一快照刷新时释放预算计数。角色的有效表达查询即时应用 suppression，不等待评分周期。

新增成本为每房间低频 O(n) 快照、每只每 120 tick 固定三项评分，以及每次原有 8-tick 生物扫描中 O(1) 的可见目标采集。每帧只做固定状态检查和少数显性观察者的局部移动修正。没有 per-bat Dictionary/List 历史、N×N 关系图、新群体扫描或第二个 AI controller。

## 可见行为

### Sentinel

安全普通 Flight 的轻量目标偏向群体中心约 190 px 的外围；存在可见 Player 时在约 230 px 观察。目标限制在群体中心 260 px 内；如果这个限制会把观察者拉进危险距离，就放弃修正。

复用原有 8-tick 可见生物扫描。玩家实际向群体移动、拿着可见 Weapon、距离较近且正在接近，或群体出现 Panic，都会增加 confidence；安静路过会降低 confidence。每次有证据 +0.18，无证据 -0.10；观察至少 24 tick 且 confidence≥0.72 才报警（持续证据通常约 32 tick）。这允许误判，但不读取输入、敌方 AI 目标或隐藏信息。

可见捕食者优先于更近的玩家。对捕食者只保持或增加距离，目标参考至少 340 px；不会主动向 Peach 靠近以确认。已有 direct danger 仍立即压过角色。

确认只调用现有 `Threatened(..., false)`，因此复用局部 Alarm 和自己的 Escape；不调用死亡/捕获广播，不制造 Trauma。独立 480 tick 报警冷却与角色中断防止持续重复报警。

### Bully

只加权原普通骚扰链：主动 Observe 动机门槛 ×0.82，Observe 时长 ×0.82，试探 Orbit 横向半径从 150 调至 115 px，FakeDive 概率 +0.14。

没有增加真实伤害，没有额外 retaliationCharges，也不续写旧 attacker memory。Approach/Circle/Dive/Attach 继续经过原 AcquireSlot；角色结束不会额外发起攻击。3 个 Bully 同时争用同一目标时仍只能获得两个 formal slots。

### Opportunist

可见潜在威胁时偏好约 270 px 的中距离观察，不主动创建新的攻击目标。危险/恐惧经历或原来可见的威胁离开扫描，会保留 600 tick 的机会候选窗口。

只有连续至少 40 tick 的扫描无可见威胁、PanicRatio≤0.10 且没有上升、Trauma<0.15、全部 suppression 已解除、既有 Fear/CorpseReminder 不阻止活动，才允许向群体中心约 95 px 的活动区轻微返回。因此比完全依赖普通漫游更主动回到活动区，但**不缩短仍在执行的 retreat，不清除 Fear，不寻找或捡尸体**。

观察者不会在已经开始普通攻击时突然取得 Sentinel/Opportunist 并接管动作；明确自卫记忆仍属于原 AI。

### 移动约束

只在 vanilla Idle/Swarm 且 DesertAI Flight/Cooldown、无普通 Target 时应用外围/Watch/返回偏置。每次检查附近 24 px 的候选点、原 ValidSwarmPosition、地形、视线；只调整 localGoal 与至多 0.16 的速度差，期望速度有界为 3。没有 teleport、独立寻路器、Boids 或生命/防御/速度等级 buff。

## 生存优先与跨系统

dead/deleting、无意识、任何 grabbed、shortcut、emergence、Rain/Burrow/lured/safari、Extreme Vengeance（包括 Follower 和 Withdraw）、Trauma≥0.42、Grief≥0.30、direct danger/retreat/Escape、DeathShock/显著 Fear/CorpseReminder、Chain/Roost，均压制显性表达。

较弱 Grief 通过评分抑制表达，尤其 Bully；强 Grief 立即停止。PTSD 比角色、Bond、Grief 愤怒都更优先。角色不修改 CanExtremeVengeance，不把 Bully 当作 True Avenger，不改变复仇组上限。Roost 和 Fly Chain 照原机制工作，角色移动不会拆链。

`DesertBatflyIntimidation.BlocksSocialRoles` 是只读查询，不创建新的 morale 状态，因此死尸不会因角色检查重新激活 activeStates。

内部调试可读：Role/Expressed、Scores、Commitment、Cooldown、EvaluationTicks、SentinelAlertConfidence、OpportunityTicks、Suppression/LastSuppression，以及房间 Flock/SnapshotAge。没有玩家 HUD 标签或永久换色。

## 整体 AI 复盘

- Flight→外围/Watch→Threatened/Escape：原局部报警入口处理撤离；停止角色并保持错峰，报警冷却不会被观察循环重新刷新。
- Bully→Observe/FakeDive→AcquireSlot→Finish：只改评分与现有动作参数，不直接写 hasSlot、不创建伤害路径；多 Bully 槽位回归通过。
- Bully + retaliation/GrabMemory：没有新增 charges、没有缩短 cooldown 或恢复旧 Target；直接威胁和已有 PTSD 逻辑照常生效。
- Opportunist→danger→retreat→return→再次 danger：机会只是过去危险的证据，SafeOpportunity 明确要求当前 suppression=None；尸体提醒与 Trauma 仍阻止返回。
- Grief/Bond/PTSD：角色不依赖 Bond 身份；高 LonerLike 可以持有 0.9 Bond；强 Grief、PTSD 中断通过测试。
- Vengeance/Follower：整个复仇生命周期（包含 Withdraw）均停止角色权重；没有把职业变成 leader/follower 身份。
- Chain/Roost/player-held/Peach-held：角色只读 suppression；不会为了站岗拆链或在被抓时纠正移动。
- corpse/Destroy/room/shortcut：无角色全局表，死亡和 ResetRoom 清空状态；快照只保存值，刷新后恢复角色预算。
- 修复原 RaiseLocalAlarm 没过滤 dead/deleting/other-room/shortcut 的接收者，避免新 Sentinel 入口对无效对象写 retreat。
- 修复危险持续时重评估计时可能在零等待而造成恢复同帧峰值：保持 seed 相位。
- 修复 Watch 可能在普通攻击中途取得角色：观察型角色不在有 Target 时新进入。
- 修复较近 Player 可能遮掉已可见 Peach：扫描优先捕食者。
- 修复外围 clamp 可能把退避点变成向捕食者/近距离玩家靠近：目标后验检查不允许缩短应保持的安全距离。
- Commitment 自然结束进入冷却；被抑制时只在角色→None 转换写冷却，没有每帧重置导致永久卡住。
- 没有保存 Role/Scores，也没有修改 Task 05A 存档或人格 seed 顺序。

## 自动验证

执行：

```powershell
dotnet build src/DryCycle.csproj -v quiet
dotnet build tests/DesertBatfly/DesertBatfly.Tests.csproj -v quiet
& tests/DesertBatfly/bin/Debug/net48/DesertBatfly.Tests.exe 'D:/Application/Steam/steamapps/common/Rain World' 'D:/Application/Steam/steamapps/common/Rain World/RainWorld_Data/StreamingAssets/mods/Ancient Site/newest/plugins/DryCycle.dll'
```

两个项目均 0 警告 / 0 错误；完整套件 **187677 assertions PASS**。主项目 DLL 输出到项目配置的游戏 plugins 目录。

安全 baseline，10,000 seeds 的单次可进入表达分布：

| 表达 | 数量 | 占比 |
| --- | ---: | ---: |
| None | 7584 | 75.84% |
| Sentinel | 193 | 1.93% |
| Bully | 1445 | 14.45% |
| Opportunist | 778 | 7.78% |

另用固定随机种子生成 1,000 个 14-bat synthetic colonies，逐个应用软预算：None=10909/14000（77.92%），显性表达平均 3.091/群，范围 0–7；117 群只有 0–1 个角色，129 群有至少 5 个角色，685 群没有 Sentinel。没有填岗或保证每群三种齐全。此为安全环境中的评分/预算统计，**不是实机时间占比**；危险、Roost、冷却会进一步减少实时表达。

原随机回归：Male 4800 / Female 5200；True Avenger 494（4.94%）；SandSpit 3729。新增测试覆盖评分稳定/finite、dominance gap、预算压力与极端越界、Flock 过滤与空值、角色真实进入/到期/冷却、错峰、各类 suppression、Bully AttackSlots、Loner-like Bond、可见威胁确认与局部报警、捕食者优先、外围安全距离、Opportunist 连续清晰观察/retreat/CorpseReminder、ResetRoom 清引用。

Task 05A 和原武器/营养/捕食/AttackSlots/流沙/出土集成测试继续通过。托管测试没有模拟完整 Unity 运动循环、实际舌头捕食或长期群落活动，因此没有声称实机验收通过。

## 待实机验收（A–F）

- A 普通群落：观察至少 10 个群落，普通个体明显最多，不应每群凑齐角色。
- B Sentinel：外围停留和转向；远处路过先观察且不必报警；接近/武器迹象后局部报警；Peach 出现时不会主动送进舌头范围。
- C Bully：更主动 Observe/FakeDive；同时最多两个 formal attacker；反杀同伴后照常 Fear/PTSD。
- D Opportunist：威胁期间谨慎；真实风险提示解除后较早回活动区；不扫描尸体或捡资源。
- E None：仍保留完整 Temperament/Nerve/Conformity/SocialBond 等差异，无角色标签。
- F Task 05A：强 Bond 的 Loner-like 仍在乎同伴；Grief 压过 Bully/Opportunist；同伴死亡后的 PTSD 优先级不变。

需现场特别观察外围偏置在狭窄地形的可见程度、长时 Commitment/Cooldown 下的实际角色数量，以及密集 Peach/武器事件中局部 Alarm 的频度；这些依赖真实游戏运动，不能由种子统计替代。

## 文件和未实现接口

新增 DesertBatflyRoleScores.cs（纯评分）、DesertBatflySocialRoles.cs（固定 runtime 状态和行为权重）、Program.SocialRoles.cs（测试）与本文档；修改 DesertBatfly.cs、DesertBatflyAI.cs、DesertBatflyIntimidation.cs、DesertSwarmRoom.cs、测试工程/入口、DiscussionStatus.txt。

第 3 项完整 Social Signal、第 8 项 ThreatStyleMemory、第 10 项 Corpse Ecology 均未实现、未更改完成状态。未来可以读取角色/信心等内部只读信息；没有新增信号广播系统、武器学习或尸体资源模型。第 5B reproduction 继续 deferred。
