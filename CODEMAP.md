<!-- codemap:v1 -->

# DryCycle Code Map

仅在任务位置不明确时使用本文件；路径明显时直接进入目标目录。

## 根目录

- `src/` — C# 主源码；按领域组织。
- `tests/` — 自动化测试；按目标领域或功能名定位。
- `mod/` — Rain World Mod 发布资源、配置、atlas 与 world 数据。
- `shader-src/` — Shader / Compute Shader 源工程。
- `scripts/` — 构建、验证与仓库维护脚本。
- `tools/` — 开发辅助工具。
- `Guard/` — 少量长期硬边界检查。
- `docs/` — 深度架构、设计与说明文档；普通局部任务不要默认读取。
- `lib/` — 编译依赖说明。
- `.github/` — CI / workflow。
- `.githooks/` — 本地 Git 提交与推送前的机械校验 Hook。

## 高价值消歧

- DevTool、ImGui、World Map、Object、Sound、Trigger 编辑器 → `src/DevUI/DevTool/`
- 游戏世界、区域、房间连接机制 → `src/WorldLink/`
- C# 渲染接入与共享渲染运行时 → `src/Rendering/`
- Shader / Compute Shader 源码 → `shader-src/`

名称已经直接对应领域的功能，优先进入对应 `src/<领域>/`，不要先扫描整个 `src/`。
