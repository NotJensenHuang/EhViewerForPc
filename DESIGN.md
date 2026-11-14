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

- 阅读工具栏正文固定为 13 DIP、常规字重；窄窗口的图标按钮为 36 DIP，适应方式选择框保持紧凑。更多菜单锚定其按钮右下方。
- 方向菜单并列显示「左→右 / 右→左」两个原生单选项，选中状态随阅读器同步；仅横向连续模式启用。方向只影响页面排列，键盘、滚轮和单页点击区域不反向。
- 缺页在单页、纵向连续与横向连续模式下保留完整页面位置，并在画布内显示缺失页号。
- 书签使用 Fluent ContentDialog，关闭后保留来源页面；管理内容随窗口宽度堆叠并可滚动。未选中书签时保留编辑区与操作布局，使用原生禁用状态，不隐藏整列。
- 在线卡片在标题下的独立信息行显示评分与本地状态；本地标识使用不透明的 `AppLocalBadgeBgBrush` / `AppLocalBadgeTextBrush`，深色为 `#214F3C` / `#D7F5E5`，浅色为 `#E0F2E8` / `#174D37`。本地库阅读按钮以 `AppBgBrush` 提供不透明底板，按钮状态仍由原生主题管理。
- 以图搜图页头状态与操作垂直居中对齐；正在建库时显示进度和取消建库操作；未完成时主按钮显示“继续建立索引”。退出后恢复由应用服务负责，显式取消不在下次启动自动恢复。
- 构建进度条显示当前阶段实际比例，状态文字含阶段、数量 / 总量与单位；无总量阶段保留活动指示。缺页副本的卡片阅读底板旁、详情和版本行均显示「继续下载」，不可用时保留禁用状态，提示选中副本原路径；错误显示于所在页面。

- 在线浏览保留首页 / 热门 / 订阅 / 排行标签，排行总览及周期选择位于同一页面。列表接近底部时追加下一批，列表底部承载加载、完成、失败重试及暂停自动续载状态；保留 ItemsRepeater 虚拟化。
