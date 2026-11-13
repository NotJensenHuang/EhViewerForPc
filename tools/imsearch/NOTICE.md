# Source and modifications

The managed backend derives from [lolishinshi/imsearch](https://github.com/lolishinshi/imsearch), pinned at `2bf2546822ca2b9b1522d868eb9f21ba441f20d2`, version 3.0.2. The original GPL-3.0 text is preserved in [LICENSE.imsearch](LICENSE.imsearch).

The ORB algorithm and 256-pair sampling pattern derive from `orb-slam3-sys/orb-slam3/ORBextractor.cc` at that revision. The complete original GPL and Willow Garage BSD notices are preserved in [ORB-SLAM3-LICENSE.txt](ORB-SLAM3-LICENSE.txt). The port retains the original pattern; its table is generated from the pinned source, not from a replacement random pattern.

Modifications made on 2026-11-13: replaced Rust orchestration, Faiss/hnsw_rs adapters and native ORB calls with managed C#; added bounded imports, cancellation, explicit metadata/version checks, authenticated HTTP, safe empty results, checksummed Deflate inverted lists, resumable segments and a windowless Windows host. SkiaSharp and SQLite remain native codec/storage dependencies. No Rust code, executable or DLL is used at runtime.

Intentional compatibility differences:

- Index, HNSW and SQLite formats are C# version 1; existing Rust/Faiss/zstd indexes must be rebuilt from original images.
- Managed gray conversion, area/cubic/Lanczos interpolation, angle calculation and deterministic random choices may differ from OpenCV/Faiss. Byte-identical descriptors or rankings across languages have not been established. Features from different profiles cannot be mixed.
- Default import policy appends aliases; `--ignore` and `--overwrite` retain the other upstream duplicate policies. Default minimum keypoints is 30 and nprobe is 16 for the reader use case.
- The backend stores direct image identities in IVF records instead of assuming contiguous image IDs. The dHash HNSW is reconstructed from persisted hashes when opening a writer.
- Authentication tokens and optional Prometheus Basic credentials use environment variables and are never printed. `--prometheus-push` is opt-in; its payload contains metrics only. HTTP remains loopback-only.
- `clean` is noninteractive for background operation; `--force` remains accepted. The upstream unused `--index-name` parameter is not emulated. JSON CLI output is structured image/alias records; table output remains available.
- Default training budget is 4,000,000 descriptors; raising `--training-vector-limit` explicitly allows up to 64,000,000. This is an allocation bound, not a claim that large-dataset performance has been tested.

BLAKE3 unkeyed hashing follows the [official algorithm](https://github.com/BLAKE3-team/BLAKE3); the small fixture file records the first 32 bytes of every official [test vector](https://github.com/BLAKE3-team/BLAKE3/blob/master/test_vectors/test_vectors.json). No native/Rust BLAKE3 package is used.

This notice preserves provenance and changes. It does not assign a license to unrelated pre-existing reader code or establish distribution clearance for the entire application.
