# Hovered Room Weather 面板说明

`Hovered Room Weather` 是 Weather Zones DevUI 中用于查看“当前鼠标悬停房间”的天气空间状态的诊断面板。

它不负责编辑 Region 调度概率，也不代表房间本身拥有独立概率。面板中的房间规则与 Region 调度数据是两层不同概念。

## 1. 数据页

默认页面显示当前悬停房间，例如：

```text
Room: SU_A53
Green Allow  Red Deny | R/Z/G source, +/- rule

FamWeather     SubWeather      DangerType
Rain G- --     LightRain R+ -- DeathRain -- --
Fog  G- --     HeavyRain -- -- IntenseHeat -- --
Heat G- --     Fog -- --       DeathSandStorm -- --
Sand G- 100%   DenseFog -- --
               HeatWave -- --
               SandStorm -- 100%
```

### 颜色

- 绿色：该项最终解析为 `Allow`。
- 红色：该项最终解析为 `Forbidden`。

### 规则来源

- `R+`：Room 明确设置为 Allow。
- `R-`：Room 明确设置为 Forbidden。
- `Z+`：Room 没有显式规则，继承 Region Default Allow。
- `Z-`：Room 没有显式规则，继承 Region Default Forbidden。
- `G+`：继续继承到 Global Default Allow。
- `G-`：继续继承到 Global Default Forbidden。
- `--`：没有对应的显式空间规则，或没有对应的调度概率值。

`+ / -` 只表示 Allow / Forbidden；`R / Z / G` 表示这个结果来自哪一层。

## 2. 三列含义

### FamWeather

显示天气 Family，例如 Rain、Fog、Heat、Sand。

当前项目中 Family 的主要职责已经转移到 Region 调度层：

- Region Family `YES / NO`
- `FamWeatherChance`
- 子天气 `YES / NO`
- 子天气 Chance

因此 Hover 面板中的 FamWeather 更适合作为诊断上下文，不应理解为“房间必须额外设置一个 Family 概率”。

### SubWeather

显示具体 Weather，例如：

- LightRain
- HeavyRain
- Fog
- DenseFog
- HeatWave
- SandStorm

房间空间规则真正关心的是具体天气是否允许在这个房间生效。

### DangerType

显示危险天气，例如：

- DeathRain
- IntenseHeat
- DeathSandStorm

这些同样使用房间 Allow / Forbidden 空间规则。

## 3. 百分比的含义

面板中出现的 `80% / 100%` 等数值属于 **Region schedule chance**，不是房间概率。

例如：

```text
SandStorm -- 100%
```

表示：

- `SandStorm` 当前没有显式房间空间规则；
- Region 调度中的 SandStorm chance 为 100%。

它绝不表示“这个房间有 100% 概率触发 SandStorm”。

Weather Zones 当前应保持以下职责划分：

```text
Region 层
├─ FamWeather YES / NO
├─ FamWeatherChance
├─ SubWeather YES / NO
└─ SubWeather Chance

Room 层
└─ 具体天气 Allow / Forbidden
```

房间层不拥有独立天气概率。

## 4. Help 页

Hover 面板右上角提供一个 `?` 标志。

点击后不打开新窗口，而是在同一个 `Hovered Room Weather` 面板内切换到说明页。

说明页内容包括：

```text
Help: how to read this panel
Green = Allow   Red = Forbidden
R+ / R- = explicit Room rule
Z+ / Z- = Region Default rule
G+ / G- = Global Default rule
-- = no explicit rule / no chance
FamWeather = weather family
SubWeather = concrete weather
DangerType = dangerous weather
% = Region schedule chance, NOT room
Rooms only decide Allow / Forbidden
```

在 Help 页中：

- 原来的 Room / FamWeather / SubWeather / DangerType 数据行隐藏；
- 面板本身、标题栏和原生控制保留；
- 右上角按钮变为 `<`，点击后返回数据页；
- 不创建额外浮窗。

Help/Data 状态是 DevUI 会话级状态；关闭再打开 Weather Zones 时仍按当前会话状态恢复。

## 5. 设计原则

这个面板只回答两个问题：

1. 当前悬停房间对具体天气最终是 Allow 还是 Forbidden？
2. 这个结果来自 Room、Region Default 还是 Global Default？

Region 是否启用某个 Family、Family/SubWeather 的调度概率，应以右侧 Region FamWeather 调度表为准。
