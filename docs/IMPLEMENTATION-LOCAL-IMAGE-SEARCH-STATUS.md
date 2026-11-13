# 本地图片搜索 C# 迁移与正式 UI 执行记录

日期：2026-11-13。对应执行手册版本4。开始HEAD：`10b8e4e5cda62e6a648c463bcda55b8eb60fbd42`。

用户先授权将完整Rust后端迁移到C#并执行，随后确认这台PC没有图库、没有运行过应用，并明确要求继续接入正式UI和导航。因此E07真实质量评测保持缺数据，正式UI不再等待E07。

开始已有的 `packaging/EhViewerForPc.Msix/EhViewerForPc.Msix.wapproj` 修改完整保留。未commit/push、未打包分发、未安装Rust/Docker/WSL、未改变系统设置。

## 已可使用的链路

设置 → 本地库 → 启用以图搜图、选择数据源/索引位置/精度、保存。然后本地库工具栏 → 以图搜图 → 建立/更新索引 → 选择、拖入或粘贴图片 → 可拖动矩形裁剪 → 搜索 → 从命中页阅读。返回保留查询与结果。

- 默认关闭；不创建图片索引、不额外遍历页图、不启动检索进程。
- 正式桌面直接调用C#后端；独立无窗口Host保留六CLI与认证HTTP。没有Rust/OpenCV/Faiss/Docker运行时依赖，仅复用SkiaSharp/SQLite原生库。
- 每代保存独立manifest与后端。更新使用SQLite BackupDatabase和独立索引文件快照，不重新解码未变化页；新增、变化页才提取。重新提取重建走新训练代际。
- 新索引成功验证后原子替换current指针；保留previous指针与上一代。清单校验失败时可读取独立校验通过的上一代；失败/取消不发布半成品。
- 删除、改版、停用源与离线文件在结果映射/打开前过滤。具体副本与原始页号定位，不用页号减一、不改历史文件伪造位置、不改默认副本偏好。
- 同副本复用阅读器仍跳页清偏移；同作品不同副本替换旧reader并释放旧进度写者；快速请求以最新序号为准。
- 下载完成与库变化合并10秒后维护；不订阅下载进度触发建库，第一次建库始终手动。配置范围或存储位置改变会取消旧任务。
- 清理只删除本功能标记、路径校验通过、无reparse point且没有当前查询占用的旧代际；保留当前和上一代。源目录从不用于生成索引。

## 工作项状态

| ID | 状态 | 实际证据与范围 |
| --- | --- | --- |
| E00 | passed | SDK11.0.100；开始git状态；既有129项基线通过 |
| E01 | passed | 来源锁、GPL/BSD保留、后端/Host/测试工程和强类型模型 |
| E02 | passed | 汉明、连续Wilson、一级/二级KModes、random/kmeans++、HNSW保存重载、BLAKE3官方边界向量、dHash近似去重 |
| E03 | passed | 托管FAST/空间分布/金字塔/方向/原ORB采样表；JPG/PNG/WebP/GIF首帧、8种EXIF、透明白底、受限解码/裁剪测试 |
| E04 | passed | SQLite事务、模式/配置拒绝、alias重放/overwrite、tar错误报告、取消和有界管道；31项后端测试中覆盖 |
| E05 | passed | 压缩磁盘IVF、校验、截断拒绝、全局KNN、分段暂停重启、幂等合并、clean后查询、独立增量快照 |
| E06 | passed | 六CLI和真实认证HTTP；托管/NativeAOT各31项进程探针通过；64图真实算法链路20项内部检查通过 |
| E07 | blocked | `real_dataset_unavailable`：用户明确确认本机没有库；没有漫画Recall/负样本/全库P95结论 |
| E08 | in_progress | 当前垂直链路已有SQLite manifest、独占writer、代际验证/发布/backup回退、活跃查询保留；独立catalog.db、完整持久化任务/断点协议及逐发布阶段崩溃矩阵仍未完成 |
| E09 | passed | 正式搜索文档、设置草稿与保存、来源选择、选图/虚拟剪贴板/拖放接线、真实拖动裁剪、深浅与宽窄离屏截图 |
| E10 | passed | Headless真实Frame测试覆盖单页/纵向/横向RTL、非默认副本、原始页5对应逻辑index2、复用/替换reader、历史覆盖、返回结果 |
| E11 | in_progress | 事件合并和增量复用已接线；锁住未变化原图仍能复用、删/停用过滤、代际保留/回退已测；100次进度/完成合并、所有空间/权限/异常退出故障矩阵尚未穷尽 |
| E12 | in_progress | 最终后端NativeAOT31项、主程序NativeAOT9项服务/增量/映射/页号模型检查均通过；真实Win32前台渲染未运行，第三方AOT警告未隐藏 |

本记录不把“正式UI已接入”等同于全部执行手册完成；E07、E08/E11剩余可靠性矩阵与原生Win32渲染另验。

## 主要代码

| 位置 | 职责 |
| --- | --- |
| `src/EhViewerForPc.ImageSearch/` | 完整托管提取、聚类、HNSW、汉明/Wilson、hash、SQLite、有界导入、磁盘IVF、增量快照 |
| `src/EhViewerForPc.ImageSearch.Host/` | WinExe六CLI、loopback HTTP、鉴权、指标/可选推送、64图生成探针 |
| `src/EhViewerForPc/Services/LocalImageSearchService.cs` | 图库标量快照、来源/路径校验、manifest、建库/更新/发布/回退/清理、结果有效性 |
| `src/EhViewerForPc/ViewModels/ImageSearchViewModel.cs` | 输入/预览/取消/裁剪/结果、建库与更新命令、精确页阅读请求 |
| `src/EhViewerForPc/Views/ImageSearchPage.axaml`及code-behind | Fluent正式页面、文件/拖放/剪贴板、Uniform裁剪坐标、懒解码命中页缩略图 |
| `MainWindow`、`ReaderViewModel` | 既有Frame导航、显式页号优先历史、同作品副本切换和旧reader释放 |
| `LocalImageSearchSettings`、`AppSettings`、`SettingsViewModel`、`SettingsPage` | schema7、默认关闭、草稿保存、索引范围与位置、精度、自动更新 |
| `ShellViewModel`、`AppServices` | 惰性组装、配置取消、可退订下载/库事件、后台维护 |
| `LocalImageSearchAppProbe`、`StartupOptions`、`Program` | 原生主程序在Avalonia/单实例逻辑之前运行无窗口真实服务探针 |
| `tools/imsearch/` | 固定来源/许可/差异、隐藏且低优先级构建与验证脚本 |

## 真实验证和退出码

所有以下最终命令为后台管道或 `UseShellExecute=false / CreateNoWindow=true`、日志重定向进程。没有显示Win32阅读器窗口，没有使用真实系统鼠标或剪贴板；Headless鼠标、剪贴板和窗口均为内存测试实现。没有读取真实图库。

| 命令/场景 | 退出码/结果 | 本机证据 |
| --- | --- | --- |
| `dotnet test tests/EhViewerForPc.Core.Tests/EhViewerForPc.Core.Tests.csproj -c Debug -p:PublishAot=false` | 0；129/129 | `.qa/image-search/tests/core-final.trx`、`core-final-tests.log` |
| `dotnet test tests/EhViewerForPc.ImageSearch.Tests/EhViewerForPc.ImageSearch.Tests.csproj -c Debug` | 0；31/31 | `.qa/image-search/tests/backend.trx`、`backend-tests.log` |
| `dotnet test tests/EhViewerForPc.ImageSearch.Ui.Tests/EhViewerForPc.ImageSearch.Ui.Tests.csproj -c Debug -p:PublishAot=false` | 0；12/12 | `.qa/image-search/tests/ui.trx`、`ui-tests.log` |
| `Build-Backend.ps1` + `Test-Backend.ps1` 托管版 | 0；31项进程检查 | `.qa/image-search/verification-managed-verified/verification.json` |
| `Build-Backend.ps1 -Aot` + `Test-Backend.ps1 -HostPath artifacts/image-search-host-aot/EhViewerForPc.ImageSearch.Host.exe` | 0；31项原生进程检查 | 最终 `.qa/image-search/verification-aot-delivery/verification.json` |
| 64张合成PNG端到端 | 0；64资产、32402描述符；8条裁剪均进入Top10；20项检查 | 上述各verification目录的 `synthetic.json` |
| 主程序 `dotnet publish ... -c Release -r win-x64 -p:PublishAot=true -o artifacts/image-search-reader-aot` | 0；有第三方警告 | 最终 `.qa/image-search/app-aot-delivery-build.log` |
| 主程序 `--image-search-probe-root <隔离空目录> --out <报告>` | 0；9项服务/增量/映射/页号模型检查 | 最终 `.qa/image-search/app-native-delivery-report.json` |
| `git diff --check -- Directory.Packages.props EhViewerForPc.slnx docs src tests tools` | 0 | 不包含用户既有MSIX文件的尾随空格 |

实际命令还使用`--no-restore`、TRX logger和results-directory以复用已恢复依赖，完整输出保存在对应log。共享Avalonia工程测试串行运行；曾发生的并行XAML输出文件锁已通过串行执行消除。

离屏截图已实际打开检查：`.qa/image-search/ui/search-1200-dark.png`、`search-1200-light.png`、`search-540-dark.png`、`search-540-light.png`、`search-results-1200.png`。结果卡片按钮越出卡片的问题已修复并重新渲染。使用[Avalonia官方Headless平台](https://docs.avaloniaui.net/docs/testing/setting-up-the-headless-platform)，而非启动真实窗口后最小化。

早期探针的合成裁剪耗时仅用于确认链路：托管样本约248ms、AOT样本约87ms（8条查询，运行负载未隔离）。它们不是规范E07的漫画P95，也不能外推245K页检索性能。CPU环境标识Intel64 Family6 Model197，逻辑处理器16；受限环境未读到完整CIM硬件信息，不编造物理内存或磁盘型号。

## 限制与下一步

1. 本机没有图库，真实漫画250条正查询/100负样本、覆盖率、两轮P95和245K页性能未测。待实际资料可用后执行E07；不会下载外部图片代替。
2. 当前应用使用代际内manifest配合最新设置/物理文件过滤；独立catalog、持久化暂停/跨重启精确断点、完整发布journal/故障注入矩阵仍是E08/E11后续。当前UI提供取消、增量更新和重新提取重建，不宣称已具备持久化“暂停/继续”。
3. 增量更新复制完整的SQLite/量化器/IVF文件以保持代际独立，未变化页不解码；磁盘复制开销需要大库测量。旧资产向量可残留于增量后端但不会映射到失效页；重新提取重建可回收它们。
4. Rust/Faiss/zstd旧索引不兼容；C#格式显式版本化。托管插值/角度/PRNG/图构建不承诺与上游逐字节一致，须从原图重建。
5. 主程序AOT发布记录了FluentAvalonia `IL2073`、Avalonia TypeHelper `IL2070/IL3050`，以及Win32 DComposition两个非blittable bool互操作警告。未压制这些警告；后台探针不初始化真实Win32渲染，不能以探针通过宣称该原生窗口路径已验证。托管Headless UI已测，MSIX沿用既有托管自包含路径。
6. 不自动发布或修改许可。GPL/BSD来源和修改声明见`tools/imsearch/NOTICE.md`。

可直接重复验证：先按tools说明后台构建，Test-Backend使用新的输出目录；UI测试只用Headless。下一项可靠性工作是E08的持久化任务/catalog和E11的故障矩阵；有真实库后并行补E07。不要重跑未受改动影响的全部耗时验证来代替缺少的数据或平台测试。
