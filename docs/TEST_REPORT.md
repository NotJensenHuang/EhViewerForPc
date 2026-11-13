# 测试与验证报告

> 生成时间：2026-11-08
> 资料库：`D:\BackUp\phoneGT5pro\ehviewer`（严格只读）
> 应用版本：Debug（功能验证）/ Release（发布构建）

## 1. 环境

| 项目 | 值 |
| --- | --- |
| CPU | Intel Core i5-13600KF（14C/20T） |
| 内存 | 32 GB |
| 系统 | Windows 11 专业版 10.0.26300 |
| 资料库磁盘 | D: 固定磁盘 2,795 GB（可用 389 GB） |
| 显示 | 3840×2160 @150%（RenderScaling 1.5） |
| .NET SDK | 10.0.402 |
| Avalonia | 11.3.6（ItemsRepeater 11.1.5，SkiaSharp 2.88.9） |

## 2. 单元测试

命令：`dotnet test tests/EhViewerForPc.Core.Tests -c Debug`

结果：**44 passed / 0 failed**（150 ms 级）。

覆盖：VERSION1/2/CBOR、0 字节/缺页/同页多扩展、目录名与 gid 冲突、稳定键、缓存签名失效（含原地改写）、缓存路径规范化、封面签名、EXIF/像素预算、原子写与损坏回退；多数据源合并（跨源合并 / 同源不合并 / 首选覆盖 / 完整优先 / 无 GID 不合并）、设置迁移（schema 1 根目录 → 数据源、路径去重、移除源清理首选映射）、按源隔离的扫描缓存。

## 3. 真实库集成测试（只读）

命令：

```bash
EHVIEWER_TEST_ROOT="D:/BackUp/phoneGT5pro/ehviewer" dotnet test tests/EhViewerForPc.Core.Tests
```

结果：**34 passed / 0 failed**（约 53 s，含三次全库扫描）。

断言：≥6,000 画廊、≥240,000 页、缺页/0 字节/重复 gid/无 gid 前缀/无 `.thumb`/WebP 均存在、最大画廊 ≥1,000 页、缓存热命中 ≥ 总数−10、热扫描不慢于冷扫描 +5 s、最大画廊首/中/末页 `SKCodec` 头可解析。

## 4. 真实库自检（`--self-test`）

报告：`docs/selftest-check.json`（快速自检）、`docs/selftest-final.json`（含只读快照）、`docs/selftest-deep.json`（全量尺寸探测）。

### 4.1 库统计

| 指标 | 实测 |
| --- | --- |
| 画廊 | 6,327 |
| 页 | 245,548 |
| 空目录 | 1 |
| 缺页画廊 | 27（39 区间） |
| 0 字节页画廊 | 13 |
| 无 `.ehviewer` | 162 |
| 无 `.thumb` | 256 |
| 重复 gid | 2 个目录 |
| 无 gid 前缀、header 有 gid | 5 |
| 最大页数 | 1,728 |
| 图片 | jpg 187,705 / webp 56,615 / png 300 / gif 63 |

### 4.2 扫描性能

| 场景 | 耗时 |
| --- | --- |
| 冷扫描（OS 元数据缓存已热） | 2.02 s |
| 冷扫描（完整冷盘：先做 257,966 文件快照后） | 81.08 s |
| 热扫描（签名缓存，6,327 命中 / 0 重扫） | 0.36–0.52 s |
| 集成测试冷扫描 | 4.44 s |

> D: 为 2.8 TB 机械盘；冷盘首次遍历 24.5 万页文件的随机元数据读取是主要成本。启动策略为“缓存优先即时呈现 + 后台扫描 + 进度/取消”，因此冷盘不会阻塞 UI；热启动 <1 s。

缓存 schema 2 的页级签名（数量/长度和/mtime/最大页号）使“目录 mtime 不变但页文件原地补写”也能触发重扫。

### 4.3 解码

| 样本 | 结果 |
| --- | --- |
| 12 个封面（360px 宽） | 9.5–22.4 ms/个，0.3–0.7 MB |
| 最大画廊（1,728 页）首/中/末页（1200px 宽） | 全部成功，约 10 ms/页 |
| 1,728 页尺寸预读（冷缓存 / 热缓存） | 2.23 s / 0.03 s，0 页未知 |
| 最大像素图（1,260×2,160） | 解码 700×1,200，3.2 MB，17.3 ms |
| 0 字节页 | 返回 null，无异常 |
| 全量探测（245,535 文件）最大图 5,304×7,952 | 解码 800×1,200，3.7 MB，170.7 ms |
| 全量探测长条图 268×3,000 | 按原始宽度解码 268×3,000，3.1 MB，26.4 ms |
| 合成长条图 977×14,998（`selftest-check`） | 解码 977×14,998，55.9 MB，46 ms（未放大、未超预算） |

### 4.4 内存

| 指标 | 实测 |
| --- | --- |
| Managed heap（解码后 GC） | 54.7 MB |
| Peak working set（含合成长条图与全库快照） | 255.2 MB |
| Working set | 171.4 MB |

### 4.5 只读验证

最终自检（`docs/selftest-final.json`）在扫描/解码前后对资料库做完整快照：

| 指标 | 扫描前 | 扫描后 |
| --- | --- | --- |
| 文件数 | 257,966 | 257,966 |
| 目录数 | 6,331 | 6,331 |
| 总字节 | 101,262,590,747 | 101,262,590,747 |
| 内容哈希（路径+长度+mtime） | `3BC70DB7…1B408A` | `3BC70DB7…1B408A` |

差异为 0，`libraryUnchanged = true`。应用自身文件仅写入 `%LocalAppData%\EhViewerForPc`（本次为临时数据目录）。

## 5. UI 冒烟（合成库）

命令：

```bash
EhViewerForPc.exe --ui-smoke --out docs/screenshots
```

报告：`docs/screenshots/ui-smoke.json`；截图：`library.png`、`library-search.png`、`library-multi-source.png`、`versions.png`、`details.png`、`reader-single.png`、`reader-continuous.png`、`settings.png`。

| 项目 | 结果 |
| --- | --- |
| 合成画廊 | 31（含 0 页、缺页、0 字节、无 gid、重复 gid、无 `.thumb`） |
| 搜索“合成样本 3” | 1 条命中 |
| 详情窗口 | 打开/截图正常 |
| 单页阅读 | 1/9，加载正常 |
| 连续模式 | 9 项，尺寸预读完成 |
| 进度恢复 | 关闭前 2/9，重开 2/9，通过 |
| 设置窗口 | 打开/截图正常 |
| 阅读器窗口模态性 | 非模态：阅读器打开期间主窗口 `IsEnabled = true` |
| 同一画廊重复打开 | 复用已有窗口（窗口数 1） |
| 关闭阅读器后的库滚动位置 | 640 → 640（`scrollStable = true`） |
| 关闭阅读器后的列表顺序 | 前 6 项 `StableId` 一致（`orderStable = true`） |
| 损坏页（0 字节）滚轮翻页 | 第 3/9 页解码失败，滚轮后到 4/9，`handled = true` |
| 首卡页数角标 | “2 页”，实测宽 19 px（`firstCardBadgeOk = true`），防止容器复用后角标消失 |
| 损坏页角标 | 所有不完整卡片均有可见的“N 个损坏页”标签（`emptyBadges = none`） |
| 库网格右边界 | 5 列完整可见（内层 Border 承担 Padding，末尾卡片不再被视口裁切） |

### 5.1 多数据源与版本合并（合成库）

第二个数据源包含前 3 个作品的副本（页数更少）以及 1 个只在备份中存在的作品。

| 指标 | 结果 |
| --- | --- |
| 数据源数量 | 2（`sources = 2`） |
| 合并后作品数 | 31（30 + 仅备份存在 1） |
| 多版本卡片 | 3（其中 1 张含 3 份副本：同源重复 GID + 备份副本） |
| 跨源副本总数 | 7 |
| 同一卡片内 `WorkId` 一致 | `true` |
| 同一卡片内 `StableId` 互不相同 | `true` |
| 版本窗口行数 | 3，摘要“3 个版本 · 2 个数据源” |
| 版本窗口默认副本 | 页数最多（8 页），排序 8 / 4 / 2 |
| 仅备份存在的作品 | 正常显示为单版本卡片 |

### 5.2 虚拟化压力（6,315 个画廊）

| 指标 | 结果 |
| --- | --- |
| 画廊数量 | 6,315 |
| UI 扫描耗时 | 0.77 s |
| `AsyncImage` 容器（初始） | 26 |
| 滚动到末尾后的容器 | 26 |
| 列表总高度 | 577,508 px |

结论：`ItemsRepeater + UniformGridLayout` 在 6,315 项规模下保持容器数量恒定（26，<100），未出现全量实例化。

## 6. 发布构建

| 项目 | 结果 |
| --- | --- |
| `dotnet build EhViewerForPc.slnx -c Release` | 0 警告 / 0 错误 |
| `dotnet publish -r win-x64 --self-contained false` | 成功，产物在 `artifacts/win-x64` |
| `dotnet publish -r win-x64 -p:PublishAot=true` | 成功，`EhViewerForPc.exe` 22.9 MB |
| Release UI 冒烟 | 通过（截图与报告来自 Release 构建） |
| 单元测试（Release） | 44/44 通过 |
| 解决方案内容 | Core / App / `packaging/EhViewerForPc.Msix` / Tests（打包工程在解决方案构建时跳过 AOT 与打包） |

### 6.1 NativeAOT 验证

| 项目 | 结果 |
| --- | --- |
| 发布命令 | `dotnet publish src/EhViewerForPc -c Release -r win-x64 -p:PublishAot=true` |
| 原生可执行文件 | 22,886,400 B（另需 `libSkiaSharp.dll`、`libHarfBuzzSharp.dll`、`av_libglesv2.dll`） |
| 托管程序集 | 无（IL 全部编译为原生代码） |
| AOT 首次冒烟 | 通过，21.81 s（含 31 画廊合成库、6,315 项目录创建、全部截图） |
| AOT 二次启动 | 通过，加载 settings/progress/library-cache/dimensions 缓存 |
| AOT 多数据源 | 通过：2 个源 / 31 个作品 / 3 张多版本卡片 / 7 份副本；版本窗口 3 行 |
| AOT 设置往返 | 通过：`schemaVersion 2`、`sources[]`（id/rootPath/enabled/lastScanUtc）、`preferredSourceByWork` 正确读写，按源生成 `library-cache-<id>.json` |
| JSON 源生成 | `StorageJsonContext`（设置/进度/扫描缓存）、`ReaderJsonContext`（页面尺寸）；输出字段与旧格式逐字段一致 |
| 运行时反射序列化 | 无（仅诊断报告在 JIT 下使用，AOT 走扁平写入器） |
| 链接器 | MSVC `link.exe` + `Microsoft.Windows.SDK.CPP.x64` NuGet 导入库 |

> 本机 Windows SDK 仅安装头文件（`Lib\10.0.26100.0\um\x64` 为空），首次 AOT 链接报 `LNK1181: advapi32.lib`。项目改为在 `PublishAot=true` 时引用 `Microsoft.Windows.SDK.CPP.x64`（`ExcludeAssets="all"`），并追加 `AdditionalNativeLibraryDirectories`，未修改系统 SDK 安装。

### 6.2 应用图标

| 项目 | 结果 |
| --- | --- |
| 源文件 | `src/EhViewerForPc/Assets/icon.ico`（9 帧：16/20/24/32/40/48/64/128/256，32bpp DIB） |
| 可执行文件内嵌 | 通过（256×256 帧数据位于 PE 资源段） |
| 窗口标题栏图标 | `Window.Icon` 加载成功（`ui-smoke.json` → `windowIcon = true`） |
| 小尺寸可读性 | 16/20/24 px 使用简化标记 + 书脊缺口；24 px 可辨识为开卷书本 |

### 6.3 MSIX 打包

| 项目 | 结果 |
| --- | --- |
| 项目 | `packaging/EhViewerForPc.Msix`（已加入 `EhViewerForPc.slnx` 的 `/packaging/` 节点，VS 可见） |
| 命令 | `dotnet build packaging/EhViewerForPc.Msix -c Release`（解决方案构建时自动跳过，需 `-p:PackMsixOnBuild=true` 强制） |
| 流程 | AOT 发布 → 布局暂存（剔除 PDB）→ SDK BuildTools 的 `MakeAppx` 打包 |
| 产物 | `artifacts/msix/EhViewerForPc_1.0.0.0_x64.msix`，15.8 MB |
| 包内文件 | 13 项：AOT exe、3 个原生库、6 个磁贴图标、`AppxManifest.xml`、`AppxBlockMap.xml`、`[Content_Types].xml` |
| 清单 | `Identity EhViewerForPc.Local` · `CN=EhViewerForPc` · x64 · `runFullTrust` · min 10.0.17763 |
| 版本注入 | `-p:MsixVersion=1.2.3.4` 同时写入清单与文件名（已验证） |
| 布局隔离 | 打包使用 `artifacts/msix/aot`，应用运行中也能打包（不占用 `artifacts/win-x64-aot`） |
| 签名 | 未签名；`-p:MsixPfx` + `-p:MsixPfxPassword` 走 `signtool` |

## 7. 已知问题与限制

1. **离线截图存在 ClearType 彩色边缘**：截图由 `RenderTargetBitmap` 在 1.5× DPI 下重绘，文字可见 RGB 边缘；实际屏幕渲染正常。已在窗口设置 `TextRenderingMode.Antialias`，但 Avalonia 11.3 的该属性对文本渲染影响有限。
2. **继续阅读定位条**：未实现（原计划可选增强）。
3. **无全局位图缓存**：页面/封面由控件独占，仅保留当前页 + 预取页；设置中的“页面缓存预算”字段保留但不再暴露 UI。
4. **动图解码开销**：GIF/WebP 在阅读器内逐帧解码循环播放（无帧缓存，内存恒为一帧）；库封面只取首帧。
5. **文件系统实时监听**：未实现，需手动刷新。
6. **超长路径**：依赖 Windows LongPaths 策略；应用内置 `\\?\` 回退，未开启策略时个别路径可能失败。
7. **“最近阅读”排序延迟**：关闭阅读器后仅更新卡片状态，不立即重排，避免列表重建导致滚动位置丢失；排序在下次刷新（F5）或切换排序时生效。
8. **MSIX 未签名**：需开发者模式 `Add-AppxPackage -AllowUnsigned`，或使用与清单 `Publisher` 匹配的证书签名。
9. **AOT 诊断报告**：`--self-test` / `--ui-smoke` 的嵌套匿名对象在 AOT 下被扁平化为字符串，截图与流程不受影响。

## 7.1 横向连续模式与动图播放（2026-11-08 第二轮）

### 横向连续模式（ReadingMode.Double）

- 语义：适应高度、逐页横排的连续模式。**不做两页配对**——双页合并的宽图与长条图按原始比例完整显示，规避“无法预知哪页是合并页”的问题。
- RTL（日漫）：显示顺序整体反转，第 1 页位于最右端，滚轮/翻页方向随阅读方向反转。
- 定位/进度：横向镜像复用纵向连续的“探针 + 二分”定位与“页内偏移”进度；`←/→` 翻页滚动到目标页起点。
- 验证（JIT 与 AOT 冒烟一致）：`items=9`、`rtlReversed=True`、`extentWidth=4101 > viewportWidth=1160`、`Step(1)` 滚动 3042.7→2519.6（RTL 向左推进）、截图 `reader-horizontal.png`（含 0 字节损坏页显示“无法加载”占位且不阻塞相邻页）。页面间零间距（`Gap=0`、`StackLayout Spacing=0`），纵/横向均无缝衔接，截图目检确认。

### GIF/WebP 动图播放

- 解码：`ImageDecoder` 支持 `SKCodecOptions(frameIndex)` 按帧解码；`DecodedImage` 携带 `IsAnimated/FrameCount/FrameIndex/FrameDurationMs`；`AnimationPlayer` 逐帧循环（无帧缓存，内存恒为一帧，每帧延迟取容器声明时长）。
- 播放范围：阅读器单页视图（`ZoomPanView`）与阅读器连续/横向条目（`AsyncImage Animate=True`）循环播放；库封面保持静态首帧，避免滚动网格解码风暴。
- 测试素材：冒烟合成库新增手写 GIF89a 编码器（`GifLzwEncode`，两帧红/蓝、100ms、无限循环）生成的动画页。
- 验证（JIT 与 AOT 一致）：`isAnimated=True`、`frameCount=2`、第 2 帧（需前帧合成）`frame1Ok=True`、播放采样 `[1,0,1,0,1,0]` 循环推进、截图 `reader-animated.png`。
- 修复过程备注：首版 LZW 编码器码宽递增时机错误（应在赋值前按新增表项值判断），经独立解码器校验定位后修正。

### 回归

- 44 单元/集成测试通过；Release 0 警告；AOT exe 22.97 MB（新增功能后 +0.07 MB）。
- 冒烟原有断言全部保持：非模态阅读器、滚动位置/顺序稳定、损坏页滚轮翻页、进度恢复、6,315 项虚拟化（26 容器）。

### 7.2 阅读器交互修复（2026-11-08 第三轮：持久化 / 模式切换 / 平滑滚动）

- **阅读内设置持久化**：在阅读器内切换阅读模式（含横向）与适应方式即写入 `settings.json` 的默认值，重开应用生效；方向与背景此前已持久化。冒烟末尾会恢复冒烟前的模式设置，避免污染真实配置。
- **模式芯片 toggle**：再次点击已激活的"横向"/"连续"芯片返回单页（此前 `SetModeAsync` 对同模式直接返回，无法用鼠标退出横向）。
- **横向平滑滚动**：滚轮步长从半屏（`viewport×0.55`）降为 `clamp(viewport×0.12, 48, 240)`，逐小步平移。
- **进度条连续化**：`SliderValue` 语义改为"页码 + 页内偏移"的连续值（如 5.5 = 第 5 页中部），纵向/横向一致；滚动时滑块实时跟随；拖动即按像素定位。断言：设 5.5 → 落在第 5 页、滑块回读 5.5、滚动位置与 VM 目标一致。
- **修复探针污染（真实 bug）**：切换到连续/横向的瞬间 ScrollViewer 仍在 offset 0，`ScrollChanged` 探针把显示序第一项误判为当前页（RTL 横向下即末页，实测打开即跳到 8/9）。修复：模式切换、尺寸调整、翻页定位期间抑制探针同步（`_suppressScrollSync`，SystemIdle 释放）。
- **修复 Offset clamp（真实 bug）**：定位请求早于布局稳定时，`Offset` 被截断到当时 extent−viewport（实测目标 1847.3 → 实停 1792.7，差 54.6px 且不回补）。修复：窗口端 Background 优先级重试一次，布局跟上后补设目标值。
- 验证（JIT 与 AOT 一致）：滑块定位 ok、切换模式保持 `pageAtProbe=2/9`、toggle 回单页、损坏页滚轮、动图推进、进度恢复全部通过；AOT 重新发布。

### 7.3 GIF 帧率与中段卡顿修复（2026-11-09，两轮）

**第一轮：节奏模型**
- 原实现是「先 `Task.Delay(帧时长)`，再解码下一帧」，实际间隔 = 帧时长 + 解码 + UI 派发；叠加 Windows 定时器量化（`Task.Delay(80)` 约 93 ms 返回）后，声明 12.5 fps 的素材只有 10.4 fps；且每帧重新 `OpenRead` + `SKCodec.Create`。
- 修复：绝对时钟排程（落后超一周期才重对齐）、解码与显示并行、尾段 ≤3 ms `SpinWait` 抵消定时器量化、整段播放复用一个 `AnimationCursor`。
- 实测（合成 10 帧 × 80 ms）：10.4 → 12.5 fps。

**第二轮：真实样本暴露的真因（用户报告「掉帧且中间卡住很久」）**
- 样本：`1592397-(C97) [MIGNON WORKS (mignon)] My junior-animated`，实为 **GIF89a 却用 `.jpg` 扩展名**，9.8 MB、600×1067、**75 帧 × 60 ms**。
- 新增 `--anim-probe <file>` 诊断（无窗口、只读）。实测逐帧解码 **8.4 ms（第 1 帧）→ 201 ms（第 74 帧）**，74 帧累计 8.3 s；逐帧新建 codec 时第 60 帧需 215 ms。
- 根因：GIF 帧是**增量帧**。`SKCodecOptions(frameIndex)` 未带 `PriorFrame`，Skia 无法在已有像素上做增量合成，只能从文件开头重建帧链——帧号越大越慢，播放到中段必然卡死，正是「中间卡住很久」。
- 修复：
  1. `DecodeAnimationFrame` 保留一块常驻合成画布，按 `SKCodecOptions(frameIndex, priorFrame)` 增量解码；仅在循环回卷或帧号回退时清空重建。
  2. `AnimationFramePool` 复用帧位图（每帧 2.6 MB 的 `WriteableBitmap` 与 `SKBitmap` 不再重复分配），控制端统一走 `Release` 归还缓冲池。
  3. 动画解码绕开与大页预取共用的信号量（单会话本来就串行，共用只会让播放排到预取后面）。
- 实测（同一文件）：逐帧解码 **112.7 → 3.0 ms**（最大 244 → 8.5 ms；74 帧累计 8.3 s → 0.22 s）；5 秒无窗口播放模拟 **82 帧 / 16.4 fps**（声明 16.6），最大间隔 60 ms；另两个样本（80 帧 × 50 ms、50 帧 × 50 ms）平均 3.3 / 4.5 ms。
- **正确性校验**：增量合成结果与「逐帧新建 codec、由 Skia 从第 0 帧重建」的参考解码逐像素比对，第 2 / 37 / 74 帧均为 **0 / 640,200 像素差异（Δ0）**，即增量路径与规范路径完全等价。
- 回归：合成库动画页改为 30 帧 × 60 ms 的**局部更新 + 透明 + disposal=1** 素材（每帧依赖前帧），冒烟记录 `observedFps` 与 `maxGapMs` 并断言 `pacingOk` / `noStall`；解码探针 11.9 → 3.7 ms，播放 16.3 fps、最大间隔 63 ms。
- 44 单元测试通过；其余冒烟断言（非模态、滚动位置/顺序稳定、损坏页滚轮、进度恢复、6,315 项虚拟化 26 容器、卡片角标）保持不变。

### 7.4 动图分配与开页卡顿（2026-11-09，第三轮）

- **分配归因**（`--anim-probe`，600×1067/75 帧样本）：解码路径 **4.0 KB/帧**、播放调度 **6.7 KB/帧**（合计约 110 KB/s，全为 Task/定时器/状态机小对象，gen0 级）；`AnimationFramePool` 计数 **rent=40 / hit=39 / new=1**，即整段播放只创建过 1 张位图，图像缓冲已实现稳态零分配。
- **开页卡顿（修复）**：播放会话打开需重新解析容器（实测 **47–59 ms**，10 MB / 75 帧），原实现在调用线程（UI 线程）同步执行。改为 `Task.Run` 打开会话，UI 线程立即返回，解析与首帧显示并行。
- **尝试并回退**：最小化/隐藏时暂停解码（在播放循环内读 `IsEffectivelyVisible`/`WindowState`）会在解码线程访问 Avalonia 属性并抛 `InvalidOperationException`（冒烟 `advancing=False`、`task-faulted`）。正确做法需由 UI 线程事件驱动，收益（闲置 CPU）与风险不成比例，本轮不做，保持每帧解码 3 ms 的现状。
- 结论：当前瓶颈已从「解码」转为「呈现节流」，p95 间隔等于声明帧时长（60 ms），无迟到帧；继续优化的边际收益很低。
- 产物：`artifacts/win-x64-aot`（23.2 MB）与 `artifacts/win-x64` 均已用含修复的源码重新发布并通过 `--anim-probe` 自检。

## 8. 结论

- 核心格式兼容、扫描缓存、解码预算、阅读进度、虚拟化与只读约束均通过真实库验证。
- 单页/连续/设置/详情/搜索/收藏/进度恢复可用；阅读器为非模态窗口，阅读期间可继续浏览资料库，关闭后库视图滚动位置与顺序保持不变。
- Release 构建 0 警告，发布产物可运行；最终只读快照哈希一致；全量探测 245,535 个页面头，最大图/长条图/1,728 页尺寸预读均在预算内。
- NativeAOT 产物（22.6 MB，含 9 帧图标资源）完成完整 UI 冒烟与二次启动，持久化 JSON 全部由源生成上下文处理。

---

## 8. 在线功能迁移验证（2026-11-10）

### 8.1 单元测试

命令：`dotnet test tests/EhViewerForPc.Core.Tests`

结果：**60 passed / 0 failed**。新增 8 项在线解析回归（`OnlineParserTests`，fixture 取自真实页面）与 8 项下载/网络回归（`DownloadAndNetworkTests`：目录名清洗、标签子目录、源生成 JSON 往返、Cookie 解析、hosts 两种顺序）。

| 用例 | 断言要点 |
| --- | --- |
| 列表行 | gid/token、标题（排除标签串）、分类中英映射、页数、发布日期、上传者、缩略图绝对地址 |
| 详情页 | `#gdd` 标签/值行（Posted/Parent/Visible/Language/File Size/Length/Favorited）、`Average:` 评分、`#gdc` 分类、`#taglist` 命名空间 + 标签 |
| 页面列表 | `/s/{hash}/{gid}-{page}` 中 gid 在前的解析、sprite 盒尺寸（200×282）作为宽高、`?p=` 分页判定 |
| 图片页 | `img#img` 地址与 `nl('keystamp')` 提取 |
| URL | 视图 URL 往返、`f_cats` 取反掩码（1023 去掉同人志与漫画 = 1017）、`f_spf`/`f_spt`、`&page=` |
| 紧凑列表标题 | 标题链接内含标签 chip 时只取 `div.glink` 文本（实测站点默认 Compact 视图） |
| HtmlLite | 容错嵌套（未闭合 `<b>`）、实体解码、块级/行内文本拼接 |

### 8.2 在线链路自检（`--online-probe`）

命令：`EhViewerForPc.exe --online-probe --out artifacts/online-probe.json`（未登录，系统代理）

| 阶段 | 结果 |
| --- | --- |
| 首页解析 | 25 项，`hasMore=true`，标题/分类/页数/日期/缩略图主机全部有效 |
| 详情解析 | 标题、`Misc`/`Doujinshi` 分类、37 页、评分 1.40、31.29 MiB、语言 Japanese、15 个标签（含 `parody:`/`character:`/`female:` 命名空间） |
| 页面列表 | 20 项，`Page=1`、`hasMore=true` |
| 图片解析 | 解析出 `*.hath.network` 直链（`resolved=true`） |
| 缩略图下载 | `ehgt.org` 22,936 bytes（`ok=true`） |

### 8.3 UI 冒烟（`--ui-smoke`）

在原有本地库断言全部保持通过的前提下，新增导航逐页截图与在线详情窗口：

- `page-online.png` / `page-search.png` / `page-favorites.png` / `page-history.png` / `page-downloads.png` / `page-settings.png` / `online-detail.png`
- 在线详情：标题、11 个标签、`14 页`，无绑定错误或异常
- 导航切换不重建页面（视图按 ViewModel 缓存），本地库滚动位置与虚拟化断言（6,315 项、约 20 个容器）保持不变

### 8.4 未覆盖

- 账号密码登录与 ExHentai 权限探测需要真实账号，未在自动化中执行（Cookie 导入/登出/站点切换已通过编译与 UI 路径验证）。
- HTTP/SOCKS5/DoH/hosts 需要外部代理环境，仅做代码路径与设置持久化验证。
- 实际内容下载未纳入自动化（避免把站点内容写入测试产物）；下载目录布局、`.thumb`/`.ehviewer` 生成与续传逻辑由本地库扫描器与单元测试间接覆盖。

### 8.5 NativeAOT 与裁剪

```
dotnet publish src/EhViewerForPc -c Release -r win-x64 -o artifacts/aot-test
```

- 结果：原生 exe **29.7 MB**，另附 `libSkiaSharp.dll` / `libHarfBuzzSharp.dll` / `av_libglesv2.dll`。
- **IL2026 / IL3050 = 0**。首轮发布曾在 `SearchPage.axaml` 的搜索历史/保存搜索模板上出现反射绑定警告（`x:CompileBindings="False"`），已改为强类型 `TextItem` + 编译绑定；二轮发布无任何裁剪/AOT 警告。
- AOT 版本运行 `--online-probe`：首页 25 项、详情（211 页 / 评分 2.43 / 515.5 MiB / 3 标签）、页列表 20 项、图片直链、缩略图 23,192 bytes，全部成功。
- 说明：本机安全策略禁止 `reg.exe`，发布末尾的后置步骤被拦截，但 `dotnet publish` 退出码为 0 且产物完整可运行。

---

## 9. 完整实现补齐（2026-11-10 第二轮）

上一轮标注为「未迁移」的能力已全部落地：种子下载、评论、标签翻译、归档下载、H@H 识别、收藏分类命名，并补充了评分、列表过滤与收藏夹搜索。

### 9.1 单元测试

命令：`dotnet test tests/EhViewerForPc.Core.Tests`

结果：**70 passed / 0 failed**（新增 10 项 `OnlineExtrasTests`）：

| 用例 | 断言要点 |
| --- | --- |
| 详情扩展 | `#gd5` 的 `archive.php` / `gallerytorrents.php` popUp 链接、`Torrent Download (2)` 计数、`apiuid`/`apikey`、`#rating_count` |
| 评论解析 | `.c1` 块 → id（`comment_<id>`）、用户、发布时间、`+35` 分数、正文、`.c7` 投票明细、`#chd` 发帖入口 |
| 种子列表 | 每个 `<form>` 一条：名称、`.torrent` 直链、`?p=` 去除、大小/做种/下载/上传者 |
| 归档页 | `hathdl_form` 的 `or` 令牌与 `do_hathdl('org'/'res')` 选项；未登录页识别 |
| 归档结果 | H@H 缺失文案、`.zip` 直链、排队中三种结果分类 |
| 标签库 | `rows` 命名空间与 `namespace:tag` 映射、缺失键返回 null |
| 过滤器 | 排除命中隐藏、包含规则白名单、上传者/标题目标、停用规则 |
| 列表标签 | 紧凑布局 `div.gt[title]` 提取 `language:chinese` 等标签，供过滤器使用 |

### 9.2 在线链路自检（`--online-probe`，未登录）

| 阶段 | 结果 |
| --- | --- |
| 首页 | 25 项；首项 7 个标签（`parody:`/`character:`/`female:`），缩略图 `ehgt.org` |
| 详情 | 66 页、评分 2.5、70.19 MiB、26 个标签、1 条评论（用户 `branday`、`10 November 2026, 10:49`） |
| 种子 | 1 个种子（70.17 MiB、4 做种、0 下载、`ehtracker.org`） |
| 种子文件 | 5,997 字节，首字节 `d`（bencode 合法） |
| 归档 | 未登录时正确返回 `requiresSignIn=true` |
| 标签翻译 | 下载 + gzip 解压 + 解析 44,873 条，`female:sole female` → 「单女主」 |

### 9.3 UI 冒烟（`--ui-smoke`）

- 新增 `page-settings-online.png`（标签翻译 / 过滤 / 收藏命名）、`online-torrents.png`、`online-archive.png`。
- 在线详情断言：标签翻译生效（`角色:卡娜莉` 等）、评论区、评分行、种子/归档入口；`hasTorrents=true`、`hasArchive=true`。
- 种子窗口：实时解析 1 个种子并渲染；归档窗口：未登录提示与禁用「请求归档」。
- 原有本地库断言保持：6,315 项 / 21 个容器、阅读器 `4 / 9`、逐页截图全部通过。

### 9.4 字体与截图

冒烟截图曾出现两类「重影/糊字」，都已定位并修复：

1. `Microsoft YaHei UI` 没有 SemiBold 字重，Skia 用合成加粗（双重描边）绘制 → 全项目 `FontWeight="SemiBold"` 改为 `Bold`，使用真实粗体字形。
2. 详情/种子/归档三个子窗口的根 `Grid` 没有背景，`RenderTargetBitmap.Render(window.Content)` 不包含 Window 背景 → 文字在透明像素上抗锯齿，导出 PNG 后出现深色描边。给三个窗口的根面板加上 `Background="{DynamicResource AppBgBrush}"` 后，文字与背景都完整不透明（同时消除了标题被裁切的错觉）。

`SaveScreenshot` 也改为两次渲染（预热 + 正式），避免捕获到异步刷新中的半成品图层。

### 9.5 NativeAOT

```
dotnet publish src/EhViewerForPc -c Release -r win-x64 -o artifacts/aot-extras
```

- 原生 exe **30.3 MB**，`IL2026 / IL3050 = 0`（`reg.exe` 后置步骤被安全策略拦截，publish 退出码仍为 0，产物完整）。
- AOT 版 `--online-probe`：首页 25 项、详情（2000 页 / 评分 1.75 / 2 人评分）、种子入口、归档登录检测、标签库 **44,873 条**（下载 + gzip 解压 + 解析 + 落盘）全部通过。
- 顺带修复了诊断报告在 AOT 下的降级：`OnlineProbeService` 改用字典构造报告，`DiagnosticReport.SerializeFlat` 现在输出完整嵌套结构（此前匿名对象会退化成 `"{ items = 25, ... }"` 字符串）。

### 9.6 未覆盖

- 账号登录后的评分、评论发布/投票、归档请求与 ZIP 下载需要真实账号；自动化只验证了匿名分支（禁用与登录提示）、解析 fixture 与请求构造。
- H@H 归档队列依赖账号已分配 H@H 客户端，无法在测试环境复现，仅验证缺失提示与页面入口。

---

## 10. 登录修复：Cloudflare 拦截与网页登录（2026-11-10 第三轮）

### 10.1 根因

`forums.e-hentai.org` 的登录页对非浏览器客户端一律返回 Cloudflare 托管挑战：

```
GET https://forums.e-hentai.org/index.php?act=Login&CODE=00
HTTP/1.1 403 Forbidden
Cf-Mitigated: challenge
Server: cloudflare

<title>Just a moment...</title>
```

即使用完整浏览器头（UA / Accept / Accept-Language / Sec-CH-UA）也一样。原实现把用户名密码 POST 到 `act=Login&CODE=01`，只会得到 `HTTP 403`，因此界面上永远显示「登录失败：HTTP 403」。这与 Android 版 EhViewer 使用 WebView 登录的原因完全一致。

### 10.2 实现

| 组件 | 职责 |
| --- | --- |
| `Services/BrowserLogin.cs` | 查找 Edge/Chrome → 用一次性 `--user-data-dir` 启动 → 读取 `/json/list` → 连接 DevTools WebSocket → `Storage.getCookies` 轮询（1.2 s）→ 结束后杀进程、删配置 |
| `ViewModels/WebLoginViewModel.cs` | 状态机（启动 / 等待 / 验证 / 完成 / 失败兜底），显示当前页面标题，手动 Cookie 导入 |
| `Views/WebLoginWindow.axaml` | 独立的登录窗口：状态卡、三步说明、登录地址、Cookie 手动粘贴折叠区 |
| `EhClient.CompleteBrowserSignInAsync` | 过滤出 `ipb_member_id` / `ipb_pass_hash` / `igneous` / `sk`，替换式导入 → `home.php` 验证 → 取 `igneous` → 探测 ExHentai → 落盘 |
| `EhClient.VerifySignedInAsync` | `home.php` 未登录会被弹到 `bounce_login.php`（200 页面），据此判定；连续两次请求以覆盖 meta refresh 的情况 |
| `EhSession.ParseCookieString` | 支持 `a=b; c=d`、`Cookie:` 头、`Copy as cURL`、JSON、DevTools 表格（`name<TAB>value<TAB>domain`） |
| `SettingsViewModel` / `ShellViewModel` / `MainWindow` | 侧栏「登录」与设置页「网页登录」都打开同一窗口；成功后刷新账号状态、收藏/历史进度 |

同时修正：密码登录遇到 Cloudflare 时会返回可操作的提示并自动打开网页登录；`cf_clearance` 等浏览器绑定 Cookie 不会被导入（只导入认证 Cookie，避免 UA 不匹配反而破坏站点访问）。

### 10.3 验证

**单元测试**：`dotnet test` → **74 passed / 0 failed**（新增 4 项：cURL、Cookie 头 + JSON、DevTools 表格、残缺 Cookie 被拒绝且标记 `RequiresBrowserLogin`）。

**登录链路自检** `--login-probe`（无账号，验证管线本身）：

| 项目 | 结果 |
| --- | --- |
| 浏览器发现 | `Microsoft Edge`（`C:/Program Files (x86)\Microsoft\Edge\Application\msedge.exe`） |
| 启动 + DevTools 连接 | 576–968 ms |
| 注入 `ipb_member_id` / `ipb_pass_hash`（`.e-hentai.org`）后读回 | 2 条，名称正确 |
| 用假 Cookie 请求 `home.php` | 被站点判定无效，返回「登录状态无效或已过期」 |

**UI 冒烟**：新增 `web-login.png`。窗口打开后真实 Edge 启动，状态显示「当前页面：请稍候… · 等待登录…」（Cloudflare 挑战页），关闭窗口后浏览器进程与临时配置目录均被清理（`Get-CimInstance` 确认无残留）。空 Cookie 导入被拒绝。

**NativeAOT**：30.7 MB，`IL2026 / IL3050 = 0`；AOT 版 `--login-probe` 同样通过（注入读回 2 条、假 Cookie 被拒绝），确认 `ClientWebSocket` + `Utf8JsonWriter` 的 CDP 实现在裁剪后可用。

### 10.4 未覆盖

- 真实账号的完整登录（输入密码 → 论坛跳转 → 读取 `.e-hentai.org` Cookie）需要人工执行；自动化只覆盖到「Cookie 出现 → 导入 → 站点验证」这一段（用注入 Cookie 模拟）。
- 企业策略禁用 `--remote-debugging-port`、或未安装 Chromium 系浏览器时，走手动粘贴 Cookie 分支。

---

## 11. 阅读历史 / 收藏状态 / 本地副本标识 / 语言标签（2026-11-10 第四轮）

### 11.1 历史改为本地记录

`历史` 页不再请求站点的 `history.php`，改为 `HistoryViewModel`：

- 数据源：`MainViewModel.SnapshotAllWorks()` + `ProgressStore`（不区分数据源是否在线）；
- 过滤：`!IsUnread`（读过、读中或已读完），按 `LastReadUtc` 倒序；
- 行内容：封面、标题、`第 N 页 / 共 M 页`、进度条、`最近阅读：今天 22:48`；
- 操作：继续阅读（先切到本地库，确保阅读器窗口的处理器存在，再执行 `OpenGalleryCommand`）、移出历史（同时清除 `WorkId` 与旧 `StableId` 进度）；
- 刷新：`LocalIndexChanged`、`GalleryListRebalanced`、导航进入、账号刷新时重建；
- 站点历史保留为页头「站点历史」按钮（浏览器打开）。

### 11.2 收藏状态

之前 `GetFavoriteSlotAsync` 请求的是不存在的 `act=popup`，详情页也没有收藏字段，导致右上角永远是「未收藏」。修复：

| 信号 | 用法 |
| --- | --- |
| `#favoritelink` 文本 | `Add to Favorites` 之外即已收藏；同时给出分类名（`Favorite 3` 或自定义名） |
| `gallerypopups.php?act=addfav` | 解析 `<option selected>` 得到槽位；仅在上面判定为已收藏时才请求 |
| 分类名匹配 | 先用设置里的 10 个自定义名 + `Favorite N` 匹配，失败才发请求 |
| 修改收藏 | 统一 POST `act=addfav`，`favcat=0..9` 或 `favcat=favdel`（站点没有 deletefav 端点），`submit=Apply Changes` |

槽位解析失败但确认已收藏时，下拉框显示「已收藏（分类未知）」，不再谎报未收藏。

### 11.3 本地已有副本

- `MainViewModel` 维护 `gid → GalleryItemViewModel` 索引（`RebuildLocalIndex`，在扫描完成、无数据源、清空视图时重建）。
- 在线列表卡片：封面左上角增加绿色「已在本机」徽章；`LocalIndexChanged` 会刷新所有在线卡片（首页/搜索/收藏）。
- 详情窗口：左栏出现「本地库已有此作品」+「在本地库中打开」按钮；点击后直接打开本地阅读器。

### 11.4 语言标签

在线列表的 `div.gt[title]` 已解析为 `language:japanese` 等标签。卡片封面右下角显示语言 chip（`中文/繁中/英文/日文/韩文…`），大小写不敏感映射到 `GalleryTags.GetLanguageDisplayName`。

### 11.5 验证

| 项目 | 结果 |
| --- | --- |
| 单元测试 | **76 passed / 0 failed**（新增 `DetailReadsFavoriteStateFromLinkText`、`FavoritePopupParsesSelectedSlot`） |
| `--online-probe` | 首页 25 项、详情 `favorited=false`（匿名）、`favoriteSlot=-1`，其余字段不变 |
| `--ui-smoke` | 新增 `page-history.png`（1 条本地记录，`第 5 页 / 共 9 页`、`今天 22:48`）、`online-local.png`（本地徽章 + 打开按钮）；`localHitCard = { inLocal: true, language: 中文 }`；6,315 项虚拟化仍为 21 个容器 |
