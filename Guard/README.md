# Guard

Guard 默认不新增，只保护能够长期、客观、低误报地静态验证的硬不变量。普通功能、阶段任务、私有实现和运行行为不在这里固化；完整工程原则见仓库根目录 `AGENTS.md`。

## 当前内容

- `build-safety.sh`：CI Build Safety Guard。执行 C# 语法解析、World Map Persistent Cache V2/V3 生产代码回归测试、直接部署写入门控检查和核心项目的可选 UI 依赖方向检查。语法解析不等同于完整 Rain World 编译。
- `tools/local-build.ps1`：本地高保真构建工具，不是 Guard 类别。使用真实 Rain World 引用隔离编译，检查最终 `DryCycle.dll` 的 NAudio 托管合并与可选 UI 依赖隔离，并编译两个 RWImGui 前端；不验证游戏运行时行为。
- `.github/workflows/guard.yml`：CI 入口，执行 `build-safety.sh` 并检查本地构建脚本的 PowerShell 语法。

真实编译、启动、功能行为和性能问题，应由对应的构建、测试、游戏运行或性能分析验证，不用字符串 Guard 代替。修改 `src/DevUI/DevTool/RWImGui` 或 `src/DryCycle.AIObservatory.RWImGui` 后，收尾时应运行 `Guard/tools/local-build.ps1`，因为只有它会拿真实 Rain World / RWImGUI 引用进行两个可选前端的语义编译。
