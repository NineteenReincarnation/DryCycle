# DevTool 重构

本目录承载 Rain World DevTools 的新一代编辑器实现。目标不是给原版 `DevInterface` 换皮，而是在保持原版、RegionKit/POM 与旧 Mod 可用的前提下，建立一套以场景为中心、可扩展、可撤销、可搜索、可渐进迁移的编辑器框架。

## 硬性设计原则

1. **完整房间画面优先**：Rain World 继续按完整屏幕渲染。ImGui 默认采用覆盖式工具面板，不通过左右 Dock 永久压缩游戏 Camera。
2. **场景优先而不是页面优先**：Objects、Sound、Triggers、Room Settings 是同一个 Scene Editor 的工具模式，不因为切换工具就清空历史。
3. **UI 与业务解耦**：ImGui 只读取 EditorState、发出 Command；保存、Undo/Redo、Selection、对象创建、兼容逻辑不写进 Draw 方法。
4. **兼容优先**：新接口 > RegionKit/POM 适配 > Vanilla Representation 适配 > Legacy DevUI fallback。
5. **输入必须集中仲裁**：文本输入、ImGui 控件、Gizmo、编辑器快捷键和玩家输入只允许经过统一 Input Router 决定所有权。
6. **Undo/Redo 是基础设施**：新原生编辑行为优先使用命令历史；无法理解的旧 DevUI/RegionKit 行为使用快照历史兜底。
7. **不依赖 UI 控件进行保存**：`Ctrl+S`、菜单、工具栏和命令面板必须调用同一个 SaveService，不再模拟点击 `Save_Settings`。

## 目录规划

```text
DevTool/
├── Core/              编辑器生命周期、上下文、Document、Selection
├── Commands/          命令注册、Save/Undo/Redo
├── History/           统一历史与快照兼容层
├── Input/             键鼠所有权与游戏输入隔离
├── Objects/           Object Catalog、搜索、对象描述
├── Compatibility/     Vanilla / RegionKit / Legacy 适配
├── RWImGui/           RWImGUI 前端
└── README.md
```

## 第一阶段范围

- Overlay Editor Shell；
- Object Library / Scene Outliner；
- Inspector 基础管线；
- Selection；
- `Ctrl+S / Ctrl+Z / Ctrl+Y / Ctrl+Shift+Z`；
- Document 级历史；
- 旧 DevUI 快照兼容；
- RegionKit/POM 适配入口；
- RWImGUI 输入捕获；
- 一键隐藏 UI、保留完整房间观察面积。

Map、Sound、Relationships 后续复用同一套 Document / Command / History / Input 基础设施扩展。