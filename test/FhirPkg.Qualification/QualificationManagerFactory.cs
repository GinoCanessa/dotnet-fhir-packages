// Copyright (c) Gino Canessa. Licensed under the MIT License.

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;

namespace FhirPkg.Qualification;

internal sealed class QualificationManagerFactory
{
    private readonly object _sync = new();
    private readonly List<ServiceProvider> _providers = [];
    private readonly FixtureRoutes? _routes;
    private readonly FixtureRequestLog? _log;

    internal QualificationManagerFactory()
    {
    }

    internal QualificationManagerFactory(
        FixtureRoutes routes,
        FixtureRequestLog log)
    {
        ArgumentNullException.ThrowIfNull(routes);
        ArgumentNullException.ThrowIfNull(log);
        _routes = routes;
        _log = log;
    }

    internal FhirPackageManager CreateRegistryManager(
        FhirPackageManagerOptions options)
    {
        if (_routes is null)
            return new FhirPackageManager(options);

        ServiceProvider provider = CreateServiceProvider(options);
        lock (_sync)
            _providers.Add(provider);

        IFhirPackageManager manager =
            provider.GetRequiredService<IFhirPackageManager>();
        return manager as FhirPackageManager
            ?? throw new QualificationInvariantException(
                $"The DI-built package manager was {manager.GetType().Name}, not {nameof(FhirPackageManager)}.");
    }

    internal ServiceProvider CreateServiceProvider(
        FhirPackageManagerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (_routes is not FixtureRoutes routes
            || _log is not FixtureRequestLog log)
        {
            throw new QualificationInvariantException(
                "Live qualification builds no fixture registry container.");
        }

        ServiceCollection services = new();
        services.AddSingleton(options);
        services.AddFhirPackageManagement();
        services.PostConfigureAll<HttpClientFactoryOptions>(factoryOptions =>
            factoryOptions.HttpMessageHandlerBuilderActions.Add(builder =>
                builder.PrimaryHandler = new FixtureRegistryHandler(
                    routes,
                    log)));
        return services.BuildServiceProvider();
    }

    internal void DisposeTracked()
    {
        ServiceProvider[] providers;
        lock (_sync)
        {
            providers = _providers.ToArray();
            _providers.Clear();
        }

        foreach (ServiceProvider provider in providers)
            provider.Dispose();
    }
}
