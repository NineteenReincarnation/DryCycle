# MantleCrab — Codex 任务规格

> 目标：为 DryCycle 设计/实现全新大型生物 **Mantle Crab**。
> 路径：`src/Creatures/MantleCrab/`
> 文档原则：**设计信息完整性第一，可执行性第二，token 消耗第三。** 只能压缩重复解释、口语和无效铺垫；不得为了省 token 删除已确认的设计点、工程约束、反例、阶段边界或 TBD。未确认内容禁止 Codex 自行补全。
> 技术原则：优先复用 Rain World 原生生命/图形范式；允许使用 DryCycle 已有 Shader/Compute/AssetBundle 管线提高表现，但不能为了“新技术”破坏 Futile/Rain World 的渲染与生命周期集成。

## 0. 当前阶段 / 禁止越界

当前优先解决：

1. 玩家最终看到的外形、程序化材质和光影；
2. 壳体物理碰撞；
3. 四条步行腿的分节、接地、支撑骨架；
4. 两条钳肢的视觉/结构骨架。

**暂不实现、不得擅自设计：** AI、生态关系、攻击、钳取/夹取/伤害、腿能否受伤/折断、世界前后景切换或背景层移动、顶部原版物品、特殊交互、食物链、Arena/Safari 等。以上均为 TBD。

注意：**生物内部的局部绘制前后层次已经确定，可实现；“进入世界背景层/前景层”仍是 TBD。两者不可混淆。**

## 1. 项目接入基线

- MantleCrab 是**独立新生物**，不是原版生物换皮。
- 注册走 DryCycle 自有 `CreatureDefinition -> CreatureRegistry -> DryCycleContent`，不要引入第三方注册框架。
- 实现路线优先参考 `src/Creatures/MossySpider/` 的独立 Creature 架构，不走 `DesertBatfly` 的 Fly 变种路线。
- 编码前检查当前 `main`，不要复制已有系统形成第二套注册/Shader/资源框架。
- 原版/Watcher 重点参考：
  - `DrillCrab` / `DrillCrabGraphics`：分节长腿、Segment/IK、足端地形接触、动态 Mesh；
  - `Deer` / `DeerGraphics`：超长支撑腿、视觉深度与渐变；
  - `MirosBird` / `Vulture`：多个 BodyChunk 组成稳定的非圆形主体；
  - `MoonCloak`：带 UV 的可变形 Grid `TriangleMesh`；
  - Watcher 自定义 shader 生物：动态 mesh + vertex data + shader。

## 2. 已确认外形

### 2.1 总体轮廓

- 上方主体为横向很宽、纵向较薄的暗色**披幕/伞盖式蟹壳**；中央视觉上更厚，两翼向外展开、收尖。
- 主体由四条极长步行腿架高，整体轮廓必须体现：**高、轻、悬、诡异，但足端有明显承重感。**
- 正面另有两条细长钳肢；钳肢不是步行腿。
- 两个红色垂下结构是**眼睛**，是核心“脸部”视觉锚点。
- 壳下有少量丝状/流苏状垂挂结构。
- 参考图壳顶的原版物品目前**完全忽略**：不要实现、不要烘进壳体轮廓、不要为其预设玩法。

### 2.2 四条步行腿

- 固定 **4 条**。
- 每条腿固定 **4 个视觉/运动段**。
- 各腿、各段的长度、比例、弯折与轮廓允许不同，但必须保持同一物种语言；禁止四腿简单复制粘贴，也禁止随机得像不同物种。
- 腿极细长，整体色域为蓝灰 / 深蓝 / 蓝紫甲壳色。
- **最底段/足端必须块状、明显粗于上方节肢**，是重要识别特征和承重视觉来源。
- 四腿有明确内部视觉深度：后层腿更冷、更灰、低对比、弱高光；前层腿更实、更深、更清晰。
- 深度差异不得主要依靠高透明 alpha；不能画成“幽灵腿”。

### 2.3 两条钳肢

- 固定 **2 条**，位于身体前方。
- 每条同样为 **4 段结构**，末端蟹钳算入四段体系。
- 与步行腿明显区分：暗红、酒红、紫红、近黑红为主。
- 当前只建立关节、段长、位置、速度、方向和绘制骨架。
- **不决定**抓、钳、刺、搬运、攻击、玩家/武器碰撞等功能；全部留扩展点。

### 2.4 眼睛

- 固定两个红眼，主体正面偏下。
- 可有轻微惯性/悬垂感，禁止大幅甩动。
- 始终保持红色系；ID 只允许小范围改变尺寸、纵横比、红色色温、亮核比例和极轻微左右差异。
- 可使用高饱和红、小范围 gloss/emission 维持暗处可读性。
- **Emission != LightSource**；默认不照亮周围环境，真实发光照明为 TBD。

### 2.5 壳缘 / 垂挂物

- 固定壳缘可以有细碎、不规则、略带纤维/毛边的 silhouette。
- 少量较长垂丝可独立轻微摆动。
- 它们当前均为视觉组件，不承担硬碰撞。

## 3. 物理 / 碰撞骨架

### 3.1 壳体：多 BodyChunk 扁平框架

- 不得用 1 个巨大 BodyChunk 代表整片宽壳。
- 第一版约 **5 个 BodyChunk** 横向覆盖主体：中央最大，向左右外侧半径逐渐减小；具体尺寸需实机调试，不把讨论中的示意数字当最终参数。
- BodyChunk 只是物理骨架，不直接等于最终可见 sprite/mesh。
- 连接不能只是 `L2-L1-C-R1-R2` 的单链，否则会像香肠一样折叠。
- 必须存在**横向主连接 + 跨节点/交叉稳定约束**，形成扁平框架，例如概念上：

```text
L2 —— L1 ===== C ===== R1 —— R2
       ╲       │       ╱
        ╲______│______╱
```

- 不要求严格照图固定连线，但必须达到：整体可倾斜、受撞有小幅弹性/晃动，却基本保持宽壳形状，不能像柔性链折起来。
- 视觉壳缘可略超过最外 BodyChunk；不能出现大面积“看得到壳却完全穿过去”的错位。

### 3.2 步行腿：Segment + IK + 轻物理

- **绝对不要**把 4×4 腿段全部做成 BodyChunk；避免卡墙、卡杆、多腿互撞、死亡爆链等问题。
- 每腿使用独立控制点/Segment 系统，参考 `DrillCrab.Leg`：位置、lastPos、速度、段长约束、IK/Verlet 类轻物理。
- 结构：`ShellAnchor -> P0 -> P1 -> P2 -> P3(Foot)`，四条连线对应四段。
- MantleCrab 是硬质节肢，不是软绳；以 **IK/长度约束确定硬关节姿态 + 少量惯性/阻尼提供生命感**。
- 真正可靠的 terrain contact 重点在足端，腿身默认不是玩家/生物的实体墙。
- 足端可使用单点或少量 terrain probes 处理平地、斜坡、台阶和边缘。

### 3.3 视觉足端与接地点必须解耦

块状大脚是**视觉体积**，不等于同尺寸的巨大硬碰撞体：

```text
████████     <- 玩家看到的块状足端
 ●  ●  ●     <- 可选的小型地形采样/支撑点
```

- 不要因为脚看起来大就给它一个大 BodyChunk 或大圆形实体碰撞。
- 视觉脚可覆盖多个 probe；probe 只负责找地/支撑稳定。
- 这样可保持足端重量感，同时避免细腿和大脚成为不合理的空气墙。

### 3.4 腿对壳体的真实支撑

- 腿应实际给壳体施加支撑，而不是简单把 Creature gravity 调小。
- 支撑基本模型：目标高度/腿伸展误差的弹簧项 + 竖向速度阻尼 + 最大支撑限制。
- 多腿共同分担，不要求四脚同时严格锁死，否则不平地形会高频抖动。
- 预期手感：4/3 脚很稳；2 脚仍可支撑；1 脚明显失稳；0 脚正常受重力。具体函数/阈值实机调。
- 玩家原则上可以从腿之间穿行；**壳体是主要 Creature 硬碰撞主体。**

### 3.5 钳肢物理骨架

- 使用与腿相似的分节控制点思想，但当前不承担身体支撑。
- 当前不成为主要玩家硬碰撞。
- 保留末端位置、lastPos、速度、方向等数据，使未来抓取/攻击不需重写 Graphics 骨架。
- 是否使用 `Appendage`、是否可被武器击中均为 TBD。

## 4. 玩家最终看到的外观：总体渲染架构

### 4.1 零人工绘制依赖

**用户无法承担人工绘制。核心版本必须在 0 张人工生物纹理下成立。**

不得要求用户制作：

- Color/Albedo atlas；
- Normal Map；
- Surface/Roughness/AO 图；
- 手绘关节/足端/眼睛 sprite；
- 固定花纹图。

允许：程序在运行时生成 RenderTexture / atlas / mask /材质缓存；这些不是人工资源。

核心路线：

> **参数化几何 / SDF 轮廓 + EntityID/Personality VisualGenome + 程序材质 + 动态 UV Mesh + Rain World 环境驱动的 Stylized 2.5D Shader。**

### 4.2 必须继续使用 Rain World/Futile 图形体系

首选：`TriangleMesh + FSprite/FContainer/SpriteLeaser + Custom Shader + 可选 Compute Material Baker`。

明确不采用：

- 3D Model 作为主体；
- Unity Standard/PBR 角色材质；
- 为了“更新技术”无理由绕开 Futile/FContainer/SpriteLeaser 的 `DrawProcedural/ComputeBuffer/SV_VertexID` 独立渲染体系；
- 真实 Z-buffer/3D ShadowCaster 作为主要解决方案。

只有出现无法由 Futile Mesh 合理解决、且收益明确的问题时，才允许重新评估低层 GPU procedural draw；不能作为默认架构。

### 4.3 壳体几何呈现

- 使用可轻微变形的 Grid `TriangleMesh` / 参数网格，参考 `MoonCloak` textured grid mesh 的 UV 变形思想。
- 壳体 alpha/轮廓由参数曲线/SDF 生成：横向宽、纵向薄、中央厚、翼端渐尖、边缘轻微不规则。
- 物种固定形状占主导；ID 只能做小范围宽度、翼角、边缘粗糙、不对称等变化，不得破坏 MantleCrab 剪影。
- Mesh 跟随 BodyChunk 框架产生位移、旋转、轻度弯曲/压缩；禁止布料式大幅飘动。

### 4.4 腿 / 关节 / 足端绘制

- 每个腿段为独立动态 mesh，按相邻关节点实时拉伸、旋转、变形。
- 每段可有独立 taper/bulge/轮廓参数，保证四节结构清晰。
- 关节使用单独参数化 JointCap / polygon/SDF 形体，遮盖 mesh 接缝并形成节肢膨大。
- 足端必须是独立参数化 polygon/mesh，厚、块状，不能只把最后一节 TriangleMesh 拉粗。
- 足端可依据地面法线有限旋转，使其在斜坡上有“落地面”。

### 4.5 钳肢绘制

- 与腿采用同类分节 mesh，但独立 VisualGenome/material 参数。
- 钳端使用参数化 polygon/SDF 生成真正的钳形，而非普通腿段末端变粗。
- 当前只保证结构和视觉，不绑定功能。

### 4.6 壳缘 / 流苏

- 固定细碎壳缘优先用 SDF/边缘噪声形成**清晰 alpha silhouette**。
- 不依赖宽范围 Gaussian/柔软半透明毛边；避免 Futile 排序/环境光下出现“发虚、幽灵边”。
- 若需要软化，可用少量空间稳定 dither/细碎 silhouette，而不是大面积低 alpha。
- 少量真正会动的长丝可使用 2~5 点轻量 mesh/rope 视觉模拟，无硬碰撞。

### 4.7 生物内部局部 Draw Order

这是**生物内部视觉层级**，不是世界背景层机制。默认建立可调整的 LocalDepth，例如：

```text
Rear walking legs
    ↓
rear/underside danglers
    ↓
Shell / underside
    ↓
Front walking legs + Pincers
    ↓
Eyes
    ↓
Future top attachments (当前不存在，仅保留最后层概念)
```

- 具体四腿哪条属于 Rear/Front 可按参考图与姿态确定。
- LocalDepth 同时驱动 draw order 与 depth tint；世界级背景/前景切换仍禁止实现。

## 5. 程序材质：出生/初始化时烘焙 + 每帧环境着色

### 5.1 阶段 A：个体“长皮肤”

`EntityID + 原版 Personality -> MantleCrabVisualGenome -> MantleCrabVisualPhenotype -> Material Baker -> 个体材质缓存`

首选使用 Compute Shader 一次性生成小型个体材质 atlas / RenderTexture；Compute 不可用时必须有 CPU/fragment fallback 或较简化的程序材质路径。

建议缓存：

- `PatternAtlas`: RGB = 基础色/花纹，A = Height；
- `SurfaceAtlas`: R = AO，G = Roughness/Gloss control，B = Material mask，A = Emission。

约束：

- 复杂 Simplex/Worley/Ridged/Domain Warp **尽量只烘焙一次**；
- 不要每帧对每个像素重新跑大量 octave noise；
- 缓存生命周期要可释放，避免大量个体永久占用 RenderTexture；
- 核心外观不能因 Compute 缺失而完全消失。

### 5.2 阶段 B：每帧实时 2.5D 环境着色

`Generated Material + Current Mesh Pose + RW Environment -> MantleCrabSurface.shader`

必须使用 Rain World 环境，而不是固定左上光：

- 当前 palette / `_PalTex`；
- `room.lightAngle` / Rain World 方向光信息；
- `room.Darkness(pos)`；
- `room.LightSourceExposure(pos)`；
- 可用时 `room.LightSourceColor(pos)`；
- 壳体和各腿段可在有限控制点采样环境亮度/颜色，再通过 vertex data 插值到 fragment。

CPU/Graphics 侧负责“这个部位当前环境有多亮/多深”；fragment shader 负责“该像素的甲壳 normal/material 对这束光如何响应”。

禁止：

- GrabPass；
- 每只 MantleCrab 每帧运行 Compute；
- Unity Standard/PBR；
- 真实 3D 光源/Z-buffer 依赖；
- 把固定方向阴影烘死进一张图。

视觉目标是 **Rain World 风格 Stylized 2.5D Lighting**，不是写实 Unity 资产商店角色。

## 6. 程序化颜色 / 花纹 / 表面

### 6.1 多尺度分层

禁止“一层 Perlin/Worley noise 覆盖全身”。细节至少分三尺度：

- **Macro**：壳中央->翼端的综合色域、主红紫条纹、大块暗区；决定远距离识别；
- **Meso**：甲壳板片、次级色带、轻斑驳；
- **Micro**：细脊、颗粒、微孔、粗糙度、高光破碎；Micro 主要进入 Height/Normal/Roughness，避免把 RGB 做脏。

可使用：Simplex/gradient noise、Worley/cellular、ridged noise、domain warp。Noise 是工具，不是最终花纹；必须受身体结构和解剖坐标控制。

### 6.2 解剖坐标 / 生长方向

- 壳：以中央->左右翼端为主要流向；红紫主纹沿这个方向展开/弯曲。
- 腿：根部->足端。
- 钳：根部/关节->钳尖。

主壳固定视觉必须保留：**深紫黑中央 + 两翼向外展开的红紫方向性纹样**。

程序条纹建议由基础 band/周期 + 曲率场 + 低频 domain warp + 局部 break/mutation 构成，而不是直接 threshold(noise)。

### 6.3 程序 Height -> Normal

Normal 不依赖人工 Normal Map。

程序 Height Field 可组合：

- 壳体宏观曲率；
- ridge；
- plate/cellular structure；
- pore/granulation；
- joint groove；
- undershell/cavity transitions。

由 Height 梯度或可用的解析 noise gradient 生成 pseudo-normal。

不同部位必须有不同宏观 normal：

- **壳体**：扁平伞状/缓拱 pseudo-normal；
- **腿段**：假圆柱/节肢横截面 normal，再叠 ridge/plate/micro；
- **足端**：不能继续用腿的圆柱 normal，使用**厚块/box/wedge-like pseudo-normal**，形成顶面、侧面、底面不同明暗；
- **钳**：硬壳楔形/弧面 normal；
- **眼睛**：圆润高 gloss normal。

腿/钳 mesh 旋转和轻微变形时，normal 必须跟随当前 UV/TBN/屏幕空间切线正确转换，禁止“腿转了但高光方向没转”。

### 6.4 AO / 自遮蔽

- 关节缝、甲壳板片接缝使用较强 AO，避免正面受光时所有凹槽被洗平。
- 壳体 underside 本身应更暗，并在腿的壳体连接根部提供一段**近壳强、向下逐渐恢复**的 undershell AO/遮蔽。
- AO 是材质/结构信息，不等同于把整个部位固定染黑。

### 6.5 材质类型

至少区分：

- 壳中央：暗、粗糙、吸光、低 specular；
- 壳两翼：更硬/薄甲壳感，中低 specular，红紫纹清楚；
- 步行腿：硬质节肢甲壳，中等**宽高光带**，关节 AO 强；
- 足端：厚重、深紫、AO 更强，块体光照明显；
- 钳肢：暖暗红/酒红/紫红，边缘可更锐利；
- 眼睛：高饱和红、高 gloss、小范围 emission。

高光采用 stylized wide highlight；避免现实 PBR 的尖锐白点和金属感。

### 6.6 Rain World 化 / Dither

- 最终色必须受 palette、darkness、fog/depth/local light 影响；换房间、昼夜和附近灯光时不能像贴着同一张截图。
- 轮廓保持清晰；内部光影可平滑。
- 如果使用生成 atlas，可让 silhouette 主要由 mesh/SDF 控制，material map 可按需要双线性采样；不要因为 material 平滑而把整个生物边缘糊掉。
- 可使用很轻的**空间稳定 blue-noise dither / quantization** 抑制过度现代的平滑渐变；不能逐帧随机导致表面闪烁。

## 7. 内部视觉深度

四条腿的背景->前层渐变是已确认视觉特征，不属于 Personality。

建议 `localVisualDepth` 同时影响：

- FContainer/sprite draw order；
- 向 fog/depth color 的偏移；
- 对比度；
- specular/highlight 强度；
- 必要时轻微饱和度。

后腿：更冷、更灰、更低对比、更弱高光；前腿：更实、更清晰。

**不要把 localVisualDepth 简化成 `alpha = 0.5`。**

## 8. VisualGenome：EntityID + 原版 Personality

### 8.1 只使用原版六项 Personality

禁止新增 MantleCrab 专属性格系统。读取原版 `AbstractCreature.Personality`：

- `energy`
- `bravery`
- `sympathy`
- `dominance`
- `nervous`
- `aggression`

原则：

> **Species Rules 保证物种识别；EntityID 决定主要遗传随机；Personality 只作为表现倾向。**

`ID ≈ 70% / Personality ≈ 30%` 只是设计倾向，不是要求所有公式硬编码 0.7/0.3。

### 8.2 Species Rules：永远不可随机掉

- 宽扁暗色披幕壳；
- 4 条四节超长步行腿；
- 2 条四节暗红钳肢；
- 2 个红眼；
- 蓝灰/深蓝紫腿；
- 深紫黑主体；
- 两翼红紫方向性主纹；
- 粗大块状足端；
- 后腿->前腿的视觉深度关系。

### 8.3 EntityID 负责主要个体差异

可稳定控制：

- 壳宽/翼角/边缘粗糙等小范围形体变化；
- base hue 细偏移；
- 主纹 seed/count/width/phase/curvature/warp；
- 斑驳位置；
- 甲壳 cell/plate 结构；
- micro ridge/pores；
- 腿色、关节与足端细节；
- 钳色差；
- 眼睛小范围差异；
- 左右局部 mutation。

不同 ID 要明显但不过度不同；远看仍首先识别为同一物种。

### 8.4 稳定随机：禁止长期依赖顺序 Random.value

不要把长期视觉基因建立在：

```text
Random.InitState(seed)
a = Random.value
b = Random.value
...
```

因为中间新增参数会改变后续所有旧个体外观。

使用命名稳定 Hash：

`Hash(ID.RandomSeed, VisualChannel)`

例如：`ShellHue / StripePhase / LeftRearLegMicro / EyeAspect / FootBulk`。

新增 VisualChannel 不应改变现有 channel 的结果，使同一 EntityID 在版本升级后尽量保持已有外观。

### 8.5 Shared Genome + 左右 Mutation

左右不是完全独立随机：

`SharedGenome + LeftMutation + RightMutation`

- Macro 高度对称；
- Meso 有有限差异；
- Micro 可更明显不同；
- 整体看起来是自然双侧结构而不是镜像贴图，也不能左右像两个物种。

### 8.6 Personality -> 视觉职责

避免多个相关 Personality 重复堆叠同一视觉参数。

- `dominance`：大尺度/厚重感；主纹更宽、足端和关节视觉体积略强；不直接决定颜色。
- `aggression`：纹样尖锐度、红紫暖色强调、钳肢攻击性色彩/边缘；不直接决定体型。
- `bravery`：主纹展示强度、完整度、对比和延伸范围；不等同 aggression。
- `nervous`：domain warp、局部断裂、细碎副纹、微不对称；不大幅改变身体形状。
- `energy`：Meso/Micro 细节频率、次级纹与细脊密度；不简单等于“更亮”。
- `sympathy`：纹样曲线/综合色彩过渡平滑度、协调度、边缘圆润倾向；不是“可爱程度”。

原版 Personality 本身存在相关性，禁止把 `aggression/dominance/bravery` 全部重复映射成“更红、更大、更亮”然后叠加爆表。

### 8.7 先生成 Phenotype，再给 Shader

不要让 shader 直接接收六个 Personality 后自行决定美术规则。

推荐：

`EntityID + Personality -> VisualGenome -> VisualPhenotype -> Baker/Shader`

Phenotype 应暴露清晰可调的美术参数，例如：`MotifScale / MotifSharpness / MotifContrast / MotifWarp / Fragmentation / Asymmetry / ChitinRoughness / LegDetail / PincerAccent`。

Shader 只负责按明确 phenotype 和环境信息绘制，便于长期调参和保持视觉一致性。

## 9. 资源 / 代码拆分（实现时可合理微调）

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

Shader 资产与 weather 分开：

```text
shader-src/Assets/DryCycle/Creatures/MantleCrab/
├─ MantleCrabMaterialBake.compute
├─ MantleCrabSurface.shader
└─ MantleCrabEye.shader   # 只有确有独立材质需求时再拆
```

- 可建立独立 creature AssetBundle；不要把生物 shader 硬塞进 weather 命名/模块。
- 复用 DryCycle 已有 `RainWorld.LoadResources` 后 AssetBundle/FShader 安全加载管线。
- Compute 是个体材质一次性生成工具，不是每帧生物模拟核心。

## 10. 推荐开发顺序

1. **Body Prototype**：约 5-chunk 宽壳 + 交叉稳定约束；验证宽度、倾斜、墙角碰撞；
2. **Standing Prototype**：4×4 段腿 + 足端 probes + 支撑力；只要求平地/坡/台阶稳定站立；
3. **Procedural Silhouette**：壳、四腿、JointCap、块状足、双钳、双红眼、局部 draw order；0 人工纹理即可读；
4. **VisualGenome/Phenotype**：稳定 ID 差异 + 原版 Personality 偏置；
5. **Material Baker**：程序 Base Color / Height / AO / Roughness / Material / Emission；
6. **Runtime Shader**：RW palette + lightAngle + darkness + local light + visual depth；
7. **Polish**：undershell AO、壳缘/流苏、box-foot normal、blue-noise/dither、cache/fallback/performance；
8. **之后再讨论移动策略、AI、钳互动、生态等**，不得提前扩展。

## 11. 第一阶段验收标准

完成“外观/碰撞原型”至少满足：

- 静止即能一眼识别：宽幕壳 + 四超长腿 + 双钳 + 双红眼；
- 4 条腿都明确读出四节，且各腿比例/姿态不是机械复制；
- 足端明显块状粗大，但实际地形 probe/碰撞保持轻量，不变成大空气墙；
- 壳体碰撞覆盖主要视觉宽度，不能只有中央一个圆；
- 壳体多 chunk 框架不会像链条折叠；
- 腿不用大量 BodyChunk，不因细长结构频繁卡墙/卡杆/互撞；
- 平地、斜坡、台阶站立不会持续高频抖动；
- 玩家原则上可从腿间穿行，壳为主要阻挡；
- **0 人工绘制生物纹理依赖**；
- 壳、腿、关节、足端、钳、眼睛均可由程序几何/材质形成；
- 同一 EntityID 重载视觉稳定；不同 ID 有明显个体性但保持 Species Rules；
- Personality 只形成统计倾向，不出现“某性格固定皮肤”；
- 主纹沿壳中央->翼端生长，腿纹沿根->足，禁止裸噪声感；
- Macro/Meso/Micro 层级可读，Micro 不把 RGB 做脏；
- 壳中心暗粗糙、翼面略硬、腿呈硬甲壳宽高光、足端有厚块光照、钳偏暖暗红；
- 关节/壳下 AO 有结构性，不是固定黑贴图；
- 后腿低对比冷色、前腿更实，并具有正确 local draw order；不是简单 alpha；
- 换 palette、明暗房间、方向光或附近局部光源后，光影方向/综合色应随环境变化；
- Mesh 旋转后 pseudo-normal/highlight 方向正确；
- 眼睛暗处可读但默认不照亮环境；
- 壳缘不依赖宽大半透明柔边；
- 不使用 3D Model/PBR/GrabPass/默认 DrawProcedural 替代 Futile；
- 不实现任何本任务列为 TBD 的玩法。

## 12. Codex 行为约束

- **完整执行本任务书，不要为了缩短实现自行删规格。**
- 开工前先检查当前 `main` 的 `MossySpider`、Registration、shader asset pipeline 和相关原版/Watcher 类。
- 每完成一个阶段先保证可编译、可生成、可测试，再进入下一阶段。
- 不一次性把 AI、寻路、战斗、生态塞进当前任务。
- 所有未确认玩法保留清晰扩展点，禁止猜设定。
- 优先模块化、稳定 seed、参数可调、资源可释放；避免把 VisualGenome、腿几何、光照公式全部硬塞入一个超大 `DrawSprites()`。
- 遇到任务书与当前代码冲突时，先报告冲突与可选方案，不要静默改设计。
