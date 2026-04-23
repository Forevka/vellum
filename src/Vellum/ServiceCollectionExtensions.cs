using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Vellum;

/// <summary>
/// DI extension methods for wiring the Vellum OCR engine into an ASP.NET Core /
/// generic-host application.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Register <see cref="IOcrEngine"/> as a singleton backed by
    /// <see cref="LazyOcrEngine"/>.  The native library is not touched until the
    /// first OCR call, so application startup succeeds even when the screen-ai
    /// component isn't installed yet.
    /// </summary>
    /// <example>
    /// <code>
    /// builder.Services.AddVellumOcr(options =>
    /// {
    ///     options.LightMode = true;
    ///     options.AutoDownload = true;   // copy from Chrome on first use
    /// });
    /// </code>
    /// </example>
    public static IServiceCollection AddVellumOcr(
        this IServiceCollection services,
        Action<VellumOptions>? configure = null)
    {
        var options = new VellumOptions();
        configure?.Invoke(options);

        services.AddSingleton(options);
        services.AddSingleton<IOcrEngine>(sp =>
            new LazyOcrEngine(
                sp.GetRequiredService<VellumOptions>(),
                sp.GetService<ILoggerFactory>()));

        return services;
    }
}
