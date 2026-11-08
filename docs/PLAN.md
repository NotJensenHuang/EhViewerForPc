# EhViewer 本地漫画阅读器 — 实施计划

> 目标平台：Windows 桌面（Avalonia，.NET 10）
> 参考实现：https://github.com/xiaojieonly/Ehviewer_CN_SXJ
> 验证资料库：`D:\BackUp\phoneGT5pro\ehviewer`（只读，绝不修改）
> 工作目录：`G:\SoftwareProject\Avalonia\EhviewerDownloadReader`

## 1. 目标与边界

### 1.1 必须实现

1. 完整扫描 EhViewer 下载目录，识别画廊、封面、页数、缺页、语言标签等本地信息。
2. 兼容 EhViewer 的下载目录格式（含新旧 `.ehviewer` 头、CBOR 头、`.thumb` 无扩展名图片、8 位数字页文件名、混合扩展名）。
3. 现代化桌面画廊库：封面网格、搜索、排序、筛选、收藏、阅读进度、继续阅读。
4. 单页阅读器：适应页面/宽度/高度/原始大小、缩放平移、键盘/滚轮/点击翻页、阅读方向、全屏、进度恢复。
5. 连续阅读（条漫/Webtoon）模式：虚拟化纵向长图流、当前页跟踪、进度恢复。
6. 双页模式（作为增强）：两页合成一屏，右到左/左到右。
7. 所有应用数据写入 `%LocalAppData%\EhViewerReader`，资料库目录严格只读。
8. 在真实资料库（6,315 个画廊、244,639 页、约 94 GB）上完成扫描、缩略图、阅读、性能验证。

### 1.2 非目标

- 不实现 EH 网站登录、在线搜索、下载、标签抓取（无网络请求）。
- 不读取 Android 应用私有数据库；标题来自目录名，元数据来自 `.ehviewer`（存在时）。
- 不做删除/移动/重命名资料库文件的操作。
- 不做跨设备同步。

## 2. 下载格式兼容性（已从源码确认）

EhViewer 源码关键点：

- `SpiderDen.getGalleryDownloadDir()`：目录名 `gid + "-" + suitableTitle`，并做文件名清洗。
- `SpiderDen.generateImageFilename(index, ext)`：`String.format("%08d%s", index + 1, ext)`。
- `GalleryProvider2.SUPPORT_IMAGE_EXTENSIONS`：`.jpg`, `.jpeg`, `.png`, `.gif`, `.webp`。
- `SpiderInfo`：
  - 文本格式 `VERSION2` 头：
    ```
    VERSION2
    <8位十六进制 startPage>
    <gid>
    <token>
    1
    <previewPages>
    <previewPerPage>
    <pages>
    <index> <pToken>
    ...
    ```
  - 旧版本（首行不是 `VERSION...`，视为 VERSION1）：
    ```
    <8位十六进制 startPage>
    <gid>
    <token>
    <unknown>
    <previewPages>
    <被消费但忽略的行>   ← 源码在 V1 下也读取这一行，只是不解析
    <pages>
    <index> <pToken>
    ...
    ```
    注意：不能按“VERSION1 少一行”实现，否则会把 pages 读成下一行并整体错位。
  - 下载目录内的 `.ehviewer` 以文本为主；新版恢复逻辑还兼容 CBOR map（`gid`, `token`，可跳过 `pTokenMap`）。本阅读器将兼容两种。
  - 页数上限 100,000，逐行 pToken 解析需容错。
- `.thumb`：EhViewer 保存的画廊封面，无扩展名；实测为 JPEG 或 WebP，也可能为 0 字节。必须按文件头嗅探，失败时回退到第 1 页生成封面。
- 实测资料库：
  - 目录 6,315 个；图片 244,639 张：jpg 187,705 / webp 56,571 / png 300 / gif 63。
  - `.ehviewer` 6,156 个（全部 `VERSION2`），`.thumb` 6,124 个（JPEG 4,882 / WebP 1,182 / 0 字节 60）。
  - 24 个画廊存在缺页（页号不连续），1 个目录无图片；必须按“实际存在的页”阅读并提示缺页。
  - 根目录存在 `.nomedia`、`.stfolder`、`*.tmp` 目录，必须过滤。

### 2.1 目录识别规则

1. 枚举根目录一级子目录；跳过隐藏/系统目录、`.stfolder*`、根目录下 64 位十六进制+`.tmp` 的临时目录、非目录项；不按“标题以 .tmp 结尾”粗暴过滤。
2. **先读取 `.ehviewer` 头，再确定身份**：
   - 目录名匹配 `^(?<gid>\d+)-(?<title>.+)$` 时取目录名 gid/title；
   - 目录名没有 gid 时，使用 `.ehviewer` 中的 gid（真实库存在 5 个此类目录）；title 使用完整目录名；
   - 目录名 gid 与 header gid 不一致时记录异常，身份用复合键，不静默合并。
3. 通过 `Directory.EnumerateFiles` 匹配 `^(?<index>\d{8})\.(?<ext>jpg|jpeg|png|gif|webp)$`（扩展名不区分大小写）。
4. 每页保留候选扩展列表：按 EhViewer 优先级 `.jpg/.jpeg/.png/.gif/.webp` 排序，过滤 0 字节，优先选择可解码候选；优先候选解码失败时可回退下一候选。页面逻辑页号不变。
5. 无图片目录仍保留在库中并标记“未完成/0 页”，可查看详情/打开文件夹，但不可进入阅读器；不静默消失。
6. 页数为 0 的目录不进入阅读器。
7. 缺页：
   - `PresentPages` = 实际存在的原始页号集合；
   - `ExpectedPages` = `.ehviewer` 的 `pages`（VERSION1/2 文本头且合法时；CBOR 头没有 pages）；
   - `MissingPages = [1..ExpectedPages] \ PresentPages`；
   - 无 header 时 `ExpectedPages = null`，只检测中间 gap，UI 标注“页数未知，可能不完整”；
   - 实际最大页号大于 header 页数时，以实际文件为准并标记 header 过期。
8. 重复 gid 目录：**全部保留**，标记 `DuplicateGid`；按 `HasEhviewer > HasThumb > PageCount > LastWriteUtc > 路径` 选出主项仅为展示排序，不删除/隐藏另一项。
9. 稳定键 `StableId` 统一用于进度、收藏、封面/尺寸缓存：
   - gid 全局唯一：`gid`；
   - gid 重复：`gid@pathHash`（所有重复项都带 pathHash，避免扫描顺序影响）；
   - 无 gid：`dir@pathHash`；
   - pathHash = 规范化完整路径（Windows 大小写不敏感）SHA-256 前 12 位。
10. 页面导航使用“现有页序号”，但显示/保存原始页号；缺页时给出映射提示。

## 3. 技术选型

| 领域 | 选型 | 理由 |
| --- | --- | --- |
| UI | Avalonia 11.3.6 + Fluent Theme（Windows 优先） | 模板环境已有；Windows-only 包可避开 DBus/NU1903 |
| 运行时 | .NET 10（global.json 固定 SDK 10.0.402；发布可 self-contained） | 本机已安装，Avalonia 11 兼容 |
| MVVM | CommunityToolkit.Mvvm | 源生成器，减少样板 |
| 图片解码 | SkiaSharp 2.88.9（与 Avalonia 11.3.6 配套，显式锁定） + Avalonia `Bitmap.DecodeToWidth` | 支持 JPEG/PNG/GIF/WebP；SKCodec 读尺寸，Avalonia 解码/降采样 |
| 虚拟化列表/网格 | `Avalonia.Controls.ItemsRepeater` **11.1.5** + `UniformGridLayout` / `StackLayout` | 已 spike 验证 6,315 假项初始仅实现约 14 个容器；版本必须锁定 |
| 持久化 | System.Text.Json + 临时文件/原子替换 | 无数据库依赖，易诊断 |
| 测试 | xUnit + 真实资料库只读集成测试（环境变量门控）+ 应用内 `--self-test` | 覆盖格式、性能、图片解码 |

依赖锁定（Directory.Packages.props）：
- `Avalonia 11.3.6`、`Avalonia.Win32`、`Avalonia.Skia`、`Avalonia.Themes.Fluent`（避免 `Avalonia.Desktop` 的 X11/DBus NU1903）。
- `Avalonia.Controls.ItemsRepeater 11.1.5`（spike 已通过；不是 11.2/11.3 版本）。
- `SkiaSharp 2.88.9`（Avalonia 11.3.6 依赖版本；不可漂移到 3.x）。
- `CommunityToolkit.Mvvm 8.x`、`xunit` 等测试包。

备选：若 ItemsRepeater 在真实模板下虚拟化失效，降级为“按行分组的 ListBox + VirtualizingStackPanel”或自研 `VirtualizingWrapPanel`；**不使用全量 WrapPanel + 分批加载**（最终仍会累积容器）。

## 4. 工程结构

```
EhViewerReader.sln
src/
  EhViewerReader/
    EhViewerReader.csproj          # Avalonia 桌面应用
    App.axaml / App.axaml.cs
    Program.cs                     # CLI 解析、单例、启动
    Assets/                        # 图标、字体（可选）
    Models/
      Gallery.cs
      GalleryPage.cs
      SpiderInfo.cs
    Services/
      AppPaths.cs
      SettingsService.cs
      LibraryScanner.cs            # 格式识别、目录扫描、缓存
      SpiderInfoReader.cs          # VERSION1/2 + CBOR
      CoverService.cs
      ImageLoader.cs               # 降采样、LRU、并发限制
      ProgressStore.cs
      LibraryCache.cs
      LogService.cs
      LibraryWatcher.cs（可选）
    ViewModels/
      MainViewModel.cs
      GalleryItemViewModel.cs
      ReaderViewModel.cs
      ContinuousPageViewModel.cs
      SettingsViewModel.cs
      GalleryDetailsViewModel.cs
    Views/
      MainWindow.axaml(.cs)
      ReaderWindow.axaml(.cs)
      SettingsWindow.axaml(.cs)
      GalleryDetailsWindow.axaml(.cs)
      Controls/
        AsyncImage.axaml(.cs)      # 懒加载、取消、占位/错误
        ZoomPanView.axaml(.cs)     # 单页/双页平移缩放绘制
        ContinuousPageView...
    Styles/                        # 颜色、卡片、按钮、滚动条
  EhViewerReader.Core/             # 纯逻辑（无 Avalonia 依赖）
    ...
tests/
  EhViewerReader.Core.Tests/
docs/
  PLAN.md
  FORMAT.md
  TEST_REPORT.md
README.md
```

实际实现可将 Core 合并进主项目以降低复杂度；但格式解析、扫描、缓存、进度必须可独立单元测试。默认保留 Core 项目。

## 5. 数据模型与扫描/缓存

### 5.1 模型

```csharp
Gallery {
  string StableId;          // gid / gid@pathHash / dir@pathHash
  long? Gid;                // 展示与搜索；不是唯一键
  long? HeaderGid;          // .ehviewer 中的 gid（可能没有）
  string Title;             // 目录名去掉 gid 前缀（无 gid 时用完整目录名）
  string FolderPath;
  int PageCount;            // 实际存在页数
  int? ExpectedPages;       // .ehviewer 声明页数；CBOR 头没有此项
  IReadOnlyList<PageRange> MissingRanges;
  bool HasMissingPages;
  bool IsDuplicateGid;
  bool IsIncomplete;        // 0 页或缺页/无 header
  string CoverPath;         // .thumb 或第一个可解码页
  string? CoverKind;        // jpeg/webp/png/gif
  string[] Languages;       // 从标题标签推导：Chinese/English/Japanese...
  bool HasDigitalTag, IsAI, IsDecensored; // 可选展示
  DateTime LastWriteUtc;
  string ExtensionSummary;
  bool IsFavorite;          // 由 ProgressStore 注入
  ReadingProgress? Progress;
}

GalleryPage {
  int Number;               // 原始页号（1-based，来自 8 位文件名）
  int DisplayIndex;         // 现有页序号（0-based）
  IReadOnlyList<PageCandidate> Candidates; // 同页号多扩展，按优先级
  PageState State;          // Ok/Empty/Corrupt/Unreadable
}
PageCandidate { string FilePath; string Extension; long Length; }
```

打开画廊时生成页面列表；库扫描阶段只保留页数/最大页号/缺页摘要。页面文件尺寸（宽高）单独由 `PageDimensionService` 缓存，不把 244k 路径长期驻留内存。

### 5.2 扫描策略

- 首次冷扫描遍历一级目录 + 每目录页文件：目标“首屏渐进可用”，全量完成目标 < 15 秒（热缓存实测 3.86 秒；冷盘需实测，不作为硬承诺）。
- 缓存文件 `library-cache.json` 记录每个目录签名：目录 mtime、页数、最大页号、`.ehviewer` 长度+mtime、`.thumb` 长度+mtime、封面种类、缺页摘要。签名未变则跳过页枚举；缓存只是性能优化，不是正确性来源。
- 启动流程：
  1. 读取 settings + cache + progress。
  2. 枚举根目录，先用 cache 中签名未变项立即呈现。
  3. 后台扫描新增/变更项，完成后原子保存 cache 并刷新列表；提供“强制重扫”。
  4. 扫描/刷新不阻塞 UI；显示进度与可取消。
  5. HDD 上避免高并发随机寻道，扫描并发度默认 1-4，可配置。
- 正在下载的目录（存在 0 字节页/临时文件/页数变化）不标记为完整；下次启动重扫。
- 缓存文件带 schema 版本、临时文件 + 原子替换、损坏回退；单实例运行。
- 缓存、进度、尺寸缓存、封面缓存都放 `%LocalAppData%\EhViewerReader`。
- 监听根目录顶层变化可选；手动“刷新”必须可用。

### 5.2.1 页面尺寸缓存（连续模式必需）

- 打开画廊时（或首次切换到连续模式时）用 `SKCodec` 读图片头获取宽高/EXIF 方向，不解码整图。
- 缓存到 `%LocalAppData%\EhViewerReader\dimensions\{stableId}.json`：原始页号、宽、高、方向、文件长度、mtime。
- 只缓存打开过的画廊；文件长度/mtime 变化时失效。
- 读尺寸并发 4-8、可取消；失败页使用 3:4 占位并标记 `Unknown`。
- 连续模式在显示前尽量获取全部尺寸（带进度/取消）；这样 `StackLayout` 高度确定，避免滚动跳动。若某个 1,728 页画廊实测读取过慢，再降级为“可见页 ±20 优先测量 + 锚点补偿”。
- 进度恢复使用“原始页号 + 页内偏移”，不依赖绝对像素。

### 5.3 封面

- 优先直接读取 `.thumb`；读取至少 12 字节并校验：JPEG `FF D8 FF`、PNG `89 50 4E 47`、GIF `GIF8`、WebP `RIFF....WEBP`。
- 0 字节/截断/解码失败：回退到“第一个可解码页”，生成 320px 宽缩略图。
- 可选磁盘缓存到 `%LocalAppData%\EhViewerReader\covers\{stableId}-{thumbLength}-{thumbMtimeTicks}.jpg`（不写资料库）；缓存键包含来源长度+mtime，源变化自动失效。
- 封面 Bitmap 由 `AsyncImage` 控件独占，容器回收/DataContext 变化时取消加载并 Dispose，不做跨控件共享 LRU（避免悬空引用）。
- 并发解码上限 4；`SemaphoreSlim` + `CancellationToken`；滚动时取消过期请求。
- 解码宽度按卡片实际像素宽度 × RenderScaling 计算，避免高 DPI 模糊。

### 5.4 进度/收藏

- `progress.json`：key 为 `StableId`（不是裸 gid；重复 gid 用 `gid@pathHash`，无 gid 用 `dir@pathHash`）。
- 字段：`pageNumber`（原始页号，1-based）、`pageIndex`（当时现有页序号）、`pageOffset`（页内滚动比例 0-1）、`pageCountAtSave`、`lastReadUtc`、`completed`、`favorite`。
- 恢复：优先按 `pageNumber` 精确匹配；缺失时取“≤保存页号的最近存在页”，再夹取 `pageIndex`，并提示“原第 N 页缺失，已定位到第 M 页”。
- 进入/翻页/滚动/关闭窗口节流保存（>=1s）；崩溃最多丢失约 1 秒进度。
- 完成判定：到达最后一页且停留/退出。
- “标记已读/未读/清除进度”支持。

### 5.5 设置

`settings.json`：资料库根目录、主题（System/Dark/Light）、默认阅读模式、默认适应模式、阅读方向（RTL/LTR）、背景色、连续模式最大解码宽度、图片缓存预算、排序/筛选偏好、窗口尺寸。
- 首次启动无根目录：欢迎页选择目录；也支持 `--root <path>` 和 `--gallery <gid>` 启动参数。
- 设置即时生效；根目录切换需要确认并重新扫描（不修改文件）。

## 6. 图片加载与内存（修订版）

### 6.1 统一像素预算

解码尺寸由四个约束共同决定（不允许“最长边 4096”把长条图压成 267px 宽）：

```text
scale = min(
    1,                                                    // 不放大
    targetWidth / originalWidth,                          // 视口宽度 × RenderScaling
    targetHeight / originalHeight,                        // 单页 fit 时
    sqrt(maxPixels / (originalWidth * originalHeight)),   // 默认 24 MP
    maxDimension / max(originalWidth, originalHeight)     // 默认 16000，低于常见 16384 纹理上限
)
decodeWidth = max(1, round(originalWidth * scale))
```

- 使用 `SKCodec` 读原始宽高/方向；用 `Bitmap.DecodeToWidth(stream, decodeWidth, HighQuality)` 解码。
- 对 `977×14998` WebP：不放大，按原始 977 宽解码（约 58 MB），远低于“2400 宽”导致的 337 MB。
- 对 `5304×7952` JPEG：FitPage 时按视口降采样；Original/8x 时仍受 24 MP + 16000 维度约束，绝不超过预算。
- 单张图片估计字节数用 `decodeWidth * decodeHeight * 4`；大图（>32 MB）解码走单独的“大图信号量”（并发 1），普通图并发 4。
- 全局页面内存预算默认 256 MB；封面独立由控件持有（约 64 MB 上限），预取页计入页面预算。
- GIF/WebP 动图只取首帧；不加载全部帧。文档注明不会播放动画。

### 6.2 单页模式

- 目标宽度 = 视口宽度 × RenderScaling（FitWidth/FitPage）；FitHeight 时按高度反推宽度；Original 时按 DPI 比例。
- 预取前后各 1 页；预取只在估计字节数允许时进行。
- 页面缓存只保留当前页 ±1-2 页，按字节预算淘汰；**当前页永不被淘汰**。
- 窗口尺寸/DPI 变化导致目标宽度明显变化时重新解码（缓存 key 带宽度档位）。

### 6.3 连续模式

- 每页宽度适应视口；使用尺寸缓存得到确定高度，列表高度稳定，无滚动跳动。
- 虚拟化只保留可见容器；每个 `AsyncImage` 独占 Bitmap，回收时取消加载并 Dispose。
- 每页解码宽度 = min(原始宽度, 视口宽度 × RenderScaling)，受 24 MP/16000 限制。
- 不预取全部页；滚动时加载可见页 ±2。

### 6.4 Bitmap 生命周期（必须遵守）

- `AsyncImage` 每次加载带代际号 + `CancellationTokenSource`；容器回收或 DataContext 变化时取消旧请求。
- await 返回后校验代际号与当前数据项；不匹配则立即 Dispose，绝不写回新项。
- 控件 detach 时 `Source = null` 并 Dispose 自有 Bitmap。
- 页面缓存淘汰时只 Dispose 不再被 UI 引用的 Bitmap；当前页/正在显示的 Bitmap 不进入淘汰。
- 不做“全局 LRU 直接 Dispose 显示中图片”的危险设计。

### 6.5 加载状态

- 占位骨架、加载动画、失败重试；坏页保留原始页号，可“跳到下一可读页”。
- 解码失败不崩溃，记录日志（不含敏感内容）。

## 7. UI/UX 设计

### 7.1 视觉方向

- 默认深色：背景 `#0B0D10`，面板 `#15181E`，卡片 `#1B1F27`，主强调色珊瑚红 `#FF6B81`，辅助青灰。
- 细圆角（10-14px）、轻微描边、封面底部渐变遮罩、标题最多两行、进度细条。
- 中文字体优先 `Microsoft YaHei UI`；数字/页码使用等宽感排版。
- 动效克制：卡片 hover 抬升/描边、页面淡入、工具栏 hover 显示；尊重减少动画设置（可选）。
- 可访问性：按钮有 ToolTip/自动化名称，键盘可达；对比度达标；支持窗口缩放 960×640 起。

### 7.2 库视图（MainWindow）

- 顶栏：品牌标识、搜索框（带清除）、排序下拉、筛选 chips（全部/在读/未读/已读/收藏）、语言筛选、刷新、设置。
- 网格：`ItemsRepeater` + `UniformGridLayout`，卡片最小宽 170-200；封面比例约 3:4；显示标题、页数、语言标签、进度条/完成勾。
- 卡片交互：单击打开阅读器；右键菜单：阅读、打开文件夹、查看详情、收藏、标记已读/未读。
- 命令不放在每个 item VM 上：`MainViewModel` 持有单例命令，卡片/菜单用 `CommandParameter` 传 item（避免 6,300 组命令对象）。
- 状态栏：显示总数、总页数、扫描状态、缓存命中；空状态/无结果状态/错误状态。
- 继续阅读条：首轮不做，核心稳定后可选。
- 搜索：`StringComparer.OrdinalIgnoreCase` 标题匹配；gid 数字精确匹配；250ms 防抖。
- 排序：最近阅读、最近下载（目录 mtime，仅作次要）、标题、页数、gid；稳定 tie-break（标题/gid/路径）。
- 过滤/排序批量替换 `ItemsRepeater.ItemsSource`（master VM 列表复用），禁止逐项 `Add` 触发 6,300 次通知。
- 详情窗口：路径、gid、实际/预期页数、缺页区间（如 `12-14, 34, 56-58`）、扩展名统计、封面预览、打开文件夹、清除进度。
- 键盘可达性：首版以鼠标为主，阅读器完整键盘操作；卡片提供 Tab 焦点 + Enter 打开，不做 6,300 项方向键 roving focus。

### 7.3 阅读器（ReaderWindow）

布局：
- 顶部细栏：返回、标题、页码、模式切换（单页/连续/双页）、阅读方向、适应模式、全屏、更多。
- 中央画布：`ZoomPanView` 绘制单页/双页；暗色背景。
- 底部：进度滑条/页码按钮、上一页/下一页；工具栏可 Tab 隐藏。
- 打开时恢复进度并显示“已恢复到第 N 页”轻提示。
- 加载中/失败占位。

单页交互契约（`ZoomPanView` 必须明确实现）：
- `MeasureOverride` 返回视口尺寸；`Render` 按 scale/offset 绘制；平移 clamp 到图片边界。
- 滚轮优先级：`Ctrl` = 缩放 > 内容溢出 = 平移 > 到边界 = 翻页。
- 指针按下/移动/释放用 4px 阈值区分 click/drag；只有放大/内容溢出时才 capture pointer。
- 单击左/右 1/3 翻页（方向可反转）；双击仅在非拖动时触发 FitPage ↔ 100%。
- 方向键/PageUp/PageDown/Space/Home/End 翻页；`+/-/0` 缩放；`F11` 全屏；`Esc` 第一次退出全屏、第二次关闭；`M` 切换模式；`D` 切换方向；`?` 快捷键帮助。
- 适应模式：FitPage（默认）、FitWidth、FitHeight、Original；缩放范围 1x-8x（相对适应比例），滚轮以指针为中心。
- 高 DPI 解码宽度使用 `RenderScaling`；窗口尺寸变化重新适配。
- 预取相邻页，缓存命中翻页目标 < 150ms。
- `e.Handled` 阻止事件冒泡，避免滚轮同时翻页两次。

连续模式：
- `ItemsRepeater` + `StackLayout`；显示前读取/缓存全部页面尺寸（带进度和取消），每页高度确定。
- 每页懒加载，宽度适应视口；可视区域外回收 Bitmap。
- 滚动时更新当前页/页内偏移；切回单页时定位到当前页；恢复进度按原始页号 + 偏移。
- 无缩放；提供可选的居中最大阅读宽度（默认 1400px）。

双页模式（核心稳定后的增强，不阻塞首轮验收）：
- 默认“封面单独显示”：第 1 页单页，之后 `(2,3),(4,5)...` 配对；RTL 只反转左右，不改变配对。
- 以当前页为组合起点；两页共享缩放/平移；进度按配对第一页；最后一页单页显示。
- 若超宽/跨页图，仍按顺序展示；可在后续优化自动单页。

全屏/沉浸：
- 全屏隐藏窗口边框；工具栏自动淡出（可选），键盘/鼠标移动唤回；Esc 退出。

## 8. 验证与测试计划

### 8.1 单元测试（不依赖真实资料库）

- VERSION2 / VERSION1 文本头解析，**V1 的“被消费但忽略的行”**；CRLF、缺行、坏 pToken 行、BOM、pages=0/100001。
- CBOR 不定长/定长 map，gid/token 提取；32MB/深度16/key64/token128 上限；损坏/超大嵌套拒绝；截断、重复 key、负 gid。
- 目录名解析（数字 gid、无 gid、Unicode、长名、尾随空格）；目录名 gid 与 header gid 冲突。
- 页文件识别（8 位、扩展名优先级、大小写、缺页、重复页号、同页多扩展、0 字节候选、忽略 `.thumb`/`.ehviewer`/`*.tmp`/`Thumbs.db`）。
- 缺页检测（中间缺页、尾部缺页、重叠、无 header、header pages 小于实际最大页号）、页数/扩展名统计。
- 缓存签名失效、序列化往返、schema 版本、损坏回退、原子写入。
- 稳定键、重复 gid、无 gid、目录改名后的进度隔离。
- 进度按原始页号恢复（缺页/增页/删页）、完成状态、收藏。
- 设置默认值与非法值回退。
- 封面 12 字节签名嗅探（JPEG/WebP/PNG/GIF/0 字节/截断）、回退第一个可解码页。
- EXIF Orientation 1/6/8 样本（至少确认展示方向和宽高比）。

### 8.2 真实资料库集成测试（只读，环境变量门控）

- 通过 `EHVIEWER_TEST_ROOT` 指定路径；未设置时动态跳过（不硬编码 `D:\...`），打 `[Trait("Category","Integration")]`。
- 扫描：断言 ≥6,300 画廊、≥244,000 页（范围断言，不写死精确数）；记录实际统计和口径。
- 固定样本：
  - `1002892`：20 页，封面/首末页可解码。
  - `3771419`：WebP，10 页。
  - `3627472` p22：同页 jpg+webp 均 0 字节 → 错误占位，不崩溃。
  - 5 组同页多扩展；22 字节图片；0 字节 `.thumb`；缺失 `.thumb`；无 `.ehviewer`。
  - 5 个无 gid 目录、重复 gid 2253457、零页目录 4164661。
  - 缺页样本（中间/尾部）。
  - 最大页数画廊 1,728 页：读取尺寸并加载首/中/末页；跳到第 1,000 页。
  - 长条图 `977×14998`；最大图 `5304×7952`：解码尺寸/峰值内存在预算内。
  - 300 字符路径/661 个 >260 路径抽样。
- 冷/热扫描耗时；缓存命中后热启动目标 < 3s（记录硬件条件）。
- 只读验证：对资料库快照“相对路径+大小+mtime+抽样 SHA-256+ADS”；应用自身 create/modify/delete/rename 必须为 0。用 `FileSystemWatcher` 监控并只统计本进程。
  - “打开文件夹”由 Explorer 打开，可能生成 `Thumbs.db`/`desktop.ini`，不属于应用写入；该功能不纳入零写入断言。
  - 额外用只读 ACL/只读副本运行扫描/阅读，验证无异常。
- 所有测试不修改下载目录；证据写入 `TEST_REPORT.md`。

### 8.3 应用自检与 UI 冒烟

- `--self-test --root <path>`：初始化服务、扫描、解码封面/页面，输出 JSON 报告并以退出码表示成功。
- 使用临时合成资料库（程序生成的纯色/几何测试图，不含敏感内容）运行完整 UI，验证：
  - 启动无异常、库网格渲染、搜索/排序/筛选、打开阅读器、翻页、连续模式、全屏、设置。
  - 通过应用内渲染截图保存 PNG 进行视觉检查（仅在合成库上）。
- 真实库仅做程序化断言/UI 树检查；不使用 lvt 截图敏感封面。lvt 为可选，不作为发布门槛。
- ItemsRepeater smoke（M1 gate）：6,315 个假卡片，已实现容器数 <100；替换 ItemsSource（搜索/排序）后仍虚拟化且不崩。

### 8.4 性能/稳定性验收（记录环境）

记录：CPU/GPU/RAM、磁盘类型、Windows 版本、RenderScaling、渲染后端；冷/热定义；3 次取中位数。

- 冷扫描全量完成目标 < 15s（不达标则改为“首屏渐进 + 后台完成”，记录实测）；热启动 < 3s。
- 库网格：6,315 项滚动无明显卡顿；容器数受控；帧时间 p95 记录。
- 单页翻页缓存命中 < 150ms；首开 < 1s（首屏）。
- 1,728 页画廊：首屏 < 1s（尺寸缓存热）、跳转第 1,000 页可完成、恢复 ±1 页、滚动不白屏。
- 长条图 `977×14998` 与 `5304×7952` 解码峰值在预算内；4 路并发不 OOM。
- 阅读 100 页后 managed heap + private working set 记录；目标 < 1GB（以实测调整，不先写死）。
- 缺少 `.ehviewer`/`.thumb`、0 字节封面、坏图片、缺页、超长中文路径均不崩溃。
- 睡眠/恢复、快速连续翻页、窗口缩放不崩溃。

### 8.5 发布

- `dotnet build -c Release` 目标：除已审计警告外零警告；若使用 Windows-only Avalonia 包应无 NU1903。
- `dotnet test` 单元测试全绿；集成测试通过 `EHVIEWER_TEST_ROOT` 单独运行。
- `dotnet publish -c Release -r win-x64 --self-contained false` 可运行；README 说明 .NET 10 运行时要求及 self-contained 可选命令。
- 生成 `TEST_REPORT.md` 记录真实数据统计、耗时、内存、已知限制。

## 9. 里程碑（无人值守执行顺序，垂直切片）

1. **M0 计划评审**：2 个子代理分别审查兼容性/性能与 Avalonia 技术风险/UX；已修订计划。
2. **M1 工程骨架与风险验证**：solution、Core/App/tests、global.json、依赖锁定、主题资源、日志/路径；ItemsRepeater 6,315 项 smoke；Windows-only Avalonia 包验证；长路径 manifest。
3. **M2 格式与扫描**：SpiderInfo（V2/V1/CBOR）、StableId、目录/页扫描（候选扩展/缺页/0 页）、缓存签名、封面嗅探、进度存储；全部单元测试 + 真实库只读扫描。
4. **M3 库 + 单页阅读垂直切片**：MainViewModel、ItemsRepeater 网格、AsyncImage、搜索/排序/筛选/收藏、单页 ZoomPan、进度恢复、真实样本阅读。
5. **M4 连续模式 + 设置/详情 + 全屏/快捷键**：尺寸缓存、连续虚拟化、设置/详情窗口。
6. **M5 真实库验证**：集成测试、自检、性能/内存、长条图、1,728 页、临时合成库 UI 截图/视觉检查；修复。
7. **M6 收尾与可选增强**：README、FORMAT、TEST_REPORT、发布构建、最终子代理代码审查；双页模式/继续阅读条作为可选增强，不阻塞验收。

每个里程碑有 exit criteria；M3/M4 不通过真实样本 gate 不进入下一阶段。

## 10. 风险与对策

| 风险 | 影响 | 对策 |
| --- | --- | --- |
| ItemsRepeater 11.1.5 与 Avalonia 11.3.6 | 虚拟化失效 | 已 spike 通过；锁定版本；M1 用真实模板再测；后备 ListBox/VirtualizingWrapPanel |
| 6,315 目录冷扫描慢/阻塞 | 启动体验差 | 后台扫描 + 签名缓存秒开 + 渐进显示 + 进度/取消 |
| 长条图/4K 大图内存爆炸 | OOM/卡顿 | 24MP/16000/视口三约束、大图串行、控件独占 Bitmap、页面预算 256MB |
| Bitmap 生命周期/异步回收 | 白屏/错图/崩溃 | 代际号 + CancellationToken；容器回收 Dispose 自有图；当前页不淘汰 |
| WebP/GIF 解码兼容 | 部分页失败 | SkiaSharp 2.88.9 + 真实 WebP/GIF 测试；失败占位/重试 |
| 长路径 >260 字符 | 无法打开 | app.manifest longPathAware；`\\?\` 回退；按文件捕获异常；真实样本测试 |
| 进度串号（重复 gid/无 gid） | 数据错误 | StableId 策略；重复 gid 全部保留；单元测试 |
| 缺页/同页多扩展/坏文件 | 阅读顺序错误 | 原始页号 + 候选扩展 + 可解码校验 + 缺页区间提示 |
| 下载目录被误写 | 用户数据风险 | 只读抽象；写入集中 AppPaths；进程级 FileSystemWatcher + 快照测试 |
| 真实库敏感内容截图 | 隐私 | UI 截图仅在合成库；真实库程序化断言/UI 树 |
| NU1903/依赖漂移 | 构建/运行风险 | Windows-only Avalonia 包；锁定 SkiaSharp/ItemsRepeater；审计 |
| 任务范围过大 | 未完成 | 垂直切片；双页/继续阅读/动画延后 |
| 无 .NET 10 运行时 | 无法启动 | 提供 framework-dependent + self-contained 发布说明 |

## 11. 验收清单

- [x] 可直接选择 `D:/BackUp/phoneGT5pro/ehviewer`，缓存就绪后 3s 内显示库（热缓存 0.36 s；冷盘首次 81 s 后台完成）。
- [x] 6,000+ 画廊封面网格可流畅滚动（6,315 项仅 16–20 个容器）；搜索/排序/筛选/收藏可用。
- [x] 打开真实画廊，单页/连续模式可阅读，进度在重启后恢复；双页为可选增强（未实现）。
- [x] `.ehviewer`（VERSION1/2/CBOR）、`.thumb`、8 位页名、缺页、混合扩展名全部兼容。
- [x] 长条图（合成 977×14,998，55.9 MB）/5,304×7,952 最大图/1,728 页画廊均在预算内加载，无 OOM。
- [x] 下载目录应用零写入（257,966 文件 / 101 GB 快照哈希一致）；所有缓存/设置/进度在 LocalAppData。
- [x] 单元测试、真实库集成测试、自检、发布构建全部通过（34/34，0 警告）。
- [x] README/格式说明/测试报告/续作计划齐全。

## 12. 子代理评审记录

### 评审 1：兼容性与性能（general）

结论：暂不进入实施，需先修订长条图像素预算与连续模式高度模型，并完成 spike。主要 P0/P1：
- 连续模式 2400-3000px 宽解码会让 `977×14998` 长条图膨胀到 337-527MB；必须改为像素预算（建议 16-24MP + 最大维度 + 视口宽度三约束），必要时分块。
- 连续模式必须定义尺寸来源/占位/恢复锚点；建议 per-gallery 尺寸缓存 + 原始页号恢复。
- 稳定键不能用裸 gid；重复 gid 两个目录都保留；目录名无 gid 时用 header gid。
- 缺页检测要基于 `ExpectedPages`，否则漏掉 13 个尾部缺页；进度保存原始页号。
- 同页多扩展要选可解码候选，不能盲选 `.jpg`；`.thumb` 嗅探需 12 字节。
- 只读验收要区分应用写入与 Explorer 写入；长路径 manifest/`\\?\`；SkiaSharp 锁 2.88.9；ItemsRepeater 做规模 spike；缓存签名不能只看目录 mtime。
- CBOR 需安全上限；token 不落日志/缓存。

### 评审 2：Avalonia 技术风险与 UX（general）

结论：有条件 Go。主要 P0/P1：
- ItemsRepeater 实际最高 11.1.5（已 spike 6,315 假项初始 14 容器）；锁定版本，fallback 不能用全量 WrapPanel。
- VERSION1 解析漏了“被消费但忽略的行”，必须修正。
- 连续模式宽高来源未定义，需要 `PageDimensionService`/尺寸缓存；建议 ListBox+VSP 或先测 ItemsRepeater 定位。
- AsyncImage/Bitmap 生命周期缺失，存在 Dispose 显示中 Bitmap 和异步错图风险；引入代际号/取消/控件独占/引用保护。
- 进度键不能只用 gid（真实库重复 gid 2253457）。
- 单页滚轮/点击/拖动语义需明确；命令不要每项一份；过滤批量替换 ItemsSource；global.json 固定 SDK；集成测试环境变量门控；NU1903 用 Windows-only 包规避；范围裁剪（双页/继续阅读/真实库截图延后）。

### 修订处置

- [x] 修正 VERSION1 解析顺序与示例。
- [x] StableId、重复 gid、无 gid header gid、目录名/header 冲突处理。
- [x] ExpectedPages/尾部缺页/原始页号进度恢复。
- [x] 同页多扩展候选 + 可解码回退；0 字节；12 字节封面嗅探。
- [x] 像素预算/长条图/大图串行/控件独占 Bitmap/页面缓存保护。
- [x] 连续模式尺寸缓存 + 全部尺寸预读（带进度/取消）+ 原始页号/页内偏移恢复。
- [x] ItemsRepeater 11.1.5 + Avalonia 11.3.6 + SkiaSharp 2.88.9 锁定；global.json。
- [x] Windows-only Avalonia 包规避 NU1903；长路径 manifest。
- [x] 只读验收语义；集成测试环境变量门控；动态统计。
- [x] 垂直切片里程碑；双页/继续阅读条/真实库截图延后为可选。
- [x] 失败页/候选回退/坏图占位/跳下一可读页。
- [x] 单页手势契约与批量过滤策略。
