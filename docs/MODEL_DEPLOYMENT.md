# 漫画翻译模型部署

当前应用提供 JMangaTranslator-Fast v1 CPU ONNX，以及三个日语 → 简体中文 GPU 试用模型。发行包附推理运行库，权重不放进发行包。两份旧通用 Hy-MT2 保持退役，其文件和旧首选项在升级时保留。

1. 打开「设置 → 界面与阅读 → 漫画翻译」，可选择 JMangaTranslator-Fast v1、Qwen3 4B 语境翻译、Hy-MT2 漫画日中微调或 Qwen2.5 7B 日中字幕微调。现有模型选择不会自动切换。
2. 选择简体中文和专用本机模型目录，点击「下载并准备模型」。JMangaTranslator-Fast 需点击「同意许可并准备模型」，其权重许可为 CC BY-NC-SA 4.0，仅限非商用。准备过程下载或复用固定版本的 PP-OCRv5 与选中模型，完成 SHA-256 校验后才设置就绪。允许取消并保留已完成资源。
3. 打开静态日文页并点击「翻译本页」。也可在阅读器开启自动翻译。JMangaTranslator-Fast 用 CPU 运行 OCR 和 ONNX 单气泡翻译，无需 Python 或 GPU。Qwen3 4B 语境翻译参考同页附近原文及同一工作进程已经处理的上一相邻页原文。可从「双语文本」补充本页场景后点击「结合语境重译」，无需录入台词。其他 GPU 试用模型保留原有双语参考格式。
4. 运行失败时核对可用磁盘空间与推理库。GPU 选项还需匹配 DirectML 和 Vulkan 的显卡驱动与显存。模型目录下若同名文件校验失败，应用保留该文件，请使用另一专用目录或自行处理冲突文件。

## 固定权重

| 模型 ID | 作者仓库与固定版本 | 权重文件 | 获取与适用范围 |
| --- | --- | --- | --- |
| `jmanga-fast-v1` | [muscgab，151ceba](https://huggingface.co/muscgab/JMangaTranslator-Fast/tree/151cebac786f262a9144fc109ac5341a94c3da12) | 两张 FP32 ONNX 图及分词和运行资源，约 1.55 GB | 应用内主动准备，仅限非商用。日语 → 简体中文，单气泡 |
| `qwen3-4b-context` | [Qwen，bc64014](https://huggingface.co/Qwen/Qwen3-4B-GGUF/tree/bc640142c66e1fdd12af0bd68f40445458f3869b) | `Qwen3-4B-Q4_K_M.gguf`，2,497,280,256 字节 | 应用内按需准备，ModelScope 作者仓优先，Hugging Face 固定提交备用。Apache 2.0，日语 → 简体中文，Vulkan 语境试用 |
| `hy-mt2-manga-zh-hans` | [fumetodev，e5c4591](https://huggingface.co/fumetodev/Hy-MT2-1.8B-JP-Manga-Finetune-chinese-zh-Hans-v1-GGUF/tree/e5c4591e678ad7a0e46a305a02468d6da4e90fb9) | `manga-zh-Hans-v1-Q4_K_M.gguf`，1,133,080,416 字节 | 应用内准备，优先 Hugging Face 镜像，失败回退原站。Apache 2.0，日语 → 简体中文 |
| `qwen2.5-7b-ja2zh` | [enet45，8d8bd26](https://huggingface.co/enet45/qwen2.5-7b-ja2zh-gguf-v1.1/tree/8d8bd267d5c260af36d8c757a92566eb319a1705) | `qwen2.5-7b-ja2zh-q4_k_m.gguf`，4,683,073,440 字节 | 应用内准备，Apache 2.0，日中字幕用微调 |
| `qwen3-4b-ja2zh-eval` | [enet45，48a25d6](https://huggingface.co/enet45/qwen3-4b-ja2zh-gguf-v1.1/tree/48a25d6ac650ef1a81937f33afac8ce0c7021573) | `qwen3-4b-ja2zh-q4_k_m.gguf`，2,497,278,784 字节 | Apache 2.0，作者要求用户接受访问条件，手动下载后用于评测 |
| `qwen3-8b-ja2zh-eval` | [enet45，b46f43f](https://huggingface.co/enet45/qwen3-8b-ja2zh-gguf-v1.1/tree/b46f43f522d343e1d2a6e0c4dc5a75100b99c5cd) | `qwen3-8b-ja2zh-q4_k_m.gguf`，5,027,783,392 字节 | Apache 2.0，同作者量化对照，用户授权下载后评测 |

Hy-MT2 漫画微调的两个下载入口使用同一发布提交和同一 SHA-256。网络传输发生临时中断时，应用可从该分段已写入的位置重试。验证中两个入口均能返回字节区间，且都可能重定向到同一个 Hugging Face CDN。

在本机实测中，两个入口的 1 MiB 字节段一致。新安装器通过镜像收到 183,432 字节并报告进度，取消后清理临时文件。完整镜像传输受当前网络吞吐限制，已保留 19,259,392 字节的可续传前缀，整份权重尚未通过 SHA-256。记录见 `.qa/hymt-mirror-check/range-comparison.txt` 和 `.qa/hymt-mirror-check/live-installer-progress.log`。

enet45 的 4B 和 8B 评测模型先在作者页面登录并取得访问权，再将原名 GGUF 放入本机专用目录。评测命令接受对应 ID，不需要在应用中保存 Hugging Face Token。全文 SHA-256 列在 `tools/translation/backend-assets.tsv`。另有 [8B Safetensors 合并仓](https://huggingface.co/enet45/qwen3-8b-ja2zh-merged-v1.1)，不直接传给 GGUF 评测入口。

```powershell
$app = (Resolve-Path '<发布目录>/EhViewerForPc.exe').Path
$native = Join-Path (Split-Path -Parent $app) 'manga-native'
$cases = (Resolve-Path 'tools/translation/quality-cases.tsv').Path
$models = (Resolve-Path '<存放权重的目录>').Path
& $app --manga-translation-worker --evaluate $cases $models $native hy-mt2-manga-zh-hans
```

JSONL 每行包含样本 ID、模型 ID、上下文模式、输出、耗时与状态。评测入口只运行 `ja → zh-CN`，校验固定权重和模型所需文件，不访问网络。JMangaTranslator-Fast 的模型 ID 为 `jmanga-fast-v1`，页和跨页样本记为不支持上下文。语料和结果对比分别见[基准报告](BENCHMARK_REPORT.md)及[翻译候选评估](TRANSLATION_MODEL_EVALUATION.md)。
