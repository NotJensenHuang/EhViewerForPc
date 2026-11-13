# 全页面布局、对齐与 Fluent 修复：实施与验收记录

日期：2026-11-12。对应计划：[PLAN-UI-LAYOUT-FLUENT-2026-11-12.md](PLAN-UI-LAYOUT-FLUENT-2026-11-12.md)。基线提交：`9805b1e`（工作区未提交改动即本轮成果）。

本文件只记录**本次实际修改**与**实际观察到的结果**；未执行或无法在本环境执行的项在 §6 单列，不用“编译通过”代替布局验收。

## 1. 结论摘要

| 计划项 | 结果 | 依据 |
| --- | --- | --- |
| §2 独立系统标题栏 | 已修改，已取证 | `client=1240×760`、`frame=1254.7×797.3`，非客户区 37.3 DIP 说明标题栏由系统绘制；`decor=Full`、`extend=False` |
| §3 全局布局规则 | 已修改 | 统一 24 DIP 内容起点、40 DIP 底部栏、共享 `.statusbar` / `.page-header` / `.settings-row` 样式 |
| §4 外壳与底部对齐 | 已修改，已测量 | 账户栏与本地库状态栏底边 **delta = 0.0 DIP**（1240 展开、800 紧凑两种状态）；右下“设置”入口及其调用链已删除 |
| §5 设置页 Fluent 设置行 | 已重写 | 七类分类栏 + 行式布局；逐类截图见 `.qa/layout-fluent-audit/settings-*-*.png` |
| §6 阅读页收敛为单一页头 | 已重写 | 通用“阅读”标题不再出现；页头 = 作品标题 + `FACommandBar`（主命令 + 溢出）；底部仅一条进度栏 |
| §7 其余页面逐页 | 已修改 | 12 个 Page 全部过审；118 张截图中 **0 处横向越界** |
| 既有回归 | 通过 | 单元测试 129/129；`--ui-smoke`、`--navigation-regression`、`--reader-regression` 全部 PASS |

## 2. 关键问题的根因与处理

### 2.1 账户区底部留白 14.7 DIP

先测量再改。视觉树显示 FluentAvalonia 的 PaneFooter 之下有三段固定内边距：

```text
ContentControl#FooterContentBorder  Margin=0,0,0,4
Grid#ItemsContainerGrid             Margin=0,0,0,8
Grid#PaneContentGrid                Margin=-1,3,-1,3
```

合计约 15 DIP，且这些值来自**编译进模板的局部值**（实测用 `FANavigationView.Styles` 覆盖 `#FooterContentBorder` / `#ItemsContainerGrid` 的 Margin **无效**，delta 仍为 14.7）。按计划“不要用负 Margin 补偿”的要求，改为结构性处理：

- 账户区不再放进 `PaneFooter`，改为主窗口根 Grid 内的 `AccountBar`：`VerticalAlignment=Bottom`、高度 40、宽度跟随 `OpenPaneLength`/`CompactPaneLength`、背景透明。
- 展开时为“账户摘要 + 登录/退出”单行；紧凑栏（48 DIP）为图标按钮 + 原生 Flyout（同一组命令）；最小导航模式下画布外不绘制，汉堡菜单展开后仍是完整行。
- 副作用修复：原先 `IsPaneOpen` 变化即写入 `SidebarCollapsed`，而控件在显示模式切换时也会翻转该属性，导致侧栏被意外持久化折叠。现改为仅在宽窗关闭窗口时保存。

### 2.2 阅读页三层页头

作品标题与阅读命令上移到 `FANavigationView.Header` 的专用 `ReaderHeader`（`AdaptiveGrid`，断点 700）：宽窗单行；窄窗标题行 + 紧凑命令行，命令在 <920 时收起文字只留图标，其余进入溢出菜单。`ReaderPage` 只剩画布 + 一条底部进度栏；页码只出现一次。

沉浸模式：`Navigation.Header = null` + 隐藏导航，页面右上保留“退出沉浸（Esc）”浮层，底部进度保留。Esc 顺序为 沉浸 → 全屏 → 返回。全屏退出恢复进入前的 Normal/Maximized（原实现固定回 Normal）。

### 2.3 全局 `WrapPanel > Button` 边距

删除全局规则（它会与局部 Margin 叠加并造成对齐漂移），改为显式 `WrapPanel.actions` 容器样式（按钮 0,0,8,8；复选框 0,0,16,8），所有依赖该规则的页面都显式加了 `Classes="actions"`，没有留下零间距。

## 3. 改动文件

| 文件 | 内容 |
| --- | --- |
| `Views/MainWindow.axaml` / `.cs` | 标题栏固定；`ReaderHeader`（FACommandBar）；`AccountBar` 与紧凑账户按钮；头部切换与账户区状态；`SidebarCollapsed` 改到关闭时保存 |
| `Styles/Theme.axaml` | 布局令牌；`Border.statusbar`、`Border.page-header`、`Border.settings-row`、`.group-title`、`ListBox.categories`；删除全局 WrapPanel 边距 |
| `Controls/SettingsRow.axaml`(+`.cs`) | 新建设置行组件：左标题/描述 + 右控件，行宽 <560 时操作区落到标签下方 |
| `Controls/IndexMatchConverter.cs` | 新建设置分类面板显隐转换器 |
| `Controls/AdaptiveGrid.cs` | 新增 `Breakpoint` 属性（默认 740，保持旧行为） |
| `Views/SettingsPage.axaml` | 重写为分类栏 + 7 个设置面板；保存为唯一页级主操作；滑块行“标签 / 弹性轨道 / 数值+单位” |
| `ViewModels/SettingsViewModel.cs` | 新增 `Categories` 与 `SelectedCategoryIndex`（仅 UI 状态，不参与保存事务） |
| `Views/ReaderPage.axaml` / `.cs` | 移除页内标题与按钮行；模式/方向/背景/重试/沉浸/全屏改为页头调用的方法；沉浸浮层；全屏恢复 |
| `Views/LibraryPage.axaml` / `.cs` | 工具区两行收敛；卡片标题行自适应；状态栏共享基线；删除“设置”按钮/事件/处理器 |
| `ViewModels/MainViewModel.cs` | 删除随之失效的 `OpenSettingsRequested` / `OpenSettingsCommand`（无其他调用者） |
| `Views/OnlineFeedPage.axaml` | 去除空 StackPanel；收藏搜索弹性；结果摘要与保存/清除按组换行；状态栏统一 |
| `Views/HistoryPage.axaml` | 操作行可换行；条目在 <480 时动作移到正文下方 |
| `Views/DownloadsPage.axaml` | 三个子页统一内边距；任务/归档卡片操作组可换行；状态栏统一 |
| `Views/ArchivePage.axaml` | 删除与壳标题重复的“归档下载”；格式下拉弹性；URL 行弹性省略 |
| `Views/TorrentPage.axaml` | 删除重复“种子下载”；页头/状态栏统一 |
| `Views/VersionsPage.axaml` | 说明文字移入滚动区；底部“关闭”统一 |
| `Views/WebLoginPage.axaml` | 顶部关闭与底部取消合并为单一入口；状态文案不再重复 |
| `Views/GalleryDetailsPage.axaml` | 去掉路径 `MaxWidth=440`，改由列约束 + 省略 + Tooltip |
| `Views/OnlineGalleryPage.axaml` | 评分三列改 `AdaptiveGrid`；评论提交行可换行 |
| `Services/UiAuditService.cs` | 设置分类改用 `SettingsCategories`；阅读沉浸改调用页面方法；新增底部对齐/窗口装饰测量与 800×760、540×420 两档补拍 |
| `Services/UiSmokeService.cs`、`NavigationRegressionService.cs` | 同步替换 `SettingsTabs` 与 `ImmersiveButton` 的取数方式 |
| `App.axaml` | 注册 `IndexMatchConverter` |

未改动：下载引擎、解码器、缓存算法、数据格式、导航架构；未升级 Avalonia/FluentAvalonia；未引入新 UI 框架。

## 4. 实测数据

环境：3840×2160 @150%（`RenderScaling=1.5`），因此 §4 所有截图同时是 150% DPI 证据。

### 4.1 底部基线（`.qa/layout-fluent-audit/audit.json`）

| 场景 | accountBottom | statusBottom | delta | 侧栏 |
| --- | --- | --- | --- | --- |
| 1240×760 深色 | 760.0 | 760.0 | **0.0** | 展开 |
| 1240×760 浅色 | 760.0 | 760.0 | **0.0** | 展开 |
| 800×760 紧凑 | 760.0 | 760.0 | **0.0** | 48 DIP 栏 |
| 540×760 / 540×420 | 不绘制（最小导航，画布外） | 760.0 / 420.0 | — | 关闭 |

### 4.2 窗口装饰

```text
decor=Full; extend=False; client=1240,760; frame=1254.6667,797.3333
```

非客户区 37.3 DIP = 系统标题栏 + 边框；客户端未扩展。同一测量在 540 宽与最大化前后一致（`Attach` 未改窗口装饰逻辑）。

### 4.3 截图矩阵

`.qa/layout-fluent-audit/` 共 **118 张**：

- 深/浅 × 1240 / 540 宽：`online`、`online-advanced`、`favorites`、`library`、`library-no-results`、`history`、`downloads-tasks/completed/transfers`、`settings-account/network/browse/downloads/reader/library/maintenance`、`versions`、`local-detail`、`local-detail-diagnostics`、`reader-single/continuous/horizontal/immersive`、`online-detail`、`archive`、`torrent`、`web-login`
- 补充档：800×760（紧凑栏）与 540×420（最小高度）各 5 张

自动越界检测（按钮/下拉框/输入框/滑块/复选框中，横向超出客户区的实例）：**0**。

### 4.4 设置行尺度

| 行 | 1240 宽 | 540 宽 |
| --- | --- | --- |
| 无描述行（“主题”） | 59 DIP（标签与控件同行） | 85 DIP（控件落到标签下方） |
| 有描述行（“注册与元数据”，含 3 个复选） | 67 DIP | 151 DIP（复选组换行） |

右侧控件右边界在同一分组内保持一致（同行网格 `*,Auto`，Auto 列贴右）。

### 4.5 回归

| 命令 | 结果 |
| --- | --- |
| `dotnet test tests/EhViewerForPc.Core.Tests` | 通过 129 / 失败 0 |
| `--ui-smoke` | `passed: true`（多源 31 作品、3 版本、下载迁移、登录态、侧栏折叠、水平模式 RTL、沉浸等断言） |
| `--navigation-regression` | 14 项 PASS + 4 项主题资源解析（含“最小化侧栏沉浸移除页头”） |
| `--reader-regression` | `passed: true`（单页/连续/横向、尺寸复用、进度、沉浸） |

## 5. 逐项对照计划

| 计划条款 | 状态 | 说明 |
| --- | --- | --- |
| §2 保留系统标题栏、不扩展 | 已修改 + 已测量 | 见 4.2；未复制参考项目的拖动/48 DIP 占位 |
| §3 内容 24 DIP、间距阶梯、底部栏 40 DIP | 已修改 | `Theme.axaml` 令牌与共享样式 |
| §3 普通页标题 20 DIP、正文 13、次级 12 | 保持 | 壳标题 20；页内不再出现第二个身份标题 |
| §3 设置内容宽度上限 1040 | 已修改 | `MaxWidth=1040`，窄窗自然收窄 |
| §3 设置右侧操作列同组右边界一致 | 已修改 | 行内 `*,Auto` 网格 |
| §3 滑块 240–320、值独立对齐 | 已修改 | 轨道 240–260，数值列 40/76 右对齐 |
| §3 删除全局 WrapPanel 边距，改用途明确容器 | 已修改 | `.actions` |
| §4.1 账户区单行、长 ID 省略 | 已修改 | 摘要 + 登录/退出，`TextTrimming` + Tooltip |
| §4.2 与状态栏共用高度/内边距、贴齐客户区 | 已修改 + 已测量 | delta 0.0；未用负 Margin/Transform |
| §4.3 224 下放不下时 Flyout；紧凑图标 + 辅助名称 | 已修改 | 紧凑按钮 `AutomationProperties.Name="账号"` + Flyout |
| §4.4 保留导航与账户之间弹性空白 | 保持 | 账户栏为独立底栏，未上移列表 |
| §4.5–4.6 删除“设置”及专用调用链 | 已修改 | 按钮/事件/处理器/订阅/命令一并删除；左侧设置入口不变 |
| §4.7 各页状态栏统一 | 已修改 | 本地库、在线/收藏、历史、下载、归档、种子、版本、设置、网页登录 |
| §4.8 “显示 N / 共 N”紧凑摘要 | 已修改 | 右侧两个数字项；扫描进度与取消保留 |
| §5 七类 + 一栏分类、窄窗可达 | 已修改 | 分类栏换行，不裁切；选中状态单一来源 `SelectedCategoryIndex` |
| §5 二元偏好 ToggleSwitch；多选保留 CheckBox | 已修改 | 代理/DoH/翻译/方向/自动继续/自动更新用 ToggleSwitch；注册/元数据三项保留 CheckBox |
| §5 滑块布局与单位 | 已修改 | 三条阅读参数与两条缓存参数均带单位 |
| §5 空间不足整行下沉 | 已修改 | `SettingsRow` 按**自身宽度** <560 下沉，不看主窗口 |
| §5 保存为唯一页级主操作；维护动作归档 | 已修改 | 头部仅有“保存设置”；打开数据目录/清除缓存移入“维护” |
| §5 保留编辑后保存语义 | 保持 | 迁移仍走 `ApplyAsync`，未改为即时生效 |
| §6 专用页头替换通用标题 | 已修改 | 见 2.2 |
| §6 FACommandBar 主次 + 溢出 | 已修改 | 主：单页/连续/横向/适应/沉浸/全屏；次：方向/背景/重试/帮助/关闭 |
| §6 宽窗单行、窄窗标题+命令行 | 已修改 | `AdaptiveGrid` 断点 700；<920 收起文字 |
| §6 去掉 560 上限、Tooltip 全名 | 已修改 | 标题弹性 + 省略 + Tooltip |
| §6 底部“上一页/进度/下一页/页码” | 已修改 | 页码仅此一处 |
| §6 画布 `*` 居中、不裁图 | 保持 | 单页留黑保留 |
| §6 重排后尺寸/Fit 重算 | 已修改 | 模式切换后仍调用 `UpdateContinuousWidth/Height`；回归通过 |
| §6 沉浸收起导航与标题、有退出与 Esc | 已修改 | 浮层按钮 + Esc |
| §6 全屏恢复原状态、Esc 顺序 | 已修改 | `_stateBeforeFullscreen` |
| §7 本地库 | 已修改 | 工具区两行、卡片标题 2 行 + 自适应高度、扫描浮层改 `MaxWidth` 380 |
| §7 在线浏览 / 收藏 | 已修改 | 空行合并；页码靠近翻页；收藏搜索弹性；分类保留键盘可达 |
| §7 阅读历史 | 已修改 | 动作可在 <480 下沉 |
| §7 下载 | 已修改 | 三子页统一；长错误换行；暂停/恢复/删除确认保留 |
| §7 共享详情 | 保留 + 验证 | 740 断点逻辑未动；本地/在线详情共用一个控件 |
| §7 在线详情 | 已修改 | 评分三列可堆叠；提交行可换行 |
| §7 本地详情 | 已修改 | 去掉固定 `MaxWidth=440`，路径可复制、可省略 |
| §7 归档下载 | 已修改 | 去重复标题；`MinWidth=320` 改弹性；URL 行弹性 |
| §7 种子 | 已修改 | 去重复标题；顶部操作可换行 |
| §7 版本 | 已修改 | 说明文字入滚动区；关闭入口唯一 |
| §7 网页登录 | 已修改 | 顶/底关闭合并；长状态换行 |
| §8 改动边界 | 遵守 | 未升级框架、未改引擎、未引入新 UI 框架 |
| §9 矩阵 1240/540、540×420、紧凑 | 已执行 | 见 4.3/4.4 |
| §9 640/920 断点逐像素、200% 文字、系统级拖动截图 | **未执行** | 见 §6 |

## 6. 未验证项与环境限制

1. **系统级完整窗口截图未采集**：审计渲染的是 `window.Content`，不含 OS 绘制的标题栏。改用窗口几何取证（`client` 与 `frame` 相差 37.3 DIP + `extend=False`），因此拖动、双击最大化、最小化/恢复、系统按钮的**人工**点检仍待执行。
2. **Windows 文本缩放（125%/200% 系统文字）未模拟**：本轮仅在 150% DPI 下验证；DPI 与系统文字比例是不同机制。
3. **639/640/641 与 919/920/921 逐像素断点**未逐一截图；已覆盖 540 / 800 / 1240 三档代表值。
4. **MSIX 打包项目无法在本机构建**：`packaging/EhViewerForPc.Msix.wapproj` 需要 Visual Studio 的 `Microsoft.DesktopBridge.props`，本机 SDK 未提供，`dotnet build EhViewerForPc.slnx` 在此环境直接失败。应用项目与测试项目均正常构建，此限制与本次改动无关。
5. **收藏模式与在线浏览共用实现**，已分别截图（`favorites-*` 与 `online-*`），但仅在线浏览做了高级面板展开态截图。
6. 欢迎页与“扫描中”浮层未单独模拟截图（审计用合成库直接进入有内容状态）。

## 7. 复现命令

```powershell
Set-Location 'G:\SoftwareProject\Avalonia\EhviewerDownloadReader'
dotnet build src/EhViewerForPc/EhViewerForPc.csproj -c Debug
dotnet test tests/EhViewerForPc.Core.Tests/EhViewerForPc.Core.Tests.csproj -c Debug --no-build
dotnet run --project src/EhViewerForPc -c Debug --no-build -- --ui-audit --data-dir .qa/layout-fluent-audit-data --out .qa/layout-fluent-audit
dotnet run --project src/EhViewerForPc -c Debug --no-build -- --navigation-regression --data-dir .qa/layout-fluent-nav-data --out .qa/layout-fluent-nav
dotnet run --project src/EhViewerForPc -c Debug --no-build -- --reader-regression --data-dir .qa/layout-fluent-reader-data --out .qa/layout-fluent-reader
dotnet run --project src/EhViewerForPc -c Debug --no-build -- --ui-smoke --data-dir .qa/layout-fluent-smoke-data --out .qa/layout-fluent-smoke
```

证据目录：`.qa/layout-fluent-audit/`（截图 + `audit.json`）、`.qa/layout-fluent-nav/results.txt`、`.qa/layout-fluent-reader/ui-smoke.json`、`.qa/layout-fluent-smoke/ui-smoke.json`。合成库位于系统临时目录，文档中未写入任何真实账号、Cookie 或私人路径。
