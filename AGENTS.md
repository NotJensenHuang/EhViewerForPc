# 项目约定

## 事实来源与适用范围

- 本项目是 EhViewer for PC：Avalonia 12.1.6 + FluentAvaloniaUI 3.0.2 + .NET 11（`net11.0`）的桌面漫画客户端，仅面向 Windows 10 / 11 x64；按最新需求，不再承担其他桌面系统的适配与发布验证。
- 产品与需求以 `PRODUCT.md` 为准，视觉以 `DESIGN.md` 为准，功能与命令以 `README.md` 为准，验证边界以 `docs/REMEDIATION.md`、`docs/TEST_REPORT.md` 等专题记录为准；与当前实现冲突时以代码为准并同步文档，不要照抄历史快照中的旧结论。

## 文件格式与语言

- 代码、XAML、配置和文档统一使用 Windows CRLF (`\r\n`) 行尾，`.gitattributes` 为 `* text=auto` 且 `core.autocrlf=true`，不要混用 LF。
- 文档与注释默认中文；`DESIGN.md`、`tools/imsearch/README.md` 等既有英文内容保留原语言，新增内容优先中文。
- 根目录 README 提供中英双语：`README.md`（首行 `[**English**](README.en.md) | **简体中文**`）与 `README.en.md`（首行 `**English** | [**中文**](README.md)`），两版章节结构、功能描述与命令保持一致；改动功能、命令、参数或数据目录时同步更新两版，不要只改一份。`docs/` 下专题文档仍以中文为主。
- 界面文案是中文硬编码，项目没有本地化资源表；不要引入半套 i18n、不要把可见文案散落进 Core 层，也不要用资源键名当显示文本。新控件沿用现有措辞与命名。

## 工程结构与依赖方向

- 四层职责：`EhViewerForPc.Core`（纯 .NET，无 Avalonia 依赖：格式解析、扫描合并、存储、在线客户端、下载引擎）、`EhViewerForPc.ImageSearch` + `ImageSearch.Host`（以图搜图后端与无窗口诊断入口）、`EhViewerForPc`（Avalonia 应用）。依赖只能自上层指向下层，Core 不得引用 Avalonia 或上层服务。
- 全局服务由 `Services/AppServices.cs` 单一持有（UI 线程创建、退出时 `Dispose`）；设置、进度、作品元数据、扫描缓存、下载队列分别由 `SettingsStore` / `ProgressStore` / `WorkCatalog` / `LibraryCacheStore` / `DownloadManager` 管理，不要在 ViewModel 里 `new` 平行服务或复制一份状态源。
- 包版本集中在 `Directory.Packages.props`（CPM），各 `csproj` 不写 `Version`；`SkiaSharp` 固定 3.119.4 与 `Avalonia.Skia 12.1.6` 匹配，不单独升级到 4.x。
- 仓库不提供 `global.json`，需 .NET 11 SDK（11.0.100+）；跨机器 / CI 复现构建时临时固定 SDK 版本，不要假定会回退到旧版。`dotnet build EhViewerForPc.slnx` 因 wapproj 不可用，MSIX 打包在 Visual Studio 内完成；命令行只构建 / 运行应用工程。

## Avalonia 绑定、导航与 UI 风格

- `AvaloniaUseCompiledBindingsByDefault=true`：绑定写作 `{Binding ...}` 但由编译绑定生成，新增或改动 `UserControl` / `DataTemplate` 必须提供 `x:DataType` 并核对属性路径；禁止 `ReflectionBinding`、运行时 `DataContext` 路径解析等反射式绑定。构建通过不能证明 AOT 产物中路径仍然有效。
- 窗口由构造函数注入创建（`new MainWindow(shell)`），不依赖运行时 XAML 加载器构造；`csproj` 中 `AVLN3001` 的 `NoWarn` 是既有窗口的特例，不是新增加载入口的许可。
- 导航统一走 `FANavigationView` + `FAFrame` + `Services/NavigationPageFactory` 的显式页面实例（AOT 兼容）；不要自建平行页面历史或独立窗口。阅读器只开在主窗口内，沉浸模式隐藏侧栏与标题。
- 样式以 `Styles/Theme.axaml` 的 `App*Brush` / `AppFont` / `AppPagePadding` 等语义资源及 `DESIGN.md` 的设计令牌为准，深浅两套主题同步维护；控件状态交由 `FluentAvaloniaTheme`，不重绘整套原生控件，不用硬编码颜色替代主题资源。
- 在线与本地详情共用 `Controls/GalleryDetailsView.axaml`，在线列表共用 `Styles/OnlineCardTemplates.axaml` 的 `OnlineGalleryCard`；同类界面复用 `Controls/`（`AsyncImage`、`SettingsRow`、`AdaptiveGrid`、`ZoomPanView`）与 `Services/` 的转换器 / 图片管线，不制造平行版本。
- 说明性文本使用 `SelectableTextBlock` 以支持 `Ctrl+C` / `Ctrl+A`，卡片和按钮内文字保持普通 `TextBlock`，避免抢占点击。布局必须验证浅色 / 深色、窄窗口（920 / 740 / 640 DIP 断点）与 540 × 420 DIP 最小尺寸。

## NativeAOT、序列化与平台差异

- Release 默认 `PublishAot=true`（Windows x64 原生发布），`EhViewerMsixBuild=true` 时退回托管自包含；AOT 代码生成只在 publish 管线发生，因此 `packaging/EhViewerForPc.Msix` 的 `PublishAotForMsix` / `InjectAotPayload` target 不要删除或改为 build 阶段执行。
- 所有持久化 JSON 走源生成上下文：Core 侧 `StorageJsonContext`（设置、进度、作品、扫描缓存、下载队列、迁移日志、标签库），视图侧 `ReaderJsonContext`（页面尺寸、UI 审计报告），以图搜图侧 `SearchJsonContext`。**新增持久化根类型必须同时登记 `[JsonSerializable]` 并调用 `JsonFileStore` 的 `JsonTypeInfo<T>` 重载**；禁止 `JsonSerializer.Serialize(object)` 等反射重载。构建成功不能证明 AOT 下序列化可用。
- HTML 解析固定使用 `Online/HtmlLite.cs` 的容错解析器，不引入依赖 CSS 选择器、正则回溯或动态代码生成的第三方 HTML 库。
- Windows 专属代码沿用 csproj 条件或 `OperatingSystem.IsWindows()` 守卫（COM、app.manifest、Windows SDK 导入库、浏览器登录），文件 / 目录打开统一走 `PlatformLauncher`，路径比较遵循 `PathIdentity`；发布验证以 Windows x64 为目标。

## 数据与文件安全（最高优先级）

- 用户导入的资料库**严格只读**：仅以 `FileAccess.Read` 打开，禁止任何写入、改名、移动、删除；`--self-test` 的「路径 + 大小 + mtime」前后快照比对不可绕过。以图搜图后端只在自己的数据目录写索引，绝不改写源页。
- 应用状态写入 `AppPaths` 数据目录（Windows 为 `%LocalAppData%\EhViewerForPc`），下载根目录与之分离。`AppPaths` 沿用旧 `EhViewerReader` 数据目录、单实例 Mutex 保留旧名称，是有意的升级兼容设计，不要「顺手清理」。
- 持久化写入统一 `JsonFileStore.Save`（临时文件 → `flushToDisk` → 原子替换）与 `DeferredSaveQueue`，不在 UI 线程同步写大文件；损坏文件走 `TryMoveCorruptFile` 回退而非静默覆盖。
- 设置保存是「合并被修改字段 + 修订号 + UI 线程快照」（`AppServices.TrySaveSettings`），不要在关闭时把启动时缓存的编辑器整体写回，也不要丢失运行时新增的数据源。
- 下载目录变更（不搬迁 / 搬迁）、断点续传、缺页副本、任务 / 归档 / 数据源路径同步、取消与崩溃恢复统一由 `DownloadManager` / `DownloadRootMover` 处理；新增路径变更必须复用迁移日志与回滚，不要直接改写路径字符串。删除下载文件需行内二次确认并等待写入结束。

## 在线链路（站点结构易变）

- 解析集中于 `Online/EhParser.cs` + `EhUrls.cs` + `HtmlLite.cs`。站点改版先用 `--online-probe` 区分网络 / 账号 / 解析问题，再改解析器，并把真实页面片段固化为 `tests/EhViewerForPc.Core.Tests/OnlineParserTests.cs` 等 fixture 回归；不能只靠调整正则自证。
- 站点约定不要凭记忆改写：`/s/{hash}/{gid}-{page}` 视图 URL、sprite 缩略图、`#gdd` 元数据行、`#taglist` 命名空间单元格、`favcat=favdel` 取消收藏、`fullimg.php` 原图、`nl` 仅作图床重试键、`api.php` 的 `rategallery` / `votecomment`、`archiver.php` 的 `or` 令牌与 `hathdl_xres`。新增请求复用 `EhClient` / `EhHttp`（Cookie、代理、DoH、hosts、连接测试），不要另起 `HttpClient`。
- 登录只持久化 Cookie，不保存密码；Cloudflare 挑战无法用请求头绕过，网页登录走 Edge / Chrome + DevTools 协议，并保留手动粘贴 Cookie 兜底，不要「优化」回表单密码登录。
- 图片尺寸（780 / 980 / 1280 / 1600 / 2400px 或原图）按请求时配置隔离缓存键，下载任务记录入队时的画质；视图页请求串行限速（260 ms），不要为提高速度取消节流。

## 异步、线程与生命周期

- UI 属性、绑定集合与命令通知必须在 `Dispatcher.UIThread` 发布；扫描、下载、解码、索引、缓存清理等后台线程不得直接改 ViewModel 状态。
- 取消等待不等于底层工作已停止：下载写入、目录搬迁、解码、动画帧池、缓存清理都要处理在途任务、迟到回调与临时文件；退出顺序固定为 `shell.Dispose()` → `AppServices.Dispose()`（保存进度 → 下载 / 索引 / 延迟写队列 / 网络 / 封面 / 解码器释放），不可打乱。
- 清理应幂等，单项失败不能跳过其他资源；后台任务必须有明确的异常观察与收尾责任，不产生未观察的 `Task`。构造函数保持轻量，服务启停显式管理。
- 图像内存由 `ImageMemoryBudget` / `ImageBudget` 统一核算（展示位图、预取、原尺寸解码缓冲、动画画布）；`ArrayPool` 租约必须可靠归还，跨线程共享后各自复制为独占位图；虚拟化列表（`ItemsRepeater`）不要改成一次性实例化。

## 构建、测试与验证证据

- 常用命令：
  ```powershell
  dotnet build src/EhViewerForPc -c Release
  dotnet test tests/EhViewerForPc.Core.Tests -c Debug
  dotnet test tests/EhViewerForPc.ImageSearch.Tests -c Debug
  dotnet test tests/EhViewerForPc.ImageSearch.Ui.Tests -c Debug -p:PublishAot=false
  dotnet publish src/EhViewerForPc -c Release -r win-x64 -p:PublishAot=true -o artifacts/win-x64-aot
  ```
  以图搜图后端另用 `tools/imsearch/Build-Backend.ps1` / `Test-Backend.ps1`（含 `-Aot` 变体）。引用主程序的测试工程需串行运行（Avalonia XAML 编译共享输出文件）。
- 无人值守入口以 `StartupOptions.Parse` 为准：`--online-probe`、`--login-probe`、`--self-test`（`--skip-snapshot` / `--deep-probe`）、`--ui-smoke`、`--ui-audit`、`--reader-regression`、`--navigation-regression`、`--anim-probe`（`--dump-frames`）、`--probe-file`、`--image-search-probe-root`、`--bookmark-probe-root`、`--verbose`。分派在 `Program.cs` / `App.axaml.cs`，其中 `--reader-regression` 由 `UiSmokeService` 内部消费；新增模式须同时注册参数、接入分派并同步 README，且保持无弹窗、不触发真实账号写入。
- 测试数据隔离：一律 `--data-dir` 指向已忽略的 `.qa/`，UI 冒烟使用合成资料库并阻断站点访问（`--ui-smoke` 自动隔离数据目录）；真实库只读，测试不得写入。烟测必须走真实代码路径，不能用测试里复制一份逻辑自证。
- Debug 通过不能证明 Release / AOT / 剪裁产物可用：涉及绑定、序列化、反射、原生资源或 UI 的改动，必须使用全新输出目录执行 AOT publish 并实际启动验证，不得拿既有 exe 或旧 `artifacts/` 当证据。站点在线结论需注明来自固定 fixture 还是真实站点；未执行的设备与真实桌面验证要明确列出。
- 性能与内存结论注明环境、场景、样本量与测量边界；未执行设备 / UI 验证要明确列出，没有测量不得宣称零分配或具体倍数收益。

## 文档与变更记录

- 每次功能修改（新功能、行为变更、默认值调整、面向用户的修复）在 `docs/CHANGELOG.md` 顶部追加一条中文记录（首次追加时创建该文件），格式 `## 日期 标题` + 每个改动一行（文件 + 做了什么），仅在对 review 有影响时补充兼容性与验证说明；纯注释、格式化、构建脚本等不改变运行行为的修改不记录。
- 重大架构决策与实测结论写入 `docs/<主题>-<日期>.md`，原始日志、报告、合成媒体放已忽略的 `.qa/`，文档中保留可复现命令与证据位置；被后续决策取代的旧文档在文首加说明并链接新文档，不静默删除历史记录。
- 改动到功能、命令、系统要求或设计令牌时同步更新 `README.md` / `README.en.md` / `PRODUCT.md` / `DESIGN.md`；`docs/TEST_REPORT.md` 等历史报告标注其对应版本，不作为当前行为的依据。

## 默认代码质量与审查标准

后续功能实现、修复、重构和 code review 默认执行以下高标准，无需用户重复提醒。审查必须覆盖功能正确性、重构方案设计、性能、内存分配和代码可读性；按风险投入验证，不以「能编译」或「已拆分类」作为完成标准。

- **功能正确性**：沿真实调用链核对行为与兼容性，覆盖正常流程、空状态、失败、取消、重试、并发及启动中退出。多入口命令统一可用条件与执行守卫；显式暂停 / 继续 / 重试、删除确认等用户意图不能因去重或单飞机制被静默丢弃。下载、扫描、索引等批处理要覆盖失败明细、部分成功与队列恢复。
- **重构设计**：先明确职责、依赖方向、状态来源和资源所有权，再确定拆分边界。保持 MVVM（`CommunityToolkit.Mvvm` 的 `ObservableObject` / `[RelayCommand]`）与单一状态源；避免只搬移代码却继续隐藏依赖或副作用。构造函数保持轻量，服务启动与停止显式管理；优先构造注入，必要的延迟解析集中在明确边界并说明原因。
- **异步与生命周期**：UI 属性、绑定集合和命令通知必须在 UI 线程发布。取消等待不等于底层工作已停止（含 SkiaSharp 解码、SQLite 连接、下载写入与临时文件）；退出需处理在途任务、迟到回调、订阅解绑和依赖释放顺序。清理应幂等，后台任务必须有明确的异常观察与收尾责任。
- **性能**：检查 UI 线程阻塞、重复 I/O、扫描复杂度、事件风暴、队列背压和不必要的启动工作。关闭功能时核对其后台活动是否也停止。区分冷路径与热路径，优先解决可感知延迟和规模增长问题（6,000+ 画廊 / 24 万页量级），不为微优化引入不必要的复杂度。
- **内存分配**：检查热路径的闭包、装箱、LINQ / 匿名对象、字符串和集合复制，以及定时器、CTS、事件订阅、原生句柄和缓冲区的生命周期。Span / Memory / ArrayPool 仅在收益明确且所有权、线程与异步边界安全时使用；池化缓冲区必须可靠归还。区分短期分配、长期保留与实际泄漏，没有测量不得宣称零分配或具体收益比例。
- **可读性**：命名准确、控制流清楚、方法职责集中；避免在一行压缩多个状态变更或清理操作。注释解释约束和原因，并随实现同步更新；移除迁移后无效的状态、重复守卫、过时注释和无用依赖。
- **验证证据**：运行与改动风险相称的构建、测试、探针或最小复现。测试桩应保留真实边界的异步时序、线程切换、属性 / 集合通知与失败行为；涉及 Avalonia UI、NativeAOT / 剪裁、SkiaSharp / SQLite 或站点在线链路时，桩测试通过不能代替集成验证。性能结论注明场景与测量依据，未执行的设备 / UI 验证明确列出。
- **审查输出**：按严重性报告可定位、可复现或有完整调用链证据的问题，给出文件位置、触发条件、影响和修复方向。区分本次引入的回归、既有问题与设计建议；不得为了覆盖维度而凑问题。说明已执行验证及剩余盲区。仅请求 review 时默认交付审查结果，生产代码修复按用户授权范围执行。
