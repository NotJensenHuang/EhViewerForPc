---
name: EhViewer for PC
description: 使用原生 Fluent 控件的桌面漫画客户端
colors:
  rose-accent-dark: "#FF6B81"
  rose-accent-light: "#B63650"
  rose-soft-dark: "#2A1A20"
  rose-soft-light: "#FDEDF0"
  success-dark: "#5AD1A0"
  success-light: "#1F9D74"
  warning-dark: "#F2C46D"
  warning-light: "#B77A12"
typography:
  headline:
    fontFamily: "Segoe UI Variable, Segoe UI, Microsoft YaHei UI, Noto Sans CJK SC"
    fontSize: "22px"
    fontWeight: 600
  page-title:
    fontFamily: "Segoe UI Variable, Segoe UI, Microsoft YaHei UI, Noto Sans CJK SC"
    fontSize: "20px"
    fontWeight: 600
  section:
    fontSize: "14px"
    fontWeight: 600
  body:
    fontFamily: "Segoe UI Variable, Segoe UI, Microsoft YaHei UI, Noto Sans CJK SC"
    fontSize: "13px"
  caption:
    fontSize: "12px"
rounded:
  chip: "4px"
  surface: "8px"
  row: "10px"
spacing:
  compact: "8px"
  group: "12px"
  standard: "16px"
  section: "20px"
  pane: "24px"
components:
  tag-chip:
    rounded: "{rounded.chip}"
    padding: "5px 12px"
  gallery-card:
    rounded: "{rounded.surface}"
    padding: "10px"
---

# Design System: EhViewer for PC

## Overview

**Creative North Star: "原生 Fluent 阅读客户端"**

界面服务于浏览、搜索、下载与阅读。标准控件由 FluentAvaloniaTheme 提供统一的外观和交互；应用增加必要的内容布局、封面与标签，不重绘整套原生控件。

**Key Characteristics:**

- 原生 Fluent 导航和标准控件。
- 深色、浅色主题共享语义资源。
- 标题、内容和操作优先；说明保持简短。
- 在线与本地详情使用同一呈现组件。

## Colors

粉色强调色用于主要操作、选择与状态强调；中性色从 Fluent 主题动态取得。上方颜色为应用自有语义色，不应替代控件主题中的各状态色。

`AppBgBrush` 映射 `SolidBackgroundFillColorBase`；替代背景使用 `SolidBackgroundFillColorSecondary`。表面、卡片、悬停分别映射 `LayerFillColorDefault`、`CardBackgroundFillColorDefault`、`SubtleFillColorSecondary`；文字使用 `TextFillColorPrimary` / `TextFillColorSecondary`，边框使用 `CardStrokeColorDefault` / `ControlStrokeColorDefault`。

**The Theme Ownership Rule.** 按钮、输入、选择、悬停、禁用和焦点状态交由 FluentAvaloniaTheme；新增样式优先引用语义资源。

## Typography

使用系统 UI 字体及中文后备。共享详情标题使用 headline，外壳页标题使用 page-title，区块标题使用 section，正文使用 body，次级信息使用 caption。等宽字体仅用于 GID、路径或诊断数据：Cascadia Mono、Menlo、DejaVu Sans Mono、Consolas 及中文后备。

## Layout

外壳使用 FANavigationView 和 FAFrame。展开导航宽度为 224 DIP，紧凑栏为 48 DIP；导航在 920 / 640 DIP 阈值调整布局。窗口最小尺寸为 540 × 420 DIP。

页头覆盖原生控件默认的 44 DIP 上边距：展开/紧凑导航使用 `24,8,24,8`，最小模式使用 `8,4,16,4`。保留返回与菜单按钮的位置；沉浸阅读将 Header 置空，避免最小模式强制保留页头。

内容使用可滚动主区域、成组操作与短状态信息。共享详情在桌面以封面和信息双栏呈现，封面为 280 × 374 DIP，辅助内容位于封面下方。可用宽度小于 740 DIP 时主栏堆叠，封面缩为 120 × 160 DIP，辅助内容移到封面旁，两者间距为 16 DIP；信息区的标签排在元数据之前。共享详情外边距以 pane 为主，元数据标签列为 76 DIP。操作按钮允许换行。

**The Narrow Detail Rule.** 窄详情缩小封面并将辅助内容并排，标签优先于元数据，避免封面将标签挤出首屏。

## Elevation & Depth

应用内容主要以 Fluent 的明暗表面与细边框区分层级，未新增自定义内容阴影。原生弹出、焦点和导航效果由控件主题管理；正常导航使用 Fluent 入口过渡，UI 烟测抑制过渡以稳定断言。

## Shapes

标准控件沿用 Fluent 形状。应用自有封面、卡片与面板使用 surface 圆角，标签使用 chip 圆角，明细行使用 row 圆角。封面裁剪在圆角边界内；卡片通常使用 1 DIP 边框。

## Components

- **按钮与输入**：使用标准控件；主要操作使用 accent 变体，次要操作使用默认或透明按钮。保留原生键盘、禁用与焦点反馈。
- **标签**：紧凑矩形按钮，内容自动换行排列。显示可译标签，悬停提供原始词；点击执行准确的标签搜索。
- **卡片与面板**：图片区、标题和状态分层，信息优先。选中状态使用强调色，普通状态引用主题表面与边框。
- **导航**：六个入口使用 Fluent 图标与文字，返回和前进由 Frame 历史管理；窄窗口收拢导航，阅读器沉浸时隐藏导航区域。
- **启动状态**：主窗口打开后保持导航可用。在线首页自动加载时使用页内状态文字，用户主动加载时显示页内进度条；本地库可直接显示缓存，扫描新来源或手动重扫时显示当前阶段。
- **共享详情**：GalleryDetailsView 负责标题、封面、元数据与标签。来源特定操作通过内容槽提供；本地文件诊断放入 Expander，错误和缺页摘要仍可见。

**The Shared Detail Rule.** 在线和本地详情共用内容结构，来源差异通过数据和操作表达。

## Do's and Don'ts

### Do:

- **Do** 使用 Fluent 原生控件、图标和主题资源。
- **Do** 保留可见的错误、缺页与进度状态。
- **Do** 用简短动词命名操作，让内容占据主要空间。
- **Do** 同时验证浅色、深色与窄窗口。

### Don't:

- **Don't** 为导航新增平行的手写页面历史。
- **Don't** 为在线与本地详情复制一套标题、封面和标签布局。
- **Don't** 在主流程重复产品说明或底层实现细节。

## Reader and Search States

- 下载入队后，本地库卡片显示队列状态。首张图片保存后，同一张卡片显示封面并启用阅读；后续原位更新页数和进度环。离开阅读器时更新对应作品的进度。
- 阅读工具栏正文固定为 13 DIP、常规字重；模式、沉浸、全屏、更多按钮与适应方式选择框统一为 32 DIP 高，窄窗口图标按钮为 36 DIP 宽。更多菜单锚定其按钮右下方。
- 方向菜单并列显示「左→右 / 右→左」两个原生单选项，选中状态随阅读器同步；仅横向连续模式启用。方向只影响页面排列，键盘、滚轮和单页点击区域不反向。
- 缺页在单页、纵向连续与横向连续模式下保留完整页面位置，并在画布内显示缺失页号。本地重试显示正在重下载的页号和失败原因；成功后原位刷新该页及尺寸，不改变当前阅读位置。
- 书签使用 Fluent ContentDialog，关闭后保留来源页面；管理内容随窗口宽度堆叠并可滚动。未选中书签时保留编辑区与操作布局，使用原生禁用状态，不隐藏整列。
- 在线卡片在标题下的独立信息行显示评分与本地状态；本地标识使用不透明的 `AppLocalBadgeBgBrush` / `AppLocalBadgeTextBrush`，深色为 `#214F3C` / `#D7F5E5`，浅色为 `#E0F2E8` / `#174D37`。本地库阅读按钮以 `AppBgBrush` 提供不透明底板，按钮状态仍由原生主题管理。
- 以图搜图页头状态与操作垂直居中对齐；正在建库时显示进度和取消建库操作；未完成时主按钮显示“继续建立索引”。退出后恢复由应用服务负责，显式取消不在下次启动自动恢复。
- 构建进度条显示当前阶段实际比例，状态文字含阶段、数量 / 总量与单位；无总量阶段保留活动指示。缺页副本的卡片阅读底板旁、详情和版本行均显示「继续下载」，不可用时保留禁用状态，提示选中副本原路径；错误显示于所在页面。
- 图片查询在现有状态区分别显示「校验并载入图片索引（实际字节百分比）」「搜索本地图片」「匹配本地页」；校验阶段可取消，不消耗检索时限。后续查询复用同一代，不重复显示载入进度。
- 单项 / 批量补页先核对原目录，已完整时显示「页面已完整，无需补页」并更新缺页筛选；零字节同页副本与旧页数保留独立诊断。
- 补页进度覆盖副本已有页数：缺 2 / 53 页的任务在等待状态也显示 51/53，并保持对应进度环比例；不能用已新增页数代替副本已完成页数。
- 未发现有效 8 位页名的普通本地图片目录使用目录标题、自然页序和首张封面，不要求源目录存在 EhViewer 元数据，也不因这些文件缺失进入缺页分类；诊断仍区分真实空图片和未知原始总数。存在编号页时，额外封面不挤占原页号，重试后的缺页占位保持一致。
- 本地库工具栏的「补齐缺页」明确作用于当前列表所有版本，控件组可换行；失败明细用页面内可展开区域。逐页更新保留现有卡片位置及进度环，自动更新不移动已有卡片，避免数据上下文短暂清空后从零重播；下载速度与页数分行显示，下载页速度放在进度数字旁。
- 本地详情的「版本管理」沿用已有主窗口导航；版本页提供检测所有版本与单版本重检。检测结果在各版本元数据下独立成行，长范围可换行，失败只标记对应版本；作品身份不变时检测不改变副本标识和重复标记。

- 在线浏览保留首页 / 热门 / 订阅 / 排行标签，排行总览及周期选择位于同一页面。列表接近底部时追加下一批，列表底部承载加载、完成、失败重试及暂停自动续载状态；保留 ItemsRepeater 虚拟化。

- 查询裁剪区显示原图色彩和透明度，EXIF 方向与裁剪坐标一致；缩小预览不改变原始裁剪数值。
- Windows 托盘沿用应用图标，原生菜单提供「显示窗口 / 退出」。设置界面使用原生开关「关闭窗口后后台运行」，默认开启；隐藏与恢复保留当前文档和位置。

- 阅读器顶部使用标准 ToggleButton / Button，与 ComboBox 统一为 32 DIP 高；去除 CommandBar 的内容留白和内部尺寸覆盖，窄窗口宽 36 DIP 并隐藏文字，模式选择保留键盘和原生选中反馈。
- 设置「界面与阅读」先展示「窗口与外观」（后台运行开关、主题），再展示「默认阅读」；不把应用级后台行为藏在阅读模式说明中。
- 索引更新进度分别展示未变页批量复用、实际提取与失败数量，新建与增量更新使用不同阶段文案，进度通知与数据库持久化批次分开。

- 本地卡片右键与版本行提供「删除此副本…」；共用行内确认控件，展示可复制完整路径及「移入回收站 / 取消」，等待写入期间禁用重复操作。回收失败或取消后，期间接受的启动请求继续生效，随后暂停则保持暂停。保留原生按钮、语义警告边框与深浅主题，窄窗按钮自然换行。
- 下载页「画廊下载」仅保留未完成任务，完成记录在「已完成」查看。
- 下载页「画廊不可用」单列站点已确认不可用的任务，沿用队列卡片展示完整失败理由；操作保留「阅读 / 目录 / 移除标记 / 移除」，任务在两类之间移动时复用同一张卡片，不重播进度。
