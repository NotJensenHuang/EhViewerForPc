using System.Runtime.CompilerServices;

// Tests exercise the source-generated JSON contexts directly, so the AOT-safe
// persistence path is covered instead of only reflection-based serialization.
[assembly: InternalsVisibleTo("EhViewerForPc.Core.Tests")]
