# Effect Hover Preview

本目录中的 Preview 代码只负责 **RoomEffect 悬停实时预览**。Obj、Sound、Trigger 不在这个功能范围内。

## 目标

Effect Preview 的兼容对象不是某个具体 Mod，而是 Rain World / Unity 的运行时行为。

因此这里禁止建立：

- RegionKit / POM / M4r 等专用 Adapter；
- 按 Mod ID、程序集名称、namespace 判断的兼容分支；
- 为某个第三方 Effect 名称维护硬编码构造表。

未知 Mod 只要最终通过正常 `RoomEffect.Type`、`RoomSettings`、`Room.AddObject`、Futile、`RoomCamera` 或 Unity Shader 全局状态实现效果，就进入同一套通用探测和回滚流程。

无法证明安全和可逆时必须 fail closed：保留第一层临时 RoomEffect 预览，关闭该类型本次会话中的高级预览。

## 当前生命周期

```text
ImGui Hover
    ↓ ~180 ms
Temporary RoomEffect(save=false, amount=0.5)
    ↓
Stage 1: 普通 RoomSettings / GetEffectAmount 读取自然响应
    ↓
Stage 2: 通用高级预览
    ├─ Room.Loaded / HookGen IL constructor recipe
    ├─ 保守 A/B Hook probe
    ├─ 扩展 bool / int / enum / amount 参数推导
    └─ 构造约定 fallback
    ↓
Ownership / Journal
    ├─ Runtime UAD
    ├─ Runtime descendant UAD
    ├─ Drawable / Camera SpriteLeaser
    ├─ safe Room field delta
    ├─ Global Shader property / keyword
    ├─ synchronous RoomCamera / Futile delta
    └─ runtime Futile + 可证明独占的 RoomCamera field
    ↓
Hover End / Save / Undo / Redo / Add / page switch / DevTools close
    ↓
identity-based rollback
    ↓
leak / ambiguity check
```

## 普世兼容机制

### 1. 临时 RoomEffect

临时 Effect 被插入 `RoomSettings.effects` 最前端，因此不知道 DryCycle 存在的代码只要正常调用：

```text
GetEffect
GetEffectAmount
遍历 RoomSettings.effects
```

就能看到预览状态。

临时对象不进入 Inspector Snapshot、History、保存文件和模板写入。

### 2. Load-time Effect bootstrap

对于只在 `Room.Loaded` 阶段创建 Renderer / Controller 的 Effect，不重新执行整个 `Room.Loaded()`。

系统会从本体方法和当前已注册 HookGen 回调的 IL 中寻找：

```text
目标 RoomEffect.Type
    ↓
条件分支
    ↓
new 非 PhysicalObject UAD
    ↓
Room.AddObject
```

只有能建立足够明确关系的构造器才会执行。复杂或含持久副作用的路径直接降级。

### 3. Runtime ownership

高级 Preview 创建的对象按对象身份持有，不按类型名删除。

Preview-owned Controller 在 `Room.Update` 中继续 `Room.AddObject` 创建非物理子对象时，优先使用 `Room.updateIndex` 建立精确父子因果；Update 循环之外只允许保守调用栈兜底。

生成 `PhysicalObject` 视为污染，立即结束高级预览并把该 `RoomEffect.Type` 标记为 Unsafe。

### 4. Runtime Futile ownership

运行中的 Preview Controller 对：

```text
FContainer.AddChild
FContainer.AddChildAtIndex
FContainer.RemoveChild
FContainer.RemoveAllChildren
```

造成的变化会在“当前执行者可证明属于 Preview”时被记录。

新建 FNode 在回滚时按身份移除；已有节点被移动、重排或暂时移除时，记录原容器和原索引并恢复。如果节点随后又被其他系统修改，则不强行覆盖，而是判定 ambiguity / Unsafe。

### 5. Runtime RoomCamera ownership

直接写 `RoomCamera` 字段的 Preview runtime type 会通过 IL 分析得到其写集合。

只有同时满足以下条件才允许继续高级预览：

- 字段类型可以安全保存和恢复；
- `RoomCamera.DrawUpdate` 的正常帧路径不写同一字段；
- 当前房间里没有非 Preview runtime object 也写同一字段；
- runtime type 不调用无法证明可逆的 RoomCamera 修改 API。

通过后，每个游戏 Update 结束记录最终 Preview 值；回滚时只有当前值仍等于最后观察到的 Preview 值才恢复原值。发生外部竞争变化则判定 Unsafe。

后续由 Preview 子对象创建的新 Controller 也会重新跑同一套 Camera safety 分析；不能安全接管时下一安全边界立即结束预览。

### 6. Global Shader journal

对于能从 Effect 运行时调用图中证明 Property ID / Property Name 的：

```text
Shader.SetGlobalFloat
Shader.SetGlobalInt
Shader.SetGlobalVector
Shader.SetGlobalColor
Shader.SetGlobalTexture
Shader.EnableKeyword
Shader.DisableKeyword
```

会在高级 Preview 前保存原值并在结束时恢复。

动态拼接、无法反推出稳定 key、或当前 Unity API 没有对应 Getter 的全局 Shader 修改不会猜测，直接关闭高级预览。

## 缓存

当前包含两类会话内缓存：

1. Unsafe cache：按 `RoomEffect.Type` 记录已证明不能安全高级预览的类型；Stage 1 仍继续工作。
2. Stage-1-only negative cache：如果某个 **已实例化 Room 对象** 中第一次探测没有产生任何高级预览产物，后续 Hover 直接使用 Stage 1，不再重复做昂贵 IL / Hook 扫描。房间重新 realization 后自动重新探测。

缓存不保存 Mod 名称，也不写磁盘。

## 结束标准

从代码架构角度，Effect Hover Preview 的核心能力到这里停止扩张。后续不再为了提高某个特殊 Mod 的命中率无限增加 symbolic execution 或私有反射。

正式结束只剩实际游戏环境验证：

- 使用游戏安装中的 `PUBLIC-Assembly-CSharp.dll`、`HOOKS-Assembly-CSharp.dll`、`MonoMod.RuntimeDetour.dll`、Unity DLL 和 RWImGui 做完整联编；
- 进游戏验证本体典型 Effect：动态数值类、AboveClouds/PinkSky/RoofTopView、SunBlock、Lightning/BkgOnlyLightning、VoidSea 等；
- 装入几个 DryCycle 从未针对适配过的第三方 Effect，分别确认 Advanced / Stage1 fallback / Unsafe 三条路径；
- 连续快速 Hover、切 Effect、Add、Save、Undo/Redo、切 Vanilla、关闭 DevTools、切房间，检查 `Room.updateList`、Drawable、SpriteLeaser、Futile layer、Shader global 和 RoomSettings 不增长；
- 多次进入/离开同一房间，确认 negative cache 不跨 Room realization 错误复用。

发现真实编译错误或运行时泄漏时只修具体问题；若某未知 Effect 因行为无法证明可逆而降级到 Stage 1，这属于预期行为，不再视为架构未完成。
