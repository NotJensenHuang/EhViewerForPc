# 变更记录

## 2026-11-15 批量复用索引清单与本地副本删除

- `LocalImageSearchService.Reuse.cs`、`Pipeline.cs`：最多 4096 条窗口批量匹配旧清单，只将新增、变更和失败页送入提取；失败元数据不再每 10 页强制刷盘，进度显示失败数。
- `LocalImageSearchService.Plan.cs`：目录枚举核对大小、mtime 与链接属性，发布前仍处理变更与删除，保留现有索引和未完成任务兼容。
- `DownloadsViewModel.cs`：完成任务只在「已完成」归档展示，处理迟到进度及页面重建。
- `DownloadManager.LocalDelete.cs`、`AppServices.LocalDelete.cs`、`WindowsRecycleBin.cs`：选定副本回收前阻止新写入并等待下载收尾；同步队列、缓存与本地匹配表，保留其他版本及作品级状态；已移出队列的活跃写入仍受跟踪，最后一个副本删除后显示空状态。
- `LocalCopyDeletionView*`、`LibraryPage*`、`VersionsPage*`：卡片右键及版本行新增删除入口，共用完整路径行内二次确认，失败保留重试。
- 验证与测量边界见 `docs/索引批量复用与本地删除-2026-11-15.md`。

## 2026-11-15 补齐托盘平台注册、设置入口与增量复用批次

- `DesktopTrayController.cs`：托盘加入 Application 的 `TrayIcons` 才启动原生创建，释放时移除自身；失去注册后不再隐藏主窗。
- `SettingsViewModel.cs`、`SettingsPage.axaml`：分类改为「界面与阅读」，分开「窗口与外观」和「默认阅读」，使后台运行开关可查找。
- `MainWindow.axaml(.cs)`：顶部改用标准原生按钮，底板与 ComboBox 统一 32 DIP，保留窄窗图标模式和重复点击后的选中状态。
- `LocalImageSearchService.Pipeline.cs`：纯复用元数据最多 1024 条提交，新特征仍按原小批上限；进度区分复用和新提取，保留既有索引及断点。
- `tests/`：补充托盘注册和平台创建 / 释放、按分类名定位设置、144 页增量提交次数与断点恢复，避免仅检查对象属性的假通过。验证见 `docs/托盘注册与增量更新修复-2026-11-15.md`。

## 2026-11-15 托盘后台运行、彩色预览与补页阅读修复

- `DesktopTrayController.cs`、`App.axaml.cs`、`AppSettings.cs`、`SettingsViewModel.cs`、`SettingsPage.axaml`：加入 Windows 托盘和默认关闭到后台的可调设置，显示原窗口 / 显式退出；恢复不重复初始化，实际退出后再按原顺序释放服务。
- `QueryImagePreview.cs`、`ImageSearchViewModel.cs`、`ImageDecoder.cs`：预览保留颜色、透明度和 EXIF 方向，独立于检索灰度流程，沿用输入预算与原始裁剪坐标。
- `AppServices.LocalDownloads.cs`、`DownloadManager.Repair*.cs`、`DownloadsViewModel.cs`：无变化的副本核对不发布索引更新，空批次和重复入队不重复触发下载队列；暂停 / 运行状态变化不重扫未变化的目录。
- `MainWindow.axaml`：阅读工具栏按钮与适应方式 ComboBox 统一为 32 DIP 高，保留窄窗口 36 DIP 图标宽度。
- `DownloadManager.PageRetry.cs`、`EhClient.cs`、`ReaderSources.cs`、`ReaderViewModel.cs`：本地重试只重新下载选中页，绕过本地图片和 URL 缓存，等待同目录在途写入并原子替换；并发合并，失败保留原页，退出取消并等待收尾。
- `ImageRequest.cs`、`ContinuousPageItem.cs`：本地请求键带文件版本，重试后更新页候选、缺页占位与尺寸；普通目录无链接时只重新载入并显示原因。
- `tests/`、双语 README、`PRODUCT.md`、`DESIGN.md`：补充重复补页、三种模式、失败 / 并发 / 退出与工具栏主题及断点回归；证据见 `docs/托盘与阅读检索界面修复-2026-11-15.md`。

## 2026-11-15 CPU 检索、旧缓存迁移、补页等待进度与普通图片目录

- `ImageSearch/DiskInvertedIndex.cs`、`BinaryDescriptor.cs`、`ImageSearchEngine.cs`、`SearchOptions.cs`：按桶顺序整块读取，Windows x64 直接解压到顺序布局记录，保留校验与取消；默认最多 8 个读取线程，移除逐向量迭代 / 计时 / 原子计数开销，不改变索引格式。
- `ImageSearch.Host/Program.cs`、`tools/imsearch/`：CLI 支持 `--threads 1..8`，默认值与应用共用，原生探针比较八线程完整结果。按用户选择不引入 GPU 检索，原有建库 GPU 加速不变。
- `LibraryCacheStore.cs`、`LibraryScanner.cs`：保留 schema 3 条目，仅重新解析受空文件 / 旧页数语义影响的条目，重扫输出当前版本，防止未扫描旧条目提前失去语义标记。
- `DownloadManager.Repair*.cs`、`DownloadManager.cs`：入队与获取网络清单前核对已有有效页，等待阶段即显示 51/53；启动后台修正旧任务，服务负责取消和退出收尾，持久化失败恢复原进度。
- `PageFileParser*.cs`、`GalleryInfo.cs`、`GalleryPage.cs`、`LibraryCache.cs`、`LibraryScanner.cs`：识别普通图片目录，以目录为标题、自然排序、首图封面；保留文件名模式到缓存，扫描不生成 `.ehviewer` / `.thumb`，编号来源仍保留真实缺页。
- `tests/`、双语 README、产品与设计文档：覆盖跨块 Top-K、1 / 2 / 4 / 8 线程、旧缓存、等待进度、自然目录阅读与真实索引；证据见 `docs/CPU检索与本地目录兼容-2026-11-15.md`。

## 2026-11-14 实际索引裁剪搜索超时与缺页误分类修复

- `ImageSearch/DiskInvertedIndex.cs`、`ImageSearchEngine.cs`：固定长度描述子批量解压，保留记录顺序、边界校验和既有格式；查询载入支持校验进度与取消。
- `LocalImageSearchService.Search.cs`、`LocalImageSearchService.Reads.cs`、`ImageSearchViewModel.cs`：按冻结代复用已校验索引，串行管理查询、取消和退出；新代 / 文件变化重新校验，首次载入与检索时限分开，旧清单一次顺序匹配结果。
- `Core/Format/PageFileParser.cs`、`GalleryPage.cs`、`LibraryCache.cs`、`LibraryScanner.cs`：整页为空计入缺页，同页非空候选和旧页数警告不误判；扫描缓存升级并自动重新生成，图片索引无需重建。
- `AppServices.LocalDownloads.cs`、`MainViewModel*.cs`、`DownloadsViewModel.cs`、详情及版本 ViewModel：单项 / 批量补页先查磁盘，已完整更新分类并跳过；清除完成任务后仍完成库更新，补页刷新独立于普通下载自动入库开关。
- `tests/`、双语 README、`PRODUCT.md`、`DESIGN.md`：补充空副本、完整页不请求图片、热缓存失效和真实裁剪验证；证据见 `docs/实际索引裁剪检索与补页修复-2026-11-14.md`。

## 2026-11-14 下载增量刷新、实时速度与批量补页

- `MainViewModel.Downloads.cs`、`MainViewModel.cs`、`GalleryItemViewModel.cs`：落盘后仅重组受影响作品，原位维护可见集合，保持已有卡片位置、未变化封面与进度环；避免 Move 清空数据上下文造成数值归零，目录扫描及页数核对在后台完成。
- `Core/Download/DownloadTransferMeter.cs`、`DownloadManager.Progress.cs`、`DownloadManager.cs`、`DownloadModels.cs`：按实际图片接收字节统计约两秒滑动速度，500 ms 发布，缓存不计入；采样任务在暂停 / 完成 / 退出时取消并等待，速度字段不持久化。
- `DownloadsViewModel.cs`、`OnlineSupport.cs`、`DownloadsPage.axaml`、`LibraryPage.axaml`：下载队列、卡片显示当前速度，下载页汇总速度；本地库新增当前列表所有副本的「补齐缺页」及失败明细。
- `Core/Download/DownloadManager.Repair.Batch.cs`、`AppServices.LocalDownloads.cs`：批量请求按原路径去重，复用或继续已有任务，一次保存和通知；部分校验失败独立记录，持久化失败或提交前取消回滚。
- `GalleryDetailsPage.axaml*`、`VersionsViewModel.cs`、`VersionsPage.axaml*`、`MainWindow.axaml.cs`：详情直达版本管理，逐版本后台检测缺页 / 0 字节 / 预期页数，支持单独重检，未知总数与离线副本分别说明。
- `tests/`、`README.md`、`README.en.md`、`PRODUCT.md`、`DESIGN.md`：补充真实流式传输、进度控件保留、批量去重 / 回滚和版本检测回归；验证见 `docs/下载增量刷新与批量补页-2026-11-14.md`。
- `tests/EhViewerForPc.ImageSearch.Tests/AlgorithmTests.cs`：受限线程池下旧取消测试可能在训练结束前未触发定时回调，改用独立线程触发和收尾，保留原取消断言；训练实现未改变。

## 2026-11-14 构建阶段进度、原路径补页与固定阅读输入
- `ImageSearch/IndexBuildProgress.cs`、`ImageSearchEngine.cs`、`DiskInvertedIndex.cs`、`CheckedFile.cs`：新增特征分配、分段、合并和校验字节的实际阶段进度，限频通知并支持校验中取消；保持既有索引格式，已有索引在单次构建中只校验一次。
- `Services/LocalImageSearchService*`、`ImageSearchViewModel.cs`、`ImageSearchPage.axaml`：后台服务保存阶段计数，页面显示当前阶段确定进度并在重开后恢复绑定，无总量阶段使用活动指示。
- `Core/Download/DownloadManager.Repair.cs`、`DownloadManager.cs`、`DownloadModels.cs`：用户主动补页使用副本独立任务键和精确原目录，持久化、暂停、继续沿用下载服务；保留原图片和元数据，同名冲突报告失败页，禁止补页任务删除原目录。
- `Services/AppServices.LocalDownloads.cs`、`ViewModels/`、`Views/LibraryPage.axaml`、`GalleryDetailsPage.axaml`、`VersionsPage.axaml`：卡片、详情和版本行提供继续下载入口，完成后定向更新原数据源，不修改全局下载目录。
- `ReaderViewModel.cs`、`MainWindow.axaml`、`ReaderPage.axaml*`：菜单同时显示左右单选项，仅控制横向连续页面排列；方向键、滚轮及单页点击区域不随排列反向。
- `tests/`、`README.md`、`README.en.md`、`PRODUCT.md`、`DESIGN.md`、`AGENTS.md`：增加真实下载边界、进度、取消恢复及输入回归；按用户最新确认解除本地库全局只读约束，暂不新增编辑 / 删除入口；验证见 `docs/构建进度与原路径补页-2026-11-14.md`。

## 2026-11-14 修复页图提取的面积缩放热点
- `ImageSearch/GrayImage.cs`：面积缩小时预计算横向权重、复用逐行纵向权重，消除每个源像素的重复边界计算；保持 double 乘法 / 累加顺序和舍入，线性 / 高阶插值与极端轴长保留原路径。
- `tests/EhViewerForPc.ImageSearch.Tests/ResizeCompatibilityTests.cs`：固定旧版像素与规范化 PNG 内容哈希金样，覆盖单轴平票舍入、并发及输出所有权，保护已有特征身份。
- `README.md`、`README.en.md`、`PRODUCT.md`、`docs/页图提取缩放热点优化-2026-11-14.md`：明确此次针对提取阶段 CPU 热点，记录新旧完整 AOT 后端对照、真实调用链与性能边界。

## 2026-11-14 修复 GPU 训练的小批次同步瓶颈
- `ImageSearch/ComputeSharpAccelerator.cs`：训练描述子容量从 8,192 提高到 131,072，仍按每 dispatch 最多 8M 次比较缩小批次；合并描述子 / 中心上传与归属 / 距离回读，减少 GPU 空档与 CPU 等待。
- `tests/EhViewerForPc.ImageSearch.Tests/GpuTests.cs`：覆盖大批次后一条尾数据、4,096 中心比较上限、空输入、平票及结果缓冲边界；保持已有索引与训练输出兼容。
- `README.md`、`README.en.md`、`PRODUCT.md`、`docs/GPU训练批次与同步开销-2026-11-14.md`：记录训练阶段诊断、CPU / GPU 等价对照与实际 AOT 测量，明确 GPU 调用墙钟时间不等于硬件忙碌率。

## 2026-11-14 持久化待办恢复并接入 ComputeSharp
- `Services/LocalImageSearchService.Plan.cs`、`LocalImageSearchService.cs`、`Core/Models/ImageIndexBuildCheckpoint.cs`：将候选页、签名与完成状态保存在独立任务表，恢复直接读取待办，训练 / 构建不回退全量页扫描；源目录核对按画廊块提交接续，旧任务首次补建计划并复用既有特征。
- `Services/LocalImageSearchService.Pipeline.cs`、`BackgroundIoScope.cs`、`ImageSearch/GrayImage.cs`：页状态与清单同一事务提交，恢复重试失败页，空计划允许重新发现后续页图；磁盘读取与 CPU 解码分离、限一路读源图，同步源 I/O 与检查点提交使用 Windows 后台优先级。
- `ImageSearch/ComputeSharpAccelerator.cs`、`IImageComputeAccelerator.cs`、`OrbExtractor.cs`、`BinaryKModes.cs`：使用 C# shader / DirectX 12 加速 FAST 评分与大批量哈明距离，保留 cell 抑制边界、平票规则、CPU 分布 / 描述子 / HNSW 逻辑，设备失败完整回退 CPU。
- `Services/LocalImageSearchService.Compute.cs`、`Directory.Packages.props`：由应用服务延迟持有 ComputeSharp 3.2.0，GPU 命令有界串行、等待完成后回收池化缓冲；主机工作区统一记账，任务收尾释放，CPU 前后处理仍有界并行。
- `tests/`：覆盖恢复首批不扫描源目录、训练 / 构建阶段保持、核对分块接续、相同元数据失败重试、GPU / CPU 特征与发布文件一致、失败回退、取消和共享设备释放。
- `README.md`、`README.en.md`、`PRODUCT.md`、`tools/imsearch/README.md`：同步 Windows GPU、兼容迁移和任务数据库说明；实测与设备覆盖边界见 ComputeSharp 实施报告。

## 2026-11-14 收敛 Windows 支持范围并记录 GPU 与恢复方案
- `README.md`、`README.en.md`、`PRODUCT.md`、`AGENTS.md`：按最新需求仅面向 Windows 10 / 11 x64，移除其他平台支持承诺与发布命令，中英文命令同步为 PowerShell。
- `docs/ComputeSharp与索引快速恢复方案-2026-11-14.md`：记录真实任务规模、恢复重枚举复现、纯 C# ComputeSharp AOT 小原型与分阶段实施方案；GPU 方案尚未接入主程序，未宣称实际建库提速。

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
