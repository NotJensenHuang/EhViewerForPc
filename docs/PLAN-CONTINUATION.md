# 续作计划 — M3 至 M6（UI 垂直切片与验收）

> 前置：`docs/PLAN.md`（总体设计与已完成的 M0–M2）
> 生成时间：2026-11-08
> 执行方式：先由 2 个只读子代理评审本计划，按 P0/P1 修订后无人值守实施
> 验证库：`D:\BackUp\phoneGT5pro\ehviewer`（严格只读）

## 1. 现状核查（代码级）

### 1.1 已完成且可用

| 子系统 | 文件 | 状态 |
| --- | --- | --- |
| 格式解析 | `Core/Format/SpiderInfoParser.cs`、`CborHeaderReader.cs`、`PageFileParser.cs`、`GalleryNameParser.cs`、`ImageSignature.cs` | VERSION1/2 + CBOR、8 位页名、候选扩展、缺页区间、稳定键 |
| 扫描/缓存 | `Core/Scanning/LibraryScanner.cs`、`Core/Storage/*` | 一级目录扫描、签名缓存、原子写、进度回调 |
| 模型 | `Core/Models/*` | GalleryInfo/GalleryPage/SpiderHeader/AppSettings/ReadingProgress/LibraryCache |
| 图像 | `Services/ImageDecoder.cs`、`ImageRequest.cs`、`CoverService.cs`、`Core/Imaging/ImageBudget.cs` | SkiaSharp 解码、EXIF、像素预算、候选回退、封面回退 |
| 控件 | `Controls/AsyncImage.cs`、`Controls/ZoomPanView.cs` | 懒加载/代际取消/控件独占 Bitmap；单页缩放平移手势 |
| 尺寸缓存 | `Services/PageDimensionService.cs` | 连续模式所需宽高缓存（未接入 UI） |
| 单元测试 | `tests/...` 29 项 | 解析、扫描、预算；基线 1 项断言已修正 |

### 1.2 基线缺陷（本次已修复）

1. `EhViewerReader.csproj` 缺 `AllowUnsafeBlocks`，主项目无法编译 → 已启用。
2. `ImageDecoder` 中 `Task.Run` 泛型推断失败、`DecodeCore` 静态方法访问实例信号量 → 已修正。
3. `ImageBudgetTests.HugeImageIsLimitedByDimension` 与 24MP 预算冲突 → 已改为 512MP 上下文的维度约束测试。
4. `app.manifest` 缺 `longPathAware`/DPI 感知 → 已补。
5. 模板残留 `Class1.cs`、`UnitTest1.cs` → 已删除。

### 1.3 未完成（本次范围）

- **全部 ViewModel 与 View**：`MainWindow` 仍是模板文本；无 Reader/Settings/Details 窗口；无主题与样式。
- 无应用启动编排（参数、单实例、服务初始化、主题应用、窗口状态保存）。
- 无自检 CLI、无真实库集成测试、无 UI 冒烟/截图验证。
- 扫描缓存只比较目录 mtime，未比较 `.ehviewer`/`.thumb` 长度+mtime。
- 无 README/FORMAT/TEST_REPORT。

## 2. 本次目标（M3–M6）

### 2.1 必须交付

1. 可运行的库视图：封面网格（6,315 项虚拟化）、搜索、排序、筛选、收藏、继续阅读、详情、刷新、选择资料库。
2. 单页阅读器：适应页面/宽度/高度/原始、缩放平移、翻页（键盘/滚轮/点击）、方向、全屏、进度恢复。
3. 连续阅读模式：虚拟化长图流、尺寸预读、当前页跟踪、进度按原始页号+页内偏移恢复。
4. 设置窗口：主题、默认模式/适应/方向/背景、单张解码预算、资料库切换、清除扫描缓存。
5. 启动参数 `--root` / `--gallery` / `--self-test` / `--ui-smoke`；单实例。
6. 真实库集成测试（环境变量门控）、自检报告、UI 冒烟截图（合成库）、README/FORMAT/TEST_REPORT。
7. 资料库零写入（应用自身），所有状态写 `%LocalAppData%\EhViewerReader`。

### 2.2 可选（仅在核心全部通过后）

- 双页模式（封面单独 + RTL 左右反转）。
- 继续阅读横向条、页码跳转输入框。

### 2.3 非目标（不变）

- 不做 EH 网络登录/在线搜索/下载/标签抓取；不写资料库；不做跨设备同步。

## 3. 关键设计决策（本次新增/修订）

### 3.1 阅读器位图归属：控件自持，不使用 VM 位图

`ZoomPanView` 增加 `Request: IImageRequest?` 与解码预算属性，由控件自己完成“懒加载 + 代际号 + 取消 + Dispose”，与 `AsyncImage` 同一套生命周期规则。理由：

- 避免 VM 持有 Bitmap 后与 `ZoomPanView.Source` 交叉 Dispose（翻页瞬间渲染旧帧可能崩溃）。
- 翻页、窗口缩放、适应模式变化都只需替换 Request 或触发重载，UI 线程不接触像素。
- 连续模式仍由 `AsyncImage` 每项独占位图，语义一致。

### 3.2 库列表数据流

- `MainViewModel` 持有 `List<GalleryItemViewModel> _all`；过滤/排序生成新列表并整体赋给 `ItemsRepeater.ItemsSource`（一次 Reset），不做逐项增删。
- 启动顺序：读 settings/cache → 立即用缓存结果填充 → 后台全量扫描 → 原子保存 cache → 整体替换列表；扫描期间 UI 可用。
- 命令集中在 `MainViewModel`，卡片通过 `CommandParameter` 传 item，避免 6,315 组命令对象。
- 封面：`GalleryItemViewModel.CoverRequest`（`CoverImageRequest`）一次创建；仅可见卡片触发解码。

### 3.3 连续模式几何

- 每项 `ContinuousPageItem` 保存 `OriginalWidth/Height`、`Width/Height`（显示 DIP）、`Top`（前缀和）。
- 显示宽度 = `min(视口宽, 1400)`；高度按原始宽高比换算；窗口宽度变化 > 32px 时防抖 200ms 重算全部高度与 `Top`。
- 滚动位置 ↔ 页面映射用前缀和二分查找；进度保存“原始页号 + 页内比例”。
- 打开连续模式前通过 `PageDimensionService` 读取全部尺寸（带取消/进度）；失败页用 3:4 占位。
- 容器回收由 `ItemsRepeater` + `StackLayout` 负责；每个 `AsyncImage` 在 detach 时释放位图。

### 3.4 单页解码尺寸

`ZoomPanView` 按自身 `Bounds` 与 `RenderScaling` 计算目标：

- FitPage：target = 视口（宽×高）× scaling；
- FitWidth：targetWidth = 视口宽 × scaling，targetHeight = null；
- FitHeight：targetHeight = 视口高 × scaling；
- Original：target = null（交给 24MP/16000 上限）。
- 缩放（Ctrl+滚轮）超过已解码分辨率 2 倍时，按新倍率重载一次（上限 8x，仍受预算约束）。

### 3.5 启动与自检

- `Program.Main` 解析参数；单实例 Mutex（`Local\EhViewerReader`），重复启动退出。
- `--self-test --root <path> [--out <json>]`：`AppBuilder.SetupWithoutStarting()` 后运行扫描/封面/页面解码/尺寸读取/只读快照，输出 JSON，退出码 0/1。**不创建窗口**。
- `--ui-smoke [--root <path>]`：无 `--root` 时在 `%TEMP%` 生成合成资料库（纯色/几何测试图，无敏感内容），启动主窗口，等待扫描完成→渲染 PNG→打开阅读器（单页/连续）→渲染 PNG→退出。截图写入 `docs/screenshots/`（仅合成库）。
- `--gallery <gid|stableId>`：启动后直接打开匹配画廊。

### 3.6 只读保障

- 应用代码对资料库只使用 `FileAccess.Read` + `FileShare.ReadWrite|Delete`；写入 API 仅位于 `AppPaths` 派生目录。
- 自检在扫描/阅读前后对资料库做快照（相对路径+长度+mtime），断言零差异；测试用副本+只读 ACL 再跑一次。
- “打开文件夹”交由 Explorer，不计入应用写入。

## 4. 实施顺序（垂直切片）

| 步骤 | 内容 | 产出/验收 |
| --- | --- | --- |
| S1 | 主题/样式、`GalleryItemViewModel`、`MainViewModel`（扫描/缓存/过滤/排序/命令） | 编译通过，单元测试覆盖过滤/排序/稳定键 |
| S2 | `MainWindow` 网格（ItemsRepeater + UniformGridLayout）、搜索/筛选/状态栏/空状态 | 6,315 项合成库滚动流畅，容器数受控 |
| S3 | `ZoomPanView` 自加载扩展、`ReaderViewModel`、`ReaderWindow` 单页 | 真实样本可翻页/缩放/恢复进度 |
| S4 | 连续模式（尺寸预读、前缀和、滚动跟踪、进度恢复） | 1,728 页画廊跳转/恢复/滚动不白屏 |
| S5 | 设置/详情窗口、全屏/快捷键、`--root/--gallery/--self-test/--ui-smoke` | 全部入口可用；自检报告生成 |
| S6 | 真实库集成测试、性能/内存测量、UI 冒烟截图、只读验证 | `TEST_REPORT.md` 数据齐全 |
| S7 | README/FORMAT、Release 构建、子代理终审与修复 | 零错误、无未处理 P0/P1 |

每步完成后立即 `dotnet build` + `dotnet test`，失败不进入下一步。

## 5. 文件清单（预计新增/修改）

```
src/EhViewerReader/
  App.axaml(.cs)                     # 主题、服务、启动窗口
  Program.cs                         # 参数、单实例、SetupWithoutStarting
  StartupOptions.cs                  # 参数模型
  Styles/Theme.axaml                 # 颜色/排版/控件样式
  ViewModels/
    MainViewModel.cs
    GalleryItemViewModel.cs
    ReaderViewModel.cs
    ContinuousPageItem.cs
    SettingsViewModel.cs
    GalleryDetailsViewModel.cs
  Views/
    MainWindow.axaml(.cs)            # 迁移到 Views 命名空间
    ReaderWindow.axaml(.cs)
    SettingsWindow.axaml(.cs)
    GalleryDetailsWindow.axaml(.cs)
  Controls/AsyncImage.cs             # 保持
  Controls/ZoomPanView.cs            # 增加 Request 自加载
  Services/SelfTestService.cs        # 自检与只读快照
  Services/UiSmokeService.cs         # 合成库 + 截图
tests/EhViewerReader.Core.Tests/
  SpiderInfoParserTests.cs
  PageFileParserTests.cs
  LibraryScannerTests.cs             # 含真实库集成测试（EHVIEWER_TEST_ROOT 门控）
  ImageBudgetTests.cs
docs/
  PLAN-CONTINUATION.md
  FORMAT.md
  TEST_REPORT.md
  screenshots/                       # 仅合成库
README.md
```

## 6. 风险与对策（本次）

| 风险 | 对策 |
| --- | --- |
| ItemsRepeater 11.1.5 与 Avalonia 11.3.6 运行期不兼容 | S2 首日做合成库 6,315 项运行验证；失败则降级为自研 VirtualizingWrapPanel（按行虚拟化），不改数据流 |
| 连续模式 1,728 项高度重算卡顿 | 防抖 + 只在宽度变化 >32px 时重算；必要时改为可见项优先测量 + 锚点补偿 |
| 单页位图/渲染竞态 | 控件自持 + 代际号 + 取消；翻页只替换 Request |
| 真实库冷扫描慢 | 缓存优先显示 + 后台扫描 + 可取消；记录冷/热耗时，不做硬承诺 |
| 敏感内容截图 | 截图仅合成库；真实库只做程序化断言/UI 树检查 |
| 长路径/0 字节/坏图 | 已有解析与候选回退；UI 层失败占位 + 跳到下一可读页 |
| 无人值守下 UI 不可见 | `--ui-smoke` 输出 PNG，逐一目检布局/文字/对比度 |

## 7. 验收清单（本次）

- [x] `dotnet build -c Release` 零错误；0 警告。
- [x] `dotnet test` 全绿：44/44（含 `EHVIEWER_TEST_ROOT` 真实库集成测试与多数据源合并测试）。
- [x] 真实库：6,327 画廊、245,548 页；缺页 27、无 header 162、无封面 256、0 字节 13，均不崩溃。
- [x] 冷/热扫描、解码、1,728 页尺寸预读、长条图/最大图记录在 `TEST_REPORT.md`（冷盘 81 s，热缓存 0.36 s）。
- [x] 库网格 6,315 项仅 26 个 `AsyncImage` 容器，滚动到末尾仍为 26。
- [x] 单页/连续进度重启恢复（冒烟：2/9 → 2/9）。
- [x] 资料库零写入：257,966 文件 / 6,331 目录 / 101 GB，快照哈希前后一致。
- [x] `--ui-smoke` 截图（library/search/details/reader/settings）与布局坐标 JSON。
- [x] README/FORMAT/TEST_REPORT 完成。
- [x] 阅读器为非模态窗口：打开期间主窗口可用（`mainWindowEnabled = true`），重复打开同画廊复用窗口。
- [x] 关闭阅读器后库滚动位置保持 640 → 640，列表顺序不变（`scrollStable/orderStable = true`）。
- [x] NativeAOT 发布成功（22.9 MB），AOT 产物完成完整 UI 冒烟与二次启动；JSON 全部走源生成上下文。
- [x] 损坏页滚轮翻页：3/9 → 4/9（`handled = true`），点击翻页同样不再依赖位图。
- [x] MSIX 打包项目产出 `artifacts/msix/EhViewerReader_1.0.0.0_x64.msix`（15.8 MB，版本注入与布局隔离均验证）。
- [x] `EhViewerReader.slnx` 含 Core / App / Msix / Tests 四个工程；解决方案构建跳过打包（VS 可见且不触发 AOT）。
- [x] 多数据源：2 个源合并后 31 个作品、3 张多版本卡片、7 份副本；同源重复 GID 不合并；`WorkId` 一致且 `StableId` 唯一。
- [x] 版本管理：3 行、默认副本按“完整性→页数”选出、可切换；“用此版本阅读/设为默认”写回设置。
- [x] 进度按作品共享：切换副本后仍恢复 2/9；旧 `library-cache.json` 与旧进度键均有回退路径。
- [x] 设置页数据源列表（启停/移除/添加）在固定宽度对话框内不裁切；首卡角标 `2 页 width=19`（`firstCardBadgeOk = true`）。

遗留（不阻塞验收）：双页模式、继续阅读条未实现；冷盘首次扫描 81 s（缓存优先 + 后台扫描已缓解）；离线截图存在 ClearType 彩色边缘。

## 8. 评审记录与处置

### 8.1 评审 1：兼容性/性能（只读子代理）

| 级别 | 问题 | 处置 |
| --- | --- | --- |
| P0 | 0 字节页未计入“不完整” | `PageScanResult.IsIncomplete` 与扫描器/缓存统一计入；新增测试 |
| P0 | 缓存签名只比目录 mtime，原地补写不失效 | schema 2：页文件数量/长度和/最大 mtime/最大页号 + `.ehviewer`/`.thumb` 长度+mtime；新增原地改写测试 |
| P0 | 解码测试无处可跑 | 测试项目引用 SkiaSharp 做真实文件头/零字节断言；Avalonia 解码器由带调度循环的 `--self-test` 覆盖 |
| P0 | 峰值内存估算不成立（多次整图复制） | 重写 `ImageDecoder`：`SKCodec.GetScaledDimensions` + 直接写入 `WriteableBitmap` 帧缓冲；仅旋转图使用临时位图 |
| P0 | 预取与“控件自持位图”矛盾 | `ZoomPanView.PreloadRequest`：控件内部预取下一页并接管位图，VM 不接触 Bitmap |
| P1 | 缩放重载会重置视图 | `Request` 变更才重置；缩放触发的重载保留缩放/位移，220 ms 防抖 |
| P1 | 连续模式跳转/尺寸预读阻塞 | 前缀和 + `ScrollViewer.Offset` 定位；尺寸预读带进度/取消，缓存到 LocalAppData |
| P1 | 零写入取证缺口 | 快照包含目录项；启动/切换根目录时拒绝数据目录与资料库互相包含 |
| P1 | 测试假绿/脆弱 | 集成测试改为谓词发现样本、耗时只记录不硬断言；`EHVIEWER_TEST_ROOT` 门控 |

### 8.2 评审 2：Avalonia/UX（只读子代理）

| 级别 | 问题 | 处置 |
| --- | --- | --- |
| P0 | 位图在 UI 线程立即 Dispose，渲染线程可能仍引用 | `AsyncImage`/`ZoomPanView` 使用 retired 位图（保留上一帧），下一帧成功后再释放 |
| P0 | `ZoomPanView` 首帧尺寸为 0 时永不重试；换页未清旧图 | `OnSizeChanged` 触发重载；`Request` 变更立即 retire 并画占位 |
| P0 | `Source` 公开可赋值，存在跨控件 Dispose 风险 | 删除公开 `Source`，页面只经 `IImageRequest` 进入控件 |
| P0 | 容器回收立即 Dispose 导致滚动反复解码 | detach 后 250 ms 宽限释放；同 Key 重挂载复用 |
| P0 | 连续模式项高塌陷 | 模板根显式 `Width/Height` 绑定，`StackLayout` 间距 12 |
| P1 | `UniformGridLayout` 未显式设置导致行高跳动 | 设置 `MinItemWidth/MinItemHeight/Spacing/ItemsStretch`；外包 `ScrollViewer` |
| P1 | 编译绑定/ContextMenu 数据上下文 | 所有模板 `x:DataType`；右键菜单在 code-behind 构建，命令直接调用 VM |
| P1 | 连续模式重算丢位置 | 重算前记录页号+页内比例，布局后回写 Offset；窗口宽度防抖 200 ms |
| P1 | 截图不可靠 | `RenderTargetBitmap` 按物理分辨率（DPI×缩放）渲染；同时输出布局坐标 JSON |
| P1 | 放大后点击翻页无反馈 | 取消 `zoom<=1` 限制，非拖动单击即翻页 |

### 8.3 交付后修复（用户反馈）

| 级别 | 问题 | 根因 | 处置 |
| --- | --- | --- | --- |
| P0 | 打开阅读窗口后无法浏览库 | `ShowDialog` 为模态调用，主窗口被禁用 | 改为 `Show(owner)`；按 `StableId` 复用已开窗口；`ShutdownMode.OnMainWindowClose` |
| P0 | 关闭阅读窗口后库滚动条位置跳变 | `RefreshItemProgress` 走 `ApplyFilter`，默认“最近阅读”排序重排 + `ItemsRepeater` 全量重建 | 关闭阅读器只更新卡片状态；进度相关筛选重建时按 `_libraryOffset` 还原；用户主动搜索/筛选/排序仍回顶部 |
| P0 | 损坏图片后滚轮/点击无法翻页 | `ZoomPanView` 把“有位图”当成翻页前提，解码失败时 `_bitmap == null` 直接丢弃事件 | 滚轮/点击翻页与位图解耦；缩放/平移仍要求位图；新增 `SimulateWheel` 测试钩子与冒烟断言（3/9 → 4/9） |
| P1 | 缺少 MSIX 分发形态 | — | 新增 `packaging/EhViewerReader.Msix`：AOT 发布 → 布局暂存 → `MakeAppx`，产出 15.8 MB MSIX |
| P1 | AOT 发布失败 | `System.Text.Json` 反射序列化在 NativeAOT 不可用；本机 Windows SDK 缺导入库 | 源生成上下文 `StorageJsonContext`/`ReaderJsonContext`；`JsonTypeInfo<T>` 入口；AOT 时引用 `Microsoft.Windows.SDK.CPP.x64` 提供链接库 |

### 8.4 多数据源与版本管理（第二轮交付）

| 级别 | 事项 | 处置 |
| --- | --- | --- |
| P0 | `EhViewerReader.slnx` 缺 MSIX 打包工程，VS 中看不到 | 加入 `/packaging/` 节点；工程在**解决方案构建**（`BuildingSolutionFile=true`）时跳过 AOT 与打包并打印提示，直接构建该工程或 `-p:PackMsixOnBuild=true` 时照常出包 |
| P0 | 多设备备份目录无法同时读取 | `AppSettings.Sources`（id/rootPath/name/enabled/lastScanUtc）+ `LibrarySource`；schema 1 的 `LibraryRoot` 在 `Normalize()` 中迁移为第一个数据源（离线盘也保留）；每源独立 `library-cache-<id>.json`，旧 `library-cache.json` 首次加载时被收养 |
| P0 | 同一作品在多源出现会重复显示 | 新增 `LibraryMerger`：按 `WorkId` 分组，仅当来自 ≥2 个不同数据源时合并；同源重复 GID 仍各自成卡 |
| P0 | 进度/收藏在换副本后丢失 | 引入 `WorkId`（GID 或 `dir@<pathHash>`，与旧 `StableId` 同形），进度按作品读写，读时回退旧键；`StableId` 在多源时追加 `@<sourceId>` 保证卡片键唯一 |
| P1 | 无版本管理入口 | `VersionsWindow`：列表 + 默认标记 + 页数/完整性/时间 + “用此版本阅读 / 设为默认 / 打开文件夹”；卡片角标 `×N`、右键菜单、详情页 `数据源/版本` 行 |
| P1 | 设置页只有单个目录 | 数据源列表（启停/移除/打开/添加）+ 清除全部扫描缓存；保存后按需重扫 |
| P1 | 卡片浮层里放 Button 导致角标测量为 0 宽 | 角标改为 `StackPanel + Border`（无 Button），并在冒烟中加 `firstCardBadgeOk` 断言 |
| P1 | 对话框/库网格右侧被裁切 | `ScrollViewer.Padding` 只偏移内容、不扣减给子元素的可用宽度，导致内容按窗口宽测量后溢出。改为把 Padding 放到内层 `Border`（主窗口、设置、详情、版本窗口统一） |
| P1 | 扫描完成后部分角标显示为空 | 被折叠的 `TextBlock` 在容器复用时保持 0 宽测量。`Galleries` 替换后对 `ItemsRepeater` 做一次 `InvalidateMeasure`；冒烟新增 `firstCardBadgeOk`/`emptyBadges` 断言 |

设计取舍：

- **只在跨源合并**：同一目录里相同 GID 的多个文件夹通常是不同汉化/版本，隐藏它们会让用户失去可选项。
- **无 GID 不合并**：标题相同不足以证明是同一作品，按路径识别更安全。
- **顺序扫描**：备份盘多为机械盘，多源并发只会互相拖慢；进度按“源序号 + 源内进度”折算成总进度。
- **不引入“合并后删除副本”能力**：资料库严格只读，版本管理只做选择。

### 8.5 未采纳/延后

- 独立 `EhViewerReader.App.Tests`（Headless）项目：当前用 `--self-test`/`--ui-smoke` 覆盖，避免新增 Avalonia.Headless 依赖。
- 全局 `PageBitmapCache` 引用计数缓存：改为控件自持 + 单页预取，内存上界更小（实测峰值 255 MB）。
- 只读 ACL 自动化：已实现快照哈希比对；ACL 副本验证留作手工步骤（README 记录）。
- 双页模式、继续阅读条：保持可选，不阻塞验收。
