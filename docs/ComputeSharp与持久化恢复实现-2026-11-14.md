# ComputeSharp 与持久化恢复实现（2026-11-14）

提取阶段后续修复：面积缩放热点与逐像素兼容优化见[页图提取优化记录](页图提取缩放热点优化-2026-11-14.md)，与训练批次修复分别验证。

后续修复：训练小批次 / 同步传输瓶颈及更新后的容量上限见[GPU 训练批次与同步开销](GPU训练批次与同步开销-2026-11-14.md)。本文保留首版样本与历史验证，不能将其性能数字当作修复后结果。

本次实施替代[前置方案](ComputeSharp与索引快速恢复方案-2026-11-14.md)中的待实施部分。仅面向 Windows 10 / 11 x64，不使用 CUDA，不执行关机。所有验证使用隔离合成源、低优先级无窗口进程；未启动或控制用户的真实阅读器，未扫描 / 修改 J 盘真实库或索引。

## 恢复与兼容协议

`build-job.json` 仍使用 schema 1，既有阶段数值不变，仅追加 `CheckingSources` 与可选准备标记。旧检查点缺失这些字段时采用默认值，不更换提取配置、内容身份、量化器或倒排文件格式。

每个未完成 generation 新增独立 `build-plan.db`：

| 表 | 内容与作用 |
| --- | --- |
| `plan_gallery` | source / stable ID、作品和目录、候选签名、核对分块进度 |
| `plan_page` | 有序页任务、待办 / 已提交 / 本轮失败状态，按 `(done, sequence)` 索引 |
| `plan_candidate` | 每页按优先级排列的相对路径、长度和 mtime |

新任务以 16 个画廊为事务单位发现候选。恢复直接流式读取未完成页，每次最多读取 128 页后先关游标，再交给有界管线；不重新生成全库内存页清单，也不逐一 stat 已提交源页。已提交计数从本应用的索引元数据读取，仍有必要的 SQLite 元数据读取，不能宣称完全没有磁盘 I/O。

共享特征 / 后端先提交，再将清单 `member` 与计划页完成状态在一个附加数据库事务中提交。保留 rollback journal 和耐久 flush，没有切成 `synchronous=OFF`，也没有简单开启 WAL 后只哈希主文件。崩溃发生在后端提交与清单提交之间时，重试可以复用特征，不能把未提交页误标完成。正常取消保留已经有序消费的健康部分。

已完成提取的任务恢复训练；存在量化器时保留它；构建阶段恢复已有段 / 待办。只有真实新增或失效页需要重新提取时才进入提取阶段。显式继续重新尝试失败页，即使元数据没有变化。继续任务的画廊集合改变时，按持久化成员关系移除旧任务、补充新画廊；不为仍保留的已计划画廊先做源目录扫描。

发布前使用独立 `CheckingSources` 阶段核对源变化，16 个画廊一块提交；在核对中断后从尚未核对的块继续。发生变化的画廊只使相应页面失效，增删 / 候选优先级变化纳入计划。此阶段仍需要枚举源目录，已从“恢复开始前必须完成的全量阻塞”移到独立可见阶段，并可接续。核对不是文件系统事务快照；检查之后继续编辑源文件时，查询仍按签名过滤，下一次更新再收敛变化。

旧版未完成任务没有候选页表，首次升级接续必须枚举一次补建计划；已提交清单和描述子保留复用，后续恢复可直接读取待办。共享 `features.db`、旧缓存迁移、当前 / 在途读者快照保护保持原协议。计划发布后回收，不生成每代重复描述子库。

## ComputeSharp 的计算范围与资源所有权

`ImageSearch` 增加 ComputeSharp 3.2.0 与纯计算接口 `IImageComputeAccelerator`；Core 不依赖 GPU 包。应用服务延迟初始化设备，构造函数不访问 GPU。使用 Windows DirectX 12 的硬件适配器，拒绝软件适配器；未设置独显显存大于零的条件。设备、资源或 dispatch 失败时整次计算回退原 CPU 结果，不接受部分 GPU 输出。

- FAST：C# shader 计算整数评分，CPU 按原 cell 范围非极大值抑制、空 cell 降阈值、稳定分布，方向与 BRIEF 仍走原实现。每次 dispatch 最多 65,536 像素；整张评分图受 4M 像素上限约束。
- KModes：描述子拆成 `uint × 8`，用 `CountBits` 算哈明距离，等距仍选最小中心序号。只对至少 1,048,576 次比较使用 GPU；小分组保留 CPU，避免上传 / fence 开销。中心最多 4,096，单批最多 8,192 个描述子，并按 8M 次比较进一步缩小 dispatch。
- CPU：保留规范化、哈希、金字塔、模糊 SIMD、空间分布、方向、描述子、位直方图和 HNSW 分桶 / 查询，未以精确全距离替换原近似分桶。

GPU 服务串行持有设备和可复用缓冲。共享默认设备有引用计数，释放一个所有者不会使另一个所有者失效。取消在有界 dispatch 间检查；已提交命令完成 fence / 回读之后才归还池化数组或释放资源。任务收尾释放设备缓冲及预算租约，服务退出等写入 / 读取结束后释放设备；页面释放不取消服务任务。

共享 GPU 暂存的主机预算预留 32 MiB，每个 CPU worker 的评分图池桶另计入页估算；单设备计算缓冲上限约 21 MiB。该预算不等同进程 RSS 或整个显卡驱动的内存。没有为核显承诺显存零复制，也没有全局剪裁警告屏蔽：仅 buffer 分配方法针对 `int` / `uint` 显式保留元数据并解释 ComputeSharp 的 `IL2026`，以实际 AOT GPU 运行补充证据。

源图片的头部 / 编码读取使用单独的一路信号量，CPU 解码在读锁外并行；读取、目录核对与同步批提交使用 `THREAD_MODE_BACKGROUND_BEGIN / END`，作用域绝不跨 await。GPU 队列有界串行，CPU 前后处理继续使用原有 worker 上限和内存背压。曾尝试固定 4 / 12 个 GPU 前后处理 worker，但端到端样本比原 CPU 默认慢，最终没有保留这个额外瓶颈。

独立 Host CLI 的默认路径保持 CPU；应用和调用计算接口的后端代码可使用 GPU。采用 C# 业务代码与 C# shader，不需要 CUDA 或自写 C++ 包装，但底层仍依赖 DirectX 与驱动。

## 验证证据

原始日志、合成媒体与全新发布目录位于 `.qa/index-gpu-20261114/`，不会作为正式资料库。

- Core 149、后端 62（显式设置 `EHVIEWER_GPU_TESTS=1`）、Headless UI 68。后端覆盖不同尺寸、cell 边界、不同 FAST 阈值、密集随机图和空白页，逐项比较最终 `OrbFeature` 的位置、角度、响应、层级与描述子；覆盖哈明平票、跨批、确定性训练、失败输出丢弃、取消、释放后复用与多个设备所有者。
- 应用回归覆盖首个待办前源枚举为零、训练 / 构建阶段不回退、量化器不变、核对分块接续、失败页重试、画廊集合变化、空计划重新发现、旧缓存迁移、发布文件等价、读者保护与退出收尾。
- 全新 Host AOT 实际运行 31 项通过：`host-verification/verification.json`。包括真实合成图片、导入 / 训练 / 暂停 / 合并 / 搜索、鉴权与本机 HTTP；Host 本身仍为 CPU。
- AOT Headless 实际 GPU 验证逐项比较 ORB 和训练，记录 NVIDIA GeForce RTX 5070 Ti、SSE2、FAST 36 次与训练分配 6 次 dispatch。Headless + Skia 窗口只存在于内存，不创建 Win32 窗口。
- 第 10 页提交后用退出码 37 直接结束子进程，不执行 finally，再由新 AOT 进程真实 `ShellViewModel.InitializeAsync` 自动接续至 11 页，复用 11 页；源路径 / 大小 / mtime 不变，完成后仅保留一代。

- 元数据规模探针在实际中断后补入 437,931 页任务 / 11,925 个画廊，437,930 页已提交，仅一页待办。用当前 AOT 的真实应用服务恢复，首个待办前源枚举为 0，缓存条件下 289.25 ms 到达提取入口；`native-scale-v3.*.log`。大量已提交行指向不存在的合成目录，若错误扫描会直接失败。该试验只验证元数据规模 / 调用路径，没有创建这些图片，也没有测真实 HDD。

最终当前源码发布到全新 `app-aot-v3` / `ui-aot-v3` 后再次实际运行通过。应用服务探针 12 项通过（`app-probe-v3.json` / `native-app-v3.*.log`）；NativeAOT Headless 的 GPU / SIMD / 双主题 / 页面生命周期通过（`native-ui-v3.*.log`），进程中断接续和规模探针分别为 `native-recovery-v3.*.log` / `native-scale-v3.*.log`。最终常规回归共 279 项，日志为 `core-tests-final.log`、`backend-tests-final.log`、`ui-tests-final-v3.log`。发布日志为 `publish-app-aot-v3.log` / `publish-ui-aot-v3.log` / `publish-host-aot.log`。

## 测量与设备边界

环境为本机 Windows x64、.NET SDK 11.0.100、20 个逻辑处理器、NVIDIA RTX 5070 Ti。编译 / 常规探针限 2 个逻辑处理器、低优先级；吞吐对照单独运行，使用实际 20 个逻辑处理器和低优先级。GPU 设备经实际硬件 dispatch 确认。

每个配置独立进程，三轮新库，图片为 1200 × 1700 合成 PNG；生成图片不计时，总建库计时包括计划、提取、训练、分桶和发布前核对，源快照不变。默认 18 worker，CPU 工作区 1024 MiB，GPU 为共享预算扣除预留后 1020 MiB。表中耗时为三轮中位数，峰值为各进程 `PeakWorkingSet64`。

| 页数 / 路径 | 总建库 ms | CPU 时间 ms | 进程峰值 MiB | 训练 ms | 分桶 ms |
| --- | ---: | ---: | ---: | ---: | ---: |
| 48 / CPU | 1084.5 | 12312.5 | 554.9 | 123.0 | 140.4 |
| 48 / GPU | 1163.6 | 9218.8 | 1085.3 | 122.1 | 137.6 |
| 384 / CPU | 7441.8 | 103046.9 | 985.3 | 464.8 | 798.0 |
| 384 / GPU | 6973.0 | 79312.5 | 1481.9 | 644.0 | 879.2 |

384 页的总时间减少 6.3%，CPU 时间减少 23.0%；48 页总时间增加 7.3%，CPU 时间减少 25.1%。因此本轮 GPU 是部分卸载，不能称为通用大幅提速；384 页训练单独仍比 CPU 慢，后续优化需要按传输 / 分配粒度继续评估。GPU 初始化 / 驱动资源也使进程峰值增加约 500 MiB，不能把计算缓冲预算当作进程内存上限。

GPU 三轮实际均完成 FAST：48 页各 2,064 次、384 页各 16,512 次；384 页另有各 168 次训练分配，48 页因阈值保留 CPU 训练。两个页数组各自的 6 个 CPU / GPU 输出 `quantizer.bin` 与 `invlists.bin` SHA-256 完全一致：

| 页数 | quantizer.bin | invlists.bin |
| --- | --- | --- |
| 48 | `9d6011b42c9889e074d4b5f6f1317000827abde478c13dc85eb592e264eb5e6c` | `4b327d597e1d09aa8e66a7b21070b687fa70a640b782e145e14fcdff7890c35e` |
| 384 | `d9036f689b5164f77f773769e9a44e15dc0fe9f00a090f5c035029ac52038a43` | `9906234c996ff9c839b854c3fc2034871d3f9f6a72902476037ada3fe4b92fd4` |

报告在 `perf-final-{cpu,gpu}-{48,384}/report.json`，产物为 `bench-aot-v2`。测量后仅补充空计划重试守卫和代码格式 / 注释，不改变此处有页计划的性能路径；最终源另经全新应用与 Headless AOT 发布运行验证。

未测 AMD / Intel 实机、真实 43.8 万页的全量提取吞吐、冷机械盘延迟 / 队列、桌面帧延迟、设备丢失或 TDR 实机故障。跨厂商依据为标准 DirectX 12 整数计算路径与失败回退，不能写成已通过三家硬件认证。性能样本是合成图片、缓存文件系统与指定页数，不能外推真实漫画库或承诺通用加速倍数。上游 Avalonia / FluentAvalonia / DataGrid 的既有剪裁与 DComposition 警告保留在发布日志；Headless 验证不覆盖真实 Win32 合成器。

## 可复现命令

```powershell
$env:DOTNET_PROCESSOR_COUNT = '2'
$env:EHVIEWER_GPU_TESTS = '1'
dotnet test tests/EhViewerForPc.Core.Tests -c Debug
dotnet test tests/EhViewerForPc.ImageSearch.Tests -c Debug
dotnet test tests/EhViewerForPc.ImageSearch.Ui.Tests -c Debug -p:PublishAot=false
dotnet publish src/EhViewerForPc -c Release -r win-x64 -p:PublishAot=true -o .qa/index-gpu-20261114/app-aot-v3
dotnet publish src/EhViewerForPc.ImageSearch.Host -c Release -r win-x64 -p:PublishAot=true -o .qa/index-gpu-20261114/host-aot-final
pwsh -NoProfile -File tools/imsearch/Test-Backend.ps1 -HostPath .qa/index-gpu-20261114/host-aot-final/EhViewerForPc.ImageSearch.Host.exe -OutputDirectory .qa/index-gpu-20261114/host-verification-new
```

AOT 产物运行统一通过 `tools/imsearch/Process.ps1` 的 `Invoke-ImsearchProcess`，它使用 `CreateNoWindow=true`、重定向和低优先级；不要直接启动普通图形入口。应用探针参数为 `--image-search-probe-root <全新隔离目录> --out <JSON>`。Headless 验证器源位于 `.qa/index-gpu-20261114/aot-runner/`，`seed <新目录>` / `resume <同目录>` 复现进程中断；`scale <另一个 seed 目录>` 只构造本应用元数据规模，不创建 43.8 万个真实页文件。

性能验证器源位于 `.qa/index-gpu-20261114/bench/`，参数依次为全新输出目录、worker（0 为自动）、工作区 MiB、宽度、页数、`cpu` / `gpu`；每次三轮，生成图片在计时外，每轮生成新库并快照比对源文件。吞吐运行前撤销 `DOTNET_PROCESSOR_COUNT=2`，并与其他构建 / 探针串行。
