// Run test collections sequentially (issue #106). The ffmpeg-touching fixtures
// and the WebApplicationFactory boot share global process state (Xabe's
// FFmpeg.ExecutablesPath, POSTGRES_* env vars); serial collections remove the
// race. The suite is small (~20s), so the lost parallelism is negligible.
[assembly: Xunit.CollectionBehavior(DisableTestParallelization = true)]
