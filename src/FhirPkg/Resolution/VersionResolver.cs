// Copyright (c) Gino Canessa. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using Microsoft.Extensions.Logging;

using FhirPkg.Models;
using FhirPkg.Registry;

namespace FhirPkg.Resolution;

/// <summary>
/// Resolves version specifiers (exact, wildcard, latest, range) to concrete <see cref="FhirSemVer"/> versions
/// by querying the FHIR package registry for available versions.
/// </summary>
/// <remarks>
/// <para>
/// This resolver handles the following version types:
/// <list type="bullet">
///   <item><description><see cref="VersionType.Exact"/>: Direct lookup in the registry's version map.</description></item>
///   <item><description><see cref="VersionType.Latest"/>: Uses the dist-tags "latest" from the package listing.</description></item>
///   <item><description><see cref="VersionType.Wildcard"/>: Parses all available versions and calls <see cref="FhirSemVer.MaxSatisfying"/>.</description></item>
///   <item><description><see cref="VersionType.Range"/>: Parses all available versions and calls <see cref="FhirSemVer.SatisfyingRange"/>.</description></item>
///   <item><description><see cref="VersionType.CiBuild"/> / <see cref="VersionType.CiBuildBranch"/>: Returns <c>null</c> (delegated to the CI build client by the orchestrator).</description></item>
/// </list>
/// </para>
/// <para>
/// Pre-release versions are included or excluded based on <see cref="VersionResolveOptions.AllowPreRelease"/>.
/// </para>
/// </remarks>
public class VersionResolver : IVersionResolver
{
    private readonly IRegistryClient _registryClient;
    private readonly ILogger _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="VersionResolver"/> class.
    /// </summary>
    /// <param name="registryClient">
    /// The registry client used to query available package versions.
    /// May be a <see cref="RedundantRegistryClient"/> wrapping multiple registries.
    /// </param>
    /// <param name="logger">Logger for diagnostic output.</param>
    public VersionResolver(IRegistryClient registryClient, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(registryClient);
        ArgumentNullException.ThrowIfNull(logger);

        _registryClient = registryClient;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<FhirSemVer?> ResolveVersionAsync(
        string packageId,
        string versionSpecifier,
        VersionResolveOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(packageId);
        ArgumentNullException.ThrowIfNull(versionSpecifier);

        VersionType versionType = DirectiveParser.ClassifyVersion(versionSpecifier);

        // CI builds are handled externally by the orchestrator
        if (versionType is VersionType.CiBuild or VersionType.CiBuildBranch)
        {
            _logger.LogDebug(
                "Version specifier '{VersionSpecifier}' for '{PackageId}' is a CI build; delegating to CI build client.",
                versionSpecifier, packageId);
            return null;
        }

        // Local builds cannot be resolved from a registry
        if (versionType == VersionType.LocalBuild)
        {
            _logger.LogDebug(
                "Version specifier '{VersionSpecifier}' for '{PackageId}' is a local build; cannot resolve from registry.",
                versionSpecifier, packageId);
            return null;
        }

        PackageListing? listing = await _registryClient.GetPackageListingAsync(packageId, cancellationToken)
            .ConfigureAwait(false);

        if (listing is null)
        {
            _logger.LogWarning("Package '{PackageId}' was not found in any configured registry.", packageId);
            return null;
        }

        PackageDirective directive =
            PackageDirective.Parse($"{packageId}#{versionSpecifier}");
        return PackageVersionSelector.Select(directive, listing, options)?.Version;
    }

    /// <inheritdoc />
    public FhirSemVer? ResolveVersion(
        string versionSpecifier,
        IEnumerable<FhirSemVer> availableVersions,
        VersionResolveOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(versionSpecifier);
        ArgumentNullException.ThrowIfNull(availableVersions);

        return PackageVersionSelector.Select(
            "unknown.package",
            versionSpecifier,
            availableVersions,
            options)?.Version;
    }

}
