using Vellum;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Vellum.Tests;

public class LazyOcrEngineTests
{
    [Fact]
    public void AddVellumOcr_RegistersSingleton_WithoutTouchingNativeLibrary()
    {
        // Point at a directory that definitely doesn't contain the screen-ai DLL —
        // registration must still succeed, and the engine must still be "not ready".
        var services = new ServiceCollection();
        services.AddVellumOcr(o =>
        {
            o.ModelDir = Path.Combine(Path.GetTempPath(), "__vellum_missing_" + Guid.NewGuid());
            o.AutoDownload = false;
        });

        using var sp = services.BuildServiceProvider();
        var engine = sp.GetRequiredService<IOcrEngine>();

        Assert.NotNull(engine);
        Assert.False(engine.IsReady);
    }

    [Fact]
    public void Ocr_WithoutBinariesAndAutoDownloadFalse_ThrowsFileNotFound_OnFirstUseNotOnStartup()
    {
        var options = new VellumOptions
        {
            ModelDir = Path.Combine(Path.GetTempPath(), "__vellum_missing_" + Guid.NewGuid()),
            AutoDownload = false,
        };

        using var engine = new LazyOcrEngine(options);

        // Construction succeeded — the whole point of the lazy wrapper.
        Assert.False(engine.IsReady);

        // First real call is where it blows up.
        var ex = Assert.Throws<FileNotFoundException>(() => engine.Ocr("doesnt-matter.jpg"));
        Assert.Contains("was not found there", ex.Message);
    }

    [Fact]
    public void Version_ForcesInitAndThusThrowsWhenMissing()
    {
        var options = new VellumOptions
        {
            ModelDir = Path.Combine(Path.GetTempPath(), "__vellum_missing_" + Guid.NewGuid()),
        };
        using var engine = new LazyOcrEngine(options);

        Assert.Throws<FileNotFoundException>(() => _ = engine.Version);
    }

    [Fact]
    public async Task EnsureReadyAsync_WithoutBinaries_ThrowsFileNotFound()
    {
        var options = new VellumOptions
        {
            ModelDir = Path.Combine(Path.GetTempPath(), "__vellum_missing_" + Guid.NewGuid()),
        };
        using var engine = new LazyOcrEngine(options);

        await Assert.ThrowsAsync<FileNotFoundException>(() => engine.EnsureReadyAsync());
    }
}
