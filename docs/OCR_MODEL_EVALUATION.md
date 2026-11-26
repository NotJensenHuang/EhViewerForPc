# 漫画 OCR 模型评估

评估日期为 2026-11-26。当前阅读器仍使用 PP-OCRv5。验证了 PP-OCRv6 Manga 的 C# ONNX 检测链和混合精度检测、识别链。选择默认模型还需要真实漫画页的同页对照，尤其是日语小字、艺术字、非日语文本及多行气泡。

## 当前链路

`MangaTranslationService` 从阅读器复制已定向的 PNG 快照，交给本地串行工作进程。`MangaOcr.Engine` 把整页长边限制到 1536 像素，准备 BGR 张量，再用 PP-OCRv5 mobile 检测器生成概率图。C# 对概率图做连通组件和旋转矩形拟合，把框映射到快照原像素。竖列附近的小标点会先合入识别框。识别前透视裁剪，做固定高 48 像素的缩放并按宽度补齐。PP-OCRv5 mobile CTC 识别器逐框处理，竖排可轮换方向、重排字形或恢复合并竖列。合格读数按邻接关系和日漫顺序合并，再交给翻译模型。检测与识别使用工作进程持有的 ONNX Runtime 1.23 DirectML 会话。

现有检测框和识别框共用同一套几何。长边降采样、带高置信错误的 CTC、小字号框外扩、识别字典覆盖，以及固定日漫阅读顺序，都是独立的质量限制。气泡、拟声词、振假名和手写文字暂无经过验证的分类器。OCR 使用 CPU 构造检测和识别张量，保持图像解码、张量准备、设备传输和推理各阶段的性能口径独立。现有模型文件的版本、尺寸、校验和来源见 [backend-assets.tsv](../tools/translation/backend-assets.tsv)。

阅读器的工作进程现在保留标点合并前每个检测框的四边形、ID 和检测分数。逐区识别保留标点合并后的几何、关联的原始检测 ID、首次文本与置信度、二次预处理得到的候选文本、最终识别结果、方向和审校状态，类别显式记为未分类。译文中由相邻行合并而成的对话携带所含识别区的 ID。低置信框进入现有的文字面板，显示原图坐标和结果，即使该页没有可以翻译的文字也能核对。候选读数保留首次识别，失败的切列恢复不会抹掉已有低置信读数。置信度是模型得分，样本中存在高置信错字。

## 固定版本和可运行性

| 候选 | 发布与许可 | 原始模型和输入输出 | 本地证据 |
|---|---|---|---|
| Hayai OCR v2.7 Supernova | `90fc61482c33858ee9ea227cd797f6fdff33345c`，2026-11-22，Apache-2.0 | 有 `config.json`、预处理与 tokenizer 文件、单文件 Safetensors、自定义配置、图像处理与建模 Python 源码。输入为漫画文字裁剪，输出为解码文字。配套可选检测器是单独的 `yolo26m-text.pt`。作者报告覆盖日语气泡与难例。 | 原仓库未含 ONNX。同作者另发的 ONNX 仓库提供 FP32、FP16、INT8、INT4 的预填充和缓存解码图。INT4 图合计约 228 MB。固定源码只做本地位置嵌入插值、张量准备、自回归生成和 token 解码。现有 C# CTC 封装无法直接运行多输入、混合元素类型及 KV 回传。两图下载在 TLS 或分段传输阶段失败，INT4 预填充图未获完整校验。没有验证量化版与原模型的逐样本等价性，未启用 Transformers 的远程自定义代码或进行本地识别。 |
| Baberu OCR v1.2 | `b9868433dbda3ed9ba1357d008599ff4ada873d5`，2026-10-19，Apache-2.0 | DINOv2 编码器和字符级自回归解码器，有单文件 Safetensors、字符词表、训练说明、FP16/INT4 视觉图及 INT8 解码图。输入是整裁剪缩放到 224² 的 RGB 张量。后续两张图分别做缓存预填充与步进，包含 Int64 token、位置和多路 KV 张量。模型作者报告日、中、英，使用 Manga109-s 人工标签补充日语手写与拟声词。 | 已审查固定提交的 ONNX 推理示例和词表接口。两张 INT8 解码图在 ORT CPU 上用零值视觉嵌入完成了预填充与 KV 步进测试，输出张量分别为 [1,257,14630] 与 [1,1,14630]，均含有限值。现有 C# C API 封装只支持单输入、单输出的 float 张量。作为单独 C# 适配器可评估多输入输出、缓存生命周期和结束条件。三张推理图均已与原站 SHA-256 核对。独立 ORT 1.19.2 CPU 运行完成，支持范围内实测与越界行为见裁剪区实测。未用真实手写样本。 |
| PP-OCRv6 Manga v0.3 | `d65838996a00ef6b1c73ca3a700d7a8c5fbbef8a`，2026-11-06，Apache-2.0 | 独立 DB 检测和高 48 的 CTC 识别 ONNX，有 FP32、FP16、Paddle 权重与 19,053 字词表。检测输出为概率图，识别输出 `[N,T,19055]`，词表额外含 blank 和空格。作者报告训练参考 Manga109-s，支持竖排裁剪旋转与气泡外检测。实验语言模型可单独下载。 | C# ORT 1.23 实测 FP32 检测器在 CPU 和 DirectML 均可运行。FP16 识别权重用两端 Cast 衔接既有 float 张量 API，DirectML 整链验证成功。CPU 路径在该混合精度图会话初始化时报 `SimplifiedLayerNormFusion`。随后续传获得官方 FP32 识别权重，C# ORT CPU 整链以原始 FP32 图成功复测。 |
| tatsumoto/manga-ocr-base | `818d076bd8b95f0f4e18968b20c70791adea681c`，2026-07-08，Apache-2.0 | 单文件 Safetensors、ViT encoder + BERT decoder、224² 图像预处理、字符级 `vocab.txt` / tokenizer 配置。模型卡列出日语竖横排、振假名、叠图和字体变化。推理输入为已检测裁剪，需外置检测器。 | 仓库无 ONNX。现有 C# 单图 CTC 路径不能加载此 encoder-decoder。可单独评估 C# 导出图与生成循环，或保留 Python 为离线基准。官方 Safetensors 为 444,060,040 字节，官方直接下载和同哈希镜像续传未完整取得。镜像暂到约 30.32 MB。本地质量与耗时暂缺。 |

四个固定提交均未列出 Safetensors 分片索引。PP-OCRv6 的词表为单独文本字典，其余候选的模型卡、配置和文件入口可按下列链接检查。作者的样本规模、覆盖和准确率属于公开转述，本项目的基准结果见下一节。

- Hayai 发布文件及作者报告。 [固定版本](https://huggingface.co/JustANormalTinkerer/hayai-ocr-v2.7-supernova/tree/90fc61482c33858ee9ea227cd797f6fdff33345c)
- Hayai 同作者的另发 ONNX 导出、权重与本地推理参考。 [固定 ONNX 版本](https://huggingface.co/JustANormalTinkerer/hayai-ocr-v2.7-supernova-onnx/tree/bc1e39f962b6c500a4855bd8eb4e02f11483bedb)
- Baberu 文件、模型卡及推理示例。 [固定版本](https://huggingface.co/genshiai-daichi/baberu-ocr/tree/b9868433dbda3ed9ba1357d008599ff4ada873d5)
- PP-OCRv6 权重、词表及参考预处理。 [固定版本](https://huggingface.co/Kellenok/PP-OCRv6_manga/tree/d65838996a00ef6b1c73ca3a700d7a8c5fbbef8a)
- manga-ocr-base 配置、预处理和模型。 [固定版本](https://huggingface.co/tatsumoto/manga-ocr-base/tree/818d076bd8b95f0f4e18968b20c70791adea681c)

## 同一批合成页实测

Windows x64、.NET SDK 11.0.100、ONNX Runtime 1.23、NVIDIA RTX 5070 Ti 16,303 MiB，20 页、300 个区域。每页 15 类，每类变化字号和位置，共用部分字符串。注音组模拟独立小假名，上下文相连的真正振假名、手写原稿与复杂线稿未覆盖。斜体仿字形组采用系统字体，不能代表手写识别。有效文字框与检测四边形外接矩形用 IoU≥0.50 做一对一匹配。匹配 CER 排除漏检。整页 CER 把漏检和因低置信未接纳的文字视作删除。CER 和 exact 均做 NFKC、首尾空白标准化。工具另输出按空白切词的 WER，对于日语参考意义有限。计时含图片解码、预处理、推理和后处理。不同进程运行时阶段存在冷启动及系统负载差异。

| 方案 | 匹配 | 检测精确率 | 匹配 CER | 整页 CER | exact | 页延迟 P50 / P95 |
|---|---:|---:|---:|---:|---:|---:|
| PP-OCRv5 CPU | 279/300 | 93.00% | 9.79% | 13.30% | 218/300 | 1160 / 1373 ms |
| PP-OCRv6 FP32 检测 + v5 识别 CPU | 299/300 | 99.67% | 9.91% | 10.08% | 224/300 | 716 / 1228 ms |
| PP-OCRv6 FP32 检测和识别 CPU | 299/300 | 99.67% | 10.32% | 10.48% | 249/300 | 940 / 1059 ms |
| PP-OCRv5 DirectML | 279/300 | 93.00% | 9.79% | 13.30% | 218/300 | 356 / 4309 ms |
| PP-OCRv6 FP32 检测 + v5 识别 DirectML | 299/300 | 99.67% | 9.91% | 10.08% | 224/300 | 452 / 1385 ms |
| PP-OCRv6 FP32 检测 + FP16 识别 DirectML | 299/300 | 99.67% | 10.32% | 10.48% | 249/300 | 453 / 2471 ms |

- 匹配的方向正确数为 v5 的 279/279 和 v6 的 298/299。小假名组在 v5 多数已有读数，但原检测框含更多边距，严格 IoU 只匹配 1/20，v6 匹配 19/20。几何匹配变化不能等同于找回 18 段漏字。
- v6 整链把「ドキッ」的不同字号渲染从 0/20 提高至 20/20，限定于一条拟声词。垂直日语测试两者均为 20/20。极小字体完全匹配从 18/20 降到 10/20，法语 CER 从 9.06% 恶化到 18.75%，两者仍将韩语 20/20 区域留为复核，未产生可接受的文本。中日混排组 v5 与 v6 整链均为 20/20。同一批中识别差异有明确的得失。
- 三组 DirectML 均实际执行了 DirectML 算子，程序逐会话检查首份运行记录。样本只覆盖单个顺序进程，未测真实漫画、8 GB 显存、持续批量。当前结果支持优先复评 v6 检测器，识别器应按语言和区域类别扩大测试。

保留原检测结果的改动已与 Git HEAD `de758cf408984dbb97ac519a844b96b6d45b44a8` 独立编译比较。同一套 v5 DirectML 权重和 20 页输入，280 个获准翻译的输出区块逐条文本与像素框一致。当前输出共保留 300 个原始框和 300 个观察记录，其中 20 个无有效读数的检测框可复核。每个观察以及每个已接纳区块的关联 ID 均可解析到源记录。

## 复现

以下命令从仓库根目录执行。需要 .NET 11、Python 3.11 与 Pillow。准备 FP16 图需要 ONNX Python 包，评测本体使用既有 C# 推理。将 `$native` 指向应用所附的 `manga-native`，将 `$baseline` 指向含现有 `det.onnx`、`rec.onnx`、`rec-dict.txt` 的模型目录。所有测试资料写入忽略的 `.qa/`。

```powershell
$work = ".qa/ocr-candidates-20261126"
$tools = "tools/translation/ocr-benchmark"
python "$tools/benchmark.py" generate "$work/synthetic"
dotnet build "$tools/OcrBenchmark.csproj" --artifacts-path "$work/build"
pwsh -File "$tools/Download-OcrArtifacts.ps1" -Candidate ppocr -Destination "$work/PP-OCRv6_manga"
python "$tools/prepare_ppocr.py" "$work/PP-OCRv6_manga" "$work/ppv6-det-with-v5-rec" --baseline-recognizer $baseline
python "$tools/prepare_ppocr.py" "$work/PP-OCRv6_manga" "$work/ppv6-fp16-cast" --half-recognizer
$bench = "$work/build/bin/OcrBenchmark/debug/OcrBenchmark.dll"
dotnet $bench $native $baseline dml "$work/synthetic/pages" > "$work/baseline-dml.jsonl"
dotnet $bench $native "$work/ppv6-det-with-v5-rec" dml "$work/synthetic/pages" > "$work/v6-det-v5-rec-dml.jsonl"
dotnet $bench $native "$work/ppv6-fp16-cast" dml "$work/synthetic/pages" > "$work/v6-fp16-dml.jsonl"
foreach ($model in "baseline", "v6-det-v5-rec", "v6-fp16") {
    python "$tools/benchmark.py" score "$work/synthetic/truth.json" "$work/$model-dml.jsonl" > "$work/$model-dml.metrics.json"
}
```

替换运行时参数为 `cpu` 可重复基线和检测混合测试。重复 v6 全 FP32 CPU 结果时，准备步骤移除 `--half-recognizer`，使用单独输出目录。评测记录在 `.qa/ocr-candidates-20261126/`，包括同一份 `synthetic/truth.json`、逐页观测 `*.jsonl` 和分组统计 `*.metrics.json`。权重下载工具验尺寸及 SHA-256，并保留可续传的临时文件。两端 Cast 只用于 C# 试验图，不改变内部权重。布局与 OCR 纠错的复核范围限于应用现有的翻译结果文字面板，本轮未加入用户编辑与重译的持久化入口。

## 独立裁剪区识别对照

生成器同时写出同页的 300 张真值裁剪，可与只做识别的候选用相同输入比较。识别对照采用各模型本身的局部输入格式，和上节生产链路处理整页的数值不可直接互换。计分保留逐区输出，检测召回和精确率为空。裁剪读取、预处理、识别与解码计入耗时。模型会话加载在计时之外。

| 方案 | 全部 CER | exact | 每裁剪 P50 / P95 |
|---|---:|---:|---:|
| PP-OCRv5 FP32 CPU | 8.96% | 248/300 | 15.4 / 21.7 ms |
| PP-OCRv6 FP32 CPU | 8.06% | 260/300 | 20.3 / 35.2 ms |
| Baberu INT4 视觉 / INT8 解码 CPU | 53.02% | 260/300 | 188.9 / 242.0 ms |

Baberu 对合成样本中日语、中文、英语及混排的 260/260 区全部文字匹配，其中包括竖排、小字和同一拟声词的变化字号。法语 20/20 区失配，CER 13.75%。韩语 20/20 区失配，其中 4 个样本生成达 256 字符的重复片段。韩语组 CER 为 712.50%，插入字符允许该指标超过 100%。结果支持将 Baberu 限定为已确认的日／中／英语识别候选，还需真实漫画、多字手写、校验截断及部署后的内存对照。该独立工具未对原始模型输出做语言筛选。

```powershell
$models = "$work/PP-OCRv6_manga"
python "$tools/prepare_ppocr.py" $models "$work/ppv6-fp32"
python "$tools/measure_crops.py" ppocr "$work/ppv6-fp32" "$work/synthetic/crops" > "$work/v6-crops.jsonl"
python "$tools/benchmark.py" score "$work/synthetic/truth.json" "$work/v6-crops.jsonl" --crops
$env:HF_HOME = [IO.Path]::GetFullPath("$work/hf-cache")
foreach ($candidate in "baberu", "hayai", "manga-ocr") {
    pwsh -File "$tools/Download-OcrArtifacts.ps1" -Candidate $candidate -Destination "$work/models-$candidate"
    if ($LASTEXITCODE -ne 0) { continue }
    python "$tools/measure_crops.py" $candidate "$work/models-$candidate" "$work/synthetic/crops" > "$work/$candidate-crops.jsonl"
    if ($LASTEXITCODE -ne 0) { continue }
    python "$tools/benchmark.py" score "$work/synthetic/truth.json" "$work/$candidate-crops.jsonl" --crops
}
```

本地裁剪工具对 Baberu 与 Hayai 只执行 SHA-256 固定的已读参考推理文件，使用 NumPy、Pillow、ONNX Runtime 与 Hayai 所需的 tokenizers 包。manga-ocr 通过本地 Safetensors、tokenizers 和 Transformers 标准 ViT/BERT 推理，禁止 Hub 下载和自定义模型代码。下载脚本与运行脚本分离，用户图像作为本地文件读入。未能在 ModelScope 查到这三组相同发布方、相同版本的镜像，另有衍生权重的搜索结果不计为原候选。

## 工件与中断记录

以下文件位于隔离目录 `.qa/ocr-candidates-20261126/`。下载脚本只接受表中的发布提交、文件长度和 SHA-256。C# DirectML 与裁剪区程序只读取本地 PNG，未把测试图像上传到搜索或模型端点。完成实测的模型在该目录留下逐项预测和计分结果。

| 工件 | 内容 |
|---|---|
| `synthetic/truth.json` | 20 页、300 区的图片名、真值、方向与人工生成坐标 |
| `baseline-dml.metrics.json`、`v6-det-v5-rec-dml.metrics.json`、`v6-fp16-dml.metrics.json`、`v6-fp32-cpu.metrics.json` | 三组 DirectML 主对照和完整 FP32 CPU 的逐类指标 |
| `legacy-head-dml.jsonl`、`with-raw-detections-dml.jsonl` | HEAD 与当前版本整页对照，当前版本具备从译文到观察记录再到原始检测框的关联 |
| `v5-crops.metrics.json`、`v6-fp32-crops.metrics.json`、`baberu-crops.metrics.json` | 三组完整真值裁剪识别对照，另有同名前缀 `.jsonl` |
| `baberu-decoder-smoke.txt` | INT8 预填充、步进与全部缓存张量的接口实测 |

- Hayai 原始 Safetensors 的连续部分位于 `hayai-ocr-v2.7-supernova/model.safetensors.partial`（3,615,259 / 633,564,960 字节）。同作者 ONNX 的位置嵌入已下载并验证。官方并行下载器取得 INT4 预填充图的 14 个文件块中的 7 个，保存在 `parallel-download/` 的稀疏 `.incomplete` 文件中。该文件缺非连续区间，不能按文件长度做断点续传，也不能作为模型加载。解码图遇到 TLS 错误。可控制的复现方式是使用 `-Candidate hayai` 从发布提交下载两个图，完整通过该量化版本的 SHA-256 后再运行裁剪对照。
- `manga-ocr-base/model.safetensors.partial` 为具有连续起始字节的 30,324,471 字节部分文件，来自相同固定提交的 HTTP 镜像请求。官方直连也进行过续传，ModelScope 同名发布入口未返回可校验文件。请从该目录用下载脚本继续；续传数据在与原站发布的完整 SHA-256 相符之前不应加载。并行下载失败留下的同尺寸预分配文件，不能用来替代这份连续部分。
- Baberu 三张图与本地参考程序已完成校验和实测，额外启动的重复下载已停止。所有本轮下载进程已退出，留存的工件都在 `.qa/`。
