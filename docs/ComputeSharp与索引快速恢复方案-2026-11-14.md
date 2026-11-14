# ComputeSharp 与索引快速恢复方案（2026-11-14）

状态：方案及隔离可行性验证，尚未将 GPU 集成到应用。替代[最初加速评估](以图搜图索引加速方案-2026-11-13.md)中的 CUDA 与跨平台候选。最新约束为：仅 Windows，优先 C#，覆盖 NVIDIA / AMD / Intel 主流 GPU，不使用 CUDA。继续遵守源库只读与后台验证要求，关机要求已取消。

## 结论与事实边界

推荐 **ComputeSharp + DirectX 12**。业务层与计算 shader 都用 C# 编写，源生成器在构建期生成 shader 描述与字节码；无需自写 C++ 包装或引入 CUDA。底层仍依赖 Windows DirectX 与显卡驱动，“C# 实现”不代表没有原生系统调用。官方说明见 [ComputeSharp](https://github.com/Sergio0694/ComputeSharp)；其 [3.0 发布说明](https://github.com/Sergio0694/ComputeSharp/releases/tag/v3.0.0)明确包含 AOT 支持。

ComputeSharp 提供跨厂商的 D3D12 路线，不等于任意老显卡、虚拟显示适配器都支持所需能力。设备选择必须确认硬件加速、shader 与资源能力，不能把 WARP 软件适配器当成 GPU。正式验收需要 NVIDIA / AMD / Intel 设备矩阵；本次只有 NVIDIA 本机实测。硬件与 shader 能力需按 [Direct3D 功能级别说明](https://learn.microsoft.com/en-us/windows/win32/direct3d12/hardware-feature-levels)检测。

本机只读查询确认 RTX 5070 Ti。隔离工程使用官方稳定包 ComputeSharp 3.2.0、`net11.0`，全新 AOT 发布后，无窗口运行 257 组 256 位哈明距离，GPU 与 C# `BitOperations.PopCount` 逐项相同，且确认 `IsHardwareAccelerated=true`、`RuntimeFeature.IsDynamicCodeSupported=false`。这里只执行一个很小的计算任务，没有跑 GPU 压测、没有测完整 ORB 或宣称建库加速倍数。

证据位于 `.qa/computesharp-20261114/`：`probe/` 为源代码与独立 CPM，`aot/` 为产物，`publish.log`、`run.stdout.log`、`run.stderr.log` 为原始日志。主程序的包引用尚未变动。

需正视 AOT 边界：3.2.0 的通用 buffer 分配 API 有 `IL2026` 标注，原因是它可能检查结构体字段类型。本次 `uint` buffer 实测通过，但这不能证明任意自定义结构体都安全。正式封装优先固定基础 `uint` / `int` 数组布局，核对上游对应分支，清除或以可审计证据解释剪裁诊断，不做全局屏蔽。不要引入运行时 shader 反射或动态编译作为必需路径。

## 恢复慢：当前可以定位到的工作

用户确认 HDD 满占用发生在“正在恢复索引，核对已完成页面”。只读取 J 盘的任务 JSON 与几个文件的大小 / 时间，得到当前任务 **437,931 页、11,925 个画廊目录**，保存阶段为 `Extracting`。没有扫描真实源库，没有打开或修改真实 SQLite 数据库。计数器查询时应用未运行、J 盘空闲，因此本次没有故障现场的物理 HDD 延迟 / 队列采样，不把代码推断冒充现场性能测量。

`LocalImageSearchService.Build` 在该文案之后，对全部 gallery 调用 `PageFileParser.Scan`，重新枚举每个目录中的页文件，再形成完整内存页表；之后才进入提取管线。`ImageIndexBuildCheckpoint` 只保存画廊列表、总页数与粗粒度阶段，没有可直接恢复的持久化页任务队列。恢复不是重新提取所有已完成图片，但必须先重新发现全部页面，才能确认哪些已完成。

另外，无论保存阶段为 `Extracting`、`Training` 还是 `Building`，该调用链都会执行枚举，随后将任务阶段写回 `Extracting`。之前的少页恢复测试验证了结果与崩溃安全，没有覆盖这个规模下的恢复启动成本。

隔离结构探针通过真实 `AppServices.ImageSearch.BuildAsync` 复现：128 个目录、4096 个空合成页，先由真实服务生成任务检查点，再分别设置保存阶段为 `Extracting` / `Building`，恢复到首次“正在提取页图”时立即取消。两种情况均重新枚举全部 4096 页，检查点都变为 `Extracting`；源路径 / 大小 / mtime 不变。日志为 `recovery.log`，源码为 `recovery/`。空页只用于触发真实枚举，未解码，温缓存计时不代表真实图片或 HDD 的恢复耗时。

后续还有这些独立风险，不能混称为当前“核对页面”阶段的全部根因：

| 路径 | 当前行为 | 优化方向 |
| --- | --- | --- |
| `PreparePageWork` / `IsSafeSourcePath` / `IsUnchangedCandidate` | 每个候选查询清单、检查路径祖先，再重复查询文件签名 | 批量流式读取清单，限制目录核对；待处理文件打开时仍核验实际位置 / 身份 |
| `MigrateOwnedFeatureCaches` | 恢复 / 清理都会检查全部自有代；迁移入口还可能触发 `VACUUM` | 持久化迁移完成状态，把空间整理移出恢复关键路径 |
| `ImageDatabase.ReadCached` | 按 image ID 枚举，再按内容哈希逐条读共享特征 | 批量获取所需哈希、按共享库键顺序读，再恢复稳定 image ID 顺序 |
| `ReadPointer` / `CheckedFile.Open` | 校验会读取整份清单 / 搜索文件；重复打开可能重复完整读 | 不可变代内复用已验证句柄与索引对象；后续再评估分块校验格式 |

## 第一阶段：先做真正的断点恢复

1. **持久化页任务计划。** 在应用自有数据目录建立 `build_tasks`，记录稳定序号、来源 / 画廊 / 页号、候选路径、枚举时签名、状态与结果引用。枚举按画廊流式提交；计划准备中崩溃，只接续未完成画廊。不能只保存 JSON 页游标，因为失败页、候选回退与部分提交不是一个连续整数能表达的。
2. **优先恢复剩余任务。** 重启只读取任务元数据与未完成页队列，复用已提交特征，不先遍历全部源文件。源目录离线与来源配置变化仍需显式处理；不能把离线误认为删除并发布空索引。
3. **按真实阶段接续。** `Extracting` 接续待提取页；`Training` 复用有效已完成量化器，否则只重做训练；`Building` 复用已提交索引段；合并 / 校验 / 发布独立记录。第一版不承诺训练迭代内断点，但训练中断不得倒退到全源重枚举。
4. **源变更核对独立调度。** 应用内下载事件标记变更画廊；外部修改在后台逐目录核对并合并进任务队列。不能仅凭目录 mtime 判断页内容未变。若以后使用 NTFS USN Journal，只在已有日志且具备读取能力时启用，检查卷身份与日志连续性；不自动创建日志或提权。没有可靠增量信号时，在最终发布前仍需完整核对，但不在每次启动时阻塞剩余计算。
5. **核对结果与提交一致。** 页结果、任务完成状态、后端引用按既有“特征先落盘、元数据后提交”的顺序协调；完成只指持久化批次。崩溃回放须幂等，不能因为页状态与向量引用跨库提交就漏任务。

预计收益是让恢复首次继续处理的成本随剩余队列 / 任务元数据增长，而不是先随 43.8 万个源文件增长。真实 HDD 上要测首次恢复任务的延迟与访问量，当前不预填秒数。

## 第二阶段：用 ComputeSharp 加速适合的计算

ComputeSharp 不会自动把 `OrbExtractor` 现有的 Dictionary / List / LINQ 代码变成 GPU 程序。按可保留语义的粒度拆分：

| 计算 | 首轮安排 | 原因与兼容边界 |
| --- | --- | --- |
| FAST 像素测试、角点评分、单元格局部非极大值抑制 | 优先写成 C# compute shader | 主要是整数比较与局部读取，适合大批并行；必须保留每个 cell 的阈值回退、边界和重叠去重顺序 |
| 角点空间分布、稳定排序 | 复用 CPU 参考实现 | 当前区域拆分与 tie-break 依赖有序集合，不宜首轮重写 |
| 金字塔缩放、模糊、方向与旋转 BRIEF | 首轮保留参考路径，之后逐项评估 | 当前缩放 / 模糊有 double 运算，方向使用三角函数；不能假设所有 GPU 都支持高效 FP64，更不能悄悄改变描述子 |
| KModes 中心分配、批量哈明距离 | GPU 分块计算，CPU 确定性归并 | `uint × 8` 表示 256 位描述子，优先使用标准位运算 / CountBits；中心相同时仍选原来的最小序号 |
| 分桶 / HNSW | 先保留现有逻辑 | GPU 全距离最近中心不等同于当前近似 HNSW，直接替换会改变分桶与查询行为 |
| PNG 编码、哈希、SQLite 与源库 I/O | 保留 CPU / 存储管线 | 首轮不改变规范化身份，也不把磁盘问题误交给 GPU |

现有 32 字节描述子在 GPU 上拆成 8 个 32 位字，避免把 `ulong`、64 位原子、固定 wave 宽度或 FP64 作为共同要求。图像采用明确的整数打包 / pitch，避免格式支持差异。shader 热路径不分配托管集合。

首轮目标是 **兼容现有提取结果**：CPU / GPU 的角点、顺序、坐标、描述子逐项对照，完整索引金样一致后才能复用同一提取身份。仅“看起来搜得到”不算等价。如果后续希望改成 GPU 全 ORB 或精确分桶，必须显式区分提取 / 量化版本；只改变分桶时应复用已有描述子重新分桶，而非重读全部源图重新提取。

本次微型哈明内核证明了 ComputeSharp 的集成可行性，没有证明 FAST 移植工作量、完整 ORB 等价性或实际收益。实施时先采集 CPU 的读取 / 规范化 / FAST / 分布 / BRIEF / 写入占比，再决定后续迁移顺序。

## 管线、磁盘与生命周期

建议新增 Windows 计算程序集，引用 `ImageSearch` 的纯计算接口；Core 不引用 ComputeSharp。服务仍由 `AppServices` 单一持有，构造时不初始化 GPU。设备选择、命令提交、buffer 池和异常收尾归同一个 GPU 执行服务所有，首轮可以全部写在 C# 中；现有 Host 继续承担无窗口诊断职责。

管线为：持久化待办 → 磁盘读取 → 少量 CPU 解码 / 预处理 → 有界 GPU 批次 → CPU 稳定分布 / 描述子收尾 → 单写入者。GPU 路径不再同时开满原来的 18 个 CPU ORB worker。批次按像素与资源字节控制，复用双缓冲、合并上传，避免每页创建资源与每个小操作一次同步。设备预算需区分独显与共享内存 GPU，不能用 DedicatedMemorySize 为零就拒绝核显。

磁盘读取与计算并行度分别控制：HDD 按物理盘限制在途读取、按画廊组织访问，不能因有 18 个 CPU worker 就对同一机械盘发 18 路随机读。训练 / 分桶用批量描述子读取，保留原始 image ID 处理顺序。当前 SQLite 特征库仍是共享单份，第一轮不重做全部存储格式，也不制造每代完整特征副本。

GPU 增速后还要测提交耗时。若固定 10 页一批成为瓶颈，可改成页数 / 字节 / 时间三重上限的自适应批次，明确最大重算窗口，保留必要 flush 与崩溃耐久性。不得简单改成 `synchronous=OFF`。若引入 WAL，必须同时处理发布快照：当前指针校验的是 `manifest.db` 主文件，仅切换 PRAGMA 会遗漏 WAL 中的已提交状态；需先得到一致快照或正确 checkpoint 再计算发布身份。[SQLite 官方 WAL 说明](https://www.sqlite.org/wal.html)也明确了顺序 I/O 优点与 checkpoint 约束。

取消先停止入队，等待已提交 GPU 命令完成再归还 buffer；GPU 命令不能靠取消一个 .NET await 强行中断。按小而有界的 dispatch 避免长 kernel 阻塞桌面。设备丢失后，未提交批次可用经过等价验证的 CPU 路径重做，已经提交的页面保留；不能切到不同提取器却继续使用旧身份。退出顺序仍为 shell → AppServices，等待任务 / fence 后释放设备与租约。

## 验收与实施顺序

先落地页任务表与阶段分派，解决用户已确认的恢复入口瓶颈；再落地 GPU FAST 与训练距离的分阶段原型，最后按实测处理描述子批读与提交。

- 用接近 43.8 万页的**元数据规模**验证：恢复首次继续处理前不重新枚举全部源页；构建阶段重启不回退到提取。大规模 HDD 实测需选择固定时段，不能用本次 4096 空页温缓存结果代替。
- CPU / GPU 覆盖自然页、密集网纹、空白、长图、EXIF、透明度、cell 边界、相同得分、不同尺寸与坏图；逐项检查最终提取结果与查询召回。
- NVIDIA / AMD / Intel 分别验证实际硬件运行与跨设备索引兼容。本机目前只证明 NVIDIA 的基础整数计算，另外两家尚未实机验证。
- 相同输入分别报告 CPU / GPU 的总建库时间、源 I/O、上传、计算、回传、提交、恢复首任务延迟、峰值 RAM / GPU 预算与桌面响应；不把 kernel 时间当总收益。
- 批次任意位置退出后无漏页 / 重复 ID，已发布索引仍可搜索，来源快照不变；全新 Windows AOT 应用与后端实际启动验证。

本次文档将 README / PRODUCT / AGENTS 收敛为 Windows 范围；历史平台验证记录保留其原有时间与版本事实。

本地证据可通过保留的 QA 工程复现（输出需使用新目录）：

```powershell
dotnet publish .qa/computesharp-20261114/probe -c Release -r win-x64 -o .qa/computesharp-20261114/aot-new
# 以无窗口进程助手启动 aot-new/ComputeProbe.exe，输出应包含硬件名称与 NativeAOT=True
dotnet run --project .qa/computesharp-20261114/recovery -c Release -p:PublishAot=false -- .qa/computesharp-20261114/recovery-fixture-new
```
