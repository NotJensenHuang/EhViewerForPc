# 漫画翻译性能与显存

测试环境为 Windows 11 x64、RTX 5070 Ti，物理显存 16,303 MiB。旧版实测为单独的历史基线，不能用于宣称本版候选能在 RTX 8 GB 下稳定运行。[历史 DirectML 会话复用实测](漫画翻译DirectML会话复用验证-2026-11-25.md)

本次改变了上下文长度与参考文字，可能增加模型预填充开销。仍限制为单条页任务、每区输出最多 256 token、上下文 2048 token，模型和 OCR 会话在同一进程复用，空闲后退出。没有 RTX 8 GB 独立机器或等效限制，权重文件尺寸也不能代表显存。未采集两份新候选的权重/KV/Vulkan 工作区、DirectML 常驻会话及驱动分项。

后续使用相同标注页、同一 OCR 和固定推理参数，至少测冷启动、稳定单/多页、翻页取消、连续批处理与显存不足后恢复。记录端到端 P50/P95、峰值/空闲显存、GPU/CPU 利用率及每页模型调用数。正式验收以 RTX 8 GB 无持续 OOM、无长批次资源增长、可取消并可单页重试为准。

## JMangaTranslator-Fast CPU 合成页验证

运行环境为 Windows 11 x64、Intel Core i5-13600KF、随包 ONNX Runtime 1.23。最终 Windows x64 NativeAOT 工作者加载实际联网准备的 JMangaTranslator-Fast 权重。同一个 1000 × 500 合成页含两个文字区域，先发一个取消请求，再在同一进程内连续提交两次。首次成功页的模型加载为 1,754 ms，随后复用模型。两次 OCR 为 375 和 270 ms，两区翻译分别为 98 和 96 ms。原始日志见 `.qa/jmanga-reader-20261126/persistent-r3.log`。样本量只有两页，不能外推到整卷或不同 CPU。这条路径使用 CPU OCR 和 CPU ONNX，无需 Vulkan 推理，未采集其长期主存峰值。
