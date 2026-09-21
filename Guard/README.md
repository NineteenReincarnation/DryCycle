# Guard

DryCycle 的 Guard 统一放在这里。Guard 只保护已经确认的重要不变量，不再按开发阶段、任务编号或历史实现细节无限增殖。

## 分类

- `build-compile.sh`：构建契约、C# 语法、前后端程序集边界。
- `startup-safety.sh`：主插件与辅助插件启动安全、依赖/部署边界。
- `runtime-hook-safety.sh`：运行时 Hook 安全。禁止 DryCycle 源码直接安装 `MonoMod.RuntimeDetour.Hook`。
- `persistence-authoring-safety.sh`：保存、原子写入、回滚与 `mergedmods` 写保护。
- `devtool-architecture.sh`：DevTool 公共 API、页面/视图、Factory、Gizmo、Sound/Trigger、Objects 等架构边界。
- `feature-specific.sh`：确实需要独立保留的功能级回归约束，目前主要是 DesertBatfly。
- `tools/devtool-local.ps1`：本地 DevTool 构建验证工具，不作为一个独立 Guard 类别。
- `run-all.sh`：本地统一入口。

## CI

GitHub Actions 只能从仓库根目录的 `.github/workflows/` 自动发现工作流，因此仓库只保留一个很薄的 CI 入口：

`.github/workflows/guard.yml`

它不保存具体 Guard 规则，只负责调用本目录的分类 Guard。以后新增规则应优先加入现有分类；只有出现新的长期不变量类别时才新增分类。

## 原则

1. 编译和真实运行验证的优先级高于静态字符串检查。
2. Guard 保护行为/架构不变量，不保护某次重构时的私有方法名、调用顺序或文件布局。
3. 同一不变量只能有一个权威 Guard，避免重复检查。
4. 功能被重构后，先判断不变量是否仍成立，再修改 Guard；不要为了让旧 Guard 通过而恢复旧实现。
5. 失败信息必须指出违反了什么不变量，尽量给出具体文件或路径。
