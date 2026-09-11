# DevTool 重构

本目录承载 Rain World DevTools 的新一代编辑器实现。目标不是给原版 `DevInterface` 换皮，而是建立一套以场景为中心、可扩展、可撤销、可搜索、可渐进迁移的编辑器框架。

## 硬性设计原则

1. **完整房间画面优先**：Rain World 继续按完整屏幕渲染。ImGui 默认采用覆盖式工具面板，不通过左右 Dock 永久压缩游戏 Camera。
2. **场景优先而不是页面优先**：Objects、Sound、Triggers、Room Settings 是同一个 Scene Editor 的工具模式，不因为切换工具就清空历史。
3. **UI 与业务解耦**：ImGui 只读取 EditorState、发出 Command；保存、Undo/Redo、Selection、对象创建和数据修改不写进 Draw 方法。
4. **核心完全自有**：ObjectDescriptor、PropertyDescriptor、Inspector、Gizmo、History、Input、Save、Selection 全部由 DryCycle 自己实现。POM、Fisobs、M4r 等第三方 Mod 只能作为设计经验参考，不建立编译依赖、软依赖、反射探测或运行时 API 调用。
5. **兼容别人，但不依赖别人**：任何 Mod 只要最终把对象和编辑行为暴露为 Rain World 公共的 `PlacedObject.Type`、`PlacedObject.Data`、`PlacedObjectRepresentation`、`DevInterface` 控件或相关 Hook，新 DevTool 就尽量从这些公共行为层兼容。不会读取第三方私有注册表，也不会因为某个第三方框架损坏而让 DevTool 自身失效。
6. **第三方可以主动获得原生体验**：其他 Mod 可以选择引用 DryCycle 的公开 DevTool API，注册对象元数据和 Inspector。依赖方向只能是“外部 Mod → DevTool API”，不能反过来。
7. **输入必须集中仲裁**：文本输入、ImGui 控件、Gizmo、编辑器快捷键和玩家输入只允许经过统一 Input Router 决定所有权。
8. **Undo/Redo 是基础设施**：新原生编辑行为优先使用命令历史；无法理解的原版/外部 DevInterface 行为使用通用状态快照兜底。
9. **不依赖 UI 控件进行保存**：`Ctrl+S`、菜单、工具栏和命令面板必须调用同一个保存入口，不再模拟点击 `Save_Settings`。

## 目录规划

```text
DevTool/
├── Core/              编辑器生命周期、上下文、Document、Selection
├── Commands/          保存、对象编辑和统一命令入口
├── History/           统一历史与通用 DevInterface 快照层
├── Input/             键鼠所有权、世界 Handle 与游戏输入隔离
├── Objects/           Object Catalog、Inspector、属性描述、搜索
├── Compatibility/     只基于 Rain World 公共 DevInterface 的兼容桥
├── RWImGui/           RWImGUI 覆盖式前端
└── README.md
```

## 当前已经完成

- 已建立覆盖式 Editor Shell，Rain World 房间 Camera 不因为左右面板缩小。
- 已建立 Room / Objects / Sound / Triggers / Map 等统一工具模式外壳；Objects 是当前第一个完整迁移目标。
- 已把原有 `Ctrl+S / Ctrl+Z / Ctrl+Y / Ctrl+Shift+Z` 能力迁入新版 Command / History / Input 基础设施，旧 `VanillaDevUIShortcutRuntime` 已删除。
- 已把历史从“跟 Page 走”改成“跟 Document 走”，同一房间里切 Objects/Room/Sound 不会清空 Undo。
- 已迁移保持 `PlacedObject` / `PlacedObject.Data` 对象身份的快照恢复逻辑。
- 已建立 Object Library、模糊搜索、来源/分类/标签元数据和 Scene 对象列表。
- 已建立自有 Inspector 属性描述体系，并提供外部 Mod 可选的公开注册入口。
- 已建立原版 `DevInterface.Button / Slider / PlacedObjectRepresentation / Handle` 的通用兼容桥，不识别任何第三方 Mod 名字。
- 已实现 Legacy DevUI 兜底：无法翻译的自定义控件可以临时恢复原始界面。
- 已让 Objects 工具模式在后台继续使用原版 `ObjectsPage` 生命周期，因此其他 Mod 对标准 DevInterface 创建/Representation 的 Hook 仍会执行。
- 已实现放置模式：Library 选择对象后在房间中点击放置，Shift 连续放置，Esc/右键取消。
- 已实现 Scene 列表 Ctrl/Shift 多选、批量删除、`Ctrl+D` 批量复制，并统一进入 Undo/Redo。
- 已统一输入仲裁：ImGui 文本、面板点击、世界 Handle、编辑器快捷键与玩家输入不再各自单独 Hook 一套逻辑。
- 已阻止“点击 Inspector/Browser 时背后的世界 Handle 同时开始拖动”的穿透问题。

## 第一阶段剩余重点

- 多选 Inspector：共同值、Mixed 状态、批量属性修改。
- 多对象场景移动事务与框选。
- Objects 常用本体对象的原生语义化 Inspector / Gizmo。
- Overlay 自动避让、Focus 模式细化和更稳定的小屏幕布局。
- 真正的 DevTool 编译/运行测试与错误清理。

Map、Sound、Relationships 后续复用同一套 Document / Command / History / Input 基础设施扩展。
