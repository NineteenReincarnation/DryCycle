# Graphics 与 PWN_AI 样例

第四阶段提供命名 Sprite、可替换 Graphics、不可变 GraphicsProfile，以及标准身体、Face、Gown、Halo、Cable 部件。Graphics 读取 Body 的位置、姿势、注视和手脚目标，只负责视觉表现，不选择 AI、对话或剧情。

## 使用标准外观

```csharp
using DryCycle.Iterators;
using UnityEngine;

var appearance = new GraphicsProfile(
    bodyColor: Color.white,
    shadowColor: new Color(0.6f, 0.55f, 0.95f),
    haloCount: 0,
    cable: false);

IteratorDescriptor definition = Iterator.Create("MYMOD_VISUAL")
    .Room("MYMOD_AI")
    .Graphics(context => new StandardIteratorGraphics(context, appearance))
    .Register();
```

不配置 `.Graphics(...)` 时使用 StandardIteratorGraphics。工厂必须返回以所传 Context 创建的新组件；Graphics、部件和 IteratorMesh 不可跨实例复用。GraphicsProfile 可以共享。已有 Descriptor 四、五、七参数构造签名保持兼容，新增八参数重载的最后一个参数为 GraphicsFactory。

## 外观配置

| 配置 | 标准实现的用途 |
| --- | --- |
| BodyColor / ShadowColor | 面部、衣袍、袖子及褶皱颜色。 |
| OutlineColor / EyeColor / AccentColor | 轮廓、眼睛和光环颜色；标准线缆使用轮廓色。 |
| Scale | 身体局部图形尺寸，范围 0.1–20；不改变 Body 碰撞尺寸。手脚世界目标仍由 Body 决定，大型角色应同时配置 BodyProfile 和 Pose。 |
| Face / Gown / Mark | 启用脸、长袍和额头标记。 |
| HaloCount | 0 表示无光环，1–8 表示多个同心光环；可用自定义部件实现其他形态。 |
| Cable | 启用 StandardCable；只有 FixedArm 提供固定锚点时才生成线缆。 |
| Element / Shader | Futile 图集元素名和游戏 Shader 字典键；默认 Futile_White / Basic。 |
| PaletteInfluence | 向当前相机调色板黑色混合的比例，范围 0–1。 |
| Glow | 降低环境调色的比例，范围 0–1；不创建房间光源。 |

标准身体、袖子、手、脸、长袍、额头圆环均由顶点色网格绘制。眼睛读取注视方向并按固定周期眨眼，袍摆随时间和身体速度轻摆；不消耗游戏随机数。FixedArm 的线缆只表现连接，不负责机械臂约束或原版多关节模拟。

## 自定义部件与非人形

直接继承 IteratorGraphics 可以完全替换标准人形组合；继承 StandardIteratorGraphics 后重写 OnInitialize，可以复用并追加部件。

```csharp
public sealed class MyGraphics : IteratorGraphics
{
    public MyGraphics(IteratorContext context) : base(context) { }

    protected override void OnInitialize()
    {
        AddPart(new ExtraVisual());
    }
}

public sealed class ExtraVisual : IteratorMeshPart
{
    public ExtraVisual() : base("ThirdArm") { }

    protected override void OnInitialize()
    {
        Polygon("Panel", new[] {
            new Vector2(-3, 0), new Vector2(3, 0), new Vector2(0, 12)
        }, Profile.AccentColor, layer: 10);
    }
}
```

以上示例注册 `ThirdArm.Panel`，默认随身体位置和朝向变换。IteratorMeshPart 提供 Polygon、Ellipse、Stroke、OutlinedPolygon、OutlinedEllipse、DrawAt 等构建方法；覆盖 OnDraw 可对固定拓扑逐顶点变形。需要完全自定义的绘制数据时继承 IteratorGraphicsPart，并通过 Register 或 Sprites.Register 添加 IteratorMesh。

标准部件包括 StandardBodyVisual、StandardFace、StandardGown、StandardHalo、StandardCable。没有强制人形接口，也没有必须存在的 Head、Gown 或腿部 Sprite；四臂、无腿或非人形可自行组合。视觉结构不会自动增加身体碰撞 Chunk。

## SpriteRegistry 与网格

`Sprites["Face.Head"]` 返回 SpriteHandle，提供 Name、Mesh、Layer、Element、Shader、Visible。`TryGet` 用于查询可选部件。部件的 Register 会自动加上部件名前缀，直接调用 Sprites.Register 则使用原名。

初始化结束后布局冻结，不能新增 Sprite、修改拓扑或更换元素及 Shader；可以更新已有网格的世界坐标、顶点色和 Visible。更换布局时创建新实例。名称区分大小写且不能重复，一个网格只能注册给一个 Sprite。数值索引仅由内部相机适配器管理。

IteratorMesh 构造时复制顶点、三角索引与可选 UV。Vertices、Colors、Triangles 是只读视图，使用 SetVertex / SetColor 更新数据。坐标、颜色、UV 必须有限；Polygon 支持不自交、无洞的简单凹多边形。没有提供 UV 时所有顶点采样元素中心，适用于纯顶点色；自定义贴图请传入元素内的归一化 UV。

每个相机创建独立的 TriangleMesh，按 Layer 从小到大排序，同层保持注册顺序。DrawSprites 将共享的世界坐标数据减去各相机位置，调色板颜色只写入该相机的 Sprite，不改写原始颜色。

## 生命周期与错误处理

创建顺序：Body / Arm 初始化 → Graphics 工厂 → Graphics.OnInitialize 与各部件初始化 → 冻结 Sprite 布局 → 创建绘制适配器 → Brain 初始化 → Conversation 初始化 → Runtime.OnCreate 及后续回调。

每帧：Brain 感知与动作 → Conversation → Runtime.OnUpdate → Body / Arm 控制和游戏物理 → Graphics 捕获前后帧状态、OnUpdate 与部件更新 → Runtime.OnLateUpdate。相机绘制调用 Render(timeStacker) 进行插值，不推进逻辑时钟。自定义 OnDraw 应直接依据输入计算结果，避免以绘制次数累加动画状态。

房间不再被观看时移除相机 Sprite 和适配器，保留 Runtime.Graphics；再次观看时重建相机 Sprite。销毁顺序为 Runtime.OnDestroy → Conversation 清理 → Brain 清理 → 移除所有相机 Sprite → 图形部件逆序清理 → Graphics.OnDestroy 与图集租约释放 → Arm / Body 清理 → 宿主、索引和 Context 清理。

- 部件初始化、更新或绘制失败：记录 Iterator / Graphics.部件名 / Phase，停用该部件并隐藏其 Sprite，保留其他部件和 Runtime；失败部件仍参与最终清理。
- Graphics 工厂或整体初始化失败：清理已接管的组件并尝试标准外观；外来或已复用的组件不会被接管、销毁。
- 整体 Graphics 更新、绘制或渲染器失败：停用本实例绘制，Body 和 Runtime 继续运行。
- 中途销毁、重复销毁及初始化失败均保留清理路径；销毁后 Render 无操作，Context 游戏引用由 Runtime 清空。

自定义组件的构造函数不要申请需要框架清理的资源；构造抛异常时框架拿不到返回实例。资源申请应放在受保护的初始化回调中。

## 资源

默认和 PWN 样例只借用游戏 Futile_White，不生成自有 Texture、材质或 Shader Bundle，不需要另行安装角色图集。

自定义 Graphics 可在 OnInitialize 内调用 `RequireAtlas("atlases/mymod_iterator")`，路径不带扩展名。首次创建相机 Sprite 时加载，后续实例共享引用计数；最后一个使用者释放时只卸载框架实际加载且仍是原对象的图集。游戏或其他 Mod 已加载的图集视为借用，不由框架卸载。

缺失图集会记录错误，缺失元素或 Shader 分别退回 Futile_White / Basic，同一适配器对同名缺失资源只提示一次。默认白图集也不可用时停用绘制。渲染器持有 Sprite 期间不卸载其图集；退出观看后可重建 Sprite，图集租约在 Runtime 生命周期结束时释放。

## PWN_AI 样例

DryCycle 启用时自动登记 `DryCycle_PWN_Sample`，显示名为“PWN 示例迭代器”，精确绑定 PWN_AI；插件停用时注销此样例。未安装该区域时不会创建实例。也可显式调用 PwnIteratorExample.Register / Unregister 管理样例。

PwnIteratorGraphics 使用白色面部、深色椭圆眼、额头大小圆环、棕色耳部接点、金色扭转头饰和白紫长袍，不绘制参考图两侧蓝球。形象为可随 Body 运动的游戏网格，无外部人物贴图。样例保留展示姿势并悬停，第五阶段加入可见玩家观察；没有对话、玩家态度或剧情行为。

本机核对的房间路径：

```text
D:/Application/Steam/steamapps/common/Rain World/RainWorld_Data/StreamingAssets/mods/ParchedWilderness/world/pwn-rooms/PWN_AI.txt
```

房间为 48×36 格。使用现有安全生成位置算法，当前文件生成于 `(470, 350)`；角色所有网格顶点位于空腔中。没有修改房间、调色板或世界连接。当前 world/pwn/world_pwn.txt 将 PWN_AI 标为 DISCONNECTED，游戏内验收需从已有房间测试或跳转入口进入。

## 验证与预览

运行 [PROGRESS.md](PROGRESS.md) 中的构建和检查命令，会额外输出：

- `artifacts/iterator-framework/pwn-iterator-preview.png`：放大外观与 1:1 游戏像素尺寸。
- `artifacts/iterator-framework/pwn-iterator-transparent.png`：透明 PNG。
- `artifacts/iterator-framework/pwn-ai-placement.png`：实际房间实墙与样例位置。
- `artifacts/iterator-framework/phase6-tests.log`：本机检查日志。

预览读取编译后 Runtime 的同一套网格和顶点色，通过 CPU 光栅化导出。相机检查使用真实 Futile TriangleMesh、SpriteLeaser 和未挂载 Stage 的容器，验证独立网格、相机偏移、调色板和清理；没有创建 Unity GPU 渲染环境。图集的原生加载、Shader 执行、真实房间光照、游戏内切换和最终视觉效果仍需 Unity 游戏内验收。
