# C# imsearch backend

This is the native Windows C# migration used by the local-image-search plan. The backend runs independently and is also connected to the reader's local-library toolbar, settings, search document and exact-copy/page navigation. See [plan](../../docs/PLAN-LOCAL-IMAGE-SEARCH-2026-11-12.md), [status](../../docs/IMPLEMENTATION-LOCAL-IMAGE-SEARCH-STATUS.md), and [source notices](NOTICE.md).

Requires the repository's .NET 11 SDK for building. It does not require Rust, Cargo, Docker, WSL, OpenCV or Faiss. Runtime codec/storage dependencies are SkiaSharp and SQLite. Existing Rust binary indexes cannot be opened: use an empty C# data directory and reimport original pages.

From the repository root, build and verify with no visible windows:

```powershell
pwsh -NoProfile -File tools/imsearch/Build-Backend.ps1
pwsh -NoProfile -File tools/imsearch/Test-Backend.ps1
pwsh -NoProfile -File tools/imsearch/Build-Backend.ps1 -Aot
pwsh -NoProfile -File tools/imsearch/Test-Backend.ps1 -HostPath artifacts/image-search-host-aot/EhViewerForPc.ImageSearch.Host.exe
```

The helpers use `ProcessStartInfo.ArgumentList`, `UseShellExecute=false`, `CreateNoWindow=true`, below-normal priority and redirected stdout/stderr. Every test run uses a new `.qa/image-search/verification/<id>` directory. A verification failure throws and records `verification.json`; it never substitutes a mock for the real backend.

Independent tests:

```powershell
dotnet test tests/EhViewerForPc.ImageSearch.Tests/EhViewerForPc.ImageSearch.Tests.csproj -c Debug
```

The host's six commands are `add`, `train`, `build`, `search`, `server`, `clean`; `--help` explains options. Use the managed DLL with `dotnet` for terminal diagnostics, or launch the WinExe via the shared process helper for windowless work. No command starts Avalonia or the reader's single-instance handler.

`add` accepts a directory or tar, requires `--report-json` outside the source directory, and never writes to source pages. It supports suffix filtering, regex rewrite, BLAKE3/dHash, duplicate policies, bounded workers and per-entry terminal statuses. Exit 0 means complete without data failures, 2 means a complete report with failed inputs (or unavailable prerequisite), and 1 means fatal failure. Check both exit code and report.

`train` requires a center count and training-image limit. Two-level training enforces D>=30*K. The default 4M-vector bound can be raised explicitly; it does not imply full-library performance. Re-training a published generation is rejected: create a new backend directory instead.

`build` supports bounded segments and `--stop-before-merge`/`--stop-after-segments`. Rerun it to resume. `clean` removes cached descriptors only; disk search still works. Cleaning unindexed caches with `--all` may require reimporting their source pages before building.

`server` defaults to loopback port 8000 and requires `IMSEARCH_AUTH_TOKEN` containing at least 43 encoded characters representing a random token. All HTTP routes authenticate: healthz, search, metrics, docs and openapi.json. Image queries require a 32-hex request_id; generation/profile values bind to the loaded index. Single-query feature overrides must equal the stored profile. No token is written to logs or settings.

Prometheus push is disabled by default. An explicit `--prometheus-push` enables metrics-only push; optional Basic credentials come from `IMSEARCH_PROMETHEUS_AUTH`. Verification uses only a private loopback receiver.

The generated 64-image probe verifies import, two-level training, paused-build recovery, exact/cropped search, empty results, idempotent import/build, clean/reload and read-only source metadata. It is not a real-comic recall benchmark. The user authorized formal UI/navigation integration while E07 waits for a real library.

Application use: Settings → local library → enable image retrieval, select sources and save. Open image search from the library toolbar and build the index. Select, drop or paste a local image, optionally drag a crop rectangle, search, then use “从此页阅读”. The selected source copy and original page number override history. Returning keeps the query and results.

Application updates copy metadata snapshots, share content-addressed descriptors in `features.db`, and prefer hard links for immutable search files. Completed pages and committed segments resume after interruption; explicit cancellation stays paused until manually continued. Legacy unfinished generations can be adopted and their descriptor blobs migrate before unused directories are reclaimed. Publication retains the current snapshot, pending job and active-query snapshots; the backup pointer no longer retains an entire prior generation. Cleanup deletes only marked application-owned generations with checked paths and no reparse points. The standalone Host keeps its self-contained backend format unless the shared-cache API is explicitly used. See [storage and recovery](../../docs/索引共享存储与断点恢复-2026-11-13.md).

UI regression is entirely in-memory with Avalonia Headless and never creates a Win32 window:

```powershell
dotnet test tests/EhViewerForPc.ImageSearch.Ui.Tests/EhViewerForPc.ImageSearch.Ui.Tests.csproj -c Debug -p:PublishAot=false
```

Run projects referencing the reader sequentially because Avalonia's XAML compilation shares project output files.

The published reader also accepts `--image-search-probe-root <new-empty-directory> --out <report.json>`. This mode runs the real app service, incremental snapshots, mapping and explicit-page reader models before Avalonia startup or the single-instance check. Launch its WinExe using the shared hidden process helper. It never scans user folders or opens an application window.
