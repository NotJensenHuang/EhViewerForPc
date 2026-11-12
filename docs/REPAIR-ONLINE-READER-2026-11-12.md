# 在线阅读 / 下载阅读 / 收藏 / 缓存 / 本地库修复记录

日期：2026-11-12。对应计划：`docs/PLAN-ONLINE-READER-AND-LIBRARY.md`。
工作目录：`G:\SoftwareProject\Avalonia\EhviewerDownloadReader`。本轮未执行 `git push`，也未关机。

## 1. 结果概览

| 问题 | 确认原因 | 现在的行为 |
| --- | --- | --- |
| 在线阅读打开慢 | `OnOnlineReadRequested` 在导航前 `await GetAllPagesAsync` 顺序扫完全部预览分页 | 先用详情页页数建立逻辑页槽并立即导航；每页的 `s?` 引用按 1-based 页号在首次显示时解析，且只取该页所在预览分页。仅当详情页缺页数时才等待一次首页分页 |
| 下载页阅读按钮无效 | `ReadTask` 只按 GID 查本地库副本，找不到就静默返回 | 由任务自身 `FolderPath` 扫描有效页构造本地阅读源并导航；不依赖入库开关、扫描状态或本地库索引。目录缺失 / 无有效图片时给出明确提示 |
| 收藏提示与服务器不一致 | `ToggleFavorite` 直接提交分类 0；下拉框改选立即提交；结果只用关键词布尔判断 | 点击收藏打开 Fluent `FAContentDialog` 选择分类；关闭/取消不发请求；提交后只读核验 `#favoritelink` 与 popup 分类，区分成功 / 拒绝 / 结果未知，并提供「重新检查收藏」。改分类时保留站点备注 |
| 无本地库时历史图片不显示 | 本地副本失联时仍使用其本地封面请求 | 仅当本地目录存在且可读时使用本地封面，否则使用按作品 ID 持久化的缩略图缓存（URL 缺失/过期也能命中已有缓存），失败显示明确占位 |
| 在线缓存不可调 | 预算与保留时间写死（2 GiB / 14 天），每 16 次写入才清理一次 | 设置 → 浏览新增磁盘上限（128–32768 MiB，默认 512）与保留时间（0–90 天，0 = 不限制，默认 0）；命中更新最近使用时间，LRU 淘汰，启动、写入与保存设置后合并清理；清理不触碰正式下载、进度与历史 |
| 下载中的本地卡片无状态 | 无下载引擎到卡片的投影 | 按规范化目录（非 GID）投影任务：封面半透明遮罩 + `FAProgressRing` 确定/不定进度 + 「下载中 · 12/76」；完成自动移除，其他版本标注「其他版本」 |
| 重复版本难筛选、刷新易误触 | 筛选枚举无重复项；工具栏与 F5 均触发全量扫描 | 新增末尾枚举 `Duplicates = 6`（旧数值不变），按 `HasMultipleVersions` 过滤；移除工具栏刷新按钮并回收列；F5 只重投影，全量扫描移到设置 → 资料库，带状态与取消 |

## 2. 关键实现

### 2.1 在线阅读即时打开与按需索引

- 新增 `Services/OnlinePageIndex.cs`：按页号定位预览分页（`(page-1)/pageSize`），分页请求串行去重；每个调用方可以独立取消等待，不会取消其他观察者共享的传输；会话结束时取消后台抓取。
- 分页容量从首个分页的实际页号最大值学习，不写死 20/40；详情页缺页数时用分页链接的最大页号估算总页数。
- `OnlineReaderSource` 构造时只建立页槽（详情页页数），`CreateRequest` 返回惰性 `OnlinePageImageRequest`：先解析 `s?` 引用再走原有磁盘缓存与解码链路。缓存键包含站点+账号作用域、GID、页号、画质与压缩宽度。
- 断网/配额不足时，若该页已有缓存文件，直接读取缓存而不是报错。
- 阅读器新增「重试本页」，单页 / 连续 / 横向模式均可替换当前页请求而不重建列表。

### 2.2 图片加载状态

- `IImageRequest.LoadAsync` 增加可选 `IProgress<ImageLoadStatus>`；`EhClient` 流式下载同时报告已收字节与 Content-Length（可空）。
- `AsyncImage` / `ZoomPanView` 绘制：解析页面 / 等待数据 / 下载（有长度时真实百分比 + 已收/总量）/ 解码中 / 无法加载（含站点错误原因）；无长度时使用不定圆环，不模拟百分比。状态更新约 140 ms 节流，终态必达。
- 缓存命中不再伪造成写入事件，避免命中触发全目录清理。

### 2.3 收藏

- `EhClient.SetFavoriteAsync` 返回 `FavoriteUpdateResult`（`Succeeded / Rejected / Unknown`，含是否核验、实际分类）。POST 后统一执行一次只读核验；核验失败但 POST 形态表明已应用时显示「已提交（未能核验）」，不谎报失败、不自动重复 POST。
- 新增 `ParseFavoriteNote`，改分类时带回原备注，避免空字符串清空站点备注。
- 收藏成功/未知后刷新收藏夹列表；详情页与列表状态一致。

### 2.4 缓存设置与历史封面

- `AppSettings` 新增 `OnlineCacheMegabytes` / `OnlineCacheDays`，`Normalize()` 限定范围；保存设置后运行中的服务立即按新策略清理。
- 清理按最近访问时间排序；正在解码或被占用的文件通过独占打开探测跳过（下载使用 `.part`，不会误删在途文件），过期 `.part` 单独按天清理。
- 历史封面优先本地可读封面 → 作品 ID 缩略图缓存 → 网络；缩略图缓存键改为 `work:{gid}`，列表 / 详情 / 历史共用同一文件，重启后仍可命中。

### 2.5 本地库

- `DownloadsViewModel.ReadTask` → `DownloadReader.TryBuildGalleryInfo` → `MainWindow` 导航到本地阅读器。
- `GalleryItemViewModel` 新增下载投影（与阅读进度完全分离）；`MainViewModel.ApplyDownloadStates` 按目录映射，卡片只接收与自身副本对应的任务。
- 设置页新增「重新扫描全部来源」与取消入口；本地库工具栏不再有刷新按钮，F5 只重投影。

## 3. 验证

| 项目 | 结果 |
| --- | --- |
| `dotnet test tests\EhViewerReader.Core.Tests` | 128 通过 / 0 失败（新增 12 项：分页定位、共享去重、独立取消、越界页、任务目录阅读、收藏备注、缓存默认值/范围、重复筛选与下载投影） |
| `--navigation-regression`（NativeAOT 发布版） | 18 项 PASS |
| `--ui-smoke`（NativeAOT 发布版） | 无 false/FAIL；生成全部截图 |
| `--ui-audit`（NativeAOT 发布版） | `error = null`，`horizontalOverflows = 0`，108 张深/浅色、宽/窄截图通过 |
| `dotnet publish -c Release -r win-x64 -p:PublishAot=true` | 生成 `.qa/online-reader-final/EhViewerReader.exe`（约 37 MB）。构建后置的注册表探测被本机沙箱策略拦截（`reg.exe`），不影响产物；三条诊断均用该产物运行 |

## 4. 未真实复测 / 仍存在的限制

- 本轮**没有使用真实账号访问站点**，也未复现真实收藏响应。收藏成功/失败/未知分支依据现有解析器与新增核验逻辑，并用离线夹具与编译期类型验证；建议首次真实收藏时观察一次日志与「重新检查收藏」。
- 下载中新增页面的**实时合并**未实现：打开阅读器后，任务继续落盘的新页需要重新进入阅读器才能看到（已落盘页可立即阅读）。
- 详情页缺页数且站点未渲染分页链接时，阅读器只能展示首个分页的页数；此类画廊极罕见。
- 缓存清理的受控超额仅限正在被解码/占用的文件；实际超额上界为这些文件大小之和，未做硬上限。
- 历史页图的**离线续读**依赖该页曾经缓存；未缓存页在断网时显示失败与重试，不声称全书离线可用。
- AOT 发布仍报告 FluentAvalonia/Avalonia 既有的 trim 警告（`IL2073/IL2070/IL3050`），与本轮改动无关。

## 5. 后续调整（同日）

- **缓存默认值**：在线缓存上限默认改为 **512 MiB**；保留时间默认 **0 = 不限制**（仅在超出磁盘上限时按 LRU 淘汰）。`SchemaVersion` 升到 6，迁移只把仍是旧默认（2048 MiB / 14 天）的值改为新默认，用户显式改过的值保持不变。
- **图片进度居中**：`FormattedText.Width` 返回的是字形紧凑宽度，而 `TextAlignment=Center` 是在 `MaxTextWidth` 段落盒内居中；旧代码用 `中心 - Width/2` 定位会让文字整体右偏甚至被裁切。改为把段落盒本身居中绘制，标题、百分比与失败原因全部精确居中。
- **可复制文本**：说明性文本（设置、详情、状态栏、阅读器信息、历史/下载列表等）共 232 处改为 `SelectableTextBlock`，支持拖选、`Ctrl+A`、`Ctrl+C`；卡片与按钮内部的 25 处文字保留普通 `TextBlock`，避免文本选中抢占点击/切换。文本类样式选择器改为 `:is(TextBlock).xxx`，两种控件共用同一套排版；可选中文本默认不进入 Tab 焦点序列。

补充验证：`dotnet test` **129 通过 / 0 失败**；NativeAOT 发布版 `--navigation-regression` **18 PASS**、`--ui-smoke` **无 false/FAIL**、`--ui-audit` **error = null / 0 横向溢出 / 108 张截图**；浅色与深色设置页均显示「512 MB」「不限制」。
