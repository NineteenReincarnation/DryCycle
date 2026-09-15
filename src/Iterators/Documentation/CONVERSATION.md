# Conversation 与对话脚本

第六阶段提供独立对话控制器、不可变脚本、条件与分支、可扩展命令、打断/恢复/取消，以及原版 HUD 输出。公共命名空间为 `DryCycle.Iterators`。本阶段没有新增运行时样例，也没有修改 PWN_AI。

## 接入与生命周期

最小注册默认创建 `EmptyConversation`，不会自动触发台词。通过 `.Conversation(ctx => new ConversationController(ctx))` 或派生控制器替换；工厂必须返回使用所传 Context 创建的新组件。Descriptor 的只读 `ConversationFactory` 与十参数完整构造重载保留此前所有构造签名。

`Runtime.Conversation` 与 `Context.Conversation` 指向同一组件。可从 Runtime 或行为模块的更新回调显式播放脚本：

```csharp
DialogueSequence sequence = Dialogue.Sequence("Greeting")
    .LookAtPlayer()
    .Say("Hello.")
    .Wait(40)
    .Build();
DialogueRun run = context.Conversation.Play(sequence, context.PrimaryPlayer);
```

创建顺序为 Body / Arm → Graphics → 玩家视图 → Brain → Conversation → Runtime.OnCreate 及后续回调。Brain.OnInitialize 时 Conversation 尚未创建；应在更新回调或 Runtime.OnCreate 之后提出播放请求。Conversation.OnInitialize 可以排队脚本，实际命令只在 Runtime Active 后执行。

每帧顺序为玩家刷新 → Brain → Conversation → Runtime.OnUpdate → Body / Arm / 物理 → Graphics → Runtime.OnLateUpdate。控制请求在 Conversation 更新开始时处理；其回调中新发出的请求延后到下一次更新，避免递归切换。

销毁时 Runtime.OnDestroy → Conversation（台词、当前命令、打断栈、排队脚本）→ Brain → Graphics → Arm → Body → 宿主及 Context 引用清理。控制器提供 `OnInitialize`、`OnUpdate`、`OnStarted(run)`、`OnEnded(run, reason)`、`OnDestroy`，无需安装额外 Hook。

## 脚本与命令

`Dialogue.Sequence(name)` 返回 Builder；`Build()` 创建独立快照，之后修改 Builder 不影响旧脚本。脚本可以共享，执行位置和计时器不能共享。条件与回调捕获的对象不会深拷贝：长期定义只捕获配置，不应捕获某个房间、玩家或 Runtime。

| API | 行为 |
| --- | --- |
| `Say(text, extraLinger = 40)` | 显示一行并等待实际 HUD 消息结束；extraLinger 是原版按文字长度计算的停留时间之外的附加帧数。 |
| `Wait(frames)` | 消耗指定次数的运行中更新；暂停和被打断时不计时。 |
| `WaitUntil(condition, timeoutFrames = 0)` | 每次运行中更新检查条件；0 表示不限时，超时只终止本次播放。 |
| `Pause()` | 脚本排队暂停自己，由 Controller.Resume 恢复后继续下一步。 |
| `LookAtPlayer()` / `LookAt(position)` | 通过 Body 提交注视意图，持续到暂停、结束或后续注视命令覆盖。 |
| `MoveTo(position, tolerance = 3, timeoutFrames = 600)` | 通过 Body 移动并等待进入容差；受现有碰撞和 Arm 约束，无路径搜索，超时结束本次播放。 |
| `Gesture(pose, frames = 40)` | 提交姿势并等待指定帧数；姿势保持到替换、暂停或播放结束。 |
| `Sound(soundID, volume = 1, pitch = 1)` | 在身体位置播放已有 SoundID，不等待音效结束。 |
| `Action(name)` | 请求 Brain 的已有具名动作，仍遵守优先级和可打断规则，不等待动作完成。 |
| `Callback(callback)` | 执行一次自定义逻辑。 |
| `Command(command)` | 插入可共享指令定义，每次进入创建新的独占执行状态。 |
| `Then(sequence)` | 嵌入另一段脚本，完成后回到父脚本。 |
| `Branch(condition, whenTrue, whenFalse = null)` | 进入时选择一个分支；false 分支为空则跳过。 |
| `Conditional(condition, sequence)` | 仅在条件成立时执行子脚本。 |
| `Random(params choices)` | 等概率选择一个分支；分支数组会复制。 |
| `When(condition)` | 只限制紧邻的前一步，进入时求值一次；连续 When 按短路 AND 组合。 |

每帧最多更新一条命令，因此瞬时命令之间也存在帧边界。Wait 的帧数包含它第一次 Update；Wait(0) 完成该步骤，但不会让下一条命令在同帧执行。分支/跳过步骤每帧最多遍历 64 次，未遍历完的部分留到下一帧。

脚本最多 1024 步，嵌套最多 32 层，Random 接受 1–128 个分支。帧数范围 0–216000，MoveTo 超时必须大于 0，单行文字最多 4096 字符；文字采用 `<LINE>` 分行标记。随机源属于各自 Controller，可在构造函数传入 randomSeed，默认 0，不修改 Unity 全局随机状态。

`DialogueCondition` 是 `bool (DialogueContext)` 委托。通过 `ctx.Iterator.StorySession`、`ctx.Player` 或调用方自己的状态检查角色、业力、Cycle、物品及剧情条件；本阶段不新增剧情变量或持久存档系统。

## 控制播放

| Controller API | 行为 |
| --- | --- |
| `Play(sequence, player = null)` | 排队开始，替换当前和整个打断栈；返回 Queued 状态的句柄。 |
| `Interrupt(sequence, player = null)` | 暂存当前播放位置，先执行插入脚本；插入完成后自动恢复上一层。 |
| `Pause()` / `Resume()` | 暂停/恢复当前脚本；返回值只说明请求是否已接收。 |
| `Cancel()` | 取消当前脚本及整个打断栈。 |
| `CancelCurrent()` | 只取消当前脚本，随后恢复上一层。 |
| `Current / InterruptedCount / IsBusy` | 当前脚本、暂存层数及是否还有脚本或控制请求。 |
| `IsInitialized / IsEnabled / IsDestroyed` | 组件生命周期状态。 |

控制请求按提交顺序处理，队列上限 64，打断栈上限 32。未初始化（OnInitialize 期间除外）、停用、销毁时不接受新请求，Play/Interrupt 返回 null；参数错误在提交时抛出。只有处于 Running 的脚本推进计时。

暂停或打断会撤回当前未完成的 Say；恢复时从该行开头重新显示，已完成的台词和 Callback 不重放，Wait 保留剩余时间。插入对话不会解除原本存在的手动暂停。Cancel 不自动恢复打断栈；需要恢复时使用 CancelCurrent。

player 缺省使用提交时的 PrimaryPlayer。绑定的玩家死亡、进入捷径或离开房间时，该脚本以 TargetUnavailable 结束；暂停和打断栈中的脚本也会检查。提交时没有玩家则运行无目标脚本，不因玩家缺席取消。

`DialogueRun` 保留 Sequence、Status、EndReason、FailureMessage、CurrentCommand、ElapsedFrames。结束状态为 Completed、Cancelled 或 Failed。OnEnded 期间通常仍可读到 DialogueContext；通知完成后清空 Controller、Iterator、Player。Runtime 在回调内被同步销毁时，其游戏引用可能更早失效，扩展者需检查空值。

## 身体输入与自定义命令

内置 Look / Move / Gesture 通过 DialogueContext 持有身体输入，在 Brain 之后应用，Runtime.OnUpdate 仍可覆盖。暂停和结束只撤回仍匹配该脚本最后一次写入的值，恢复进入前的输入；不会回滚已经发生的位移或已经播放的音效。Action 是一次 Brain 请求，脚本结束不会强制停止该动作。

自定义 `DialogueCommand` 实现 `CreateExecution(context)`，每次返回一个新的 `DialogueCommandExecution`。执行状态实现 `Update()`（true 为完成）、可选的 `Pause()` / `Resume()` 以及 `OnDispose()`。Dispose 由基类保证幂等；暂停不销毁整个执行状态，完成/取消/失败/卸载都会释放它。禁止返回外来、复用或已释放的状态。

自定义命令通过 `Context.LookAt`、`LookAtPlayer`、`MoveTo`、`Gesture` 使用相同的输入回收机制；直接修改 Body、订阅事件或创建资源时，由自定义命令负责回收。投影系统属于第八阶段，本阶段不提供无实现的 Projection 占位 API；已有外部系统可通过 Callback / Command 接入。

## HUD 输出与错误边界

默认 `RainWorldDialogueOutput` 在该房间当前相机的 HUD.DialogBox 中显示文本，调用游戏翻译器，使用原版逐字显示及停留时间。HUD 缺失、已有台词或 permanentDisplay 占用时等待，当前脚本保持 Running，其他框架组件正常更新。没有在等待 HUD 时自动跳过台词。

输出按消息对象身份记录所有权，不调用原版 Interrupt 清空共享队列。取消只移除自己提交的消息；若它在队首，则通过集中反射适配调用游戏私有 InitNextMessage，正确交接下一条外部台词。相机离开房间或 HUD 被替换时撤回该视图的消息。新加入的视图从下一条 Say 开始接收，不重播已提交的当前行。

传入自定义 `IDialogueOutput` 可替换呈现方式。TryShow 返回 null 表示暂时无法显示；返回的 `IDialogueLine` 通过 IsComplete 报告结束，Dispose 仅清理该条输出。输出提供者由调用方管理，控制器只释放它返回的台词句柄。

条件、命令、输出或 OnStarted 失败只结束本次脚本，后续脚本仍可播放。OnUpdate 失败停用对话组件，保留 Brain、Body、Graphics 与 Runtime。工厂或初始化失败清理已接管的内容并回退 EmptyConversation。OnEnded/OnDestroy/命令回收中的异常记录到日志，其他清理继续。

本阶段新增三组必要托管验证，结果见 [开发进度](PROGRESS.md)。验证包含真实游戏程序集的 HUD 消息队列清理及私有方法访问；台词呈现使用可控输出进行调度检查，没有验证真实游戏中的字体、逐字绘制、翻译或多相机显示。
