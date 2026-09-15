# DevTool Phase 4 — 大数据 UI 虚拟化 / 可见区计算

第四阶段实现已完成。本阶段只处理超大数据集下的 UI 渲染成本、可见区计算和相关 retained projection 生命周期，不进入第三方兼容层，也不进行最终架构合并。

## 核心原则

- 固定行高的大列表只提交当前可见行，避免总条目数直接决定逐帧 ImGui 提交成本。
- 搜索 / 过滤结果优先 retained；源 snapshot 引用或搜索条件没有变化时，不重新构造过滤结果。
- 分组列表先构建连续 run，再在 run 内虚拟化，保证来源标题、分类标题和现有交互语义不变。
- 2D Map Canvas 不使用一维列表 clipper，而是按当前视口矩形做 room bounds 相交测试，只绘制可见房间。
- 可折叠卡片、可变高度行和天然短列表不强行套固定行高 clipper，避免滚动高度漂移和 UI 语义退化。

## 已完成

- `DevToolListClipper`：统一封装 Dear ImGui 原生 `ImGuiListClipper` 生命周期，避免各 View 重复处理 unsafe/native lifetime。
- Object Library：按 Source / Category run 保留投影，只绘制当前可见对象类型。
- Object Scene：按 Source / Category run 保留投影，只绘制当前可见场景对象；多选、Shift 范围选择和 Ctrl 切换语义保持不变。
- Map Browser：搜索 / Layer 过滤结果 retained，并对结果列表虚拟化。
- Map Canvas：建立 RoomIndex 索引；位置同步避免重复线性查找；房间绘制按当前画布视口做 bounds culling；命中测试只检查本帧可见房间。
- Relationship Matrix：搜索与“仅显示覆盖项”结果 retained，矩阵行使用 clipper 提交。
- Sound Scene：场景声音列表使用 clipper，只提交可见声音。
- Sound Library：搜索投影 retained；按资源来源构建连续 source run，并在每个 run 内只提交可见 sample；来源标题语义保持不变。
- Trigger Library：搜索结果改为 retained 的 source-index 投影，只提交可见类型。
- Trigger Scene：场景触发器列表使用 clipper，只提交可见触发器。
- Retained lifecycle：DevTool 生命周期结束时统一释放 Object / Map / Relationship / Sound / Trigger 等第四阶段新增投影和缓存。

## 有意不做固定行高虚拟化的区域

- Sound Group 卡片：可折叠且展开高度由组内资源数决定，不满足固定行高前提。
- Trigger 的 Event Type / Karma / Entrance / Slugcat 等选项：数据规模天然较小，虚拟化收益低于复杂度成本。
- World Explorer 的 Connection / Issue：条目含条件性详情文本，属于可变高度行；第四阶段不以牺牲滚动正确性为代价强套 `ImGuiListClipper`。
- 其他短列表：继续直接绘制，避免为了虚拟化而虚拟化。

## 验收结论

- 空列表与无匹配结果均保留原有空状态。
- 过滤、选择、多选、创建、Tooltip、来源标题、工作组目标等既有交互路径没有改变数据模型职责。
- 大型固定行列表的 ImGui 提交量现在随可见行数增长；过滤投影只在源 snapshot / 查询条件变化时重建。
- Map Canvas 的绘制和命中测试已从全房间提交收敛到当前视口可见房间。
- Revision / Dirty System、History、Input Router、后端 command queue 的职责边界未被第四阶段改写。

## 验证说明

当前仓库没有为本 PR head 产生 GitHub Actions / status check，因此第四阶段收口只声明代码实现与静态 diff 检查完成，不伪造“CI 已通过”或“游戏内已运行验证”。实际 DLL 编译和 Rain World 内交互回归仍应由本地构建完成。
