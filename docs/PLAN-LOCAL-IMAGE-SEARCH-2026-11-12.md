# 本地库局部图片搜索：imsearch Rust 后端完整迁移到 C#

日期：2026-11-13。版本：4。用户已授权更新文档后开始实施，并明确要求接入正式UI和导航。实际状态见 [执行记录](IMPLEMENTATION-LOCAL-IMAGE-SEARCH-STATUS.md)。本文件替代版本2，不再执行旧的ManagedDocker方案。

当前已交付C#后端、正式搜索/设置页面和命中页Frame导航，并有172项测试与后台AOT探针证据。真实漫画评测因本机无库未运行；catalog/持久化任务及完整故障矩阵等尚未完成项保留在执行记录，不以UI接入代替全部手册验收。[验证报告](REPORT-LOCAL-IMAGE-SEARCH-POC.md)

## 0. 范围与约束

- 将 imsearch 的 **整个 Rust 后端迁移为原生 C# / .NET 11**：算法、提取、训练、索引、存储、导入、六个CLI命令、HTTP及诊断。不得以Rust进程、Rust DLL、Docker包装或单纯C#客户端冒充迁移。
- 默认模式 `Disabled / ManagedCSharp`，不要求安装Rust、Cargo、Docker、WSL、Faiss或OpenCV。原C++ ORB-SLAM3依赖所需的算法也改为托管实现；SkiaSharp仅编解码，SQLite负责持久化，允许两者现有原生运行库。
- 必须保留ORB→KModes→HNSW→磁盘IVF→汉明TopK→Wilson/Count主链路；不能以普通感知哈希或全库逐图片扫描替代。
- **构建与验证始终后台执行**：管道或无窗口进程、日志重定向，不启动可见阅读器、不抢焦点、不移动用户窗口。视觉检查必须走离屏路径；无法离屏的项目记not_run，不能先弹窗再最小化。
- 保留既有工作区修改，开始时已有MSIX工程修改不覆盖。不自动commit/push、分发、安装软件或改系统配置。
- 原图库只读，开发数据和上游检出放`.qa/image-search/`。不自动扫描全量真实图库，不读取未配置磁盘，不上传图片。
- 用户确认本机没有图库，并明确要求继续正式UI和导航；E07保留为真实质量评测，不再阻止E08–E12接入。不得将合成成绩写成真实漫画质量。

## 1. 固定来源、兼容性与许可

上游：[lolishinshi/imsearch](https://github.com/lolishinshi/imsearch)，固定提交[`2bf2546822ca2b9b1522d868eb9f21ba441f20d2`](https://github.com/lolishinshi/imsearch/commit/2bf2546822ca2b9b1522d868eb9f21ba441f20d2)，Cargo版本3.0.2。不跟随master自动更新。

迁移要求能力与算法链路完整，不要求PRNG、浮点、图像插值、图构建结果逐字节相同。新C#索引显式版本化，**不直接读取Rust/Faiss/zstd二进制索引**，旧数据从原页图重建。采用.NET内置压缩和托管HNSW序列化，格式差异写入锁文件/帮助，未知格式拒绝打开。兼容性差异不免除功能验收。

保留GPL-3.0许可证、来源与移植修改说明到`tools/imsearch/`，移植文件标明来源。迁移不消除上游和ORB-SLAM3的许可义务；本轮不自行决定全仓许可证，正式分发前仍需处理许可和源码提供安排。

`tools/imsearch/upstream.lock.json`记录repository、完整commit、Cargo.lock与ORB源码真实SHA256、C#索引/提取版本、有意差异；不再记录Rust补丁或Docker镜像。

## 2. 完整迁移矩阵

| 上游 | C#必须保留的职责 |
| --- | --- |
| config/main/cli/mod | 强类型参数、默认值、范围检查、退出码、帮助与版本 |
| orb.rs/utils.rs/orb-slam3-sys | 灰度尺寸策略、动态特征数、金字塔、FAST双阈值、空间分布、方向、原256对采样表和32字节描述符 |
| hamming.rs/ivf/utils | PopCount汉明、TopK、跨桶全局KNN |
| kmodes.rs/cli/train | random/kmeans++、位众数、空簇、收敛、一级/二级加权训练和不平衡统计 |
| hnsw.rs/ivf/quantizer | 分层图插入/搜索、M=32、efConstruction=128、efSearch>=16、保存/重载、dHash去重索引 |
| ivf/invlists/array | 内存分桶、全局向量Id、XOR中心残差 |
| ivf/invlists/ondisk | 版本表头、偏移、压缩、校验、只读RandomAccess、解压预算 |
| ivf/invlists/vstack | 分段与逐桶合并，不装入全库描述符 |
| ivf/mod | 量化、nprobe、多桶匹配、每条查询总计K个邻居、IO/计算时间 |
| db/migrations | SQLite事务、hash模式、别名、描述符缓存、统计、indexed状态 |
| imdb.rs | 图片/向量归属、批次/暂停/恢复、清理、Wilson/Count聚合 |
| dhash.rs/utils::ImageHash | BLAKE3精确hash、8字节dHash、可选近似重复判断 |
| cli/add | 目录/tar、suffix/过滤/append、有界worker、逐项报告、取消与错误传播 |
| cli/build/clean | 增量分段、stop-before-merge、clean-before-merge、缓存清理语义 |
| cli/search/server | 查询参数、评分、候选上限、loopback HTTP |
| server/metrics | multipart、身份、实际鉴权、空结果、并发/体积限制、health/metrics/API文档 |
| tests | 对应算法、CLI、磁盘索引、暂停和资源释放回归，补上游错误路径 |

逐行状态记录在执行记录；只有部分实现不能标完整迁移。

## 3. 工程结构

- `src/EhViewerReader.ImageSearch/`：net11.0独立后端类库，算法/解码/SQLite/索引/任务，不依赖Avalonia、全局AppServices或Rust。
- `src/EhViewerReader.ImageSearch.Host/`：WinExe无窗口CLI/ASP.NET Core HTTP，复用同一后端，JSON源生成，不启动主程序或单实例锁。
- `tests/EhViewerReader.ImageSearch.Tests/`：独立xUnit，真实可解码合成集和故障回归。
- Core：图库身份、位置/代际模型和清单；应用层：快照/设置/协调器/选图裁剪/导航。桌面默认直接后台调用C#后端，Host供诊断和进程隔离。
- 复用SkiaSharp 3.119.4，固定Microsoft.Data.Sqlite 10.0.12，集中包版本，无ORM。NativeAOT需实际publish并运行，不能以build替代。

## 4. 参数、图像与资源预算

默认关闭、手动首次建库。所有参数集中定义且进入profile指纹。规范化：首帧、全部8种EXIF方向、裁剪在方向修正后的像素坐标严格校验、白底合成、round(.299R+.587G+.114B)灰度、最长边2048且不放大、去元数据。解码前检查文件<=50MiB、单边<=16000、像素<=24000000。读取前后长度/mtime改变返回SourceChanging，全部资源及时释放。

ORB：500特征、scale=1.2、8层、FAST=20/7、area、有方向、maxAspectRatio=5、maxFeatures=1000。上游maxSize=1080x768为高x宽；只有两边均超限才缩小，比例取两个方向比例的最大值。保留动态特征数和原描述符采样表，普通OpenCV ORB不算等价替代。

低特征`count<=30`跳过但计覆盖率。匹配k=3、distance=64、Wilson（同时支持Count），z=2.326保留上游均值/方差公式；不是二项命中率。HNSW M=32、efConstruction=128、efSearch>=max(16,nprobe)；nprobe默认16，产品4/16/64且不超过中心数。候选100，最终具体副本页最多30，分数不显示百分比。

准备worker=1、提取最多2、查询并发=1、写任务=1，所有队列有界。单暂存批<=500页或128MiB，待消费<=2批，画布预算128MiB；默认2000页构建一段；磁盘分段合并，不缓存全库。查询超时30秒、响应<=2MiB，取消后不得更新晚到UI。

资产：payloadSha256=SHA256(canonicalPng)，assetId=SHA256(UTF8(normalizerVersion + 换行 + payloadSha256))，别名`pages/<64位小写hex>.png`。特征指纹涵盖normalizer/提取器版本、maxSide、全部ORB和尺寸参数。后端BLAKE3去重与资产身份分开，不能偷偷换hash仍称相同算法。

## 5. SQLite、导入、训练和磁盘索引

后端保存图片、hash模式、alias、descriptor-cache、vector-stats、分段检查点及profile。schema显式版本化；未知版本不删除。图片/hash/描述符/统计同一事务。相同内容新alias追加，重复alias重放不重复向量；不依赖图片Id连续来推导向量归属。

目录只读；tar拒绝绝对路径、逃逸、链接、重复alias与超限条目。每项报告added/already_present/skipped_low_features/failed及featureCount/errorCode，最后complete summary；报告.part关闭刷新后原子改名。任何管道或报告失败终止worker并传播，不能吞错、漏项或死锁。0=成功，2=完整但含数据失败/前置输入缺失（结构化区分），1=致命错误。

训练支持一级/二级KModes、random/kmeans++和固定seed。二级要求D>=30*K，一级floor(sqrt(K))，一级样本最多1024*一级中心数；二级用累计样本权重分配且总数恰为K。处理全相同向量、空簇和距离溢出，报告距离/不平衡度。

构建按批次量化、保存XOR残差与向量Id；每段临时文件flush/rename后记检查点，重放不重复。索引含magic/版本/profile/量化器身份、桶偏移/大小/记录数/校验；只读RandomAccess，加载拒绝截断/越界/错误配置，解压有预算。合并逐桶分块，不装入全库。暂停在安全段边界，clean只删特征缓存，不删原图/索引；清缓存后的重训/重建限制明确报错。

搜索HNSW选nprobe桶，桶内汉明、跨桶每个查询描述符总计K个最近邻，再按distance筛选并映射图片评分。空特征、空桶、无匹配正常返回空列表。

## 6. CLI、HTTP与后台执行

完整实现`add/train/build/search/server/clean`和help/version。参数通过ArgumentList传递，不拼shell。宿主WinExe不创建窗口；UseShellExecute=false、CreateNoWindow=true；Start-Process必须WindowStyle Hidden并重定向。只停止任务自有进程。

HTTP只监听loopback；token环境变量传入（至少32随机字节对应编码），不打印、不落设置/参数。所有入口health/docs/metrics/search认证，认证先于multipart/解码；并发超限429，大小/像素预检。保留multipart file、orb_nfeatures、orb_scale_factor、nprobe并校验；正式应用锁profile，拒绝悄然改变提取参数。新增request/generation/profile身份。

协议`ehreader-imsearch-csharp/1`。响应包含protocol/backend_build_id/generation_id/profile_id/request_id/time/result，result保留[[score,alias],...]。错误体含error.code/message/request_id，不泄露token/路径/SQL/图片。healthz仅真实加载后ready，返回来源提交、构建版本、profile/代际、实际索引图片数。metrics输出请求/错误/耗时与资源；docs/openapi.json仅列已实现接口。

## 7. 应用清单与可靠发布

数据目录：catalog.db（最新位置/revision/任务）、writer.lock、current.json、publish-journal.json、generations/<id>/manifest.db/backend/validation.json、staging/<job>/。源图目录独立且只读。位置键=SourceId+GalleryStableId+PageNumber+相对路径；WorkId/GID不足以标副本。一个资产可有多位置；skipped/failed计覆盖率，离线不删。

catalog更新事务递增revision；manifest冻结快照，发布前关闭连接，活跃WAL用BackupDatabase，不只复制主文件。未知schema只禁用本功能。索引目录不能与图库双向包含，junction/symlink无法可靠判断则拒写。

任务Pending→Discovering→Preparing→Importing→Training→Building→Validating→Publishing→Completed；支持PauseRequested/Paused/Cancelling/Cancelled/Failed。重启未完成任务Paused，不自动读图；进程内锁+跨进程FileShare.None，不抢锁。

发布冻结清单→完整性/抽样查询→发布日志→原子替换current（备份）→打开新代际→清日志。失败回滚旧指针/服务，首次失败NoIndex；每阶段注入中断。路径从受限generationId推导。旧request/generation/profile响应丢弃。

结果按代际indexed资产→member→最新catalog.present且资产相同→源启用/选择→路径归属→存在及长度/mtime过滤。具体副本页取最高分、稳定排序，不能按后端任意路径打开文件。

## 8. 合成与真实效果验收

合成至少64张不同纹理/线条PNG、已知裁剪，真实跑解码→提取→导入→训练→磁盘索引→搜索→映射；不能以mock或随机描述符代替图像端到端。算法独立测试允许手工向量。

真实只读抽样已配置且启用源约2000页、最多5000页，先登记范围；无样本E07 blocked: real_dataset_unavailable，不下载替代集冒充。250正查询：不同源页100条25%面积、100条50%、50条10%，宽高乘sqrt(p)，seed=20261112；常规两组各至少25条缩小/JPEG。负样本100：空白20、低纹理20、文字20、确定不在库40。镜像/重绘/旋转单列。

报告包含覆盖率/失败原因、包括未入库页分母的Recall@1/10、入库子集Recall、负样本最高分分布、固定暖机后至少两轮P50/P95、建库训练时间/峰值RSS/临时最终空间。主nprobe16，对照4/64保持集合不变。

**E07通过条件：25%/50%合计整体Recall@10>=90%，温热端到端P95<=3秒，无崩溃/身份错配/错误副本映射。** 不降低门槛、不筛掉失败例。10%组单列；小库通过不代表历史245548页全库验证，后者需单独授权和测量。

## 9. 应用入口和导航（已授权实施，真实质量另验）

本地库工具栏以图搜图→现有Frame子页，不增顶层导航。文件/拖放/用户触发粘贴/矩形裁剪，Uniform扣留白映射像素（左上floor右下ceil）。查询图、取消、覆盖数、短状态；结果命中页缩略图、标题、来源、副本页号、匹配分与从此页阅读。

设置Enabled=false、StorageDirectory、SourceIds（空=启用源）、Nprobe=16、AutoUpdateAfterDownload=true、KeepPreviousGenerations=1；无DockerImageId/endpoint/token。沿用草稿保存/取消/重载，旧设置迁移默认关闭；启用不自动建库。关闭不产生扫描、数据库或后端进程。

UI线程复制全部Versions不可变快照，后台不持有ViewModel。LibraryChanged/TaskCompleted命名handler退订；进度不建库，完成事件安静10秒合并，单写任务，维护中新信号下一轮。

LocalPageReadRequest携带具体GalleryInfo+原始页号；initialPageNumber在历史恢复前按Pages.Number找实际index，不用页号减一、不写历史伪造定位。页不存在显式失败；已打开同副本也跳页清offset；同作品不同副本换源且不改默认偏好。最后有效点击生效、旧reader正确Dispose不双写进度。验证缺页/无GID/completed历史/连续与横向RTL/返回结果。

遵循PRODUCT.md/DESIGN.md Fluent，深浅/1200x800与540x700/键盘。只做离屏验证，无法离屏记not_run。

## 10. 执行依赖

| ID | 工作 | 依赖与完成证据 |
| --- | --- | --- |
| E00 | 工作区/SDK/现有测试基线 | HEAD、原始git状态、后台日志 |
| E01 | 来源锁/矩阵/C#工程和模型 | E00；锁文件、版本/配置测试 |
| E02 | 汉明/Wilson/KModes/HNSW/BLAKE3/dHash | E01；标准向量、近邻/重载测试 |
| E03 | 托管ORB与受限编解码 | E01；采样/方向/空白/裁剪回归 |
| E04 | SQLite/目录/tar/有界导入报告 | E02/E03；去重/别名/失败/只读测试 |
| E05 | 磁盘IVF/分段/合并/clean/恢复 | E02/E04；截断/损坏/重放/暂停测试 |
| E06 | 六CLI/认证HTTP/无窗口探针 | E03–E05；真实Host/401/空图/指标 |
| E07 | 真实漫画质量门槛 | E02–E06；脱敏Recall/P95/覆盖率报告 |
| E08 | Core清单/协调器/代际发布 | E04/E05；WAL/身份/失效/恢复 |
| E09 | 设置和搜索子页 | E08；离屏UI证据，E07独立待真实图库 |
| E10 | 副本页号导航 | E09；缺页/历史/同reader切副本 |
| E11 | 下载扫描增量/资源控制 | E08/E10；debounce/关闭/只读 |
| E12 | AOT/回归/交付 | Host依赖E06；应用依赖E09–E11 |

每步更新执行记录：pending/in_progress/passed/failed/blocked、changed files、实际命令/退出码/报告、观察结果、未完成与下一步。不得无运行证据写passed。

## 11. 后台验证与完成门槛

各工程单独build，不对含wapproj的整个slnx运行dotnet build。禁用DOTNET_CLI_TELEMETRY_OPTOUT=1以减少后台环境注册表访问，所有输出重定向.qa。实现tools/imsearch/Build-Backend.ps1与Test-Backend.ps1统一入口，等待无窗口进程并核对退出码/报告。

验证：独立后端xUnit；既有Core.Tests一次；桌面Debug build；Host Release win-x64 NativeAOT publish及运行同一合成集；最终应用AOT串联。真实路径/token/图像留.qa，不进入版本控制。

必须覆盖空结果、损坏/超大图、错误token先于解码、参数边界、重复导入/别名、hash模式不一致、未知格式、截断/损坏桶、配置错配、clean后查询、build重放/暂停、源只读、取消不死锁、中文/空格/长路径、Skia/SQLite AOT加载。

后续应用加WAL备份、缺页/副本、删除/替换/禁用过滤、下载合并、writer.lock、发布每阶段恢复、旧响应、设置取消和RTL。全部有证据才能标应用完成。

后端完整迁移要求第2节全模块、六命令、真实HTTP/合成检索、重载/恢复和AOT通过；省略项必须明确，不能用不同名称掩盖。整个本地库功能还需E07和E08–E12通过。

网络/依赖失败继续无依赖工作，不装软件；样本缺失保留E07 blocked；效果失败记录类别和曲线不降门槛；AOT缺工具不冒充通过；权限拒绝准确报告且不绕过。普通实现选择/修编译错误无需再次确认。不自动提交、发布或把未验证能力写入README已验证功能。
