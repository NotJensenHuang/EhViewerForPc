# 变更记录

## 2026-11-14 解除索引并行瓶颈并扩展训练与分桶
- `ImageSearch/IndexBuildOptions.cs`、`OrderedImagePipeline.cs`：默认按逻辑处理器数减 2 使用 1–32 worker，工作区随 CPU 与 GC 内存上限扩展到 128–1024 MiB，保留有界队列与原页序提交。
- `Services/ImageMemoryBudget.cs`、`LocalImageSearchService.Pipeline.cs`：索引与前台位图分别限额、统一记账，移除共享 128 MiB 对较大页图的隐性串行限制；进度显示并行度，诊断记录活动页峰值和配额等待时间。
- `ImageSearch/BinaryKModes.cs`、`ImageSearchEngine.cs`、`Services/LocalImageSearchService.cs`：中心分配、位直方图和描述子查桶使用有界并行，保留随机序列、平票规则、向量 ID 和发布文件内容。
- `ImageSearch/ImageImporter.cs`、`ImageSearch.Host/Program.cs`：目录导入随资源自动扩容，`add` / `train` / `build` 支持 `--workers 1..32`；保留 Tar 的单 payload 约束。
- `tests/`：覆盖 12 worker 并发、训练与发布文件等价、运行中取消、前后台内存隔离和等待时退出；性能与验证证据见并行扩容专题。

## 2026-11-14 实现索引 CPU 并行、批量写入与 SIMD
- `ImageSearch/OrderedImagePipeline.cs`、`ImageWorkBudget.cs`、`Services/LocalImageSearchService.Pipeline.cs`：有界提取、原页序消费、共享图像内存预留、可取消背压及完整收尾；默认自动选择 1–4 个 worker。
- `ImageSearch/ImageDatabase.ImportWriter.cs`、`SharedFeatureCache.cs`：复用连接与预编译语句，每 10 页按共享特征 / 后端 / 清单顺序提交检查点，取消保存健康部分，失败回滚当前批。
- `Services/LocalImageSearchService.cs`：无变化更新保留发布快照，跳过复制 / 提取 / 训练 / 合并；记录分阶段诊断，不改变提取配置或数据格式。
- `ImageSearch/ImageKernels.cs`、`GrayImage.cs`、`OrbExtractor.cs`：灰度和高斯模糊增加 AVX2 / SSE2 与标量回退，默认 x64 AOT 使用 SSE2；保持舍入、透明度和特征兼容。
- `ImageSearch/ImageImporter.cs`、`ImageSearch.Host/Program.cs`：独立目录导入复用有界管线，支持 1–6 worker 和 1–10 条写入批次；归档源与可写路径强制分离，阻止报告覆盖输入 Tar。
- `Views/BookmarksView.axaml`：未选择书签时保留编辑区域，禁用输入与操作，选中后启用。
- `tests/`：增加 SIMD 等价、乱序背压、失败与内存等待取消、批次回滚、无变化更新及书签禁用状态回归。

## 2026-11-13 重设计索引存储并恢复中断任务
- `ImageSearch/SharedFeatureCache.cs`、`ImageDatabase.cs`：将多代重复描述子迁入单份按提取配置 / 内容哈希去重的 SQLite 特征库；按批提交迁移后再删除旧副本，清理无引用内容。
- `ImageSearch/ImmutableFileLinks.cs`、`ImageSearchEngine.cs`、`DiskInvertedIndex.cs`：增量快照优先硬链接不可变文件，安全替换新文件；保留在途读取，清理崩溃残留段，删除后再加入的内容正确重入索引。
- `Services/LocalImageSearchService.*`、`Core/Models/ImageIndexBuildCheckpoint.cs`、`StorageJsonContext.cs`：持久化任务与阶段，复用已提交页面和索引段；兼容无检查点的旧版未完成目录，发布后仅保留当前 / 任务 / 在用快照，备指针不再强制保留上一整代。
- `Services/LocalImageSearchService.Checkpoints.cs`、`LocalImageSearchService.Reads.cs`：协调检查点读取与原子保存，退出等待在途状态 / 搜索文件读取，避免迟到句柄占用。
- `ViewModels/ShellViewModel.cs`、`ImageSearchViewModel.cs`、`Views/ImageSearchPage.axaml`：图库完成初始加载后自动接续，主动取消等待手动继续，关闭页面不取消后台任务。
- `tests/`、`Services/LocalImageSearchAppProbe.cs`：覆盖重启恢复、显式暂停、旧缓存迁移、去重回收、移除后重加与旧文件在读时发布。

## 2026-11-13 合并在线入口并改为滚动加载
- `Core/Online/EhParser.cs`：补齐列表评分 sprite 解析，使用匿名首页真实片段回归；在线探针报告包含评分。
- `Styles/OnlineCardTemplates.axaml`、`Styles/Theme.axaml`：标题下独立显示评分，将本地标识移出封面并使用双主题不透明高对比配色。
- `Views/LibraryPage.axaml`：阅读按钮添加不透明底板，保留原生悬停、按下和禁用状态。
- `ViewModels/ShellViewModel.cs`、`OnlineFeedViewModel`、`Views/OnlineFeedPage`：参照 e7da9e1 之前的结构，把订阅和排行合并回在线浏览标签，移除独立导航与平行页面状态。
- `ViewModels/OnlineFeedViewModel.cs`、`Views/OnlineFeedPage.axaml.cs`：取消上下页按钮，滚动接近底部时追加服务器下一页；增加 GID 去重、单飞、失败重试、旧请求取消、循环续页保护和连续空增量的自动加载边界。
- `tests/`：覆盖真实评分片段、卡片颜色对比度、滚动触发与虚拟化、重试、取消及标签 / 书签兼容。

## 2026-11-13 修复缺页阅读、书签交互与索引后台任务
- `Services/ReaderSources.cs`、`ViewModels/ReaderViewModel.cs`、`Views/ReaderPage.axaml`：按原始页号补齐缺页占位，三种阅读模式保持总页数和进度。
- `Views/MainWindow.axaml`、`Views/MainWindow.axaml.cs`：修正适应方式字号和窄窗口按钮宽度，将更多菜单锚定到实际按钮。
- `Views/BookmarksView.axaml`、`ViewModels/ShellViewModel.cs`、`ViewModels/OnlineFeedViewModel.Search.cs`：书签管理改用主窗口对话框，打开书签不自动展开高级搜索。
- `Styles/OnlineCardTemplates.axaml`、`ViewModels/OnlineSupport.cs`：在线列表显示完整 / 缺页本地副本标识，随本地扫描更新。
- `Services/LocalImageSearchService.cs`、`ViewModels/ImageSearchViewModel.cs`、`Views/ImageSearchPage.axaml`：索引由应用服务持有，离页继续、重入恢复状态、支持主动取消并在退出时等待写入收尾；修正页头操作对齐。
- `tests/EhViewerForPc.ImageSearch.Ui.Tests`：增加真实缺页序列、页面释放后建库、在线状态更新及双主题多尺寸无窗口回归。
