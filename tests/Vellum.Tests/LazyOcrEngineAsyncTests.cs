using Vellum;
using Xunit;

namespace Vellum.Tests;

public class LazyOcrEngineAsyncTests
{
    [Fact]
    public async Task OcrAsync_WithoutBinaries_ThrowsFileNotFound_Async()
    {
        var options = new VellumOptions
        {
            ModelDir = Path.Combine(Path.GetTempPath(), "__vellum_missing_" + Guid.NewGuid()),
        };
        using var engine = new LazyOcrEngine(options);

        await Assert.ThrowsAsync<FileNotFoundException>(
            () => engine.OcrAsync("whatever.jpg"));
    }

    [Fact]
    public async Task OcrAsync_RespectsCancellationBeforeInit()
    {
        var options = new VellumOptions
        {
            ModelDir = Path.Combine(Path.GetTempPath(), "__vellum_missing_" + Guid.NewGuid()),
        };
        using var engine = new LazyOcrEngine(options);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        // WaitAsync throws TaskCanceledException (derives from OperationCanceledException);
        // accept either here for robustness against implementation details.
        var ex = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => engine.OcrAsync("whatever.jpg", ct: cts.Token));
        Assert.Equal(cts.Token, ex.CancellationToken);
    }

    [Fact]
    public async Task EnsureReadyAsync_IsIdempotent()
    {
        // EnsureReadyAsync should be safe to call many times concurrently.
        // We can't actually succeed without binaries, but we CAN verify that
        // N concurrent callers all propagate the same FileNotFoundException
        // instead of corrupting _initLock state (e.g. releasing an unacquired
        // semaphore would surface as SemaphoreFullException).
        var options = new VellumOptions
        {
            ModelDir = Path.Combine(Path.GetTempPath(), "__vellum_missing_" + Guid.NewGuid()),
        };
        using var engine = new LazyOcrEngine(options);

        var tasks = Enumerable.Range(0, 8)
            .Select(_ => Task.Run(async () =>
            {
                try { await engine.EnsureReadyAsync(); return (Exception?)null; }
                catch (Exception ex) { return ex; }
            }))
            .ToArray();

        var results = await Task.WhenAll(tasks);
        Assert.All(results, ex => Assert.IsType<FileNotFoundException>(ex));
    }
}
