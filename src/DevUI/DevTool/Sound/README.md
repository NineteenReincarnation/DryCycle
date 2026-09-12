# DevTool Sound / 音效组

## 目标

Sound Group 是 **DevTool 编辑器模板**，不是 Rain World 房间运行时格式。

点击“应用到当前房间”后，组内项目会被展开成普通的 `AmbientSound` / `DirectionalSound` / `SpotSound` 并写入 `RoomSettings.ambientSounds`。房间保存之后不再依赖 `sound-groups.xml`；发布地图时，即使玩家没有任何 Sound Group XML，也不会因此产生运行时依赖。

## 一体化工作流

Sound 编辑器使用一个跨页面共享的 **Working Group / 工作音效组**。选择一次后，Library、Scene 和 Inspector 都使用同一个目标组，不需要在每个声音上重复选择 Group。

### Library

Library 的 `Destination` 支持三种模式：

- `Scene`：只创建到当前房间。
- `Working Group`：不经过 Scene，直接把资源以当前声音类型的默认参数加入工作音效组。
- `Scene + Working Group`：一次点击同时创建到当前房间，并把实际创建出来的声音参数快照写入工作音效组。

目标模式会保持不变，适合连续录入一批声音。

### Scene

Scene 支持多选：

- 单击：单选。
- `Ctrl + Click`：追加或取消选择。
- `Shift + Click`：范围选择。
- `Ctrl + A`：全选。

选中多个声音后可以一次加入 Working Group，也可以直接使用“从选择新建音效组”。后者会保存每个声音当前的类型、音量、音高、方向、位置、半径和衰减等参数快照。

### Inspector

单选声音时可以直接“加入当前声音”；多选时 Inspector 会改为批量加入当前 Working Group。Working Group 的选择本身仍由 Browser 顶部统一管理。

## 文件位置

### 本地开发者库

默认：

```text
Rain World/BepInEx/config/DryCycle/DevTool/sound-groups.xml
```

Sound 页面可以指定另一个本地文件夹。自定义目录只影响开发者个人库。目录、Reload、Default 等低频设置收纳在 Groups 页的 `Library Settings` 中，不占用日常编组流程。

### Mod 可移植标准

```text
mods/ModName/music/sound-groups.xml
```

DevTool 会扫描当前启用 Mod 的标准文件。标准 Mod 文件只读；要修改它，请直接编辑 Mod 工程中的 XML。

## 优先级与重复 ID

每个 `<SoundGroup>` 必须有稳定、全局唯一的 `id`。显示名称 `<Name>` 可以修改，不参与身份判断。

建议格式：

```text
ModID.GroupName
```

读取优先级：

1. 当前启用 Mod，按 Rain World 实际 Mod 优先级从高到低。
2. 本地 `BepInEx/config/DryCycle/DevTool/sound-groups.xml`。

出现重复 ID 时只采用优先级最高的一份，并在 DevTool Problems / 预警窗口持续报告冲突。

## XML v1

```xml
<?xml version="1.0" encoding="utf-8"?>
<SoundGroups version="1">
  <SoundGroup id="drycycle.desert.ambient">
    <Name>荒漠环境音</Name>

    <Sound
      type="Omnidirectional"
      sample="Wind_LOOP.wav"
      volume="0.65"
      pitch="0.90" />

    <Sound
      type="Directional"
      sample="DistantWind.wav"
      volume="0.50"
      pitch="1.00"
      doppler="0.20"
      directionX="1.0"
      directionY="0.0" />

    <Sound
      type="Spot"
      sample="MetalCreak.wav"
      volume="0.40"
      pitch="1.00"
      doppler="0.00"
      x="0.72"
      y="0.46"
      radius="900"
      taper="0.50" />
  </SoundGroup>
</SoundGroups>
```

支持的 `type`：

- `Omnidirectional`
- `Directional`
- `Spot`

Spot 的 `x` / `y` 是 0~1 的房间归一化位置。应用到另一个尺寸不同的房间时，会按目标房间 `PixelWidth` / `PixelHeight` 恢复坐标。

## 资源诊断

音效组中的 sample 会经过当前 Rain World 环境实际可见的 Ambient Sound 资源目录进行验证。

DevTool 会持续报告：

- 重复 Sound Group ID。
- XML 无法读取或根节点错误。
- Sound Group 缺少 ID。
- 不支持的声音类型。
- Sound 缺少 sample。
- sample 在当前游戏 + DLC + Mod 环境中缺失。

存在缺失 sample 时，组仍然可以读取；UI 会标出缺失项，“应用可用项”会跳过缺失资源。

## Undo

应用整个 Sound Group 是一次编辑操作：无论组里有多少声音，只产生一条 History 记录，因此一次 Undo 即可撤销整个应用。

Sound Group 本地库本身是开发者模板数据，不和房间 `RoomSettings` 的 Undo 栈绑定；加入或创建 Group 不会制造房间 History 噪音。
