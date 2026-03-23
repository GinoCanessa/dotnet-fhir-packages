// Copyright (c) Gino Canessa. Licensed under the MIT License.

namespace FhirPkg.Cli;

/// <summary>
/// Centralised factory for creating <see cref="FhirPackageManager"/> instances in CLI commands.
/// Defaults to direct construction; can be overridden via <see cref="FactoryOverride"/>
/// for unit testing or custom DI scenarios.
/// </summary>
internal static class ManagerFactory
{
    /// <summary>
    /// Optional override for the factory function. When set, <see cref="Create"/> delegates
    /// to this function instead of calling the <see cref="FhirPackageManager"/> constructor directly.
    /// Set to <c>null</c> to restore the default behaviour.
    /// </summary>
    internal static Func<FhirPackageManagerOptions, FhirPackageManager>? FactoryOverride { get; set; }

    /// <summary>
    /// Creates a new <see cref="FhirPackageManager"/> for the given options.
    /// </summary>
    /// <param name="options">The manager configuration options.</param>
    /// <returns>A configured <see cref="FhirPackageManager"/> instance.</returns>
    public static FhirPackageManager Create(FhirPackageManagerOptions options) =>
        FactoryOverride?.Invoke(options) ?? new FhirPackageManager(options);
}
