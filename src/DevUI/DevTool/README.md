# DevTool 重构

本目录承载 Rain World DevTools 的新一代编辑器实现。目标不是给原版 `DevInterface` 换皮，而是建立一套以场景为中心、可扩展、可撤销、可搜索、可渐进迁移的编辑器框架。

## 硬性设计原则

1. **完整房间画面优先**：Rain World 继续按完整屏幕渲染。ImGui 默认采用覆盖式工具面板，不通过左右 Dock 永久压缩游戏 Camera。
2. **场景优先而不是页面优先**：Objects、Sound、Triggers、Room Settings 是同一个 Scene Editor 的工具模式，不因为切换工具就清空历史。
3. **UI 与业务解耦**：ImGui 只读取 EditorState、发出 Command；保存、Undo/Redo、Selection、对象创建和数据修改不写进 Draw 方法。
4. **核心完全自有**：ObjectDescriptor、PropertyDescriptor、Inspector、Gizmo、History、Input、Save、Selection 全部由 DryCycle 自己实现。POM、Fisobs、M4r 等第三方 Mod 只能作为设计经验参考，不建立编译依赖、软依赖、反射探测或运行时 API 调用。
5. **第三方兼容只依赖 Rain World 公共行为**：如果其他 Mod 已经把对象注册进 `PlacedObject.Type` 或通过原版 `DevInterface` Hook 扩展行为，新 DevTool 只观察和操作 Rain World 最终暴露出来的对象/页面状态，不读取第三方私有注册表，也不要求第三方存在。
6. **输入必须集中仲裁**：文本输入、ImGui 控件、Gizmo、编辑器快捷键和玩家输入只允许经过统一 Input Router 决定所有权。
7. **Undo/Redo 是基础设施**：新原生编辑行为优先使用命令历史；无法理解的原版/外部 DevInterface 行为使用通用状态快照兜底。
8. **不依赖 UI 控件进行保存**：`Ctrl+S`、菜单、工具栏和命令面板必须调用同一个 SaveService，不再模拟点击 `Save_Settings`。

## 目录规划

```text
DevTool/
├── Core/              编辑器生命周期、上下文、Document、Selection
├── Commands/          命令注册、Save/Undo/Redo
├── History/           统一历史与通用 DevInterface 快照层
├── Input/             键鼠所有权与游戏输入隔离
├── Objects/           Object Catalog、Inspector、属性描述、搜索
├── RWImGui/           RWImGUI 前端
└── README.md
```

## 第一阶段范围

- Overlay Editor Shell；
- Object Library / Scene Outliner；
- 自有 Inspector / PropertyDescriptor 基础管线；
- Selection；
- `Ctrl+S / Ctrl+Z / Ctrl+Y / Ctrl+Shift+Z`；
- Document 级历史；
- 原版 DevInterface 通用快照兼容；
- RWImGUI 输入捕获；
- 一键隐藏 UI、保留完整房间观察面积。

Map、Sound、Relationships 后续复用同一套 Document / Command / History / Input 基础设施扩展。
