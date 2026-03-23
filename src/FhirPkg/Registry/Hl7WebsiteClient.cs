// Copyright (c) Gino Canessa. Licensed under the MIT License. See LICENSE in the project root.

using System.Net;
using FhirPkg.Models;
using Microsoft.Extensions.Logging;

namespace FhirPkg.Registry;

/// <summary>
/// Registry client that resolves and downloads core FHIR packages from the HL7 website
/// (<c>https://hl7.org/fhir/</c>) as a last-resort fallback.
/// </summary>
/// <remarks>
/// <para>
/// This client only supports <see cref="PackageNameType.CoreFull"/> packages with
/// <see cref="VersionType.Exact"/> or <see cref="VersionType.Latest"/> version specifiers.
/// </para>
/// <para>
/// URL pattern: <c>{baseUrl}/{releasePath}/{packageName}.tgz</c> where <c>releasePath</c>
/// is derived from the FHIR release embedded in the package name (e.g., <c>R4</c>, <c>STU3</c>).
/// </para>
/// </remarks>
public sealed class Hl7WebsiteClient : RegistryClientBase, IRegistryClient
{
    /// <summary>
    /// Maps <see cref="FhirRelease"/> enum values to the URL path segment used on the HL7 website.
    /// </summary>
    private static readonly Dictionary<FhirRelease, string> ReleasePathMap = new()
    {
        [FhirRelease.DSTU2] = "DSTU2",
        [FhirRelease.STU3] = "STU3",
        [FhirRelease.R4] = "R4",
        [FhirRelease.R4B] = "R4B",
        [FhirRelease.R5] = "R5",
        [FhirRelease.R6] = "R6",
    };

    /// <summary>
    /// Maps the release segment in a core package name to a <see cref="FhirRelease"/>.
    /// Package names follow the pattern <c>hl7.fhir.{release}.{type}</c>.
    /// </summary>
    private static readonly Dictionary<string, FhirRelease> PackageSegmentToRelease =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["r2"] = FhirRelease.DSTU2,
            ["dstu2"] = FhirRelease.DSTU2,
            ["r3"] = FhirRelease.STU3,
            ["stu3"] = FhirRelease.STU3,
            ["r4"] = FhirRelease.R4,
            ["r4b"] = FhirRelease.R4B,
            ["r5"] = FhirRelease.R5,
            ["r6"] = FhirRelease.R6,
        };

    /// <summary>
    /// Initialises a new <see cref="Hl7WebsiteClient"/>.
    /// </summary>
    /// <param name="httpClient">The HTTP client to use for requests.</param>
    /// <param name="endpoint">The HL7 website endpoint (typically <see cref="RegistryEndpoint.Hl7Website"/>).</param>
    /// <param name="logger">The logger instance.</param>
    public Hl7WebsiteClient(
        HttpClient httpClient,
        RegistryEndpoint endpoint,
        ILogger<Hl7WebsiteClient> logger)
        : base(httpClient, endpoint, logger)
    {
    }

    // ── IRegistryClient properties ──────────────────────────────────────

    /// <inheritdoc />
    public override IReadOnlyList<PackageNameType> SupportedNameTypes { get; } =
    [
        PackageNameType.CoreFull,
    ];

    /// <inheritdoc />
    public override IReadOnlyList<VersionType> SupportedVersionTypes { get; } =
    [
        VersionType.Exact,
        VersionType.Latest,
    ];

    // ── IRegistryClient methods ─────────────────────────────────────────

    /// <inheritdoc />
    /// <remarks>
    /// The HL7 website does not support catalog search. Returns an empty list.
    /// </remarks>
    public override Task<IReadOnlyList<CatalogEntry>> SearchAsync(
        PackageSearchCriteria criteria, CancellationToken cancellationToken = default)
    {
        Logger.LogDebug("SearchAsync is not supported for the HL7 website; returning empty list");
        return Task.FromResult<IReadOnlyList<CatalogEntry>>([]);
    }

    /// <inheritdoc />
    /// <remarks>
    /// The HL7 website does not support package listings. Returns <see langword="null"/>.
    /// </remarks>
    public override Task<PackageListing?> GetPackageListingAsync(
        string packageId, CancellationToken cancellationToken = default)
    {
        Logger.LogDebug("GetPackageListingAsync is not supported for the HL7 website; returning null");
        return Task.FromResult<PackageListing?>(null);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Constructs the tarball URL from the <see cref="FhirRelease"/> inferred from the package name
    /// (or provided via <see cref="VersionResolveOptions.FhirRelease"/>).
    /// The URL pattern is <c>{baseUrl}/{release}/{packageName}.tgz</c>.
    /// </remarks>
    public override Task<ResolvedDirective?> ResolveAsync(
        PackageDirective directive,
        VersionResolveOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(directive);

        Logger.LogInformation(
            "Resolving {PackageId} via HL7 website fallback", directive.PackageId);

        // Determine the FHIR release: explicit option takes priority over inference.
        var release = options?.FhirRelease ?? InferReleaseFromPackageName(directive.PackageId);

        if (release is null)
        {
            Logger.LogWarning(
                "Cannot infer FHIR release from package name '{PackageId}' and no explicit release was provided",
                directive.PackageId);
            return Task.FromResult<ResolvedDirective?>(null);
        }

        if (!ReleasePathMap.TryGetValue(release.Value, out var releasePath))
        {
            Logger.LogWarning(
                "No URL path mapping found for FHIR release {Release}", release.Value);
            return Task.FromResult<ResolvedDirective?>(null);
        }

        var tarballUrl = $"{BaseUrl}/{releasePath}/{Uri.EscapeDataString(directive.PackageId)}.tgz";

        Logger.LogInformation(
            "Resolved {PackageId} via HL7 website → {TarballUrl}",
            directive.PackageId, tarballUrl);

        var result = new ResolvedDirective
        {
            Reference = directive.ToReference(),
            TarballUri = new Uri(tarballUrl),
            SourceRegistry = Endpoint,
        };

        return Task.FromResult<ResolvedDirective?>(result);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Performs a simple HTTP GET on the tarball URL. The caller must dispose the result.
    /// </remarks>
    public override async Task<PackageDownloadResult?> DownloadAsync(
        ResolvedDirective resolved, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(resolved);

        var url = resolved.TarballUri.ToString();
        Logger.LogInformation("Downloading core package from HL7 website: {Url}", url);

        var response = await GetResponseAsync(url, cancellationToken).ConfigureAwait(false);

        if (response is null)
        {
            Logger.LogWarning("Core package not found at {Url}", url);
            return null;
        }

        try
        {
            return await CreateDownloadResultAsync(response, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            response.Dispose();
            throw;
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// Publishing to the HL7 website is not supported. Always returns a failure result.
    /// </remarks>
    public override Task<PublishResult> PublishAsync(
        PackageReference reference, Stream tarballStream, CancellationToken cancellationToken = default)
    {
        Logger.LogWarning("Publishing to the HL7 website is not supported");

        return Task.FromResult(new PublishResult
        {
            Success = false,
            StatusCode = HttpStatusCode.MethodNotAllowed,
            Message = "The HL7 website does not support package publishing.",
        });
    }

    // ── Private helpers ─────────────────────────────────────────────────

    /// <summary>
    /// Infers the <see cref="FhirRelease"/> from a core package name such as
    /// <c>hl7.fhir.r4.core</c> by extracting the third dot-separated segment.
    /// </summary>
    private static FhirRelease? InferReleaseFromPackageName(string packageId)
    {
        var segments = packageId.Split('.');

        // Core package names follow the pattern hl7.fhir.{release}.{type}
        if (segments.Length >= 3 &&
            PackageSegmentToRelease.TryGetValue(segments[2], out var release))
        {
            return release;
        }

        return null;
    }
}
