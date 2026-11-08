# EhViewer 下载格式说明

本文记录阅读器实际兼容的目录结构、元数据与边界情况，依据 EhViewer / Ehviewer_CN_SXJ 源码与真实资料库（6,327 个画廊）验证。

## 1. 目录结构

```
<资料库根目录>/
  1002892-<标题>/                  # gid + "-" + 清洗后的标题
    00000001.jpg                   # 8 位十进制页号 + 扩展名
    00000002.webp
    ...
    .ehviewer                      # 文本或 CBOR 元数据（可选）
    .thumb                         # 封面（无扩展名，可选）
  无 gid 前缀的目录/                # 允许，gid 从 .ehviewer 读取
  .stfolder/                       # 忽略
  <64位十六进制>.tmp/               # 忽略
  .nomedia                         # 忽略
```

识别规则：

- 只扫描根目录一级子目录，跳过隐藏目录、`.stfolder*`、`<hex>.tmp`、符号链接/重解析点。
- 目录名匹配 `^(?<gid>\d+)-(?<title>.+)$`；无匹配时标题取完整目录名。
- 目录名 gid 与 `.ehviewer` gid 不一致时保留两者，记录 `HasGidConflict`，不静默合并。

## 2. 页文件

- 文件名：`%08d` + `.` + 扩展名，例如 `00000001.jpg`；`00000000` 无效。
- 支持的扩展名（EhViewer `SUPPORT_IMAGE_EXTENSIONS`）：`.jpg` `.jpeg` `.png` `.gif` `.webp`，大小写不敏感。
- 同一页号可能存在多个扩展名（下载切换过格式）。候选排序：
  1. 非 0 字节优先；
  2. `.jpg` > `.jpeg` > `.png` > `.gif` > `.webp`；
  3. 文件名排序。
- 解码失败时回退到下一个候选；全部失败显示错误页，不崩溃。
- 页数统计按“实际存在的页号”计算，`DisplayIndex` 连续，但 UI 显示原始页号。

## 3. `.ehviewer`

### 3.1 VERSION2（文本）

```
VERSION2
<startPage，8 位十六进制>
<gid>
<token>
<ignored: 1>
<previewPages>
<previewPerPage>
<pages>
<index> <pToken>
...
```

### 3.2 VERSION1（文本，首行不是 `VERSION...`）

```
<startPage，8 位十六进制>
<gid>
<token>
<ignored>
<previewPages>
<ignored>            ← 源码仍会消费这一行，不能省略
<pages>
<index> <pToken>
...
```

> V1 与 V2 的行数相同；实现按“VERSION2 多一行 previewPerPage”处理，V1 下该行仅消费不解析。

### 3.3 CBOR（新版恢复逻辑）

文件首字节落在 `0xA0..0xBF`（CBOR map）时按 CBOR 解析，读取 `gid` / `token`，可跳过 `pTokenMap`。限制：32 MB、嵌套深度 16、key 64 字节、token 128 字节；超限/截断/重复 key/负 gid 均拒绝并回退。

### 3.4 容错

- CRLF、UTF-8 BOM、缺行、坏 pToken 行不影响 gid/pages 的提取。
- `pages` 必须 `1..100000`；否则标记 header 无效，页数按实际文件推断。
- `StartPage` 仅记录，不参与页号计算。
- 解析失败不会阻止阅读：`HeaderError` 记录原因，页数按实际文件统计。

## 4. `.thumb`

- 无扩展名；实测包含 JPEG（`FF D8 FF`）、WebP（`RIFF....WEBP`）、以及 0 字节/截断文件。
- 读取前 12 字节嗅探格式；0 字节或解码失败时回退到“第一个可读页”作为封面。
- 封面解码宽度按卡片实际像素宽度 × RenderScaling 计算，并受单张像素预算约束。

## 5. 缺页与不完整

- `ExpectedPages`：`.ehviewer` 声明的 `pages`（CBOR 无此项）。
- `MissingRanges = [1..ExpectedPages] \ 实际页号`；无 header 时只检测中间 gap。
- 实际最大页号 > `ExpectedPages` 时以实际文件为准，标记 header 过期。
- 目录存在 0 字节页时标记 `ZeroByteFiles`，画廊视为“不完整”，详情显示“N 个损坏页”。
- 0 页目录保留在库中并标记“无图片”，不进入阅读器。

## 6. 稳定键（进度/收藏）

| 情况 | StableId |
| --- | --- |
| gid 唯一 | `1234567` |
| gid 重复 | `1234567@<pathHash>`（所有重复项都带后缀） |
| 无 gid | `dir@<pathHash>` |
| 目录名 gid 与 header gid 冲突 | `gid@<pathHash>` |

`pathHash` = 规范化完整路径（Windows 小写、去尾分隔符）SHA-256 前 12 位十六进制。

## 7. 扫描缓存签名（schema 2）

缓存条目保存：

- 目录 mtime；
- `.ehviewer` 长度 + mtime；
- `.thumb` 长度 + mtime；
- 页文件数量、长度总和、最大 mtime、最大页号；
- 页数、缺页区间、扩展名统计、封面路径、gid 等派生信息。

任一签名变化即重新扫描该目录。该设计可捕获“目录 mtime 不变但页文件被原地补写/截断”的情况。缓存损坏时移到 `*.corrupt-<时间>` 并回退为空缓存。

## 8. 实测分布（2026-11-08）

| 项目 | 数值 |
| --- | --- |
| 画廊 | 6,327 |
| 页 | 245,548 |
| 空目录 | 1 |
| 缺页画廊 | 27（39 个区间） |
| 0 字节页画廊 | 13 |
| 无 `.ehviewer` | 162 |
| 无 `.thumb` | 256 |
| 重复 gid | 2 个目录 |
| 无 gid 前缀但 header 有 gid | 5 |
| 最大页数 | 1,728 |
| 扩展名 | jpg 187,705 / webp 56,615 / png 300 / gif 63 |
