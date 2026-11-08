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

命令：`dotnet test tests/EhViewerReader.Core.Tests -c Debug`

结果：**44 passed / 0 failed**（150 ms 级）。

覆盖：VERSION1/2/CBOR、0 字节/缺页/同页多扩展、目录名与 gid 冲突、稳定键、缓存签名失效（含原地改写）、缓存路径规范化、封面签名、EXIF/像素预算、原子写与损坏回退；多数据源合并（跨源合并 / 同源不合并 / 首选覆盖 / 完整优先 / 无 GID 不合并）、设置迁移（schema 1 根目录 → 数据源、路径去重、移除源清理首选映射）、按源隔离的扫描缓存。

## 3. 真实库集成测试（只读）

命令：

```bash
EHVIEWER_TEST_ROOT="D:/BackUp/phoneGT5pro/ehviewer" dotnet test tests/EhViewerReader.Core.Tests
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

差异为 0，`libraryUnchanged = true`。应用自身文件仅写入 `%LocalAppData%\EhViewerReader`（本次为临时数据目录）。

## 5. UI 冒烟（合成库）

命令：

```bash
EhViewerReader.exe --ui-smoke --out docs/screenshots
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
| `dotnet build EhViewerReader.slnx -c Release` | 0 警告 / 0 错误 |
| `dotnet publish -r win-x64 --self-contained false` | 成功，产物在 `artifacts/win-x64` |
| `dotnet publish -r win-x64 -p:PublishAot=true` | 成功，`EhViewerReader.exe` 22.9 MB |
| Release UI 冒烟 | 通过（截图与报告来自 Release 构建） |
| 单元测试（Release） | 44/44 通过 |
| 解决方案内容 | Core / App / `packaging/EhViewerReader.Msix` / Tests（打包工程在解决方案构建时跳过 AOT 与打包） |

### 6.1 NativeAOT 验证

| 项目 | 结果 |
| --- | --- |
| 发布命令 | `dotnet publish src/EhViewerReader -c Release -r win-x64 -p:PublishAot=true` |
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
| 源文件 | `src/EhViewerReader/Assets/icon.ico`（9 帧：16/20/24/32/40/48/64/128/256，32bpp DIB） |
| 可执行文件内嵌 | 通过（256×256 帧数据位于 PE 资源段） |
| 窗口标题栏图标 | `Window.Icon` 加载成功（`ui-smoke.json` → `windowIcon = true`） |
| 小尺寸可读性 | 16/20/24 px 使用简化标记 + 书脊缺口；24 px 可辨识为开卷书本 |

### 6.3 MSIX 打包

| 项目 | 结果 |
| --- | --- |
| 项目 | `packaging/EhViewerReader.Msix`（已加入 `EhViewerReader.slnx` 的 `/packaging/` 节点，VS 可见） |
| 命令 | `dotnet build packaging/EhViewerReader.Msix -c Release`（解决方案构建时自动跳过，需 `-p:PackMsixOnBuild=true` 强制） |
| 流程 | AOT 发布 → 布局暂存（剔除 PDB）→ SDK BuildTools 的 `MakeAppx` 打包 |
| 产物 | `artifacts/msix/EhViewerReader_1.0.0.0_x64.msix`，15.8 MB |
| 包内文件 | 13 项：AOT exe、3 个原生库、6 个磁贴图标、`AppxManifest.xml`、`AppxBlockMap.xml`、`[Content_Types].xml` |
| 清单 | `Identity EhViewerReader.Local` · `CN=EhViewerReader` · x64 · `runFullTrust` · min 10.0.17763 |
| 版本注入 | `-p:MsixVersion=1.2.3.4` 同时写入清单与文件名（已验证） |
| 布局隔离 | 打包使用 `artifacts/msix/aot`，应用运行中也能打包（不占用 `artifacts/win-x64-aot`） |
| 签名 | 未签名；`-p:MsixPfx` + `-p:MsixPfxPassword` 走 `signtool` |

## 7. 已知问题与限制

1. **离线截图存在 ClearType 彩色边缘**：截图由 `RenderTargetBitmap` 在 1.5× DPI 下重绘，文字可见 RGB 边缘；实际屏幕渲染正常。已在窗口设置 `TextRenderingMode.Antialias`，但 Avalonia 11.3 的该属性对文本渲染影响有限。
2. **双页模式、继续阅读条**：未实现（原计划可选增强）。
3. **无全局位图缓存**：页面/封面由控件独占，仅保留当前页 + 预取页；设置中的“页面缓存预算”字段保留但不再暴露 UI。
4. **GIF/WebP 动图**：只显示首帧。
5. **文件系统实时监听**：未实现，需手动刷新。
6. **超长路径**：依赖 Windows LongPaths 策略；应用内置 `\\?\` 回退，未开启策略时个别路径可能失败。
7. **“最近阅读”排序延迟**：关闭阅读器后仅更新卡片状态，不立即重排，避免列表重建导致滚动位置丢失；排序在下次刷新（F5）或切换排序时生效。
8. **MSIX 未签名**：需开发者模式 `Add-AppxPackage -AllowUnsigned`，或使用与清单 `Publisher` 匹配的证书签名。
9. **AOT 诊断报告**：`--self-test` / `--ui-smoke` 的嵌套匿名对象在 AOT 下被扁平化为字符串，截图与流程不受影响。

## 8. 结论

- 核心格式兼容、扫描缓存、解码预算、阅读进度、虚拟化与只读约束均通过真实库验证。
- 单页/连续/设置/详情/搜索/收藏/进度恢复可用；阅读器为非模态窗口，阅读期间可继续浏览资料库，关闭后库视图滚动位置与顺序保持不变。
- Release 构建 0 警告，发布产物可运行；最终只读快照哈希一致；全量探测 245,535 个页面头，最大图/长条图/1,728 页尺寸预读均在预算内。
- NativeAOT 产物（22.6 MB，含 9 帧图标资源）完成完整 UI 冒烟与二次启动，持久化 JSON 全部由源生成上下文处理。
