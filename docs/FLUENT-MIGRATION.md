# Fluent 迁移与验证

日期：2026-11-12。运行环境：Windows x64、.NET SDK 10.0.402、Avalonia 12.1.6、FluentAvaloniaUI 3.0.2。

## 行为

- 使用 `FANavigationView` 与 `FAFrame` 管理导航和返回/前进，主页面保留实例。历史最多保留 24 项，失去导航引用的详情和阅读器释放资源。
- 标签搜索显式导航，即使在线浏览已被选中，也会离开详情。返回恢复来源详情及侧栏选择。
- 在线与本地共用 `GalleryDetailsView`。旧 `.ehviewer` 不含完整标签，因此读取其 token 补充在线元数据，并保存到本机作品缓存；已有缓存可离线显示。
- 启用下载目录入库和自动更新后，首张图片保存即入库，后续变化按秒合并，只扫描变化的画廊。缺页副本可阅读，当前可读末页不会将整本标记为读完。
- 标准控件由 Fluent 主题管理；删除重复说明，保留错误与缺页状态。窄详情缩小封面并优先展示标签；折叠侧栏隐藏账号区。

## 验证

```powershell
dotnet test tests/EhViewerReader.Core.Tests/EhViewerReader.Core.Tests.csproj -c Debug
dotnet publish src/EhViewerReader/EhViewerReader.csproj -c Release -r win-x64 -p:PublishAot=true -o .qa/fluent-aot-final
.qa/fluent-aot-final/EhViewerReader.exe --navigation-regression --data-dir .qa/aot-final-navigation-data --out .qa/aot-final-navigation
.qa/fluent-aot-final/EhViewerReader.exe --ui-smoke --data-dir .qa/aot-final-smoke-data --out .qa/aot-final-smoke
```

- 116 项测试通过，覆盖缓存标签、token 缓存以及不完整作品的阅读进度。
- NativeAOT 产物的 18 项交互和主题检查通过；包括标签跳转、原生返回/前进、共享控件绑定、窄窗标签首屏可见、增量入库和主题资源。
- NativeAOT 全应用烟测通过，`ui-smoke.json` 的 `passed` 为 `true`，退出码为 0。覆盖六个主页面、阅读器模式、沉浸、动图、多数据源、主题、窄窗、侧栏折叠和下载目录迁移。
- 界面评审的首轮四项修正已确认；用户指出页头留白后追加 Terra 全页面检查及修复，见 UI-REVIEW-2026-11-12.md。
- 使用 `lvt` 对 NativeAOT 程序执行真实 UI Automation 点击：设置 → 下载 → 原生返回；内容恢复为设置页，侧栏选择同步恢复。验证进程已关闭。

原始截图和结果位于 `.qa/aot-final-navigation/`、`.qa/aot-final-smoke/`，隔离数据和产物不提交。

## 范围与限制

烟测使用合成图片和隔离数据，网络访问被定向到本机，不使用真实账号。下载入库测试通过实际下载事件和已落盘文件驱动，未执行真实站点的整本下载。

NativeAOT 发布成功，但依赖仍报告裁剪/AOT 警告：FluentAvalonia `FAFrame.SourcePageType.get` 的 IL2073，以及 Avalonia `TypeHelper.GetPropertyOrIndexer` 的 IL2070/IL3050。应用使用显式页面工厂、Frame 自身历史上下文及编译绑定；上述发布产物已实际运行验证，未通过全局抑制隐藏这些警告。

FluentAvalonia 3.0 的 Frame 在无缓存历史恢复时会调用类型工厂。适配器从 Frame 当前 BackStack/ForwardStack 的 Context 恢复实例，避免运行时反射构造页面；应用不维护第二份页面历史。
