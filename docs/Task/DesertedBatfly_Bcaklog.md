# Desert Batfly Backlog

> 本文件是 Desert Batfly 历史 Task 清理后的唯一待办入口。
>
> 只记录尚未实现、尚未完成设计、尚未完成本地编译/实机验收或仍需调参的内容。
> 已经完成、已经否决、已经被当前架构替代的内容不在本文件保留。
>
> 完成某项后，直接从本文件删除对应待办，不建立“已完成墓地”。

## 1. 使用规则

- 当前源码与测试始终高于历史 Task 文档。
- 不得根据已删除的历史 Task 恢复旧文件名、旧职责或旧架构。
- Task02 的 Sentinel / Bully / Opportunist / SocialRole 已正式否决，不属于 Backlog。
- Task14 的 Desert Batfly 架构重构、生命周期归属清理、内部 Hook/Reflection/façade 清理已经完成，不属于 Backlog。
- 下面标为“实机验收”的项目，默认含义是：代码侧已经存在或已经有对应实现，但还没有足够证据把该功能标为 Live Verified。
- 任何实机验收都应以当前 `src/Creatures/DesertBatfly/` 的 `DB_*` 架构为准，不允许为了匹配旧 Task 文本恢复历史 API。

---

# 2. 尚未实现 / 尚未完成设计

## 2.1 Reproduction / 繁殖系统（原 Task05B）

**状态：尚未深入讨论，禁止直接实现。**

仍未确定并未实现的内容：

- Courtship / 求偶展示；
- Accept / Neutral / Reject 求偶响应；
- MateBond / 配偶关系；
- MateBond 是长期还是临时；
- SocialBond 是否、以及如何升级为 MateBond；
- 配偶死亡是否具有独立于普通 SocialBond 的 Grief / Trauma；
- Gravid / 怀孕或携卵状态；
- 繁殖期是否改变体型、质量、风险评估、回巢倾向；
- mating / 交配；
- egg / 产卵；
- larva / juvenile / 幼体；
- Parenting / 亲代行为；
- Kinship / 亲属关系；
- Hive reproductive credit / 群落繁殖额度；
- 与 Colony Migration / Abstract population 的人口增长接口；
- 是否真正跨 Cycle / 存档增加 Desert Batfly 数量；
- 怀孕时长、产卵数量、繁殖季节、配偶选择等具体参数。

硬约束：

- 当前不能自行补完设计；
- 不现场生成幼体；
- 不建立假装完整的 MateBond / Gravid / Parenting 空壳系统；
- 真正实现前必须单独完成设计讨论；
- 接入 Task09 背景恢复时必须避免“背景恢复 + 真实繁殖”双重无限增殖。

---

## 2.2 Corpse Ecology / 尸体生态

**状态：方向采纳，仍需深入讨论后再实现。**

已确认但尚未完成的方向：

- Desert Batfly 尸体可以成为活体的弱 danger cue；
- 多具尸体可以形成短期高危险区域，但不能成为永久恐惧场；
- 高 Conformity 个体可更容易响应尸体危险信息，但不能全房心灵感应；
- 尸体来源（Player / Peach / 其他 Predator）可影响风险解释；
- 某些 Scavenger / Predator 是否会被尸体吸引；
- 是否需要 freshness / 腐败阶段；
- danger cue 持续多久；
- 多尸体如何叠加；
- 是否、以及如何影响 Task09 的 Mortality / Predator / Shelter inference；
- 如何避免与现有 CorpseWarning、Intimidation、PredatorTrauma、Task11 Acute、Task12 Alarm 重复结算；
- 若通过 Task12 传播，应由“发现尸体的真实个体”发出可观察信号，而不是尸体系统直接全群广播。

实现前必须先确定：

1. 哪些生物会被尸体吸引；
2. 是否做 freshness / decay；
3. 危险提示持续时间；
4. 多尸体叠加上限；
5. 与迁徙压力的接口；
6. 捕食者聚集是否形成二次生态反馈；
7. 与现有死亡恐惧链的唯一所有权。

---

# 3. 已有实现，但仍待实机验收 / 调参

## 3.1 Injury & Experience / 伤病与经历后遗症（原 Task04）

**状态：当前代码已有 Injury / Recovery domain；旧 Task 中仍有效的实机场景尚需统一验收。**

需要完成：

- Rock：保留普通 blunt health damage / stun / shock / injury，但该次 Rock Violence 不能直接完成 live -> dead；之后 Spear / Bite / Predator / Drowning 等仍可正常杀死；
- Spear survivor：Health、Wing Injury、Shock、PlayerTrauma、Escape、Recovery 链正常；
- 单侧伤：稳定左右不对称、转向半径变化、视觉 wing asymmetry、无随机漂移；
- 双侧伤：整体操控下降但无错误单侧偏转；
- Peach Tongue Survivor：默认不凭空增加 Wing Injury，Shock + PredatorTrauma 正常；
- Peach Bite Survivor：Health / Wing Injury / Shock / PredatorTrauma 合理；
- Severe Injury + Vengeance：身体能力可以压过复仇移动，但不能破坏复仇状态生命周期；
- Grief + Injury：情绪仍存在，但不能绕过 PhysicalCapability；
- Roost Recovery：恢复明显快于普通 Flight；
- Cycle Recovery：Wing Injury 保留并恢复，Personality 不变，Trauma 不被错误清零；
- Shortcut / room transition：伤病保持并在离开后重新评估；
- legacy save：旧存档缺伤病字段时安全默认；
- 11+3 / 20+ 个体压力下无新 O(N²)、无明显 GC 热点。

已经作废、不再验收的旧 Task04 内容：Sentinel / Bully / Opportunist injury suppression。

---

## 3.2 Sex + SocialBond + Grief（原 Task05A）

**状态：代码侧已实现，实机行为仍需验收。**

待验收场景：

1. 同一 AbstractCreature 多次 realization 后 Sex 不改变，外观二型稳定；
2. Female 高 Temperament / Male 低 Temperament 等组合真实存在，Sex 不覆盖 Personality；
3. 长期同 Chain / Roost 只缓慢形成弱到中等 Bond，不在数秒内拉满；
4. Peach Rescue 后 `rescued -> rescuer` Bond 增益明显高于反向增益；
5. 以后 partner 再被捕获时，安全且无 severe PTSD 的强 Bond 个体更愿意参与现有 Rescue，但不突破并发限制；
6. severe PTSD 可以压过 Bond rescue motive；
7. 强 Bond partner 死亡时 Grief / Trauma 强于普通同类死亡，而无关系个体只接普通死亡影响；
8. Grief 表现继续受 Temperament / Nerve 影响，但不能绕过 PTSD / Vengeance / Injury 资格；
9. Bond 非对称性在真实行为中成立；
10. partner 离开房间后 Bond 保留，但没有跨房坐标追踪或心灵感应。

仍需关注：

- save/load 与 malformed identity 安全；
- rescue 事件不因 Tongue + Grasp 重复加 Bond；
- death 不重复叠加 Grief / Trauma；
- dead bat 不继续采样 Bond；
- partner 消失后无 NullReference；
- Roost partner bonus 不形成永久互追挂点。

---

## 3.3 Colony Migration + Weather Refuge + Cross-Room Navigation（原 Task09）

**状态：当前 `World/Travel` / Colony / Refuge 实现已经存在；仍需完整实机验证。**

待验收场景：

1. 普通群落：无长期 Peach、仅偶发天气时，多 Cycle 不应频繁迁徙；背景恢复缓慢正常；
2. Peach 长期捕食：压力逐步累积，小批次真实个体迁往更合适 Colony，原 Colony 不瞬间补满；
3. 单次 HeatWave：回巢/躲避，但不直接永久迁徙；
4. 连续 HeatWave：EnvironmentalPressure 累积；Home shelter 足够时 ShelterFailure 低，反复失败时长期迁徙概率上升；
5. 区域级 IntenseHeat：所有 Colony 同样危险时不发生 A->B->C->A 乱迁；Home 足够则留巢，不足则提前 Refuge，天气后 ReturnHome；
6. Refuge route：短但致命路线必须输给稍长安全路线；
7. Travel 中遇 Predator：暂停 Travel -> Escape -> 威胁结束后 Resume；
8. Severe Wing Injury：不强迫伤者盲目长途撤离，允许近 Refuge / local shelter；
9. 回迁：环境反转后允许旧居民回迁，但 cooldown / hysteresis 防止每 Cycle 往返；
10. 多 Colony 共用 Refuge：临时共用安全房，但 Home Colony ownership 不合并，灾后分别返回。

还需验证：

- abstractize / realize 后 TravelIntent 连续；
- EntityID / Personality / Sex / Bond / Trauma / Injury 在迁徙前后不变；
- route cache 不退化成每只 bat 独立高频 world search；
- 20~30 bat 情况下无区域级昂贵扫描；
- Debug 能解释为什么迁、为什么不迁、为什么选择当前 Refuge / route。

---

## 3.4 Neutral Social Life + Micro-Flock（原 Task10）

**状态：代码侧完成，Scenario A~M 仍待实机验收与调参。**

- A：15~20 只中性房间观察 2~3 分钟，应出现多种互动，同时保留 Solo Flight；
- B：低 Temperament / 高 RoostAffinity 群体应更常见 Companion / Group / Roost，而不是 Chase；
- C：高 Temperament / 高 Nerve 群体 SocialChase / PassBy 更多，但不能造成伤害、AttackSlot 或 Trauma；
- D：高 Bond pair 更容易 Companion / 一起 Roost，但分开后仍独立生活；
- E：6~10 只健康个体形成 3~6 只松散 MicroFlock，不形成无人机编队；
- F：迎面/交叉 PassBy 应稳定左右分流，不一起向上弹；
- G：TerrainType.Floor 平台底面 Roost 可以通过 Invitation / ChainSocialization 形成倒挂群；
- H：玩家接近/投矛时 social 立即取消，Fear / Escape 接管；
- I：Peach 进入 GroupDrift 时 social 取消，Alarm / Intimidation / Fear 正常接管；
- J：SocialChase 中受伤时 interaction 取消，严重伤进入 recovery；
- K：Task09 EmergencyRefuge 出现时立即释放社会状态，不延迟跨房旅行；
- L：原版 Drop / Passive 不被 Task10 强行拉回 BatFlight/social；
- M：20~30 只长期运行无明显周期 GC spike、无每帧 O(N²) 扫描。

调参目标：

- interaction ratio；
- 横向群体观感；
- vertical-column reduction；
- Roost 聚集程度；
- cooldown / repetition；
- 20~30 bat 性能。

---

## 3.5 Threat Signature Memory / Player Combat Learning（原 Task11）

**状态：CODE-SIDE COMPLETE；Scenario A~P 尚未 Live Verified。**

- A Spear learner：多次 Spear 经历后 Circle / Dive / Attach / SideApproach 出现可见但非完美调整；
- B Rock learner：主要学习 Projectile + BluntStun，偏短横向规避，不像 Spear 一样极端远离；
- C Firecracker / Startle：学习 Startle，打断 Roost/GroupDrift，再次看到对应物品时提前警惕；
- D Explosive Spear：Projectile / Piercing / Explosion / AreaDenial 多标签正确，爆后不立即回爆心；
- E Grabber：高 GrabCapture 后 Attach / 贴身 Circle 明显下降；
- F Pursuer：持续追击形成 PursuitPressure，之后撤退更早、更彻底；
- G Counter killer：只反杀正在正式攻击的个体时学习 CounterKill，后续更多 Probe/FakeDive；
- H ordinary kill：先手杀未攻击 Batfly 不应错误增加 CounterKill；
- I Passive/Retreat：RetreatTendency 缓慢建立，只给合适人格有限信心；
- J Co-op：Player0 / Player1 形成独立记忆槽；
- K Memory decay：多个 Cycle 无对应证据后显著衰减；
- L Change style：旧风格与新风格可并存并逐渐重新评价；
- M PTSD override：强 PTSD 不能被 NonAggression / Retreat 反向覆盖；
- N Extreme Vengeance：仍允许复仇，但战术更谨慎；
- O Mass casualty：AcuteMassCasualty 立即打断中性互动并促使散开/逃离；
- P 20~30 bat stress：多武器事件下帧率、GC、witness scan、debug 稳定。

---

## 3.6 Observable Social Signal Network（原 Task12）

**状态：CODE-SIDE COMPLETE；本地完整构建和 Scenario A~P 仍需最终验收。**

- A 普通 Alarm：高 Conformity 更容易响应，但不整房同步；
- B 墙后近距离 Alarm：close acoustic 有弱反应，远墙后无反应；
- C Relay 两跳：A->B->C，禁止 Hop3；
- D Player Kill：direct witness 获得直接 Intimidation/Task11 evidence，仅接 Alarm 的个体不能获得详细 Threat Memory；
- E Peach Tongue Distress：Bond / Nerve 影响 rescue interest，但不保证所有人救；
- F Rally：已有 Avenger 才能发 Rally，不创建第二个 Avenger；高风险个体可拒绝；
- G PTSD vs Rally：PTSD 不被 Conformity/Rally 覆盖；
- H RoostCall：只提高合法 Roost/Chain 响应，不绕过 ChainTile；
- I HarassSignal：只提供正式 Harass context，仍受 CanHarass / Injury / PTSD / AttackSlots；
- J SocialChase isolation：不产生 HarassSignal、Rally、CounterKill formal aggression；
- K SafeSignal：只加速 signal concern decay，不清 PTSD / ThreatMemory；
- L False Safe：仍存在 Player / Predator / projectile 时拒绝 Safe；
- M Task11 Acute Explosion：间接接收者只得到 Alarm，不因此训练 ExplosionPressure；
- N Task09 priority：Travel route 不被 Rally/Roost/Harass 抢走；
- O 20~30 bat stress：packet 数量受控，无反馈风暴、GC spike 或全群每帧扫描；
- P 长时间稳定性：generation/ring buffer 正常回收，无永久 AlarmPressure、失效 emitter 或状态泄漏。

---

## 3.7 Environmental Activity & Shelter Behavior（原 Task13）

**状态：当前 `World/Environment` 实现已经存在；Scenario A~U 仍需完成实机验收。**

- A RoomSettings Fog only：无 DryCycle Fog 时 Task13 必须保持 Calm；
- B LightRain healthy：大部分行为近正常，仅有轻微休息差异，无 Refuge/Migration；
- C LightRain injured/dehydrated：伤者/低 Nerve 更早休息，真实暴露可获得弱 moisture benefit，不同步避雨；
- D DryCycle Fog：活动半径下降、长距离 Harass/SocialChase 减少，近距离社会生活保留；
- E DenseFog familiar Home：活动显著收缩、Roost/Home 增强，熟悉环境导航优于陌生环境，不使用脚本化周期撞墙；
- F DenseFog displacement：房间确实不可用时只能通过 Task09 临时撤离，Fog 清后 ReturnHome，HomeColony 不变；
- G early HeatWave：合适 Personality 的 agitation/aggression 先于 mass shelter；
- H sustained HeatWave：活跃/攻击与阴影/倒挂/疲劳个体产生可见分化；
- I IntenseHeat + player：多数健康成年个体可获得 damage permission，但仍由 AttackSlots 限制，同时大量个体 Home/Roost/Burrow；
- J IntenseHeat injured：严重伤病个体应强烈偏向 survival；
- K Sandstorm forecast：明显早于普通 HeavyRain/Fog 收缩活动，玩家能读出预警；
- L active Sandstorm Home：强 Home retention、很少 discretionary travel、多 shelter cluster/Burrow/Roost、主动 Harass 降低；
- M player blocks Hive/Burrow：漫游攻击保持低，但入口附近 defensive aggression 可以提高；
- N DeathSandstorm late：禁止新增长距离 relocation，只允许极窄 Task09 emergency exception；
- O Task09 overlap：Task09 拥有控制帧时 Task13 不再拉 local anchor；
- P Vengeance overlap：轻 Heat 可提高活动，hard survival 暂停而非删除 Vengeance，Recovery 后可恢复；
- Q mixed weather：兼容轴组合，不允许 HeavyRain + LightRain 等同族重复叠加；
- R shelter crowding：20 bats 分散多个 anchor，禁止单点堆积；
- S no good shelter：弱 fallback/Hive/短距离 sheltered flight，不 teleport/非法挂点；持续严重失败才报告 bounded LocalShelterFailure；
- T Recovery：天气结束后个体错峰恢复活动，不同一帧整群释放；
- U 20~30 bat stress：无 Task13 引入的明显 frame spike，无 per-frame terrain / pairwise scan explosion。

---

# 4. 跨系统最终验收

在 Desert Batfly 整体进入“稳定完成”前，还需要至少完成一次综合实机压力回归：

- 20~30 只 realized Desert Batfly；
- 同时覆盖 Weather + Travel + Social + Signal + Threat + Injury；
- 玩家投矛 / Rock / Explosion / Grab 与 Predator 并存；
- 多房移动、shortcut、abstractize/realize、save/load；
- 无明显 GC spike；
- 无每帧 O(N²) 群体扫描；
- 无多套 locomotion owner 同时写控制；
- 无状态跨房/死亡后泄漏；
- Observatory/Trace 能解释最终行为 owner 和主要原因；
- 不恢复 Task02 社会角色；
- 不恢复旧 `DesertBatflyAI.cs` / `DesertBatflyHooks.cs` / Bridge/Reflection 等历史架构。

---

# 5. 历史 Task 清理说明

本 Backlog 取代以下旧 Desert Batfly Task 文件作为待办入口：

- `Task_02_EmergentSocialRoles.txt`：已否决，不迁入待办；
- `Task_04_Injury_Experience.txt`：只保留仍需验收的有效场景；
- `Task_05_SocialBond_Sex.txt`：保留 5A 实机验收与未实现的 5B；
- `Task_09_ColonyMigration_WeatherRefugeNavigation.txt`：只保留剩余实机验收；
- `Task_10_NeutralSocialLife_MicroFlockInteraction.txt`：只保留 Scenario A~M；
- `Task_11_ThreatSignatureMemory_PlayerCombatLearning.txt`：只保留 Scenario A~P；
- `Task_12_ObservableSocialSignalNetwork.txt`：只保留本地构建/Scenario A~P；
- `Task_13_EnvironmentalActivity_ShelterBehavior.txt`：只保留 Scenario A~U；
- `Task_14_DesertBatfly_ArchitectureRefactor.txt`：架构重构已完成，不迁入待办。

旧 Task 的完成内容仍可通过 Git 历史查看，但不再占用当前 `docs/Task`。
