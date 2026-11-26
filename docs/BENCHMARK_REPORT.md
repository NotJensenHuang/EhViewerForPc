# 漫画翻译基准与验收矩阵

| 任务书条目 | 产品与评测状态 | 证据或尚需核验 |
| --- | --- | --- |
| §1 架构、旧模型与显存审计 | 已完成旧版调用链与问题定位 | [架构审计](ARCHITECTURE_AUDIT.md) |
| §2、§9.1 检测、方向、小字与低置信复核 | 产品保留原判定所用信息，另有可运行 OCR 基准 | [OCR 模型评估](OCR_MODEL_EVALUATION.md)。合成区域的结果不能替代真实页 CER、检测召回和类别分层 |
| §3 日中模型与多语言能力 | JMangaTranslator-Fast CPU 产品选项及两个 GPU 试用选项；4B 和 8B 评测入口接受经授权的本地权重；其他候选有独立核验路径 | [部署](MODEL_DEPLOYMENT.md)、[翻译候选](TRANSLATION_MODEL_EVALUATION.md)、[语言矩阵](LANGUAGE_COVERAGE.md) |
| §4 语气、成人语境、拟声词、人物和跨页术语 | 页内及同会话上一相邻页参考已接入；单区独立生成并可复核 | 持久与人工锁定术语、人物跟踪、受影响页重译和人工保真盲评尚未达到验收 |
| §5 区域 ID、格式、失败与保留输出 | 本机 IPC 校验坐标、ID 和失败状态，文本面板可查看原文和草稿 | 仅启发式拦截照抄、假名残留、有文字却只输出标点或符号，以及疑似重复，尚缺系统性的语义遗漏、错语向和新增内容校验 |
| §6 修复与排版 | 可运行合成修复基准，阅读器仍用覆盖层 | [修复对照](漫画图像修复对照-2026-11-26.md)，全页遮罩、竖排、艺术字、修复后导出待落实 |
| §7、§8、§10 工程、RTX 8 GB 和批量验收 | 原 C# 服务与进程取消保留，GPU 相关代码走原生部署 | [实现报告](IMPLEMENTATION_REPORT.md)、[性能报告](PERFORMANCE_REPORT.md)。缺少 8 GB 真机、真实图库长批次、显存与延迟对比 |

`tools/translation/quality-cases.tsv` 有 25 条合成案例，含 12 条日中样本。日中包含普通对话、拟声词、OCR 错字、人物指代，以及单句、页内与跨页参考。每条目前只有一种上下文条件。比较上下文收益时，要对同一条原文分别运行空参考、同页参考和跨页参考，保持模型权重、OCR、采样等条件一致。完整版需至少 200 条双语人工确认译文，另行覆盖真实成人语境并断开自动指标与盲评。

## 本轮的可复核结果

- OCR 使用 20 张合成页、300 个标注区域。C# DirectML 的 v5 基线检测匹配 279/300、整页 CER 13.30%；v6 检测器搭配 v5 识别为 299/300、10.08%；v6 检测与识别整链为 299/300、10.48%。v6 某些小字体与非日语样本反而退化，暂未替换默认方案。 [OCR 模型评估](OCR_MODEL_EVALUATION.md)
- JMangaTranslator-Fast 已用作者分发的真实 ONNX 权重，在本机 CPU、6 线程逐气泡运行。25 条中 7 条日中单区有实译，5 条上下文项和 13 条其他语向按能力显式跳过。7 条耗时范围 38.38–80.18 ms。没有以此小样本人为定级。CC BY-NC-SA 4.0 权重可由用户在阅读器设置主动同意后按需准备，仅限非商用。 [翻译候选评估](TRANSLATION_MODEL_EVALUATION.md)
- 两个公开 GGUF 候选的发布者提交、字节数、SHA-256 和提示模板已核对。窗口内做了实际网络准备尝试，Range 1 MiB 可读，大块传输在多次 TLS 失败或超时后仍只收到部分分段。两个同名发布者 ModelScope 查询返回 404。未拿部分文件冒充权重，尚无两模型完整下载、推理输出与保真评分。Qwen3 4B 和 8B 的评测需先在发布者处取得权重访问授权。 [模型部署](MODEL_DEPLOYMENT.md)
- 集成定向测试 46/46 通过，覆盖 OCR 原始框与源区关联、试用及旧模型守卫、阅读器自动翻页、页请求上下文确认、IPC、取消及保留失败草稿。用全新目录 `.qa/translation-candidates-aot-20261126-r3` 发布 Windows x64 NativeAOT，实际启动并核对旧两档、错误语向及不完整的公开或授权模型均返回退出码 3。真实桌面通过深色 1200×700、740×650 及浅色 920×650、640×650、540×420 DIP，验证候选控件、未选模型时禁止准备、选中微调后保存偏好并启用准备。UIA 证据在 `.qa/translation-candidates-desktop-r3-20261126`。未用部分权重启动推理。
- 新 NativeAOT 产物另运行 `--reader-regression --data-dir <隔离数据目录> --out <隔离结果目录>`，退出码 0。合成阅读器截图与 `ui-smoke.json` 保存于 `.qa/translation-candidates-reader-regression-r3-20261126/`。
- 多语独立评测在 Windows 11、i5-13600KF、CPU 六线程运行。OPUS 英中权重完成 1 条受支持样本，首次 303.67 ms；M2M100 完成 17 条，P50/P95 673.52/1137.03 ms，跳过 5 条上下文和 3 条精确繁中；NLLB 产生 20 条结构完整输出，P50/P95 907.80/1308.65 ms，跳过 5 条上下文。NLLB 的 j04 拟声词输出为重复标点，属于语义失败。状态记录结构完整性，不能从状态或速度推导保真。原始输出在 `.qa/translation-candidates-20261126/`，独立人工评分尚未执行。 [翻译候选评估](TRANSLATION_MODEL_EVALUATION.md)
- OCR 补跑官方 FP32 v6 CPU 整链，同一批样本匹配 299/300、整页 CER 10.48%、P50/P95 940/1059 ms。Baberu 三张真实 ONNX 图验证了 300 张真值裁剪的 CPU 识别，CER 53.02%、完全匹配 260/300、P50/P95 188.9/242.0 ms。日/中/英及混排合成子集 260/260，韩语 20/20 错，四条输出达 256 字重复；此测试不含检测或真实漫画页。Hayai 与 manga-ocr-base 有独立路径，尚无可计入对照的本地整组结果。 [OCR 模型评估](OCR_MODEL_EVALUATION.md)
- MADLAD-400 3B MT 的固定 Q4K GGUF 通过 SHA-256。随包 llama.cpp 因模型张量名长度限制拒绝直接加载。独立评测从已校验的量化文件恢复 BF16 后用 Transformers CPU 六线程推理，共 17 条结构完整输出，P50/P95 13.61/29.83 秒，跳过 5 条上下文与 3 条精确繁中。此结果是量化权重的 CPU 对照，不能证明本机 GGUF 产品适配或质量合格。结果为 `.qa/translation-candidates-20261126/madlad-verified.jsonl`。 [翻译候选评估](TRANSLATION_MODEL_EVALUATION.md)

## JMangaTranslator-Fast 阅读器验收

- 真实 Windows 11 桌面从空模型目录开始，通过「同意许可并准备模型」实际联网下载与校验 12 个固定文件，包括共享的 PP-OCRv5。总量 1,570,188,242 字节，本次约 57.1 秒。生成了校验回执且未遗留部分文件。截图与控件状态在 `.qa/jmanga-reader-20261126/prepared-online.png` 及 `prepared-online-tree.json`。
- 最终产物 `.qa/jmanga-reader-aot-20261126-r3` 用上一步联网取得的原版权重运行真实 CPU OCR 与 JManga ONNX 无窗工作进程。1000 × 500 合成日语页识别并翻译 2 处，阅读器成功绘制中文覆盖层并显示可复制双语。源 PNG 翻译前后 SHA-256 一致。原始响应、图上与双语截图保存在 `.qa/jmanga-reader-20261126/worker-online.json`、`reader-overlay-online.png` 和 `reader-text-online.png`。
- 原生串行工作进程接收实际取消并返回确认，随后在同一进程实际翻译两页。第二页复用已加载模型。集成定向测试 50/50 通过。测试覆盖已选和退役模型、许可要求、CPU 工作进程协议、分词边界、OCR 与阅读器回归。
- 真模型逐句对照中，C# 的 7 条日中单气泡与作者 Python 后端输出逐条一致。本机对照用于接入验证。长篇连续处理、真实漫画大规模人审与其他硬件尚未验收。 [翻译模型评估](TRANSLATION_MODEL_EVALUATION.md)
- NativeAOT r3 的本机评测入口从在线安装目录读取真实权重，输出 7 条有效单气泡译文，5 条带页或跨页上下文的样本显式标为不支持。见 `.qa/jmanga-reader-20261126/jmanga-evaluate-r3.jsonl`。
- 最终 r3 桌面验证覆盖深色 1200 × 700、740 × 650 及浅色 920 × 650、640 × 650、540 × 420 DIP。每种尺寸均可选择模型，许可说明可见，安装控件显示同意许可。最小窗口通过现有滚动区域访问下方控件。另执行合成 `--reader-regression`，退出 0。截图和读者探针分别位于 `.qa/jmanga-reader-20261126/layouts/`、`reader-regression-r3/ui-smoke.json`。
