# MantleCrab 外观 / 碰撞原型

依据 `MantleCrabTask.md` 和用户补充原稿实现。壳顶物品完全忽略。这里记录实现与证据，不改写任务书；AI、移动决策、生态、攻击、抓取、受伤/断腿、世界背景层、Arena/Safari 等仍为 TBD。

## 实现覆盖

| 范围 | 实现 |
| --- | --- |
| 独立生物 | `MantleCrabEnums` / `Definition` 接入现有 CreatureRegistry、DryCycleContent；关闭 AI / 自动迁移；不增添注册框架。世界数据类型名为 `MantleCrab`。 |
| 壳体碰撞 | 5 个渐小半径 BodyChunk、10 条相邻及跨节点连接；另有质量守恒的局部形状恢复力，防止扁平距离图在承重时中央下陷。允许整体倾斜；没有世界坐标高度锁。 |
| 四条步行腿 | 每条 4 个控制点 / 4 段，独立比例、位置、lastPos、速度、长度约束；脚接触时末节方向受地面法线限制。没有腿段 BodyChunk。 |
| 足端 / 支撑 | 三列窄探针查找平地、Floor、台阶、向上斜坡；锁定接地点并复核地形 / 可达性；弹簧、速度阻尼、最大力限制。所有腿使用同一帧速度快照，避免共享锚点导致顺序依赖。4/3/2 脚可支撑，1 脚容量不足，0 脚正常重力。 |
| 钳肢 | 2 条、各 4 段；末段由掌部和两条独立弯曲钳瓣组成。仅提供被动下垂姿态与末端位置 / 速度 / 朝向，没有碰撞、伤害或夹取逻辑。 |
| 程序轮廓 | 61 个 Futile 网格组件；壳体参数网格、独立节段与关节盖、独立厚块足部、双红眼、眼柄和 6 条轻物理垂丝。无人工绘制纹理。 |
| 局部层次 | 后腿 → 垂丝 → 壳 → 前腿 → 钳 → 眼；同一个 Midground 容器内排序。后腿冷色、低对比、弱高光，最终 alpha 恒为 1。 |
| 个体差异 | EntityID.RandomSeed + 命名稳定 Hash + 原版六项 Personality → Genome → Phenotype。不改 Unity Random 状态；宏观共享，局部 mutation 有限；性格分工不重复叠加体型/红色。 |
| 材质 | 896×256 个体 atlas，7 个部位。下半部 RGB 色纹 / A Height，上半部 AO / Roughness / Material / Emission。方向性主纹、板片接缝和微细纹理分别处理；微细结构不污染基础色。 |
| 烘焙与降级 | 有 Compute 时初始化 Dispatch 一次；失败 / 不支持时 CPU 一次性生成等价 atlas。缺失 Surface shader 时，使用不透明的程序网格与预采样材质顶点色降级。 |
| 光照 | 当前 `_PalTex`、原版 `_lightDirAndPixelSize`；每个组件有限位置采样 Darkness / LightSourceExposure / LightSourceColor，并插值环境色。高度梯度叠加壳、圆柱、块足、楔形钳和圆眼的不同 normal，利用网格 UV 导数转换方向。 |
| 润色 | 壳下 / 关节 AO、宽高光、粗糙中心、红紫翼纹、块足侧面、空间稳定量化抖动、清晰几何边缘；红眼 emission 不创建 LightSource。 |
| 生命周期 | 材质按 SpriteLeaser 引用计数，在原生 CleanSpritesAndRemove 后卸载 atlas、Release RenderTexture 并销毁生成纹理；多摄像机共享同一个个体材质。离屏不保留永久个体缓存。 |
| 资源管线 | 独立 `drycyclecreatures` 包，使用现有安全 LoadResources 时机、路径解析和 FShader 管线；现有 Unity 构建器新增仅构建生物包的入口。 |

单个 atlas 为 917,504 字节的 RGBA32 像素数据（约 0.875 MiB；不含驱动 / 网格开销）。无每帧 Compute、GrabPass、3D 模型、Standard/PBR 或独立 DrawProcedural 管线。

## 本机验证：2026-09-09

- C# Debug 构建成功，0 警告 / 0 错误。
- MantleCrab 托管测试：**10,476 项检查通过**。包括 200 个 ID 的重复生成、命名通道独立性、性格职责与物种边界、500 个可达 IK 姿态、平地 / 台阶 / 斜坡探针、失去地形 / 超距支撑拒绝、原生 BodyChunkConnection 受冲击保持壳宽、4/3/2 脚受力收敛、实际 Graphics 生成网格的有限值检查。
- 可达姿态的最大足端求解误差为 **0.3013 px**；4 脚简化受力循环后期高度振幅为 0（此为托管模型数据，不是游戏测试结果）。
- Unity **2020.3.45f1c1**，RTX 4060 Laptop GPU：Surface shader 编译和 Compute 执行通过；全部 atlas 字节对照 CPU 输出，最大误差 **1/255**，平均误差约 **0.055/255**。
- GPU 离屏渲染直接使用生产 `MantleCrabGraphics.PoseSprites` 导出的 61 个组件，验证两侧方向光、暗场局部照明以及 CPU atlas 路径；方向光切换确实改变输出。预览不是游戏截图，也不是手工绘制替代品。
- 恢复任务后，当前 main 的 `DB_EventHub` 存在 37 个 CS0122 访问错误。将外层 Hub 必须读取的事务成员、构造器及其状态类型改为 internal，消除整项目编译阻断，不改事件行为。既有 DesertBatfly 测试通过前段检查后，在 `Creature.Violence` 的 Unity 原生 ECall 处发生 SecurityException；**该套测试未完整通过**。

## 复现

在仓库根目录运行：

```powershell
./tests/MantleCrab/Validate.ps1 -BuildBundle
```

脚本先构建到本地测试输出并导出测试夹具，再依次运行 Unity GPU 验证和生物 AssetBundle 构建；任一步失败即中止。可通过参数指定 RainWorldDir / UnityEditor。

产物在 `artifacts/mantlecrab/`（忽略的验证输出）：`preview-left.png`、`preview-right.png`、`preview-dark-local.png`、`preview-cpu-bake.png`、`baked-atlas.png`、`unity-validation.txt`。运行常规 Release 构建可把 DLL 与 `mod/assets/drycycle/drycyclecreatures` 部署到已配置的游戏 mod。

## 尚需游戏内验收

源码、托管循环和 GPU 离屏渲染不能替代 Rain World 实机验收。尚未验证：墙角 / 狭窄空间受撞、与玩家及其他生物的碰撞、真实斜坡和台阶长时间站立、动态失足、进出房间 / 多摄像机缓存释放、实际房间 palette 和灯光组合、禁用 shader 后的原生 Basic 降级观感、实际帧耗与显存变化。

实机应优先在宽敞平地检查比例与接地，再测试斜坡、台阶、单侧失足及壳翼撞墙。玩家应能从腿间穿行，壳体覆盖主要可见宽度；后腿不能呈半透明。重复加载相同 ID 并切换明暗房间检查图案稳定和光照变化。所有尺寸、弹性和站立参数仍允许按这些实机证据调校。
