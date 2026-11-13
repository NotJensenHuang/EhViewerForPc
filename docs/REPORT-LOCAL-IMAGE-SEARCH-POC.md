# 本地图片搜索 C# 迁移验证报告

2026-11-13。本报告覆盖合成图片与正式应用接线，**不是E07真实漫画质量报告**。用户确认本机没有图库，真实Recall/P95与245K页规模尚未验证。

## 固定来源和环境

- 上游 `lolishinshi/imsearch`，提交 `2bf2546822ca2b9b1522d868eb9f21ba441f20d2`，版本3.0.2；哈希与兼容差异见 `tools/imsearch/upstream.lock.json` 和 NOTICE。
- C# / .NET11.0.100；Windows x64；逻辑处理器16。CIM读取受限，未声称取得完整CPU型号、物理内存或磁盘参数。
- 固定提取profile：`a55fe0824f16e908d101fe83c33d9e974da70df746a1531cfd19229575eb34c9`；ORB500、scale1.2、8层、FAST20/7、area、有方向、Wilson、k3、distance64、nprobe16。
- 后端没有Rust、Cargo、Docker、WSL、OpenCV或Faiss运行依赖。原生运行库为现有SkiaSharp与SQLite。

## 执行结果

| 验证 | 实际结果 |
| --- | --- |
| 既有功能回归 | 129/129通过，exit0 |
| C#后端测试 | 31/31通过，exit0 |
| 正式UI和导航Headless测试 | 12/12通过，exit0 |
| 独立Host托管探针 | 31项进程/协议检查通过，exit0 |
| 独立Host NativeAOT探针 | 31项进程/协议检查通过，exit0 |
| 主程序NativeAOT服务探针 | 9项通过，exit0；包括增量SQLite快照、具体副本映射与显式页号模型 |

合成集为seed20261112生成的64张320×448线条/框/圆PNG，实际导入64个唯一资产、32402个描述符。8条已知来源裁剪查询全部进入Top10；原页查询排名第一。导入重放、分段暂停恢复、重复build、clean后重载、空白查询和源文件长度/mtime不变等20项内部检查通过。

最后一次后端NativeAOT探针观测的8条裁剪耗时分位数约61.6ms，进程峰值working-set约42MiB。样本极小，未按E07做固定暖机、两轮漫画查询、负样本或冷盘测试；这个数值仅是链路观察，不构成召回或性能承诺。

正式应用使用真实后端和SQLite清单验证了：跨源重复页映射、禁用源/删除/替换过滤、未变化原图被独占锁定时仍可复用全部特征、独立代际保留、失败不覆盖旧索引和损坏指针读取上一代。Headless真实Frame测试覆盖单页、纵向连续和横向RTL，从命中页打开、复用reader、切换同作品副本与返回保留结果。

全部构建/探针在后台运行；UI使用[Avalonia Headless](https://docs.avaloniaui.net/docs/testing/setting-up-the-headless-platform)的内存窗口、鼠标和剪贴板，不影响PC前台。深浅主题1200×800与540×700截图，以及真实结果卡片截图已打开检查。

## 证据与复现

- 测试：`.qa/image-search/tests/backend.trx`、`ui.trx`、`core-final.trx`。
- Host：`.qa/image-search/verification-aot-delivery/verification.json` 与 `synthetic.json`；托管版目录为 `verification-managed-verified`。
- 主程序：`.qa/image-search/app-aot-delivery-build.log`、`app-native-delivery-report.json`。
- 截图：`.qa/image-search/ui/`。均为合成图，不含真实图库内容。
- 后台构建、验证命令和使用入口见 `tools/imsearch/README.md`；范围与未完成项见 `IMPLEMENTATION-LOCAL-IMAGE-SEARCH-STATUS.md`。

主程序AOT发出的第三方裁剪/动态类型/Win32 DComposition互操作警告保留在日志中，未压制。未启动真实Win32窗口，因此不能以无窗口探针通过来宣称该原生渲染路径已经验证。当前结论是：C#后端及正式UI/导航合成链路通过；真实漫画质量、全库吞吐和完整故障矩阵待验。
