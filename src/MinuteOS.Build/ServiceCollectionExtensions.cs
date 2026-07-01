using Microsoft.Extensions.DependencyInjection;
using MinuteOS.Build.Graph;

namespace MinuteOS.Build;

/// <summary>
/// DI registration for the minuteOS build system. Register it and resolve
/// <see cref="IBuildRunner"/> to drive builds programmatically:
///
/// <code>
/// var provider = new ServiceCollection()
///     .AddLogging()
///     .AddMinuteosBuild()
///     .BuildServiceProvider();
///
/// var runner = provider.GetRequiredService&lt;IBuildRunner&gt;();
/// var project = ProjectConfig.Load(root);
/// var config  = BuildConfiguration.Create(project, "host", root);
/// await runner.BuildAsync(config);
/// </code>
/// </summary>
public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddMinuteosBuild(this IServiceCollection services)
    {
        services.AddSingleton<IBuildRunner, GraphBuildRunner>();
        return services;
    }
}
