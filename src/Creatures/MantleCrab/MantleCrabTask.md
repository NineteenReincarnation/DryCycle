# MantleCrab — Codex 任务规格

> 目标：为 DryCycle 设计/实现全新大型生物 **Mantle Crab**。本文件只记录已确认设计与实现约束；未确认内容禁止 Codex 自行补全。
> 路径：`src/Creatures/MantleCrab/`
> 原则：优先复用 Rain World 原生生命/图形范式，但允许使用 DryCycle 已有 Shader/Compute/AssetBundle 管线提升表现。

## 0. 当前阶段与禁止越界

当前只优先解决：

1. 生物可见外形/程序化材质；
2. 壳体物理碰撞；
3. 四条步行腿的分节、接地与支撑骨架；
4. 两条钳肢的视觉/结构骨架。

**暂不实现/不得擅自设计：** AI、生态关系、攻击、钳取/夹取/伤害、腿能否受伤/折断、世界前后景切换、顶部原版物品、特殊交互、食物链、Arena/Safari 等。上述均为 TBD。

## 1. 项目接入基线

- 这是**独立新生物**，不是某个原版生物换皮。
- 未来注册走 DryCycle 自有 `CreatureDefinition -> CreatureRegistry -> DryCycleContent`，不要引入第三方内容注册框架。
- 新生物实现路线参考 `src/Creatures/MossySpider/`，而不是 `DesertBatfly` 的 Fly 变种路线。
- 需要参考原版/Watcher 源码时，重点看：
  - `DrillCrab` / `DrillCrabGraphics`：分节长腿、IK/Segment、足端接地、动态 Mesh；
  - `Deer` / `DeerGraphics`：超长支撑腿、视觉深度渐变；
  - `MirosBird` / `Vulture`：多个 BodyChunk 组成稳定非圆形主体；
  - `MoonCloak`：带纹理的可变形 Grid `TriangleMesh`；
  - Watcher 生物自定义 shader：动态 mesh + vertex data + shader 的组合。

## 2. 已确认外形

### 2.1 总体轮廓

- 上方是横向很宽、纵向较薄的暗色“披幕/伞盖”式蟹壳主体；中央厚、两翼向外延展并收尖。
- 主体下方悬挂四条极长步行腿，使整只生物呈“高、轻、悬、诡异”的轮廓。
- 正面另有两条细长钳肢；钳肢不是步行腿。
- 两个醒目的红色部分是**眼睛**。
- 壳下有少量下垂丝状/流苏结构；它们主要属于视觉细节。
- 参考图壳顶的原版物品目前**完全忽略**，不要实现、不要烘进壳体外观。

### 2.2 四条步行腿

- 固定为 **4 条**。
- 每条腿固定为 **4 个视觉/运动段**；各腿段长度、比例、弯折与轮廓允许不同，但必须保持同一物种语言。
- 腿非常细长，整体为蓝灰/深蓝紫甲壳色。
- 最底一段/足端必须明显**块状且比上方节肢粗**，承担强烈的视觉承重感。
- 四腿存在明显的内部前后深度关系：后层腿更冷、更灰、更低对比；前层腿更深、更清晰。不要用高透明度制造“幽灵腿”。

### 2.3 两条钳肢

- 固定为 **2 条**，位于前方。
- 每条钳肢同样按 **4 段结构**设计，其中末端蟹钳计入四段体系。
- 钳肢色调明显区别于普通腿：暗红、酒红、紫红、近黑红。
- 当前只建立分节位置、朝向和绘制骨架；**不决定**钳、抓、刺、搬运、攻击等互动。

### 2.4 眼睛

- 固定两个红色眼睛，位于主体正面偏下，是核心视觉锚点。
- 眼睛允许轻微惯性/悬垂感，但不要大幅摆动。
- 颜色始终保持红色系；个体差异只允许小范围改变尺寸、纵横比、红色色温和亮核比例。
- 可有小范围 emission/高光以保证暗处可读，但 **Emission != LightSource**；是否真正照亮环境为 TBD。

## 3. 物理/碰撞骨架（当前预期）

### 3.1 壳体

- 不要用 1 个巨大 BodyChunk 代表宽壳。
- 第一版目标：约 **5 个 BodyChunk** 横向组成扁平主体（中央最大，越向外半径越小），通过多条 `BodyChunkConnection` 保持为“略有弹性但基本刚性”的宽壳框架。
- 壳可以整体倾斜、轻微晃动/变形，但禁止像毛毛虫一样折叠。
- 视觉壳缘可略超出最外侧碰撞，但不能出现明显大面积穿模。

### 3.2 步行腿

- **绝对不要**把每个腿关节做成 BodyChunk；4×4 段会导致过多实体碰撞、卡墙/卡杆/互相挤压。
- 每条腿使用独立 Segment/控制点系统，思路接近 Watcher `DrillCrab.Leg`：位置、上一帧位置、速度、段长约束、IK/Verlet 混合。
- 形式：`ShellAnchor -> P0 -> P1 -> P2 -> P3(Foot)`，对应四段。
- 该生物是硬节肢，不应表现成软绳；优先 **IK + 轻物理惯性/阻尼**，保证关节清晰。
- 真正可靠的地形接触重点放在足端；腿身不要默认成为四堵实体墙。
- 足端可使用小型 terrain probe（单点或少量采样点）处理平地、坡面、砖块边缘与台阶。
- 腿提供真实支撑力给壳体：基于目标高度/伸展误差的弹簧 + 阻尼，不要单纯把 gravity 调小。
- 支撑应允许 4/3/2 脚稳定、1 脚明显失稳、0 脚正常下落；具体阈值后调。
- 玩家原则上应能从腿间穿行；壳体才是主要硬碰撞主体。

### 3.3 钳肢

- 使用与腿类似的分节控制点骨架，但当前不承担主体支撑和玩家硬碰撞。
- 为未来互动预留端点位置/速度/方向，不提前决定 `Appendage`、抓取或伤害。

## 4. 玩家最终看到的外观：核心渲染方案

### 4.1 关键约束

**用户无法进行人工绘制。** 因此：

- 不依赖手工绘制的 `Crab_Color.png / Normal.png / Surface.png`；
- 不要求人工 atlas 美术；
- 主体、花纹、材质、Normal、AO、粗糙度、眼睛等均应可由程序生成；
- 允许存在纯程序生成的运行时 RenderTexture/Atlas，但不是人工资源。

目标不是简单 `FSprite.color` 或纯顶点渐变，而是：

> **参数化几何/SDF + 稳定 VisualGenome + 程序材质生成 + 动态 UV Mesh + Rain World 风格实时 2.5D Shader**。

### 4.2 几何呈现

**壳体：**
- 使用可轻微变形的 Grid `TriangleMesh`/参数网格；参考 `MoonCloak` 的 textured grid mesh 思路。
- 壳体 alpha/轮廓通过参数化曲线/SDF 生成：宽扁主体、中央厚、两翼尖、边缘少量不规则。
- 网格跟随壳体 BodyChunk 框架产生整体位移、旋转和轻度弯曲；不要做布料式大变形。

**腿：**
- 每段单独动态 mesh，沿关节点拉伸/旋转；视觉上保留真实四节结构。
- 关节处有单独程序几何/JointCap，遮接缝并形成节肢膨大。
- 足端使用独立参数化 polygon mesh，轮廓厚、块状，可随地面法线轻微调整朝向。

**钳肢：**
- 与腿同类的分节 mesh，但使用独立色彩/材质参数；末端钳形使用参数化/SDF/polygon 生成。

**壳缘/流苏：**
- 固定细碎毛边可由 SDF/边缘噪声直接形成 silhouette；
- 少量真正会摆动的长丝使用轻量 2~5 点 mesh/rope 视觉模拟；无硬碰撞。

### 4.3 程序材质：两阶段

#### A. 个体首次创建/Graphics 初始化：一次性“长皮肤”

`EntityID + Personality -> MantleCrabVisualGenome -> GPU/CPU Material Baker -> 个体材质缓存`

首选使用 Compute Shader 一次性生成小型个体材质 atlas / RenderTexture；若硬件不支持 Compute，必须保留 CPU/fragment fallback 或简化程序材质路径。

建议生成：

- `PatternAtlas`: RGB = 基础/花纹颜色，A = Height；
- `SurfaceAtlas`: R = AO，G = Roughness/Gloss control，B = Material mask，A = Emission。

**不要每帧重新执行大量 octave noise。** 复杂 Simplex/Worley/Domain Warp 应尽量一次烘焙；每帧 shader 只做环境光/材质响应。

#### B. 每帧实时着色

`Generated Material + current mesh pose + RW palette/light/darkness/local light/depth -> MantleCrabSurface.shader`

必须接入 Rain World 环境而不是固定“左上光”：

- 当前 palette / `_PalTex`；
- `room.lightAngle` / Rain World 现有方向光信息；
- `room.Darkness(pos)`；
- `room.LightSourceExposure(pos)`；
- 可用时采样局部 `LightSourceColor(pos)`；
- 每条腿/壳体可在若干控制点采样局部环境，vertex data 负责传递局部亮度/深度，fragment shader 负责像素级材质响应。

禁止：
- Unity Standard/PBR 直接套用；
- GrabPass；
- 每个生物每帧 Compute；
- 依赖真实 3D 光源/Z-buffer；
- 把预设阴影烘死在一张图片中。

视觉目标为 **Rain World 风格的 Stylized 2.5D Lighting**，不是写实 Unity 资产。

## 5. 程序化材质规则

### 5.1 多尺度细节

必须分层，禁止“一张 Perlin noise 覆盖全身”。

- **Macro (大尺度)**：壳中央->翼端的色域、主红紫条纹、大块暗区；
- **Meso (中尺度)**：甲壳板片、次级色带、轻微斑驳；
- **Micro (微尺度)**：细脊、颗粒、孔洞、粗糙度、高光破碎；Micro 主要进入 Height/Normal/Roughness，不要把颜色做脏。

建议工具：Simplex/gradient noise、Worley/cellular、ridged noise、domain warping；噪声必须受解剖坐标约束，不可裸噪声阈值化。

### 5.2 解剖坐标/生长方向

- 壳：纹样从中央向左右翼端扩展/弯曲；
- 腿：纹理从根部沿节肢向足端发展；
- 钳：从根部/关节沿向钳尖发展。

主壳参考图必须保持：深紫黑中央 + 两翼向外展开的红紫方向性纹样。

### 5.3 程序 Height -> Normal

- Normal 不依赖人工 Normal Map。
- 用程序 Height Field（壳曲率 + ridge + plate + pores + joint groove）求局部梯度生成 pseudo-normal。
- 腿应有“假圆柱/节肢横截面”宏观 normal，再叠 ridge/plate/micro normal；腿旋转/网格变形时 normal 必须随当前 UV/TBN/屏幕切线正确变换。
- 壳体使用扁平伞状 pseudo-normal，使房间光方向变化时高光方向同步变化。

### 5.4 材质差异

至少区分：

- 壳中央：暗、粗糙、吸光、低 specular；
- 壳两翼：略硬质/薄甲壳感，中低 specular，红紫纹明显；
- 步行腿：硬质节肢甲壳，中等宽高光、关节强 AO；
- 足端：更厚重、更深紫、AO 更强；
- 钳肢：暖暗红/紫红、可更锐利；
- 眼睛：高饱和红 + 小范围 gloss/emission，暗房仍可读。

高光使用 stylized wide highlight，避免现实 PBR 的尖锐白色镜面点。

### 5.5 Rain World 化

- 最终色必须受 palette、darkness、fog/depth 影响；不同房间/昼夜不能像贴着同一张截图。
- 内部前后腿深度用 **depth tint / fog-color shift / contrast/specular attenuation** 表达，不默认使用 alpha 透明。
- 可加入很轻的空间稳定 blue-noise dither / quantization，避免过度现代化的 Photoshop 平滑渐变；不能逐帧抖动造成闪烁。

## 6. VisualGenome：ID + 原版 Personality

### 6.1 只用原版性格

不要新增性格系统。使用原版 `AbstractCreature.Personality` 六项：

- `energy`
- `bravery`
- `sympathy`
- `dominance`
- `nervous`
- `aggression`

总体规则：

> **Species Rules 保证物种识别；EntityID 决定主要个体随机；Personality 只作为偏置。**

建议视觉差异权重约 `ID 70% / Personality 30%`，不是硬编码比例，核心是 Personality 不能覆盖物种固定特征。

### 6.2 物种固定视觉（不可随机掉）

- 宽扁暗色壳；
- 4 条四节长腿；
- 2 条四节暗红钳肢；
- 2 个红眼；
- 蓝灰/深蓝紫步行腿；
- 深紫黑主体；
- 两翼红紫方向性纹样；
- 粗大块状足端；
- 后腿->前腿的深度渐变。

### 6.3 EntityID 主要控制

可稳定决定：基础色相细偏移、条纹 seed/count/width/phase/curvature/warp、斑驳位置、甲壳细胞纹、micro ridge、腿色细偏移、关节/足端细节、钳色差、眼睛小范围差异、左右微变异。

**不要用顺序 `Random.value` 作为长期视觉参数源。** 使用命名稳定 Hash：

`Hash(ID.RandomSeed, VisualChannel)`

如 `ShellHue / StripePhase / LeftRearLegMicro / EyeAspect`。以后新增 channel 不得改变旧个体已有外观。

### 6.4 左右对称规则

使用：`SharedGenome + LeftMutation + RightMutation`。

- Macro 约高度对称；
- Meso 有有限差异；
- Micro 可明显不同；
- 禁止左右两边像两个完全不同物种。

### 6.5 Personality -> 视觉职责

避免多个相关性格重复控制同一参数。

- `dominance`：大尺度/厚重感；主纹更宽、足端/关节视觉体积略强；不直接决定颜色。
- `aggression`：纹样尖锐度、红紫暖色强调、钳肢攻击性色彩/边缘；不直接决定体型。
- `bravery`：主纹展示强度、完整度、对比度和延伸范围；不等于 aggression。
- `nervous`：domain warp、局部断裂、细碎副纹、微不对称；不大幅改变身体形状。
- `energy`：Meso/Micro 细节频率、二级纹/细脊密度；不简单等于“更亮”。
- `sympathy`：纹样曲线/过渡平滑度、综合色彩协调、边缘圆润倾向；不是“可爱程度”。

不要把 `aggression/dominance/bravery` 都叠加成“更红更大更亮”，原版 Personality 本身存在相关性。

## 7. 预期代码/资源拆分（实现时可微调）

```text
src/Creatures/MantleCrab/
├─ MantleCrabTask.md
├─ MantleCrabEnums.cs
├─ MantleCrabDefinition.cs
├─ MantleCrab.cs
├─ MantleCrabGraphics.cs
└─ Rendering/
   ├─ MantleCrabVisualGenome.cs
   ├─ MantleCrabVisualPhenotype.cs
   ├─ MantleCrabMeshBuilder.cs
   ├─ MantleCrabMaterialCache.cs
   └─ MantleCrabRenderingMath.cs
```

Shader 资产建议独立于 weather：

```text
shader-src/Assets/DryCycle/Creatures/MantleCrab/
├─ MantleCrabMaterialBake.compute
├─ MantleCrabSurface.shader
└─ MantleCrabEye.shader   # 仅在确有必要时拆分
```

可使用独立 creature AssetBundle；不要把生物 shader 逻辑硬塞进 weather 命名/模块。复用 DryCycle 现有 `RainWorld.LoadResources` 后加载 AssetBundle/FShader 的安全管线。

## 8. 推荐开发顺序

1. **Body Prototype**：5-chunk 左右的宽壳，验证比例、倾斜、墙角碰撞；
2. **Standing Prototype**：4×4 段腿 + 足端 probe，只要求能稳定站在平地/坡/台阶；
3. **Procedural Silhouette**：壳、腿、关节、足端、双钳、双眼全部无人工贴图可见；
4. **VisualGenome**：ID 稳定个体差异 + Personality 偏置；
5. **Material Baker**：程序 Color/Height/AO/Roughness/Emission；
6. **Runtime Shader**：RW palette + room light + darkness + local light + visual depth；
7. **Polish**：壳缘、流苏、blue-noise/dither、性能/cache/fallback；
8. **之后再讨论移动/AI/钳互动等机制**，不要提前实现。

## 9. 第一阶段验收标准

完成“外观/碰撞原型”时至少满足：

- 静止时即能一眼认出参考图轮廓：宽幕壳 + 四长腿 + 双钳 + 双红眼；
- 4 条腿均可明确读出四节，足端明显粗大块状；
- 壳体硬碰撞与视觉宽度基本一致，不能只有中央一个圆；
- 腿不会因使用大量 BodyChunk 导致卡墙/卡杆/互撞；
- 站在平地、斜坡、台阶上不会持续高频抖动；
- 玩家可从腿间穿行，壳体为主要阻挡；
- **零人工绘制生物纹理依赖**；
- 同一 `EntityID` 每次加载视觉必须稳定；不同 ID 应明显但不过度不同；
- Personality 只产生统计倾向，不能一眼形成“某性格固定皮肤”；
- 后腿低对比冷色、前腿更实，且不是简单 alpha 透明；
- 换 palette、明暗房间或附近局部光源后，壳/腿光影与环境一致；
- 程序花纹必须沿解剖方向生长，不能表现为裸 Perlin/Worley 噪声；
- 眼睛暗处可读但默认不照亮环境；
- 不实现任何本任务明确列为 TBD 的机制。

## 10. Codex 行为约束

- 开始写代码前先检查当前 `main` 的 `MossySpider`、Registration 和 shader asset pipeline，复用现有约定，不复制旧代码形成第二套注册/资源系统。
- 每完成一个阶段先保证可编译/可测试，再进入下一阶段；不要一次性把 AI、寻路、战斗、生态全部写入。
- 所有“未确认玩法”必须留为清晰扩展点，不自行猜设定。
- 优先可读、模块化、稳定 seed、可调参数；避免把视觉公式硬塞进一个超大 `DrawSprites()`。
