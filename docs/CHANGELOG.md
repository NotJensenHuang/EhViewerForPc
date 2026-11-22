# 变更记录

## 2026-11-22 加快本地以图搜图建库与增量更新

- `PageFileParser.cs` 与 `LocalImageSearchService.Plan.cs` 直接从目录条目生成索引页，按画廊批量写入或核对页计划。
- `LocalImageSearchService.Pipeline.cs` 每批比较最多 4,096 页，从已发布清单判定整库是否可直接复用。
- `OrbExtractor.cs` 在 CPU 上一次计算每格的强弱角点，CPU 与 GPU 路径均按扫描顺序直接收集结果。`GrayImage.cs` 加快两条路径共用的金字塔缩放。机械硬盘、CPU 与 GPU 的实测见[索引性能验证](索引枚举与特征提取-2026-11-22.md)。

## 2026-11-21 自动代理模式

- `AppSettings.cs` 和 `SettingsViewModel.cs` 将「自动」设为默认选项，保留已有配置保存的代理模式与图片代理开关。
- `VpnConnection.cs` 和 `EhHttp.cs` 根据 Windows VPN 或常见 TUN 网卡的连接状态，为新请求选择跟随系统或站点直连。
- `UpstreamHostsService.cs` 允许自动模式下的站点直连复用上游地址缓存，`OnlineProbeService.cs` 报告生效的代理模式。见[自动代理模式验证](站点直连与封面验证-2026-11-21.md#自动代理模式验证)。

## 2026-11-21 站点直连自动同步原版地址

- `UpstreamHostsService.cs` 和 `UpstreamHostsParser.cs` 从原版 GitHub 默认分支后台检查 hosts，按七个站点域名校验完整地址表，复用最近一次通过校验的缓存。
- `EhHttp.cs`、`EhClient.cs` 和 `AppServices.cs` 让页面与图片共用更新结果，新地址在建连时生效，保留手动 hosts 和本地后备。
- `AppPaths.cs` 和 `StorageJsonContext.cs` 将地址表、ETag 与校验时间原子写入应用数据目录的 `upstream-hosts.json`。

## 2026-11-21 应用内站点直连与链接直达封面

- `EhHttp.cs`、`DirectNetworkInterface.cs` 和 `AppSettings.cs` 增加按物理网卡连接的站点直连模式，移植上游站点地址与免 SNI 握手，并验证原域名和证书链。
- `SettingsPage.axaml` 和 `SettingsViewModel.cs` 提供「站点直连」选项，支持独立编辑 hosts，连接测试区分站点响应与画廊列表解析成功。
- `EhParser.cs` 识别详情页 CSS 背景封面，粘贴画廊链接时可直接加载对应站点的封面。
- `OnlineProbeService.cs` 报告实际详情封面域名，`UiAuditService.cs` 记录站点直连设置的深浅色与宽窄窗口。物理网卡、证书、正文图与测试结果见[站点直连与封面验证](站点直连与封面验证-2026-11-21.md)。

## 2026-11-21 画廊链接直达与复制

- `EhUrls.cs` 和 `OnlineFeedViewModel.cs` 识别 E-Hentai 与 ExHentai 画廊链接，搜索时直达链接指定站点的详情。
- `EhClient.cs` 和在线详情、阅读、下载链路传递链接指定的站点。常规浏览保留原站点设置，旧下载记录仍可继续使用。
- `OnlineGalleryPage.axaml` 在浏览器打开入口旁提供「复制链接」，复用主窗口提示报告复制结果。
- Core 210 项、在线 UI 21 项通过，使用模拟站点和隔离剪贴板。Windows x64 NativeAOT 导航回归通过，覆盖跨站链接与 540 × 420 DIP 下的复制按钮。完整 UI 回归有两项已知的阅读器重试失败。报告存于 `.qa/gallery-links-20261121/`。

## 2026-11-20 下载入队立即显示在本地库

- `ShellViewModel.cs` 和 `DownloadManager.cs` 使用在线列表已有的信息入队，下载工作线程获取详情并记录权威页数。
- `DownloadsViewModel.cs` 和 `MainViewModel.Downloads.cs` 在任务入队后发布卡片。首张图片原位启用阅读，移除尚无图片的任务时撤下卡片。下载中的空卡片从队列恢复，实际页信息继续使用本地库缓存。
- `GalleryMerger.cs` 和本地库、版本管理视图保留已有可读副本，等待中的副本显示下载状态并禁用阅读与缺页操作。
- 下载回归使用阻塞画廊详情的 HTTP 测试桩，确认详情返回前卡片已出现，首图原位启用阅读，移除空任务后卡片撤下，以上操作均不触发全库扫描。相关五项回归通过，报告在 `.qa/download-queue-visible-20261120/test-results/`。
- Windows x64 NativeAOT 导航回归和 UI 冒烟通过。由 6,349 个作品组成的合成库在首图写入前显示排队卡片，阅读按钮禁用。报告及截图在 `.qa/download-queue-visible-20261120/native-library/`。

## 2026-11-20 本地库按新增范围加载

- `MainViewModel.cs` 在启动时载入有效来源缓存，新增或启用来源时保留已载入的卡片和目录快照。缺失缓存只扫描对应来源，手动刷新可重扫全库。
- `DownloadsViewModel.cs` 合并同一任务的逐页更新，持续消费已落盘的任务，打开本地库即可看到队列中已有页的副本。
- `MainViewModel.Downloads.cs` 和 `LibraryCacheStore.cs` 持久化下载首图与结束状态。定向缓存保留完整来源快照边界，同步扫描期间的下载结果。
- Windows x64 只读探针使用 9,184 个合成缓存作品和截图中三个真实下载目录，最终显示 9,187 个作品，扫描状态零次变化。三个目录的 321 个文件前后名称、大小和修改时间一致。回归另覆盖新增来源、已存在卡片保留，以及移除下载任务后重新启动仍可从缓存找到副本。
- 新 NativeAOT 产物通过导航及冒烟探针。Core 198 项、UI 157 项通过，另两项阅读器重试在旧版同样失败。日志和报告在 `.qa/download-add-only-20261120/`。

## 2026-11-19 修复下载首图入库和阅读触发重复扫描

- `DownloadsViewModel.cs`、`MainViewModel.Downloads.cs` 和 `LibraryScanner.cs` 按归一化后的副本路径发布首图和完成状态。扫描进行中也可入库，后来完成的扫描合并定向更新。
- `DownloadManager.cs`、`MainWindow.axaml.cs` 和 `ReaderViewModel.cs` 修正并发落盘的页数回退，并将普通下载事件、阅读校验和返回下载页的更新限定在相关作品或副本。
- `ShellViewModel.cs`、`LocalImageSearchService*.cs` 和 `ImageIndexBuildCheckpoint.cs` 在下载完成后只检查相应副本，复用其余索引清单。定向任务可从检查点接续。
- `SettingsPage.axaml` 和双语 README 标明首张图片保存时自动入库。
- 合成 HTTP 测试复现旧版首图落盘后仍缺卡片、打开阅读器才登记的顺序。新版覆盖新作品和已有另一份副本的作品，验证首图、阅读和下载完成均保留同一目录的单一副本，资料库全量扫描时间戳保持原值。
- Core 198 项通过，UI 156 项通过。两项既有阅读器重试失败已在 `62691d9` 上复现。Windows x64 NativeAOT 的导航、冒烟及图片索引探针通过。默认排序审计选中不同作品时报告了横向滚动区内 8 个视口外按钮；固定标题排序复测时旧版与新版各生成 122 张截图，均无越界标记。报告位于 `.qa/download-refresh-20261119/`。

## 2026-11-19 修复冷启动窗口无响应

- `App.axaml.cs`、`ViewModels/ShellViewModel.cs`、`ViewModels/OnlineFeedViewModel.cs`：窗口显示后再启动在线首页加载，在线与本地分别就绪，保留先到达的用户刷新或搜索请求。
- `Core/Online/EhClient.cs`：网页请求从 UI 调用时先切到线程池，避免系统代理同步查找阻塞消息循环。
- `Services/AppServices.cs`、`ViewModels/MainViewModel*.cs`：下载目录准备、资料库目录检查和缓存卡片数据生成在后台执行，取消或退出时丢弃过期结果。
- `OnlineParityIntegrationTests.cs`：模拟进入 HTTP 请求时同步阻塞，验证窗口先打开，且等待期间可以切到本地库并完成资料库初始化。
- 验证：Core 198 项通过。Avalonia UI 153 项通过，另两项阅读器重试失败在变更前的 `62691d9` 上复现。Windows x64 NativeAOT 新产物通过 `--ui-smoke` 和 `--navigation-regression`，本机回环服务挂起 TLS 握手的 3 次独立进程启动中窗口均持续响应，记录见 `.qa/startup-native-20261119/slow-handshake/result.json`。

## 2026-11-17 修复缺页状态、损坏页分类与阅读器重下进度

- `Core/Format/PageFileParser.cs`、`Core/Format/PageFileParser.Natural.cs`、`Core/Models/GalleryPage.cs`：损坏页不再计入 `MissingRanges`，`PageScanResult.IsIncomplete` 同时考虑损坏页；缺页只表示没有可用字节的页。
- `Core/Download/DownloadModels.cs`、`Core/Download/DownloadManager.cs`：`DownloadTask` 新增 `CorruptPages`；目录核对按「有效 / 损坏 / 缺失」三态重算，卡片文案区分为「N 页缺失、M 页文件损坏（可重试）」；完成任务清空损坏记录。
- `Core/Download/DownloadManager.Repair.cs`：补页允许替换容器无效的同名文件（0 字节、截断、错误内容），有效同名文件仍保留并报告失败——此前损坏页会永久卡在「原目录存在同名文件」。
- `Core/Download/DownloadManager.PageRetry.cs`：单页重下增加进度中继（同目录并发调用共享同一次下载并各自收到进度），上报解析 / 解码状态；成功后同时清除失败页与损坏页记录。
- `Services/ReaderSources.cs`、`Services/AppServices.LocalDownloads.cs`：打开本地阅读器恢复便宜的目录扫描，容器校验改为页面显示后后台执行（197 页目录冷缓存实测 991 ms → 0.3 ms 打开）；`CanRedownload` 暴露给阅读器。
- `ViewModels/ReaderViewModel.cs`、`ViewModels/ContinuousPageItem.cs`、`Views/ReaderPage.axaml`：缺失/损坏页显示「重新下载本页」与进度条，连续/横向模式同样可重下；切换阅读模式补发 `ShowSingleView` 通知，修复模式切换后单页画布可见性不刷新。
- `ViewModels/OnlineSupport.cs`、`Views/DownloadsPage.axaml`：下载卡片增加「N 页缺失 · M 页损坏」细分文案。
- `Views/MainWindow.axaml.cs`：从阅读器返回下载页时同步任务状态，补齐后立即不再显示缺页。
- `Core/Download/DownloadManager.Repair.cs`、`Core/Download/DownloadManager.cs`、`Core/Download/DownloadManager.PageRetry.cs`：页写入成功后清掉同页号其他扩展名的无效遗留文件。重下换容器（webp → jpg）时旧截断文件会留在目录里，导致「171 个文件全齐但仍报 1 页损坏」；只删除容器校验判定无效的文件，有效候选一律保留。
- `Views/MainWindow.axaml`、`Views/OnlineFeedPage.axaml`、`ViewModels/ShellViewModel.cs`：删掉窗口顶端那条 2 像素全局进度条，列表页的加载条改绑 `ShowBusyBar`——启动自动刷新不再挂线，只有用户主动刷新才显示，避免标题栏下同时叠两条进度条。
- 验证：`dotnet test tests/EhViewerForPc.Core.Tests -c Debug` 198/198 通过（新增「换容器补页后清除旧截断文件」）；`--ui-smoke` 截图确认窗口顶部无加载条（`library.png`、`online-local.png`）。真实案例 `3173749` 第 171 页：本地 49640 字节，站点返回 57930 字节（与 RIFF 声明一致），确认是导入时截断的副本。
- 验证：`dotnet test tests/EhViewerForPc.Core.Tests -c Debug` 197/197 通过；`--ui-smoke` 的截断页断言通过（`第 2 页已损坏，可重新下载`，画布隐藏，重下按钮可见，截图 `reader-corrupt.png`）；`PublishAot` 后用 `artifacts/win-x64-aot-repair2-20261117` 实际启动并跑完 `--ui-smoke`。本机 197 页目录实测：带容器校验的冷缓存扫描 991 ms，便宜扫描 0.3–0.5 ms，`ReaderViewModel` 构造 0.4–0.8 ms（热）/34.6 ms（首次含 JIT）。

## 2026-11-16 页级下载失败按图床换源重试

- `Core/Online/EhClient.cs`：`ResolveImageAsync` 在解析不到图片或拿到 509 占位图时，用页面里的 `nl` 换源键重新请求一次视图页再按新地址解析；新抽 `FetchViewerHtmlAsync` 统一串行化与节流。509 判定收紧为 `/509.gif` 与结尾 `/509`，避免把正常图床路径误判成配额错误。下载失败消息补上主机与容器识别结果，HTTP 失败不再只有状态码。
- `Core/Online/EhParser.cs`：`TryParseImagePage` 先解析 `nl` 键再检查 `#img`，加载失败的页面也能拿到换源键。
- `Core/Online/EhHttp.cs`：图片请求不再声明 `image/avif`，避免服务端返回本地容器校验与解码都不支持的格式。
- `Core/Format/ImageSignature.cs`：WebP 改为「文件不短于 RIFF 声明长度」，允许尾部填充并继续拒绝截断；新增 `DescribeFile` 供失败消息使用。
- `Core/Imaging/GalleryImageStore.cs`：`GetAsync` 增加 `refresh`，重试时绕过在线图片缓存——占位图本身可能是完整 GIF，容器校验发现不了。
- `Core/Download/DownloadManager.cs`：单页获取失败后重试 3 次并退避，重试时重新解析地址；HttpClient 超时不再当作任务取消，只有外部 token 真正取消才中止整批下载。
- `Core/Online/EhClient.cs`、`Core/Download/DownloadManager.cs`：509 配额是外部可恢复条件，`EhException.QuotaExceeded` 标记后单页与整批都不再重试；任务转为「已暂停」并提示配额不足，避免继续逐页请求把剩余配额打光。
- `Core/Download/DownloadManager.PageRetry.cs`：「重试本页」的分页定位改为按首页条数定位 + 相邻分页 + 全列表兜底，避免固定步长猜错就报「在线画廊中找不到当前页」。
- `tests/EhViewerForPc.Core.Tests/RemediationTests.cs`：覆盖 nl 换源（缺图与 509）、瞬时失败后重试完成、配额耗尽暂停且只请求一次、WebP 尾部填充与截断判定。
- `Core/Online/EhParser.cs`、`Core/Online/EhModels.cs`、`Core/Online/EhUrls.cs`、`Core/Online/EhClient.cs`：解析 `home.php` 的 Image Limits 为 `EhImageLimits`。额度从未触发时限流时不打印数字，改为识别站点的「No restrictions … in effect」措辞并标记为无限制；`GetImageLimitsAsync` 固定读主站（`exhentai.org/home.php` 返回 404，认证 cookie 两个域都装），未登录或页面变化时返回 null。
- `ViewModels/ShellViewModel.cs`：账号区新增 `ImageLimitLabel` / `ImageLimitDetail` 与 `RefreshImageLimitsCommand`；启动、登录变化和下载任务出现 509 时按 30 秒节流自动刷新，退出登录清空读数；已登录但未读到时显示「未读取」而不是留空。
- `Views/MainWindow.axaml`：账号栏改为两行（账号 + 图片额度），额度单独一行避免窄侧栏先截掉数字；按钮只对齐首行，内容块垂直居中。额度整行就是刷新按钮，展开态与折叠浮出共用同一入口；浮出层固定宽度并加大行距，避免长说明换行挤压。
- `tests/EhViewerForPc.Core.Tests/OnlineParserTests.cs`：覆盖额度数字解析、超限标记、无限制措辞与缺少标签时不臆造数值。
- 验证：`dotnet test tests/EhViewerForPc.Core.Tests -c Debug` 187/187 通过；`dotnet build src/EhViewerForPc -c Release` 与 `PublishAot` 通过；`--ui-smoke` 截图确认账号区两行左对齐、按钮与首行同一基线；用真实登录页面驱动 Core 解析器得到 `unrestricted=True`，证实该账号当时没有图片配额限制。

## 2026-11-15 修复两日审查中的副本身份、编号与删除调度

- `ViewModels/VersionsViewModel.cs`：单目录缺页检测在作品身份未变时保留完整扫描的副本 StableId 和重复标记，避免已有图片索引命中消失。
- `Core/Format/PageFileParser.cs`、`Core/Scanning/LibraryScanner.cs`：有效 8 位页名优先保留原始编号，额外封面不压缩缺页；定向重查旧误分类缓存，保留其余 schema 3 / 4 缓存命中。
- `Services/ReaderSources.cs`：重试后的页重载保留真实元数据并合并已知总数，保持尾页与缺页占位。
- `Core/Download/DownloadManager.LocalDelete.cs`：删除保护解除后恢复受影响等待任务的调度，保留随后暂停的用户意图；成功删除不启动无关等待任务。
- `tests/`：加入重复副本真实建库检索、三种阅读模式、旧缓存以及删除失败 / 取消 / 成功的回归测试；证据与范围见 `docs/REVIEW-2026-11-14-15.md`。

## 2026-11-15 托盘菜单补齐页面直达与本地库图搜图

- `Services/DesktopTrayController.cs`：菜单改为「显示窗口 → 各页面 → 本地库图搜图 → 退出」。页面项直接遍历 `ShellViewModel.NavItems` 生成，新增页面无需再改托盘，避免维护第二份清单；点击先恢复前台再切页（隐藏窗口上跑导航动画会白做一次布局）。设置迁移期间导航会被 `SelectedNav` 拒绝，菜单点击改为弹出提示而不是静默丢弃。`OpenImageSearch` 复用窗口既有入口，不新建第二套图搜图页面。
- `Views/MainWindow.axaml.cs`：新增 `ActiveImageSearch` 内部属性，供托盘回归断言当前文档是图搜图页。
- `tests/EhViewerForPc.ImageSearch.Ui.Tests/TrayIntegrationTests.cs`：菜单项断言更新为 9 项完整清单；新增 `TrayMenuBringsTheWindowBackAndSwitchesToEveryPage`，逐个点击页面项断言「窗口回到前台 + `SelectedNav` 命中对应页面」，并断言图搜图项把 `ImageSearchPage` 压入导航帧。
- 验证：UI 测试 19 / 19 通过（含新增用例）。托盘菜单由系统原生绘制，外观与点击热区仍需在真实桌面手工确认。

## 2026-11-15 重复启动改为唤醒已运行窗口

- `Services/SingleInstanceEndpoint.cs`（新增）：命名管道通道承载 `SHOW` / `OPEN <id>` 请求，管道名带会话号（`EhViewerReader.Activation.v1.<session>`），协议为换行分隔 UTF-8 纯文本，不走序列化上下文。接收端在 UI 线程完成窗口恢复后才回 `OK`，客户端 4 次 × 250 ms 重试覆盖首实例初始化窗口期；监听循环单次失败只记录并重建服务端，`Dispose` 取消 + 关闭等待中的连接 + 最多等 1 秒，未结束则保留令牌源到进程退出。
- `Services/WindowActivation.cs`（新增）：恢复窗口的唯一入口。`SetForegroundWindow` 被前台锁拒绝时降级为置顶切换（下一轮调度清除标志）并在仍未激活时闪烁任务栏；`AllowSetForegroundWindow` 由第二实例向首实例 PID 授予一次性前台许可。
- `Program.cs`：互斥锁已被占用时不再返回 0 后静默结束，改为投递请求（含 `--gallery`）再退出；新增探针第二进程入口 `--single-instance-probe-signal`（退出码 0 已投递 / 5 无人接收）。
- `App.axaml.cs`：窗口首次 `Opened` 时启动监听（此时平台句柄已存在）；`Exit` 顺序改为「停监听 → 托盘 → Shell → 服务」；`--single-instance-probe` 使用隔离数据目录且跳过资料库初始化。
- `Services/DesktopTrayController.cs`：托盘「显示窗口」与第二实例激活共用 `WindowActivation.Restore`，不再各自维护一套显示逻辑。
- `StartupOptions.cs`、`Services/SingleInstanceProbe.cs`（新增）：`--single-instance-probe <json>` 无人值守验证「隐藏窗口 → 第二进程投递 → 窗口恢复 → 作品 ID 转发 → 请求计数」。
- 验证：Debug 与 NativeAOT（`artifacts/si-aot-final`，全新输出目录）均运行探针通过，5 项断言全部为 true（含前台焦点 `foreground: true` 与作品 ID 转发）；互斥锁被占用时探针返回 6，与「已唤醒」区分；真实双启动第二进程 105 ms 内退出、仅保留一个进程、隔离目录无告警日志；Core 回归 173 项通过。MSIX 打包形态尚未实机验证。

## 2026-11-15 沉浸模式隐藏左下角账号按钮

- `Views/MainWindow.axaml.cs`：紧凑态账号按钮的可见性补上 `!immersive` 条件。此前只判断了面板展开态，面板收起时（窄栏或手动收起），该按钮在沉浸模式下仍固定于客户区左下角，压在阅读器底部的「上一页」按钮上。展开态的账号栏原本已隐藏，现在两种形态在沉浸时都不再显示，退出沉浸后按面板状态恢复。
- `Services/UiSmokeService.cs`：沉浸截图检查新增账号区域隐藏断言；`tests/EhViewerForPc.ImageSearch.Ui.Tests/ImageSearchIntegrationTests.cs` 增加回归测试，覆盖「收起面板 → 进入沉浸 → 账号消失 → 退出沉浸 → 账号恢复」。

## 2026-11-15 修复 MSIX 任务栏图标黑边

- `packaging/EhViewerForPc.Msix/Assets/Square44x44Logo.targetsize-*_altform-unplated.png`、`..._altform-lightunplated.png`：补齐 16 / 20 / 24 / 30 / 32 / 36 / 40 / 48 / 60 / 64 / 72 / 80 / 96 / 256 两套无底板 AppList 图标。此前包内只有带底板 PNG，任务栏与开始菜单会回退到带底板图标，并用清单 `BackgroundColor="#0B0D10"` 绘制方形底板，形成图标外圈黑边。生成时按主图实际边缘裁切（512 图中 460×460 @ 26,26），只保留抗锯齿边缘，不再沿用主图四周约 5% 的透明留白，图标在任务栏中更贴近边缘。
- `packaging/EhViewerForPc.Msix/Package.appxmanifest`：`BackgroundColor` 改为 `transparent`；即使主题对比度判定使系统回退到带底板图标，也不会再出现深色方形底板。图标自身的深色圆角底板保留。
- `packaging/EhViewerForPc.Msix/EhViewerForPc.Msix.wapproj`：注明 `Assets\*.png` 通配必须保留，新增无底板资源随包自动收集。
- `src/EhViewerForPc/Assets/icon.ico`：9 个尺寸帧的 AND 透明掩码此前全为 0，按 alpha=0 的像素重建为 1；像素数据、帧尺寸与文件大小不变，避免走掩码的旧渲染路径把透明区画成黑色。窗口与 Alt+Tab 使用该 ICO，任务栏优先使用包内 AppList 资源。
- 验证：新增 28 个 PNG 均为 8 位 RGBA、尺寸与 targetsize 一致、四角 alpha=0、中部左右边缘 alpha=255（贴边且未裁掉圆角）、中心不透明；以 `makepri new` 重建资源索引并 dump，确认 28 个候选按 `TargetSize-16…256 + AlternateForm-UNPLATED / LIGHTUNPLATED` 登记；`makeappx pack` / `unpack` 成功，包内 34 个 Assets（14 个 unplated、14 个 lightunplated）。需在 VS 中重新生成 MSIX 并重装后观察任务栏（旧图标可能被 Shell 缓存，必要时取消固定后重新固定或重启资源管理器）。

## 2026-11-15 画廊不可用下载任务单独归档

- `Core/Download/DownloadModels.cs`、`DownloadManager.cs`、`DownloadManager.Repair.Batch.cs`：失败理由为「画廊不可用（可能已删除或需登录）」的任务写入持久化标记，移入独立分类并跳过「全部开始」、自动启动与调度；移除标记、显式继续 / 重试、重新入队或主动补页时清除标记，补页批次保存失败时随其余字段回滚。
- `Core/Online/EhParser.cs`：站点答复抽为固定常量与精确匹配判定，网络、配额和解析失败不会被误归入不可用。
- `ViewModels/DownloadsViewModel.cs`、`OnlineSupport.cs`、`Views/DownloadsPage.axaml`：新增「画廊不可用」页，任务在队列与该页之间原位移动；提供移除标记、阅读、打开目录与移除任务，状态栏与页签名显示不可用数量。
- `Services/UiAuditService.cs`、`tests/`：审计增加不可用页截图；核心测试覆盖失败标记、持久化、跳过自动重试与重新入队恢复，UI 测试覆盖分类移动与移除标记。
- 兼容性：`downloads.json` 结构版本不变，新增字段缺失时按未标记读取；启动加载会把非失败状态上的残留标记清掉。验证：Core 173 / UI 142 通过；Debug 与 NativeAOT 审计各 122 张截图、无横向溢出。

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
