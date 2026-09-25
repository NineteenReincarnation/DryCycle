# Map 制图工作区

入口：`New UI → Map → 制图 / Cartography`。与世界地图、玩家地图并列，使用当前区域和角色时间线的地形数据。

## 设计来源与取舍

参考 [Ved-s/Cornifer](https://github.com/Ved-s/Cornifer)，本次阅读的提交是 `db040babba8dab743e4a34ebb188aa10f2faff21`。重点研究了它的 MapObjects、Layer、Connections、选择/拖动、UndoActions 和 Capture 导出流程。Cornifer 的房间、标注、连线、图层分工适合制图，但 DryCycle 使用自己的编辑器会话、烘焙、命令和呈现边界，不能移植它的独立 MonoGame 程序及全局状态。

本实现为 DryCycle 原生代码，不调用 Cornifer 程序集，也不要求安装其独立程序。范围是完整的**排布 → 标注 → 图层 → 保存 → 图片导出**流程；高级选项提供显式的 Cornifer `state.json` 导入，导入警告会显示在工作区中，不承诺所有旧工程特性完全等价。

| Cornifer 中参考的工作方式 | 在 DryCycle 中的实现 |
| --- | --- |
| 房间与标注共同组成地图 | 稳定房间名绑定来源；独立作者文档记录制图位置 |
| 拖动、框选、加选/减选 | 前端只预览共享位移；释放后一次命令、一次撤销 |
| 图层控制绘制顺序 | 可重命名、显隐、锁定、调透明度、排序；删除图层迁移对象 |
| 文本和图标对象 | 多行中文/英文文字、图钉、庇护所、业力门、珍珠、危险标记、线条、区域框 |
| 连接随房间移动 | 复用现有地图的真实连接和烘焙端口；未确定的端口显示虚线与诊断 |
| 图片和分层导出 | 透明 PNG、保留图层组和文本的 SVG、统一画布的分层 PNG ZIP |

## 组件边界

```text
游戏 AssetManager 解析当前启用模组的区域文件
              ↓ 后台解析 / 地形解码
              ↓ detached source
CartographyDocument ← CartographyEditing ← 带文档 ID/版本的命令
        ↓                   ↓
CartographyStorage     现有 EditorHistoryService 的独立文档历史
        ↓
作者 XML / 文件冲突检查 / 原子替换 / .bak

CartographyDocument + detached source
              ↓
CartographySceneBuilder + CartographySceneCache
              ├── CartographyView / ImGui 画布
              └── 冻结场景 → 后台 CartographyExporter → PNG / SVG / ZIP
```

- UI 不写文件，不读 live Room/World，不通过 Hook 或 Detour 调用 DryCycle 自有模块。
- 初始排布来自所选角色对应的区域地图文件。之后制图位置独立；世界地图和游戏玩家地图不会被制图操作回写。`补充新房间`只添加新来源，保留已有排布。
- 稳定帧保留源几何和场景；移动一个房间时只替换其视觉节点，连线使用新的端口位置，其余房间/标注可继续复用。平移和缩放只改变视图变换；画布剔除不可见的节点和地形片段。
- 拖动过程中只平移已有节点、更新相关连线预览；不会每帧重建所有房间地形。一次释放才提交作者数据。
- 保存、撤销和重做使用已有编辑器统一入口。制图激活时 `Ctrl+S / Ctrl+Z / Ctrl+Y` 操作制图历史，切回世界/玩家地图后恢复原来的历史。
- 检查器草稿以脱离 UI 的命令暂存。即使文字控件尚未失焦，Ctrl+S 和关闭 DevUI 也会提交已经输入的作者数据。命令同时携带来源身份和作者版本；过期拖动不能误写到另一区域。
- 关闭 DevUI、重建 Session 和切换区域时，进程内保留文档及其历史；不是自动保存到磁盘。退出游戏前仍需保存项目。

## 操作

- 打开制图时显示玩家当前区域和角色；上方区域列表可直接选择其他区域，支持搜索缩写或全称，格式为 `B5 · 古代遗址`。名称来自游戏的区域显示名和翻译，保留模组提供的中文名称；中文编辑器使用游戏当前语言的译名，英文编辑器使用原始名称。
- 普通使用自动读取当前游戏及启用模组，无需设置安装路径。高级选项用于切换角色或显式导入 Cornifer 存档。
- 区域读取、地形解码和场景准备在后台执行；快速连续选择只显示最后一次请求，失败时保留之前的地图并显示原因。最近四个区域的来源和预览可复用；文件路径、大小或修改时间变化会使缓存失效，`刷新`可强制重新读取。切换和缓存淘汰仅清理派生数据，保留未保存文档及撤销历史。
- 左键选择/拖动；空白处拖动框选；Shift 加选，Ctrl 减选。
- 右键或中键平移，滚轮以鼠标为中心缩放；右键取消当前放置工具。
- 选择文字、图标后点击放置；线条和区域框用拖动确定尺寸。
- 多选检查器提供六种边缘/中心对齐及水平/垂直等间隔分布。
- 画布获得输入时，方向键移动 1 单位、Shift 加速为 10 单位；Ctrl+A 全选可编辑对象；Delete 删除；Ctrl+D 复制标注。
- 活动图层决定新标注的归属；隐藏或锁定的图层不能接收编辑。
- 删除房间只是从此制图文档移除，不删除游戏房间。可用 `补充新房间`重新加入。
- 通过检查器的地图样式调整地形、水、连接、背景，切换房间名和实心地形裁切。标题保存为文档元数据和 SVG 标题；需要可见标题时放置文字标注。

## 保存与导出

默认工程位置：

```text
BepInEx/config/DryCycle/Cartography/<区域>-<来源身份哈希>.xml
```

来源身份包括解析后的 world 文件路径、区域、角色和时间线。源几何、缓存和运行时合并结果不进入作者 XML。

保存验证完整文档，再写同目录临时文件并原子替换；旧文件保留为 `.bak`。如果文件自读取后被外部修改，会报告冲突并保留内存修改。`另存项目副本`只写新路径，不覆盖已有工程，也不改变当前工程的保存基准。打开其他工程前必须先保存当前修改，并验证来源身份和格式版本；未来版本/损坏文件不会被自动降级覆盖。

导出使用与画布相同的几何、颜色、图层顺序和标注语义，默认写到工程目录下的 `Exports`，支持修改路径。分层 PNG 的尺寸、边距和原点一致；ZIP 中的 `layers.txt` 记录从后到前的图层对应关系。PNG 支持中文和 alpha。SVG 保留文本而非转轮廓，接收设备仍需要对应字体。画布使用编辑器字体，PNG 使用所选系统字体，字形度量可能略有差异。

导出器在 worker 中处理冻结文档与场景，不访问 Unity/ImGui，不阻止继续编辑。导出结果代表点击时的版本；导出不会把后来编辑标成已保存。可见房间地形不完整会阻止导出；缺少精确端口的连线则明确保留虚线提示。输出上限为单边 16,384 像素、32 megapixels，在分配 bitmap 前检查。

PNG 使用 Rain World 安装中已有的 `System.Drawing.dll` 和 Windows GDI+；无新增第三方运行时包。当前实现和验证目标是本项目的 Windows 游戏安装；其他平台的 GDI+ 后端未验证。

图片叠加、富文本、游戏图集图标和连接控制点编辑均沿现有文档与场景边界实现；Cornifer 的独立窗口和全局状态不进入 DryCycle。

## 验证

`tests/Cartography.Tests` 直接编译生产模型、运行时、编辑器历史、场景、缓存、持久化和导出代码，仅替代游戏及 Unity 边界。验证包括区域路径解析、中文全称、当前玩家区域、连续切换、缓存失效与淘汰、未保存修改和撤销保留、刷新期间保存，以及作者文档、文件冲突和图片导出。

验证输出位于源码目录外的 `../Build/DryCycle`：

```powershell
$outputDir = Join-Path $PWD '../Build/DryCycle/artifacts/cartography/build'
dotnet build src/DryCycle.csproj -c Release -p:DeployToGame=false "-p:OutputPath=$outputDir"
dotnet build src/DevUI/DevTool/RWImGui/DryCycle.DevTool.RWImGui.csproj -c Release "-p:GameModOutputDir=$outputDir"
dotnet build tests/Cartography.Tests/Cartography.Tests.csproj -c Release
& ../Build/DryCycle/bin/Cartography.Tests/Release/net48/Cartography.Tests.exe ../Build/DryCycle/artifacts/cartography/validation --game 'D:/Steam/steamapps/common/Rain World'
```

`--game` 会读取本机 B5、CC、SU 的真实区域文件，解码全部房间、生成场景并导出 PNG；不修改游戏区域文件。托管测试和图片检查不等于游戏内交互验收，游戏内仍需验证字体显示、Map/制图切换、拖动与 Undo、文字输入时 Ctrl+S、DevUI 关闭重开，以及后台导出期间继续编辑。
