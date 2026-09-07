# Documentation Organization

`docs/` 只允许保留两个一级目录：`Task/` 与 `Discussion/`。

## Task

`docs/Task/` 只存放正式任务书、被否决但需要保留的任务记录，以及可直接交给 Codex / Agent 执行的任务规格。

后续新增任务书必须直接放入 `docs/Task/`，不得再放回 `src/`，也不得为某个模块额外建立任务书子目录。

文件名应携带任务编号或模块前缀，以便在扁平目录中区分归属。

## Discussion

`docs/Discussion/` 存放讨论总表、设计讨论稿、讨论备份、实现状态、完成记录、讨论索引、设计说明和其他非执行型开发文档。

后续新增讨论文件必须直接放入 `docs/Discussion/`，不得再放回 `src/`，也不得额外建立 Discussion 子目录。

## Hard Rule

最终目录结构必须保持：

```text
docs/
├─ Task/
└─ Discussion/
```

除这两个目录外，不再在 `docs/` 下建立其他文档分类目录，也不在 `docs/` 根目录直接放文档。
