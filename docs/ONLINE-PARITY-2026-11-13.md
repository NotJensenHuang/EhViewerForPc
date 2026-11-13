# 在线导航、搜索和原版书签兼容补齐

日期：2026-11-13。此记录覆盖用户指出的订阅、排行榜、搜索表单和书签栏缺项，不将本次修改解释为整个 Android 项目的全部功能均已复现。

## 依据

对照 [Ehviewer_CN_SXJ](https://github.com/xiaojieonly/Ehviewer_CN_SXJ) 提交 `d129f202b219d992ad4069dbdc70bbbd3eed5c5b`，并核对用户提供的两张截图。上游源码仅作为功能和格式参考。

- [ListUrlBuilder.java](https://github.com/xiaojieonly/Ehviewer_CN_SXJ/blob/d129f202b219d992ad4069dbdc70bbbd3eed5c5b/app/src/main/java/com/hippo/ehviewer/client/data/ListUrlBuilder.java)：模式、URL、高级参数和书签恢复。
- [AdvanceSearchTable.java](https://github.com/xiaojieonly/Ehviewer_CN_SXJ/blob/d129f202b219d992ad4069dbdc70bbbd3eed5c5b/app/src/main/java/com/hippo/ehviewer/widget/AdvanceSearchTable.java)：11 位选项标记。
- [QuickSearch.java](https://github.com/xiaojieonly/Ehviewer_CN_SXJ/blob/d129f202b219d992ad4069dbdc70bbbd3eed5c5b/app/src/main/java/com/hippo/ehviewer/dao/QuickSearch.java)、[QuickSearchDao.java](https://github.com/xiaojieonly/Ehviewer_CN_SXJ/blob/d129f202b219d992ad4069dbdc70bbbd3eed5c5b/app/src/main/java/com/hippo/ehviewer/dao/QuickSearchDao.java)：原版书签 JSON 和 SQLite 列定义。
- [EhDB.java](https://github.com/xiaojieonly/Ehviewer_CN_SXJ/blob/d129f202b219d992ad4069dbdc70bbbd3eed5c5b/app/src/main/java/com/hippo/ehviewer/EhDB.java)、[DaoMaster.java](https://github.com/xiaojieonly/Ehviewer_CN_SXJ/blob/d129f202b219d992ad4069dbdc70bbbd3eed5c5b/app/src/main/java/com/hippo/ehviewer/dao/DaoMaster.java)：原版“导出数据”是 schema 8 的 SQLite 数据库；“导入数据”读取全部 DAO 表。
- [TopListParser.java](https://github.com/xiaojieonly/Ehviewer_CN_SXJ/blob/d129f202b219d992ad4069dbdc70bbbd3eed5c5b/app/src/main/java/com/hippo/ehviewer/client/parser/TopListParser.java)、[EhTopListAdapter.java](https://github.com/xiaojieonly/Ehviewer_CN_SXJ/blob/d129f202b219d992ad4069dbdc70bbbd3eed5c5b/app/src/main/java/com/hippo/ehviewer/ui/scene/topList/EhTopListAdapter.java)：七类总览、四个时间范围、画廊列表分页。
- [EhEngine.java](https://github.com/xiaojieonly/Ehviewer_CN_SXJ/blob/d129f202b219d992ad4069dbdc70bbbd3eed5c5b/app/src/main/java/com/hippo/ehviewer/client/EhEngine.java)、[EhUrl.java](https://github.com/xiaojieonly/Ehviewer_CN_SXJ/blob/d129f202b219d992ad4069dbdc70bbbd3eed5c5b/app/src/main/java/com/hippo/ehviewer/client/EhUrl.java)：在线图片搜索的 multipart 字段和两个上传地址。

## 当前行为

左侧导航新增独立的“订阅”“排行榜”“书签栏”。在线浏览保留首页、热门和常驻搜索框。所有页面通过已有 FluentAvalonia Frame 缓存和返回机制接入。

- 订阅页使用 `/watched`，搜索模式默认订阅；普通搜索也能切换到一般、订阅、指定上传者和指定标签模式。
- 排行榜总览展示画廊、上传者、标签贡献、Hentai@Home、EHTracker、清理贡献、评分与评论。每类有昨日、本月、今年、全部四个时间范围。画廊总览的时间按钮打开完整画廊榜。按上游行为固定请求 E-Hentai 的排行榜，分页参数使用 `p`。
- 搜索表单具备十类分类和截图中的所有高级选项。关闭高级选项时不发送高级筛选。页数范围验证失败时留在表单并提示。
- “在线图片搜索”可选择图片、相似度扫描、仅搜索封面、包含被删除画廊；选择图片在后台规范化，只有点击“上传并搜索”才向当前站点发送。此功能与本地库的“以图搜图”各有入口。
- “标签检索”在已下载的标签翻译库中按英文或中文匹配，可显式更新标签库，并向搜索框插入规范的命名空间标签。
- 分页跟随服务器提供的 next URL，每页替换结果；上一页后再次下一页使用正确位置，不追加重复行。请求取消和过时响应不会覆盖较新的列表。
- 书签栏支持保存当前已经执行的列表状态、打开、名称与关键词编辑、完整搜索条件编辑、上移、下移和删除。完整条件编辑通过原搜索表单保存回原书签。

### 高级参数对照

| 界面条件 | 参数 | QuickSearch 位值 |
| --- | --- | --- |
| 标题 / 标签 / 描述 | `f_sname` / `f_stags` / `f_sdesc` | 1 / 2 / 4 |
| 搜索种子文件名 | `f_storr` | 8 |
| 仅显示有种子的画廊 | `f_sto` | 16 |
| 低愿力 / 差评标签 | `f_sdt1` / `f_sdt2` | 32 / 64 |
| 只显示被删除的画廊 | `f_sh` | 128 |
| 禁用语言 / 上传者 / 标签排除项 | `f_sfl` / `f_sfu` / `f_sft` | 256 / 512 / 1024 |
| 最低评分 | `f_sr`、`f_srdd` | 独立字段 `minRating` |
| 页数范围 | `f_sp`、`f_spf`、`f_spt` | 独立字段 `pageFrom` / `pageTo` |

修复了原桌面实现把“仅含种子”映射为 `f_storr`、把删除画廊映射为 `f_sto` 的错误。分类继续使用 `f_cats` 的排除掩码。

## 书签文件兼容

“书签栏 → 导入书签”可直接读取 Android 设置中“导出数据”生成的 `.db`；无需先转换 JSON。输入数据库以只读方式打开，读取 `QUICK_SEARCH`，不改写原文件，不导入下载、历史、收藏或账号。旧版没有 `PAGE_FROM` / `PAGE_TO` 列的备份按未设置页数处理。

“导出书签”默认生成 schema 8 `.db`，含与上游列定义一致的十张表：只有 `QUICK_SEARCH` 写入书签，其余表为空，使原版完整导入流程能够读取。数据库结构保存在 `src/EhViewerReader.Core/Online/EhBookmarkSchema.sql`，列名和类型依据上述上游 greenDAO 定义生成，不包含用户数据。

原版字段 `NAME`、`MODE`、`CATEGORY`、`KEYWORD`、`ADVANCE_SEARCH`、`MIN_RATING`、`PAGE_FROM`、`PAGE_TO`、`TIME` 均有映射。分类 `-1` 表示全部，选项 `-1` 表示高级选项关闭。11 个选项位逐一保存；中文、空格、引号、`$` 和标签语法按原字符串保留。导出按当前列表顺序写入递增 `TIME`，以兼容 Android 按时间排序的方式。

JSON 支持原版 `QuickSearch.toJson()` 字段组成的数组、`{"quick_search":[...]}`，以及 Wi-Fi 数据包的 `{"data":{"quick_search":[...]}}` 包装。JSON 导出采用 `quick_search` 包装，复用原版记录格式；原版标准“导入数据”入口仍应选择 `.db`。

同名导入项跳过，既有书签保留；文件全部解析成功后才合并。未知模式、错误字段和超限数据报告失败，避免部分导入。桌面本地导出先写临时文件，成功后原子替换目标，失败保留既有备份。

桌面设置保留对旧 `savedSearches` 的兼容，并新增完整 request，JSON 使用现有源生成序列化。上游 QuickSearch 自身没有排行榜的 `follow` / 时间范围字段；桌面在导出时额外写入 `READER_TOPLIST` / JSON `toplistIndex` 以支持自身往返，原版忽略该扩展，其既有恢复限制仍存在。图片搜索和账号凭据不属于书签格式。

## 验证与证据

构建和测试使用 `ProcessStartInfo`、`UseShellExecute=false`、`CreateNoWindow=true` 和较低进程优先级。界面测试采用 Avalonia Headless 的内存窗口，未打开真实桌面窗口、触碰系统剪贴板或抢占焦点。

- 核心回归：143 项通过，含 URL 对照、11 位高级选项、原版 SQLite 只读导入、完整十表导出、JSON / 设置往返、重复合并、损坏输入、失败导出保留旧备份、上传 host 的 cookie 和 multipart 协议、七类排行榜解析。
- 界面回归：22 项通过。覆盖正式导航、完整书签条件编辑与恢复、导入导出、排序、中文标签、无效输入、服务器 URL 翻页返回、宽窄窗口和明暗主题。原本的本地以图搜图回归一并运行。
- NativeAOT：发布成功，无窗口书签探针 6 项全部通过，实际运行了原版数据库导入、十表数据库导出、数据库 / JSON 往返、设置源生成序列化和同名合并。

证据在忽略目录 `.qa/online-parity/`：`tests/core.trx`、`tests/ui.trx`、`tests/rankings.trx`、`core-tests.*.log`、`ui-tests.*.log`、`ranking-ui-test.*.log`、`ui/*.png`、`build-aot.*.log`、`native-probe.json`。原版源代码快照保留于 `.qa/ehviewer-upstream/`。NativeAOT 产物在 `artifacts/ehviewer-online-parity-aot/`。

可在无前台窗口模式运行发布后的书签探针：

```powershell
# 为 root 使用一个全新的空目录；不使用真实应用数据。
EhViewerReader.exe --bookmark-probe-root <empty-directory> --out <report.json>
```

本机没有既有图库和已登录账号；本轮使用独立备份夹具、模拟站点响应和本地回环分页服务验证，未上传用户图片。真实站点的登录态订阅、线上图片检索结果、Android 真机导入交互仍需使用者环境确认，不能将协议/结构测试称为线上验证。原有第三方 NativeAOT 警告不在本次修复范围，发布日志保留原始信息。
