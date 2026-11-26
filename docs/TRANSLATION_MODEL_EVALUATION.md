# 非 GGUF 与多语翻译候选核验

| 候选与核验版本 | 权重、架构与许可 | 推理路径和限制 |
| --- | --- | --- |
| JMangaTranslator-Fast v1，文件集 `151ceba` | 368M 单气泡编码解码，根目录 1.47 GB Safetensors，ONNX 发布版 1.54 GB，包含 encoder、decoder 和 72 MB host 数组。权重 CC BY-NC-SA 4.0，代码 MIT。训练含 OpenSakura 与作者私有漫画文本 | C# 可使用 ONNX Runtime 加载两个计算图，还需实现 SentencePiece、host 数组处理和自回归解码。单气泡接口没有页或跨页上下文。作者测 Manga109-s 的参考译文为模型生成，作者速度测试的 RTX 3060 为 12 GB。[模型卡](https://huggingface.co/muscgab/JMangaTranslator-Fast) · [ONNX 文件](https://huggingface.co/muscgab/JMangaTranslator-Fast/tree/main/onnx) · [权重许可](https://huggingface.co/muscgab/JMangaTranslator-Fast/blob/main/LICENSE-weights.md) |
| NanoSakura-2.2-0.2B，`5f09a52` | 752 MB Safetensors，MIT，Transformer 编码器和 Mamba2 解码器，包含 tokenizer.json 和 tokenizer_config.json。配置 auto_map 指向发布者的 modeling_mamba2_s2s.py。训练资料含 ACG 蒸馏与维基平行文本 | 需要审计和固定自定义代码并验证本地推理或 ONNX 导出。未找到发布者提供的可直接装载 ONNX。当前 runner 不执行此代码。[权重目录](https://huggingface.co/telecomadm1145/NanoSakura-2.2-0.2B/tree/5f09a52217890f8ceff884e69e4deec24e91e2fb) · [模型卡](https://huggingface.co/telecomadm1145/NanoSakura-2.2-0.2B) |
| enet45 Qwen3-8B Ja2Zh 1.1 merged | 发布仓库显示 Safetensors、Apache 2.0 与 Ja2Zh 字幕训练数据引用。仓库网页的敏感内容访问提示使本轮未独立核验索引和各分片大小 | 同作者另有固定量化版，候选尚无推理证据。不从上游 Qwen 通用模型的效果外推该微调版。[权重目录](https://huggingface.co/enet45/qwen3-8b-ja2zh-merged-v1.1/tree/main) |
| Qwen3-4B 通用 | 官方提供 Apache 2.0 的 Safetensors 及 GGUF 量化发布 | llama.cpp 具备独立运行路径，翻译需要明确语言和聊天模板模式。保真、上下文和峰值显存需要真实推理。[原始权重](https://huggingface.co/Qwen/Qwen3-4B) · [GGUF 文件](https://huggingface.co/Qwen/Qwen3-4B-GGUF/tree/main) |
| NLLB-200 distilled 600M | Meta 原始仓库提供 PyTorch 权重、SentencePiece 和 tokenizer 配置，使用 M2M100ForConditionalGeneration 模型族。CC BY-NC 4.0，作者将其定位为句级研究模型 | 已有语言标记接口的离线 runner，需先准备经审核并固定摘要的 Safetensors 转换，或单独核验 ONNX 导出。计划使用 `zho_Hans`、`zho_Hant` 区分字形。无实际漫画保真数据。[模型卡](https://huggingface.co/facebook/nllb-200-distilled-600M) · [权重目录](https://huggingface.co/facebook/nllb-200-distilled-600M/tree/main) |
| M2M100 418M | Meta 原始发布使用 PyTorch 权重和 SentencePiece，MIT | 句级多语直译，目标语强制标记。离线 runner 的安全张量要求与 NLLB 一致。原版中文代码 `zh` 无简繁区分。[模型卡](https://huggingface.co/facebook/m2m100_418M) · [权重目录](https://huggingface.co/facebook/m2m100_418M/tree/main) |
| MADLAD-400 3B MT，`fa184c6` | Apache 2.0。T5，11.8 GB Safetensors 和 1.65 GB Q4K GGUF，发布目录包含 tokenizer.json、tokenizer_config.json、spiece.model。训练包括多语网页语料和平行数据 | CPU 本地 runner 使用目标语标记。GGUF 权重存在，T5 解码器与现有 Hy-MT 逐 token 运行路径需要独立适配与验证。FP32 权重大小已超过 8 GB，量化运行峰值需要实测。[模型卡](https://huggingface.co/google/madlad400-3b-mt) · [权重目录](https://huggingface.co/google/madlad400-3b-mt/tree/fa184c6) |
| OPUS-MT en→zh，`8d2be98` | Marian，312 MB 单个 Safetensors，SentencePiece、词表和 tokenizer 配置。固定版本 Hub 卡片标注 Apache 2.0。原始 OPUS/Tatoeba 权重来源、许可和转换文件分发需在启用前一并核对 | 用作英中单气泡对照。使用明确的简体目标标记。请求反向、跨页或其他语言时显式记为不支持。不将日中、英中和中日视为同一权重。[模型卡](https://huggingface.co/Helsinki-NLP/opus-mt-en-zh) · [转换快照](https://huggingface.co/Helsinki-NLP/opus-mt-en-zh/tree/8d2be98afc1636a4304936a2c5a013d10e02720e) |
| Tower-Plus-9B | 22 语言翻译模型，Gemma2，Safetensors，CC BY-NC-SA 4.0 | 可作为新一代研究对照。当前无合适量化、8 GB 显存或漫画质量证据。[模型卡](https://huggingface.co/Unbabel/Tower-Plus-9B) |

## 可复现的质量边界

文本测试工具 `Measure-Seq2Seq.py` 从本地目录读取 Safetensors 权重，只接受框架已有架构，禁用网络和模型仓库自定义代码。结果按样本 ID 保存状态、实际输出、耗时、设备、线程、最大输出长度、beam 数、模型及版本。单句模型对 page 与 cross-page 条目标记 `unsupported_context`，避免让拼接上下文后的整段译文和真正逐气泡输出混算。

各候选的作者成绩属于外部证据，独立实测见下文。进入默认路由前需要固定权重与运行参数，以同一份人工确认语料独立执行。单气泡、页上下文和跨页应分别评分，并分别报告未支持、截断和空输出。按含义 25、语境 20、自然 15、语气 10、术语 10、成人语境 10、拟声词 5、无遗漏和添加 5 评分，不适用的项记为缺项。盲评员需逐条辨别原文是否被省略、弱化、增补或变更人物关系，自动字符匹配无法代替该步骤。

现有 25 条合成样本用于验证语言路由和可复现性。缺少 200 条双语人工确认样本、受控真实成人语境、跨页一致性盲评和 RTX 8 GB 峰值。在取得这些证据之前不把这里的候选登记为产品可用模型。


## 本机证据与复现

Windows、Python 3.11.8、Torch 2.0.1+cu118、Transformers 4.46.0 和 SentencePiece 0.2.0。OPUS-MT 固定版本的配置和分词器成功下载并被本机加载，分词器含简体标记 >>cmn_Hans<<。312,062,468 字节权重的三个 100 秒请求分别收到 11,974,837、7,154,196 和 5,783,966 字节，均以 curl 28 结束。发布者响应头提供的权重摘要已固定到准备脚本。

用随机初始化微型 Marian 权重和同版分词器验证本地 Safetensors 加载、生成、截断分类和匿名表。25 条结果包含 1 条主动触发截断、5 条不支持上下文、19 条不支持方向，匿名表行数为 25。输出在 .qa/translation-candidates-20261126/random-marian-prefix.jsonl 和 review-smoke-prefix/，该结果只验证工具链。

从仓库目录运行以下命令。依赖为 PyTorch、Transformers、SentencePiece、Safetensors，下载与人工评审产物放在 .qa。运行准备脚本采用单个进程和独立输出目录，重启时保留 .part 续传。匿名表和映射文件各自保存在同一个新目录，向评审员提供 review.tsv。

~~~powershell
$qa = '.qa/translation-opus-en-zh'
pwsh -File tools/translation/Prepare-OpusBenchmark.ps1 -Destination $qa
python tools/translation/Measure-Seq2Seq.py --family opus-en-zh --model-id Helsinki-NLP/opus-mt-en-zh --revision 8d2be98afc1636a4304936a2c5a013d10e02720e --checkpoint $qa --output "$qa/run.jsonl"
python tools/translation/New-BlindReview.py --seed 20261126 --output "$qa/blind" "$qa/run.jsonl" .qa/translation-jmanga/run.jsonl
~~~


### JMangaTranslator-Fast v1

Windows 11，Intel Core i5-13600KF，ONNX Runtime 1.19.2 CPU Execution Provider，6 线程，FP32，贪心解码。同一 25 条语料中，7 条日中单气泡得到非空译文且未截断，5 条日中上下文题返回不支持，13 条非该语言方向返回不支持。两轮生成文本一致。两轮按条统计的 P50 为 51.95 / 67.843 ms，最近秩 P95 为 80.18 / 80.418 ms。只计已加载模型分词至输出的时间。

- 「ガタンッ！」译为「咣当！」，保留冲击声。
- 「既読だけつけて、返事はなし？」译为「只显示已读，不回消息？」，表达略生硬。
- 「避妊は相手任せにしない。」译为「避孕不交给对方。」，语义大致保留，命令语气和汉语自然度有偏差。
- ONNX 上游解码限制片假名输出，对需保留日文外号、专名或 SFX 的用例仍需独立测试。[作者后端实现](https://huggingface.co/muscgab/JMangaTranslator-Fast/blob/151cebac786f262a9144fc109ac5341a94c3da12/jmt_fast/backends.py)

结果保存于 .qa/translation-candidates-20261126/jmanga-onnx.jsonl 与 jmanga-onnx-r2.jsonl。可复现命令从仓库根目录执行，依赖 Python 3.11+、onnxruntime、numpy、tokenizers 与 sentencepiece。上游模型代码已核对，工具校验图、分词器、许可证和作者 Python 源文件的固定 SHA-256，再从已核验的源字节加载其 CPU 路径。权重许可 CC BY-NC-SA 4.0，该运行器用于非商用评测。

~~~powershell
$qa = '.qa/translation-jmanga'
hf download muscgab/JMangaTranslator-Fast --revision 151cebac786f262a9144fc109ac5341a94c3da12 --local-dir $qa LICENSE LICENSE-weights.md config.json jmt_fast/__init__.py jmt_fast/backends.py jmt_fast/common.py joint.model onnx/decoder_fp32.onnx onnx/encoder_fp32.onnx onnx/host_fp32.npz tokenizer/tokenizer.json vocab.json
python tools/translation/Measure-JManga.py --checkpoint $qa --output "$qa/run.jsonl"
~~~

本机实际从作者 [ModelScope 发布](https://modelscope.cn/models/muscgab/JMangaTranslator-Fast) 获取同一组资产，运行器的固定摘要检查均通过。

### OPUS-MT 英中

原始 HF 权重下载超时后，通过作者 [ModelScope 发布](https://modelscope.cn/models/Helsinki-NLP/opus-mt-en-zh) 获取 309,944,974 字节的 Flax 数据，使用 tools/translation/Convert-OpusFlax.py 按上游 Marian 参数布局转换，重建文件完整 SHA-256 与 [HF 固定版本](https://huggingface.co/Helsinki-NLP/opus-mt-en-zh/discussions/13/files) 完全一致。推理使用 CPU、6 线程、4 beam 与明确简体标记。

- 唯一英中样本「I owe you one, captain.」得到「我欠你一个人情 队长」。称谓和意义保留，缺少标点。
- 该次首次推理为 303.669 ms。结果在 .qa/translation-candidates-20261126/opus-verified.jsonl。仅一条，无法排名或比较速度。
- 两个候选的 JSONL 产生 50 行匿名复核模板，文件位于 .qa/translation-candidates-20261126/review-jmanga-opus/review.tsv。没有共同支持的测试语向，无模型对模型的质量比较结论。

上述复核是初步人工核读。尚无双盲比较、真实成人语料复核或 8 GB GPU 数据。


## 当前运行状态

| 模型 | 状态与使用边界 |
| --- | --- |
| JMangaTranslator-Fast v1 | 已在同一语料日中子集实际运行。只支持单气泡，权重为非商用许可，属于独立评测。 |
| NanoSakura-2.2-0.2B | 固定版本为 5f09a52217890f8ceff884e69e4deec24e91e2fb，单个 752,415,808 字节 FP32 Safetensors，公开 SHA-256 为 0e6324344ad76994b607f45b2dc2d352bf0cb8d1f268b0fb6faed8b127d1764a。MIT。已下载配置及作者的自定义 Python 推理类进行源审查，分词器已完成续传，真实权重下载后未达完整校验，部分文件已留存。已用随机微型同结构在 CPU 执行生成路径验证，模型真实质量仍未验证。作者使用 Mamba2 递归缓存与交叉注意力，输入句尾须手动加 <eos>。标准 Transformers 自动识别、现有 llama.cpp 与 C# ONNX 路径均无法直接读取此格式。可用固定源码类手动装载 Safetensors 或显式导出编码器、单步解码图和缓存状态，需独立验证。 |
| Qwen3 通用与 enet45 Qwen3 8B 微调 | 官方通用 4B 有 llama.cpp 路径，未执行该通用模型对照。同作者提供 Apache 2.0 的 8B Q4 K M GGUF，固定版本 b46f43f522d343e1d2a6e0c4dc5a75100b99c5cd。该仓库访问需授权，8B 仍是待评测。[部署说明](MODEL_DEPLOYMENT.md) |
| M2M100 418M | 已用实际权重在同一语料完成 17 项。与 JMangaTranslator-Fast 在 7 个日中单气泡交集上可比较。 |
| NLLB-200 600M | 已使用官方镜像固定权重在同一语料执行 20 项，日中拟声词和部分语义保真严重退化。 |
| MADLAD-400 3B | Google 发布了 Safetensors 和 Candle 量化 T5 实现及示例，本机用 Q4K 数据恢复到 BF16 后执行同一语料 17 项，发现部分严重保真失败。项目 llama.cpp 未能加载 Google Q4K，暂不纳入应用。 |
| OPUS-MT 英中 | 已在同一语料执行唯一英中样本，可与 M2M100 及 NLLB 作同输入参考。 |
| Tower-Plus-9B | 仅调查，当前无同一输入的本地权重推理证据。 |

### M2M100 418M

作者 [ModelScope 发布](https://www.modelscope.cn/models/facebook/m2m100_418M) 的 PyTorch 文件 SHA-256 为 d907ea45e4e4b9db163382a6674f6218b3c59566fe06d77f4055c208b4e87ed1。只解析固定文件内张量数据，保留 FP32，转换为 Safetensors 后 SHA-256 为 c061a21eec15453944783ceb4a13a216477dd4765c98fac10811818adf994968。CPU 6 线程、4 beam，同一语料中 17 项输出非空，包括 7 项日中和 10 个多语言方向。5 个上下文问题及 3 项需精确繁中字形的方向按 runner 能力规则跳过。

- `ガタンッ！` →「被打了!」，把动作音变为一项动作判断。
- `ドキッ` →「触摸」，拟声词失真。
- `避妊は相手任せにしない。` →「避孕不允许。」，反转了语义。
- `I owe you one, captain.` →「我欠你一个,老板。」，省略人情并改变称谓。
- `Garde la porte ouverte, s’il te plaît.` →「Halten Sie die Tür offen, bitte.」，主要意思保留，语域从熟人转为敬语。

输出在 .qa/translation-candidates-20261126/m2m100-verified.jsonl。正式准备可通过 [Hugging Face 自动转换版](https://huggingface.co/facebook/m2m100_418M/commits/refs%2Fpr%2F19) 的 Safetensors 和相匹配 tokenizer 文件复现模型家族级对照；应在结果中独立标明转换快照。


### NLLB-200 distilled 600M

作者 [ModelScope 发布](https://www.modelscope.cn/models/facebook/nllb-200-distilled-600M) 的 PyTorch 文件 SHA-256 为 c266c2cfd19758b6d09c1fc31ecdf1e485509035f6b51dfe84f1ada83eefcc42。解包固定文件内张量数据并保留 FP32 后，Safetensors SHA-256 为 c26fea1b0788456e3e0d6dfd0165cacec798a0b6d957c9045a234826994dec41。CPU 6 线程、4 beam，20 项输出非空且未触发截断。覆盖 7 项与 JManga 相同的日中单气泡和 13 个多语言测试方向。5 个上下文问题按 runner 能力跳过。P50 为 907.798 ms，最近秩 P95 为 1308.653 ms，最慢一条 10510.34 ms。异常慢的样本是拟声词变为 62 字符标点序列。

- 「ガタンッ！」输出逗号串和感叹号，拟声词失真。
- 「ドキッ」→「医生.」，把心跳拟声译为职业。
- 「お酒は二十歳になってから。」→「我已经20岁了.」，漏掉饮酒限制并加入第一人称。
- 「駅の向こうに小さな本屋がある。」→「這裡有一間小書店.」，使用繁体字，但丢失火车站对面的空间关系。
- 「避妊は相手任せにしない。」→「避孕措施是我不愿意承担的.」，改变语义主体和责任表达。

输出在 .qa/translation-candidates-20261126/nllb-verified.jsonl。公开的 [Hugging Face 自动转换版](https://huggingface.co/facebook/nllb-200-distilled-600M/commits/refs%2Fpr%2F45) 提供 Safetensors 的可选准备路径。模型卡的研究用途和非商用许可仍适用。

这四个 CPU 模型使用同一语料的匿名评审表共 100 行，文件在 .qa/translation-candidates-20261126/review-four-cpu/review.tsv。JManga、M2M100、NLLB 的日中交集为 7 条，OPUS、M2M100、NLLB 的英中交集为 1 条。上列项目是非盲初步核读，尚未取得独立双语评分。M2M100 的 17 项已加载推理记录 P50 673.519 ms，最近秩 P95 1137.027 ms。精确繁体字段上的「不支持」是 M2M100 runner 以避免静默字形切换为目的采取的路由约束。


### MADLAD-400 3B Q4K

下载了 Google 在 [官方镜像](https://www.modelscope.cn/models/google/madlad400-3b-mt) 发布的 1,654,597,280 字节 Q4K，SHA-256 为 ea6e5531a3e95213c7f0635988d119e078a655c09306e47851e15d4c0c3f9c37。项目同版 llama.cpp b12650 拒绝加载，报张量名长度 68 超过 64 字节限制。记录在 .qa/translation-candidates-20261126/madlad-gguf/llama-cli-probe.stderr。该失败发生于模型加载阶段，尚未进入解码。

独立 CPU 对照读取同一 GGUF 权重数据，分块恢复为 BF16 后交给 Transformers T5 推理。Q4K → BF16 保留已量化的值及后续 BF16 舍入，因此不是 FP32 原权重结果，也不是 Candle 的原生量化执行速度。首批输出如「ガタンッ！」→「加顿！」和「ドキッ」→「多基」，这两项拟声词仍有明显问题。

依赖 gguf 0.17.1，其他包版本同本节的 Torch/Transformers。Google 的 model-q4k.gguf、config.json、generation_config.json 及其同版分词器需在同一个本机目录。参见 [Google 固定版本资产](https://huggingface.co/google/madlad400-3b-mt/tree/fa184c6)。

~~~powershell
python tools/translation/Measure-Seq2Seq.py --family madlad --madlad-q4k --model-id google/madlad400-3b-mt --revision ea6e5531a3e95213c7f0635988d119e078a655c09306e47851e15d4c0c3f9c37 --checkpoint .qa/madlad-gguf --num-beams 1 --max-new-tokens 64 --output .qa/madlad-gguf/run.jsonl
~~~


本轮同一 25 条语料得到 17 条可解析译文，包括日中 7 条和多语 10 个方向。5 条页或跨页上下文与 3 个需精确繁体字形的方向跳过。CPU 6 线程、Q4K 恢复为 BF16、贪心解码下，已加载的 P50 为 13,613.633 ms，最近秩 P95 为 29,828.046 ms。

- 「避妊は相手任せにしない。」误生成「避孕药不得与其它药物一起使用。」。原文谈双方责任，译文额外生成了药物合用说明。
- 「お酒は二十歳になってから。」得到「20歲時開始喝酒。」。请求为简中，输出混用繁体字，并弱化年龄限制。
- 法语「Garde la porte ouverte, s’il te plaît.」得到「Bitte halte die Tür offen.」，保留原意和同辈语域。
- 西语「El puente no aguanta mucho más.」得到「橋はあまり使われない。」，把承载危险误译为使用频率。

结果保存于 .qa/translation-candidates-20261126/madlad-verified.jsonl。交付脚本另行复跑 j07，重新观察到相同措辞。五模型的统一匿名表为 .qa/translation-candidates-20261126/review-five-cpu/review.tsv，共 125 行及对应的单独映射文件，目前未取得独立评审员评分。

### NanoSakura ONNX 技术探针

作者固定自定义源码已审查，源码使用 Torch 张量运算、Transformers 生成接口、交叉注意力和显式卷积及 SSM 状态。测试以固定随机种子初始化缩小的同结构模型，PyTorch 2.0.1 的 ONNX 导出器在原始注意力算子处报错。将该注意力换成等价的矩阵乘、掩膜和 softmax 后，导出编码器及解码单步图到 opset 17。ORT 1.19.2 CPU 对两种源长度、各两次解码缓存更新所得 logits 和状态与原 PyTorch 一致，容差 rtol 1e-5、atol 1e-6。此结果验证显式缓存接口的技术路径，完整权重导出、C# 接线、输出质量与 8 GB 环境仍未测。

~~~powershell
python tools/translation/Probe-NanoSakuraOnnx.py .qa/nanosakura/modeling_mamba2_s2s.py .qa/nanosakura-onnx-new
~~~

命令第一项为固定版本中经过审核的作者源码，脚本验证其 SHA-256。该探针只需要作者源码及本地 Python 依赖 PyTorch、Transformers、ONNX 和 ONNX Runtime。


完整权重的本地评测入口接受上述固定版本下由用户下载的 model.safetensors、config.json、tokenizer.json 和 modeling_mamba2_s2s.py。脚本校验四件摘要，只用已审阅的本地类与安全张量。Transformers 4.46.0 不识别仓库配置里的 TokenizersBackend，评测入口直接用 tokenizer.json 构造 PreTrainedTokenizerFast，并按模型要求添加句尾标记。真实分词器与随机微型 Safetensors 的本地读入和生成已通过。正式权重从网络取得 21,360,640 字节后停止，保留可续传 .part。正式日中语料尚未验证。

~~~powershell
$qa = '.qa/translation-nanosakura'
hf download telecomadm1145/NanoSakura-2.2-0.2B --revision 5f09a52217890f8ceff884e69e4deec24e91e2fb --local-dir $qa config.json tokenizer.json modeling_mamba2_s2s.py model.safetensors
python tools/translation/Measure-Seq2Seq.py --family nano-sakura --model-id telecomadm1145/NanoSakura-2.2-0.2B --revision 5f09a52217890f8ceff884e69e4deec24e91e2fb --checkpoint $qa --num-beams 1 --output "$qa/run.jsonl"
~~~
