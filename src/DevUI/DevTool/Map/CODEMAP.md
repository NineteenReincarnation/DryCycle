<!-- codemap:v1 -->

# Map Code Map

仅用于 Map 内部职责消歧。

- `./` — World Map 核心：作者状态、编辑运行时、几何、持久缓存、地形来源与恢复。
- `Cartography/` — 独立制图工作区、作者文档与导出。
- `PlayerMap/` — Player Map 构建、缓存与运行时集成。

World Map 编辑器问题默认先看本目录根部；不要因为名称包含 world 就先进入 `src/WorldLink/`。纯 ImGui 布局与绘制仍属于 `../RWImGui/`。
