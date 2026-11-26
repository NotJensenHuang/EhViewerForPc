# 翻译语言方向覆盖

以下是候选模型的接口能力与本轮取样范围。直接表示模型一次生成目标语言。候选执行器没有配置中间语，也不会在某方向不可用时改用英语中转。源语与目标语取自测试样本，未测源语自动识别。

| 候选接口 | 日、简中、英、韩、法、德、西之间的直接方向 | 繁中 |
| --- | --- | --- |
| Qwen3 系列通用 LLM | 可提示模型直接翻译，各方向仍需单测 | 可指定字形，尚无合格率 |
| NLLB-200 | 用源语代码及目标语强制首 token | 独立代码 `zho_Hant`，简中用 `zho_Hans` |
| M2M100 | 用源语代码及目标语强制首 token | `zh` 未区分字形，精确繁中方向跳过 |
| MADLAD-400 | 统一模型用 `<2xx>` 目标标记，源文本直接输入 | 本工具只启用 `<2zh>`，精确繁中方向跳过 |
| OPUS-MT | 逐权重核对有向语言对，本工具只启用 `en→zh-CN` 的检查 | 该权重有普通话简繁目标标记，本工具仅启用简体 |
| Hy-MT2 漫画微调 / enet45 日中微调 / JMangaTranslator-Fast / NanoSakura | `ja→zh-CN` 候选 | 暂无字形和反向能力实测 |

`tools/translation/quality-cases.tsv` 的多语集包括 13 个不同方向，有 `fr→de`、`de→es`、`es→fr`、`zh-TW→ko` 等非英语中转场景。每方向目前一条。另有 12 条日中样本，包含拟声词、口语、成年语境、称谓及省略句。它们是本次编写的合成文本和参考译文，未采用第三方双语人工复核，不能推导漫画全集准确率。

语言列表与使用接口可查阅 [Meta NLLB 模型卡](https://huggingface.co/facebook/nllb-200-distilled-600M)、[Meta M2M100 模型卡](https://huggingface.co/facebook/m2m100_418M)、[Google MADLAD 模型卡](https://huggingface.co/google/madlad400-3b-mt)和 [Helsinki-NLP OPUS 模型卡](https://huggingface.co/Helsinki-NLP/opus-mt-en-zh)。

阅读器的日语 → 简体中文模型包括 JMangaTranslator-Fast CPU 单气泡模式，以及 Hy-MT2 漫画微调和 Qwen2.5 7B 字幕微调两个 GPU 试用选项。具备读取所选模型资产与提示词的能力仍需和质量验收分别判断。独立基准的其他语言方向暂不进入阅读器。

本机单句实测覆盖 NLLB 的全部 13 个多语样本方向，M2M100 和 MADLAD 各 10 个方向，OPUS-MT 的英中 1 个方向。上述模型成功产出字符串只证明语言令牌和模型接口可以工作。所得结果包括字形符合却丢失信息、称谓改变和拟声词完全失真。[翻译候选评估](TRANSLATION_MODEL_EVALUATION.md)
