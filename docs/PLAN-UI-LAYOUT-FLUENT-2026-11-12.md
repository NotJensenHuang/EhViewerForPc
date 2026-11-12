# 全页面布局、对齐与 Fluent 修复执行计划

日期：2026-11-12。基线提交：`9805b1e`。状态：**已实施**；验收与逐项证据见 [UI-LAYOUT-FLUENT-VERIFICATION-2026-11-12.md](UI-LAYOUT-FLUENT-VERIFICATION-2026-11-12.md)。

交付目的：供另一个 agent 直接实施。此次只新增计划文档，没有修改产品代码，没有运行应用或重新执行测试。

审查由主 agent 与用户指定的 `gpt-6.1-terra` 子代理完成。Terra 检查全部 12 个 Page XAML、MainWindow、共享详情组件及相关布局代码，并抽查已有逐页截图。

## 1. 用户要求与证据边界

本轮必须达成：

- 修正左下账户区过多的底部留白，使其与右侧状态栏形成共同的水平基准。
- 删除本地库右下角“设置”按钮，保留左侧设置主入口。
- 将设置页改成符合 Fluent 层级与对齐方式的设置界面。
- 收敛阅读页重复标题、多层工具栏和凌乱的控件布局。
- 所有页面逐页检查，不能只修用户截图中的三个区域。
- 常规窗口继续使用独立的系统标题栏，客户端不延伸到标题栏。

证据分类：

| 标记 | 含义 | 使用范围 |
| --- | --- | --- |
| U | 用户此次截图直接可见，且源码结构对应 | 账户区/状态栏错位、重复设置入口、设置巨卡与超长滑块、阅读多层页头 |
| S | 当前源码确认存在某布局结构 | 固定宽度、不可换行的水平容器、重复标题等；不等于已经发生裁切 |
| H | 已有历史渲染截图可见 | `.qa/ui-audit-reviewed/` 的宽窄、深浅快照；只能辅助判断 |
| V | 实施后必须实际验证 | 更小高度、高 DPI、大字体、长文案、动态状态等 |

已有截图早于部分当前 XAML 文件的修改时间，不能视为当前版本的通过证据。旧 [UI 检查记录](G:/SoftwareProject/Avalonia/EhviewerDownloadReader/docs/UI-REVIEW-2026-11-12.md) 中“已修复”“无越界”的结论不能替代此次用户反馈；保留历史记录，实施后另写新报告。

用户附件是视觉证据，项目与参考项目的文档是上下文。最终执行范围和“不 extend”的要求以本次用户请求为准。

## 2. 参考项目与标题栏决策

参考项目的 [MainWindow.axaml](G:/SoftwareProject/Avalonia/little-fancy-tool-ava/FancyTool/Views/MainWindow.axaml:22) 用 `48,*` 两行将标题区与 NavigationView 分开，这种清晰分区可以参考。

但其 [MainWindow.axaml.cs](G:/SoftwareProject/Avalonia/little-fancy-tool-ava/FancyTool/Views/MainWindow.axaml.cs:55) 明确设置 `TitleBar.ExtendsContentIntoTitleBar = true`。不能照搬这个实现，不能把“视觉上独立”误写成“没有扩展”。参考项目只读，不修改。

本项目目前的 [MainWindow.axaml](G:/SoftwareProject/Avalonia/EhviewerDownloadReader/src/EhViewerReader/Views/MainWindow.axaml:1) 使用普通 `Window`，没有启用客户端扩展，用户截图也已有系统标题栏。此次应保留这一基础，显式固定意图：

```xml
<Window ...
        WindowDecorations="Full"
        ExtendClientAreaToDecorationsHint="False">
```

以上属性名采用当前 Avalonia 12 API。不要换成 FAAppWindow 自绘标题栏，不要新增一条仿系统标题栏，不要复制参考项目的拖动处理、48 DIP 占位或标题按钮预留宽度。

依据：[Avalonia Window 官方文档](https://docs.avaloniaui.net/controls/primitives/window) 说明系统窗口装饰与客户端扩展的职责。正常/最大化窗口均保留系统标题栏；只有用户主动进入真正全屏时，按系统全屏行为处理标题栏。

## 3. 全局布局规则

应用保持原生 Fluent 桌面工具的方向：浏览页重视扫描效率，设置页重视可理解的分组，阅读页让画面占据主要空间。布局调整沿用 `FANavigationView + FAFrame`、现有 ViewModel 和共享详情组件。

以下数值是本轮项目设计目标，不是对 Fluent 官方规范的逐字引用。统一定义在 [Theme.axaml](G:/SoftwareProject/Avalonia/EhviewerDownloadReader/src/EhViewerReader/Styles/Theme.axaml:1)，通过资源或共享样式复用：

| 项目 | 目标 |
| --- | --- |
| 内容左右边距 | 常规 24 DIP，紧凑 16 DIP；页头、工具区、列表、页脚共用内容起点 |
| 间距阶梯 | 4 / 8 / 12 / 16 / 24；组内紧凑、组间清楚 |
| 底部栏 | 标准单行最小高度 40 DIP，底部外边距 0；异常长状态可向上增高 |
| 普通页标题 | 20 DIP；正文沿用应用 13 DIP；次级状态 12 DIP，不再缩小来掩盖拥挤 |
| 设置内容宽度 | 最大 1040 DIP，靠内容区左侧对齐，右侧余量自然留白 |
| 设置右侧操作列 | 通常 200–320 DIP，同组右边界一致；空间不足时移到标签下一行 |
| 设置滑块 | 常规轨道约 240–320 DIP，最大 360 DIP，当前值独立对齐；窄窗随容器缩小 |
| 标准控件 | 尺寸、焦点、悬停、禁用状态优先使用 FluentAvalonia 默认主题 |

先处理 [Theme.axaml](G:/SoftwareProject/Avalonia/EhviewerDownloadReader/src/EhViewerReader/Styles/Theme.axaml:201) 的全局 `WrapPanel > Button` / `WrapPanel > ComboBox` 右下边距。它与局部 Margin 叠加，使按钮和下拉框难以对齐。改为具有明确用途的局部容器样式，并检查原先依赖该全局规则的所有页面；不能直接删掉规则后留下零间距。

一页只保留一个负责表达页面身份的标题。空 StackPanel、已无用途的 Auto 行、空白占位列一并清理。长标题/路径用可伸缩列约束，允许省略或换行，并保留完整信息的 Tooltip/复制方式。

## 4. P1：外壳与底部对齐

主要文件：

- [MainWindow.axaml](G:/SoftwareProject/Avalonia/EhviewerDownloadReader/src/EhViewerReader/Views/MainWindow.axaml:28)
- [MainWindow.axaml.cs](G:/SoftwareProject/Avalonia/EhviewerDownloadReader/src/EhViewerReader/Views/MainWindow.axaml.cs:138)
- [LibraryPage.axaml](G:/SoftwareProject/Avalonia/EhviewerDownloadReader/src/EhViewerReader/Views/LibraryPage.axaml:180)
- [LibraryPage.axaml.cs](G:/SoftwareProject/Avalonia/EhviewerDownloadReader/src/EhViewerReader/Views/LibraryPage.axaml.cs:27)

已确认：PaneFooter 使用 `Margin="16,8,16,16"`、Spacing 8 的竖排账户/按钮；本地库状态栏单独使用 `Padding="22,9"`、MinHeight 36。两边没有共同高度/对齐规则。实际底部空白还可能受 NavigationView 模板影响，不能断言只减 16 DIP 就一定修好。

执行：

1. 将展开侧栏的账户区收敛为一行：账户摘要在左、登录/退出动作在右，垂直居中；长 ID 省略。沿用登录/退出命令与保存期间禁用逻辑。
2. 账户区与页面状态栏使用同一套底部栏高度/内边距资源，底边贴齐客户端底部。先检查当前 FluentAvalonia 模板中 PaneFooter 的实际布局边界，消除额外底部槽位或内边距；不要用负 Margin、TranslateTransform 或按截图估算的补偿量修正。
3. 侧栏 224 DIP 下若完整账户文案与按钮放不下，账户摘要按钮打开原生 Flyout，显示账户及登录/退出动作，保持一行高度；紧凑侧栏使用有辅助名称的账户图标，窄窗菜单展开后仍可访问账户功能。
4. 保留导航主体到固定账户区之间的弹性空白；要消除的是账户区下方多余空间，不是把账号推到导航列表中间。
5. 删除 LibraryPage 第 195 行的“设置”。它仅通过 `OnOpenSettings → SettingsRequested → shell.Navigate("settings")` 打开左侧同一个设置页，没有独立功能。
6. 同步删除这条入口专用的事件、处理器和订阅。不要误删其他仍有调用者的 OpenSettings 命令或空库恢复入口。
7. 统一在线、历史、下载、设置和辅助页现有状态栏的高度、文字中心线及左右内边距；状态栏各自保留原数据绑定，不为纯布局新增全局状态服务。
8. 本地库右侧“显示 N / 共 N 个作品”合并为紧凑摘要；扫描中保留进度和取消，窄窗允许摘要缩短，不能挤掉取消动作。

验收：同一窗口坐标系下，展开侧栏账户栏与右侧状态栏底边差 ≤1 DIP，普通单行状态的视觉中心线差 ≤2 DIP；账户栏下方不再有额外空行。若状态栏因错误文案增高，账户仍保持底部停靠。侧栏展开/折叠、登录/未登录、扫描中/完成均检查。右下“设置”消失，左侧设置仍可用。

## 5. P1：设置页改为 Fluent 设置行

问题来源：[SettingsPage.axaml](G:/SoftwareProject/Avalonia/EhviewerDownloadReader/src/EhViewerReader/Views/SettingsPage.axaml:277) 的通栏 tile、四组固定 150 DIP 的控件，以及“标签 → 通栏 Slider → 数值”反复堆叠。更换背景或圆角不能解决层级与对齐问题。

采用左侧标题/必要描述、右侧操作控件的设置行；高级参数使用可展开分组。依据：[Microsoft 设置页指南](https://learn.microsoft.com/en-us/windows/apps/design/app-settings/guidelines-for-app-settings)。这是设计模式参考，不能把 WinUI Community Toolkit 的 SettingsCard 类名直接写进 Avalonia。

本地 FluentAvaloniaUI 3.0.2 的 XML 已确认包含 `FASettingsExpander`、`FASettingsExpanderItem`。优先使用当前包已有控件；必要的简单设置行可用共享 Grid 组件实现。具体属性以本地 API/示例为准，不假设存在 `FASettingsCard`，不引入另一套 UI 框架。

结构目标：

```text
设置                              [保存设置]
账号 / 网络 / 浏览 / 下载 / 阅读 / 资料库 / 维护

默认阅读
┌ 主题                                  [跟随系统 v] ┐
│ 默认模式                              [单页     v] │
│ 背景                                  [黑色     v] │
│ 适应方式                              [适应页面 v] │
└ 日漫方向（右→左）                         [开关]   ┘

图像与缓存                                      [展开]
  解码像素上限                   [──●──────]   24 MP
  图像内存预算                   [─●───────]  256 MB
  封面解码宽度                   [──●──────]  360 px

保存/迁移状态；必要时显示取消迁移
```

执行要求：

- 保留七个类别与原有顺序；宽窗使用一条分类栏。窄窗或文字放大后若不能完整显示，用绑定同一选中状态的类别选择器，确保七项全部可达；不得另建一套独立设置状态。
- 阅读页移除四列固定宽度表单，使用统一设置行。默认阅读和图像缓存分组；技术参数可放入 FASettingsExpander，展开后仍显示单位与当前值。
- 二元偏好使用原生 ToggleSwitch；批量勾选或多选数据源继续使用 CheckBox。标签、描述、控件的视觉与辅助名称关联清楚。
- 数值滑块使用 `标签 * / 弹性滑块 / 当前值 Auto` 的受约束布局；长描述可以换行，值和单位不得被截掉。保留原最小值、最大值、取整和保存逻辑。
- 空间不足时，整行操作区移到标签下方并左对齐，不再堆成两列/四列不等宽孤岛。按设置行实际可用宽度判断，不能只看主窗口宽度。
- 把“保存设置”保留为唯一页级主要动作；“打开数据目录”“清除扫描缓存”归入维护类别，避免设置页顶部像工具箱。迁移取消和错误状态继续固定可见。
- 当前应用采用编辑后保存的流程。本轮保留该语义，尤其目录迁移不能因改成开关就立即执行。不得把设计指南的即时生效建议当成改写保存事务的授权。

七类检查清单：

| 类别 | 具体修改/验证 |
| --- | --- |
| 账号 | 登录状态与操作同一设置行；密码/Cookie 为次级展开区；长用户信息换行/省略；迁移期间禁用保持 |
| 网络 | 模式选择、代理字段和 DoH 各成组；现有 AdaptiveGrid 堆叠逻辑保留并检查标签随字段一起移动 |
| 浏览 | 图片质量、压缩宽度、标签翻译分行；缓存上限/天数用受约束滑块；过滤规则窄窗两行；收藏槽名称完整可编辑 |
| 下载 | 根目录用弹性输入框，浏览/打开动作窄窗换行；并发值右对齐；迁移模式、警告、取消状态不能隐藏 |
| 阅读 | 按上方结构实施；主题、模式、背景、适应和方向独立对齐；三条技术参数不再横贯窗口 |
| 资料库 | 数据源路径弹性占宽、操作窄窗移到下一行；禁用、移除、扫描状态与取消动作保持可达 |
| 维护 | 放置数据目录与缓存维护动作；内容按实际高度排列，不人为撑满一张巨卡 |

验收：七类逐类截图；同组下拉框/开关右边界统一，所有数值有单位；540 DIP 宽度与文字放大后可滚动访问全部控件；切类别不会丢失未保存编辑；保存、登录、目录迁移及取消仍走原流程。

## 6. P1：阅读页收敛为一个页头与一个底部进度区

已确认的三层结构：MainWindow 通用“阅读”标题/站点按钮，加 [ReaderPage.axaml](G:/SoftwareProject/Avalonia/EhviewerDownloadReader/src/EhViewerReader/Views/ReaderPage.axaml:14) 的作品信息行，再加模式/适应/方向/重试/沉浸/全屏按钮行。页码又在底部重复显示。标题还被 `MaxWidth="560"` 人为限制。

目标结构：独立系统标题栏 → 阅读专用页头 → 画布 → 紧凑翻页进度区。

执行：

1. 为阅读页提供专用 NavigationView Header 内容，替换通用“阅读”标题。作品标题与命令放在这一页头里，ReaderPage 本体不再保留第二份标题和整排按钮。保留原生返回/菜单位置，正常阅读仍可返回。
2. 使用已确认存在于 FluentAvaloniaUI 3.0.2 的 `FACommandBar`，按主次组织操作。常用：阅读模式、适应方式、沉浸、全屏；方向、背景、帮助、普通重试可进入“更多”。失败时在错误附近保留显眼的重试动作。
3. 宽窗页头单行，标题占弹性空间；窄窗最多分成标题行与紧凑命令行。命令优先进入溢出菜单，不用 WrapPanel 将八个按钮随意排成多行。模式选择互斥，保留 M/D/F11 等快捷键及选中语义。
4. 去掉标题固定 560 DIP 上限，按容器省略，Tooltip 提供完整作品名。来源、缺页和恢复状态在有意义时简短显示；不重复显示三份页码。
5. 底部保持“上一页 / 弹性进度 / 下一页 / 当前页码”，共用底部栏间距。方向切换后的按钮与方向键行为沿用现有语义；滑块端点、拖动和禁用状态不变。
6. 画布使用剩余 `*` 空间，按图片比例居中。单页的留黑本身是正确行为，不为消除空白裁图或强制铺满。连续/横向模式继续使用各自的真实滚动视口和虚拟化路径。
7. 重排页头后核对尺寸计算、Resize 通知与 Fit 重新计算；不能因工具栏换位置导致图片超出视口、反复缩放或进度跳动。
8. 同步调整 [MainWindow.UpdateAdaptiveLayout](G:/SoftwareProject/Avalonia/EhviewerDownloadReader/src/EhViewerReader/Views/MainWindow.axaml.cs:494)，避免它每次恢复通用 NavigationHeader，覆盖阅读专用页头。
9. 当前 [SetImmersive](G:/SoftwareProject/Avalonia/EhviewerDownloadReader/src/EhViewerReader/Views/ReaderPage.axaml.cs:49) 仅隐藏 ReaderHeading。重构后使沉浸时确实收起导航和普通标题，仍有可发现的退出操作、Esc 和必要进度；本轮不要求新增自动隐藏动画。
10. 沉浸与全屏继续是两个独立动作：沉浸不能隐式开启标题栏扩展；退出全屏应恢复进入前 Normal/Maximized 状态，避免当前 ToggleFullscreen 固定返回 Normal。Esc 先退出沉浸，再退出全屏，之后返回来源页。

验收：单页、纵向连续、横向连续、沉浸、全屏均有正常/窄窗截图；通用“阅读”标题不再重复，顶部不再出现三层操作区。正常模式保留独立系统标题栏。模式/方向/适应切换、前后翻页、拖动、进度恢复及返回来源页回归通过。

## 7. P2：其余页面逐页执行表

下表是 Terra 的逐页评审归纳。标为静态风险的条目先通过真实尺寸/状态验证，再采用对应修正；不要把未复现问题写成既成 Bug。

| 页面/组件 | 位置与证据 | 执行项与验收重点 |
| --- | --- | --- |
| 本地库 | [LibraryPage](G:/SoftwareProject/Avalonia/EhviewerDownloadReader/src/EhViewerReader/Views/LibraryPage.axaml:12)，U/S | 数据源、排序、搜索、筛选、语言目前分散多行。搜索为主行，筛选/排序/语言成组并在有空间时共行，窄窗按完整分组换行。卡片、页头、状态栏使用共同内容起点；卡片固定高度需验证大字体/长标题，不以缩小字体解决。第 160 行扫描浮层固定宽 380，改为随视口收缩 |
| 在线浏览 | [OnlineFeedPage](G:/SoftwareProject/Avalonia/EhviewerDownloadReader/src/EhViewerReader/Views/OnlineFeedPage.axaml:8)，S/H/V | 合并空 Auto 行与工具区；页码靠近翻页按钮。高级选项第 74 行上限 240，在 420 高窗口可能挤没结果区，低高时用可滚动弹出/展开区域，收起后恢复列表空间。结果摘要与保存/清除搜索动作窄窗分行 |
| 收藏 | [OnlineFeedPage](G:/SoftwareProject/Avalonia/EhviewerDownloadReader/src/EhViewerReader/Views/OnlineFeedPage.axaml:39)，S/H/V | 与在线页共用实现但必须单独验收。搜索框固定 260 加水平按钮，改为弹性输入与可换行动作。11 个分类保留选择和键盘可达性；历史截图已有分类换行，不重复声称它必然裁切 |
| 阅读历史 | [HistoryPage](G:/SoftwareProject/Avalonia/EhviewerDownloadReader/src/EhViewerReader/Views/HistoryPage.axaml:14)，S/H/V | 顶部操作可换行；第 44 行封面/正文/固定动作三列窄时把动作移到正文下方。保留标题两行、进度与最近时间；零页条目保持不可读语义。历史样本未见裁切，重点测更小高度和大字体 |
| 下载 | [DownloadsPage](G:/SoftwareProject/Avalonia/EhviewerDownloadReader/src/EhViewerReader/Views/DownloadsPage.axaml:8)，S/H/V | 三个子页逐一测：任务、已完成、归档传输。顶部多层固定区域压缩低高视口；任务/已完成卡片的正文优先，长错误换行，操作窄时下一行。保留暂停/恢复/重试、进度和删除确认，统一底部状态基线 |
| 共享详情 | [GalleryDetailsView](G:/SoftwareProject/Avalonia/EhviewerDownloadReader/src/EhViewerReader/Controls/GalleryDetailsView.axaml:9)，S/H/V | 现有 740 DIP 以下小封面+侧栏、标签优先逻辑方向正确，保留并验证断点两侧。宽版 280×374 封面、76 DIP 标签列在长标题/放大文本时检查。标题、元数据、操作统一起点，不复制在线/本地两套布局 |
| 在线详情 | [OnlineGalleryPage](G:/SoftwareProject/Avalonia/EhviewerDownloadReader/src/EhViewerReader/Views/OnlineGalleryPage.axaml:43)，S/H/V | 评分三列/五个星按钮、评论行、提交状态横排做窄窗验证，必要时堆叠。主操作已有 WrapPanel，先去叠加间距再评估。收藏对话框最小宽度 320，验证真实窄窗、焦点与滚动 |
| 本地详情 | [GalleryDetailsPage](G:/SoftwareProject/Avalonia/EhviewerDownloadReader/src/EhViewerReader/Views/GalleryDetailsPage.axaml:22)，S/H/V | 文件诊断的固定标签列和底部横向按钮组窄时换行；长路径受弹性宽度约束，保持可复制。诊断展开不应挤压共享标题/封面，也不能遮住末尾操作 |
| 归档下载 | [ArchivePage](G:/SoftwareProject/Avalonia/EhviewerDownloadReader/src/EhViewerReader/Views/ArchivePage.axaml:11)，S/H/V | 壳标题与页内“归档下载”重复，保留作品信息/副标题，将刷新等归入统一页级工具区。第 34 行 ComboBox MinWidth 320 改为受容器约束；第 58 行按钮+URL 的水平容器改弹性布局。关闭等同返回时只保留一个清楚入口 |
| 种子 | [TorrentPage](G:/SoftwareProject/Avalonia/EhviewerDownloadReader/src/EhViewerReader/Views/TorrentPage.axaml:14)，S/H/V | 消除壳/页内重复标题；顶部操作与状态条窄窗可换行。第 37 行已有 AdaptiveGrid，保留其堆叠能力；测长名称、多条记录、空状态和下载动作可达性 |
| 版本/本地副本 | [VersionsPage](G:/SoftwareProject/Avalonia/EhviewerDownloadReader/src/EhViewerReader/Views/VersionsPage.axaml:13)，S/H/V | 顶部多行信息在低高窗口可能挤压列表，非必要说明并入滚动区。现有三个操作支持换行，避免回退为固定横排；长来源/路径可查看，关闭/返回不被遮挡 |
| 网页登录 | [WebLoginPage](G:/SoftwareProject/Avalonia/EhviewerDownloadReader/src/EhViewerReader/Views/WebLoginPage.axaml:10)，S/H/V | 统一壳/页内标题；浏览器操作、长状态与 Cookie 区改为弹性列/换行。顶部关闭和底部取消按实际语义合并：若会取消正在运行的登录，必须保留取消能力。滚动到底检查所有操作；不能因历史截图未滚到底就断言按钮不可达 |

历史快照中，历史、下载、种子、版本等代表样本没有明显横向裁切；这些页面的窄高/放大风险属于 V 项。审查覆盖所有页面，不代表每页都必须大改。

## 8. 实施顺序与改动边界

按以下顺序执行，每阶段完成后再处理下一阶段，避免同时改全局样式和所有页脚而失去回归定位：

1. **外壳与共用规则**：显式非扩展标题栏、底部栏、账户区、删除重复设置入口、收窄全局 WrapPanel 样式。交付主窗宽/窄/折叠对比。
2. **设置**：先完成阅读类别作为设置行样板，再覆盖其余六类。保留保存、迁移、取消、登录和扫描状态。
3. **阅读**：专用页头/命令溢出、画布与底部进度，随后验证模式和窗口状态恢复。
4. **列表与详情**：本地库、在线、收藏、历史、下载、共享详情及两种详情；用已确定的共用规则修整。
5. **辅助页与总验收**：归档、种子、版本、网页登录；重拍逐页矩阵，补真实系统标题栏证据，写最终报告。

不改下载引擎、解码器、图片内容、缓存算法、数据格式或导航架构；不升级 Avalonia/FluentAvalonia；不为修布局引入 ReactiveUI 或 Web UI 框架。保留 ItemsRepeater 虚拟化、现有命令守卫和编译绑定。

若增加设置行或页级工具栏组件，只抽取真实重复布局。相关事件订阅要随页面离开解绑，读取当前页的视觉状态，不另造页面历史或长期同步服务。

实施 agent 收到本文即可在本项目执行，不必再次确认已明确的删除入口、布局方向或不扩展标题栏要求。遇到缺失数据先使用现有隔离合成 fixture，不操作真实账号或用户目录来制造验收状态。

## 9. 验收矩阵与复现命令

主矩阵：全部页面/子类别 × 深色/浅色 × 1240×760、540×760 DIP。额外边界：540×420、640×480、639/640/641、919/920/921 DIP 宽度，以及最大化。记录实际 ClientSize、RenderScaling；窗口 Width 与物理截图像素不能混为一谈。

针对外壳、设置、阅读与长文案页面，补 125%/150%/200% DPI 和文字放大；检查导航切换、Flyout、滚动到底、Tab 焦点、Space/Enter、Esc。若工具不能独立模拟系统文字比例，明确记录限制，不能把放大截图当作通过。

状态覆盖：空/加载/错误/有内容；账号登录/未登录；扫描中/完成；下载暂停/失败/完成/迁移；详情长标题/标签/路径；阅读缺页/加载失败/进度恢复。需要完整文案的错误不能只被省略且无展开途径。

使用已存在的入口，所有数据目录与输出目录必须隔离：

```powershell
Set-Location 'G:\SoftwareProject\Avalonia\EhviewerDownloadReader'
dotnet build EhViewerReader.slnx -c Debug
dotnet test tests/EhViewerReader.Core.Tests/EhViewerReader.Core.Tests.csproj -c Debug --no-build
dotnet run --project src/EhViewerReader -c Debug --no-build -- --ui-audit --data-dir .qa/layout-fluent-audit-data --out .qa/layout-fluent-audit
dotnet run --project src/EhViewerReader -c Debug --no-build -- --navigation-regression --data-dir .qa/layout-fluent-nav-data --out .qa/layout-fluent-nav
dotnet run --project src/EhViewerReader -c Debug --no-build -- --reader-regression --data-dir .qa/layout-fluent-reader-data --out .qa/layout-fluent-reader
```

当前 UiAuditService 的固定矩阵只有 1240/540 宽与 760 高，需补充最小高度、断点和账户/页脚的真实 Bounds 测量。设置类别若从 TabControl 调整，不得让脚本找不到 SettingsTabs 后仍截图并误报覆盖，应按实际类别选中并断言当前类别。

[UiSmokeService.SaveScreenshot](G:/SoftwareProject/Avalonia/EhviewerDownloadReader/src/EhViewerReader/Services/UiSmokeService.cs:963) 渲染的是 `window.Content`，不会拍到原生系统标题栏。因此还必须捕获真实操作系统完整窗口截图，并实际检查拖动、双击最大化、最小化/恢复、关闭、系统按钮和全屏恢复。仅应用内容截图不能证明“不 extend”验收通过。

复用现有 smoke/回归入口验证布局和交互；仅为布局新增有意义的边界/基线测量，不写只断言 XAML 常量的单元测试。正常 pass 后不反复整套重跑，只有新改动、失败或未解决问题才追加对应检查。

## 10. 完成条件与交付

- [ ] 正常与最大化窗口保留系统独立标题栏，显式未开启客户端扩展；有完整窗口证据。
- [ ] 账户区贴底、左右底部基准对齐，展开/紧凑/最小导航模式均正常。
- [ ] 本地库右下“设置”及其专用调用链已删除，左侧设置入口正常。
- [ ] 设置七类都使用一致的行式层级；阅读不再四列散排与三条通栏滑块。
- [ ] 阅读只保留一处页头，命令有主次和窄窗溢出；模式、翻页、恢复、返回、沉浸及全屏通过回归。
- [ ] 全部 12 个 Page、收藏模式、共享详情和下载/设置子页均有逐页结论。
- [ ] 540×420 下主操作和滚动区域可达；无无法访问的分类、按钮、长错误或被遮住的底部控件。
- [ ] 构建及相关既有回归通过；未执行项、平台限制单独记录。
- [ ] 新增 `docs/UI-LAYOUT-FLUENT-VERIFICATION-2026-11-12.md`，逐项标注本计划问题编号/章节、修改文件、实际尺寸、前后截图与验证结果。
- [ ] 原始截图、Bounds/缩放数据与日志放 `.qa/layout-fluent-*`，不把真实账号/Cookie/私人目录数据写入文档。

最终报告必须区别“已修改”“实际验证通过”“待验证”。不以编译成功、截图数量或自动越界检测为零代替人工布局验收。

## 11. 本次计划审查说明

本轮已完成源码、三张用户截图、参考项目标题栏实现和 Terra 逐页静态/历史截图审查；没有执行实施步骤。独立的原生桌面布局审查不运行面向网页 DOM/CSS 的机械检测器。

Questions skipped: 用户已明确要求输出可交接的修复计划，现有截图与源码足以确定方向，无需补充产品问答。
