# Iterator Framework 开发进度

更新日期：2026-09-13。

范围依据：《Iterator Framework 迭代器轮子开发任务书》第 81、91 节。第一阶段完成后保留阶段检查点，后续阶段按各自设计、编译、运行测试和边界测试推进。

## 第一阶段已实现

- `IteratorID`：验证、Parse/TryParse、注册查询、Ordinal 值语义，创建 ID 无注册副作用。
- `IteratorDescriptor`：创建后不可变、输入集合防御性复制、默认名称、精确多房间绑定、只读字符串 Metadata。
- `IteratorBuilder` 与 `Iterator.Create`：Fluent API、独立 Build 快照、批量房间验证、外部扩展方法入口。
- `IteratorRegistry`：ID/房间/Oracle ID 查询，保留注册顺序，稳定只读快照，同一 Descriptor 重复注册幂等，冲突预检，按 Descriptor 身份安全注销。
- 游戏 ID 适配：使用 `Oracle.OracleID`，保护原版、未启用 MSC 和其他 Mod 已登记的 ID，注销后释放框架拥有的 ExtEnum。
- 基础 Validation：null、空白、非法 ID、缺失房间、重复房间、元数据和全局冲突检查。
- 基础 Logger：自动 ID/Module/Phase 上下文、BepInEx 适配、Trace 回退、日志接收器及重入保护。
- 公开 API 使用说明、源码 XML 注释、独立外部调用测试项目。

## 已运行的验证

主项目及独立测试项目 Release 编译：**0 个警告，0 个错误**。

测试：**13/13 组通过，1564 项断言，0 个失败**。

测试项目引用实际编译的 `DryCycle.dll`，运行时加载本机安装的 `Assembly-CSharp.dll`，没有源文件链接副本或 Oracle 模拟类型。

覆盖内容：

1. 任务书最小示例、名称默认值、ID/房间/Oracle ID 查询。
2. ID 格式、null、边界长度、大小写、值相等和哈希契约。
3. Descriptor 输入防御、只读集合，以及 Builder 后续修改不影响已注册定义。
4. 批量添加房间时的重复或非法输入不会部分写入。
5. 重复 ID、大小写房间冲突及失败后无残留、可再次注册。
6. 注册顺序、旧快照稳定性、注销再注册的顺序。
7. 原版/MSC/外部 Mod 的 ID 保护。
8. 重复注销、显式重载、旧 Descriptor 不能移除新注册。
9. 真实 ExtEnum Index 变化时映射保持正确。
10. 无效查询、参数异常与直接构造时的输入校验。
11. 日志作用域、接收器异常、递归日志、异常格式化失败、Trace 失败隔离。
12. 外部程序集直接构造 Descriptor 和使用 Builder 扩展方法。
13. 40 轮、每轮 4 个定义的注册/注销循环，检查所有 ID 与房间释放。

复现命令（仓库根目录）：

```powershell
dotnet build .\src\DryCycle.csproj -c Release -p:DeployToGame=false -v minimal
dotnet build .\tests\IteratorFramework.Tests\IteratorFramework.Tests.csproj -c Release -p:DeployToGame=false -v minimal
& .\tests\IteratorFramework.Tests\bin\Release\net48\IteratorFramework.Tests.exe "D:/Application/Steam/steamapps/common/Rain World"
```

其他机器可在构建时传入 `-p:RainWorldDir="游戏目录"`，运行测试时传入同一个目录。测试项目默认关闭游戏部署。

本轮 DLL 输出：`src/bin/Release/DryCycle.dll`。没有覆盖游戏目录的 DLL 或资源。

## 验证边界

- 没有运行 Unity 游戏房间测试；第一阶段没有实体生成、Runtime 或 Hook。
- 循环测试验证的是注册表和 ExtEnum 的释放，不是 Room 切换或真实 Session 重启。
- 基础日志隔离测试不代表后续图形、行为、对话模块已经具备异常隔离。
- 没有验证外部 Mod 管理器自动卸载：当前外部注册者按文档保留并注销自己的 Descriptor。
- 本阶段不加载图形或对话资源；资源缺失、生命周期重复初始化和存档迁移测试随相应实现加入。

## Public API 检查点

当前设计已经过代码审阅和独立调用编译验证：公共接口无需传递 Hook；Builder 不拥有注册状态；Descriptor 不暴露可变集合；Registry 不缓存游戏实体；ID 映射不依赖可变 Index。文档中的第一阶段调用示例可供使用者确认接口体验。

## 后续阶段

| 阶段 | 范围 | 状态 |
| --- | --- | --- |
| 1 | ID、Descriptor、Builder、Registry、Validation、Logger | 已完成本阶段实现和托管验收 |
| 2 | Runtime、Context、Lifecycle、Oracle 与 Room 绑定、创建销毁 | 未开始 |
| 3 | Body、Arm 抽象、基础移动、Pose | 未开始 |
| 4 | Graphics、SpriteRegistry、Profile、Halo、Face、Gown、Cable | 未开始 |
| 5 | Brain、Action、StateMachine、Sensors、玩家观察 | 未开始 |
| 6 | Conversation、Sequence、Conditions、Commands、Branch、Interrupt | 未开始 |
| 7 | Player/Item/Creature/Pearl/Weapon Interaction | 未开始 |
| 8 | Environment、Gravity、Neuron、Music、Projection、Room Effects | 未开始 |
| 9 | Module、Dependency、Save、Persistent State、Migration | 未开始 |
| 10 | DevConsole、Debug、Diagnostics、Compatibility、完整文档与示例 | 未开始 |

第二阶段首先设计集中 Hook、原版路径隔离、Runtime 状态转换、注销与实例销毁的协调，随后接入房间并做游戏内验证。完整框架尚未达到任务书第 100 节最终验收标准。
