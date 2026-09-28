<!-- codemap:v1 -->

# DevTool Code Map

仅用于在 DevTool 内定位职责；目标目录已明确时不要读取其他模块。

- `Commands/` — 编辑命令与统一保存入口。
- `Compatibility/` — Rain World 公共 DevInterface 兼容边界。
- `Core/` — 编辑器生命周期、Document、Session、Selection 与核心状态。
- `Debug/` — 开发诊断与调试表现。
- `Dialog/` — 会话资源浏览与结构化预览。
- `Extensions/` — 对外 DevTool API 与扩展注册。
- `Factories/` — 原生对象、Effect、Sound、Trigger 等作者对象创建入口。
- `Gizmos/` — 场景 Gizmo 编辑命令。
- `History/` — Undo/Redo、快照与编辑事务。
- `Input/` — 输入所有权、快捷键与场景交互仲裁。
- `Map/` — World Map、Player Map 与 Cartography。
- `Objects/` — Object Catalog、Inspector、多选与运行时协调。
- `Preview/` — 临时运行时预览、所有权与安全回滚。
- `Relationships/` — 生物关系编辑。
- `Room/` — RoomSettings 与 RoomEffect 编辑。
- `RWImGui/` — ImGui 前端、页面、Shell 与 Widgets；纯界面布局优先从这里定位。
- `Sound/` — 环境音浏览、创建与参数编辑。
- `Triggers/` — Trigger 与 TriggeredEvent 编辑。
- `World/` — world.txt 作者数据、世界拓扑及相关运行时协调。

UI 绘制问题优先 `RWImGui/`；实际数据、状态或行为问题优先对应后端领域，不要因为界面入口位于 ImGui 就默认在那里实现业务逻辑。
