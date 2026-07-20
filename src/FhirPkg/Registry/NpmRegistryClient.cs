// Copyright (c) Gino Canessa. Licensed under the MIT License. See LICENSE in the project root.

using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using FhirPkg.Cache;
using FhirPkg.Installation;
using FhirPkg.Models;
using FhirPkg.Utilities;
using Microsoft.Extensions.Logging;

namespace FhirPkg.Registry;

/// <summary>
/// Registry client for standard NPM registries (e.g., <c>registry.npmjs.org</c> or
/// private Verdaccio / Artifactory instances).
/// </summary>
/// <remarks>
/// Supports the standard NPM registry protocol for searching, listing, resolving, downloading,
/// and publishing packages. FHIR-specific version resolution (wildcard, range) is supported
/// through <see cref="FhirSemVer"/>.
/// </remarks>
public sealed class NpmRegistryClient : RegistryClientBase, IRegistryClient
{
    private const int CopyBufferSize = 81_920;
    private const long MaxPublishManifestBytes = 1024 * 1024;
    private readonly PackageInstallLimits _installLimits;
    private readonly Func<string> _createTempDirectory;

    /// <summary>
    /// Initialises a new <see cref="NpmRegistryClient"/>.
    /// </summary>
    /// <param name="httpClient">The HTTP client to use for requests.</param>
    /// <param name="endpoint">The NPM registry endpoint.</param>
    /// <param name="logger">The logger instance.</param>
    public NpmRegistryClient(
        HttpClient httpClient,
        RegistryEndpoint endpoint,
        ILogger<NpmRegistryClient> logger)
        : this(
            RegistryHttpTransport.CreateUnverified(httpClient),
            endpoint,
            logger,
            PackageInstallLimits.FromEnvironment())
    {
    }

    internal NpmRegistryClient(
        RegistryHttpTransport transport,
        RegistryEndpoint endpoint,
        ILogger<NpmRegistryClient> logger)
        : this(
            transport,
            endpoint,
            logger,
            PackageInstallLimits.FromEnvironment())
    {
    }

    internal NpmRegistryClient(
        RegistryHttpTransport transport,
        RegistryEndpoint endpoint,
        ILogger<NpmRegistryClient> logger,
        PackageInstallLimits installLimits,
        Func<string>? createTempDirectory = null)
        : base(transport, endpoint, logger)
    {
        ArgumentNullException.ThrowIfNull(installLimits);

        _installLimits = PackageInstallLimits.ResolveManager(installLimits);
        _createTempDirectory = createTempDirectory
            ?? (() => TempDirectory.Create("fhirpkg-npm-publish"));
    }

    // ── IRegistryClient properties ──────────────────────────────────────

    /// <inheritdoc />
    public override IReadOnlyList<PackageNameType> SupportedNameTypes { get; } =
    [
        PackageNameType.CoreFull,
        PackageNameType.CorePartial,
        PackageNameType.GuideWithFhirSuffix,
        PackageNameType.GuideWithoutSuffix,
        PackageNameType.NonHl7Guide,
    ];

    /// <inheritdoc />
    public override IReadOnlyList<VersionType> SupportedVersionTypes { get; } =
    [
        VersionType.Exact,
        VersionType.Latest,
        VersionType.Wildcard,
        VersionType.Range,
    ];

    // ── IRegistryClient methods ─────────────────────────────────────────

    /// <inheritdoc />
    /// <remarks>
    /// Calls the standard NPM search endpoint: <c>GET {baseUrl}/-/v1/search?text={name}</c>
    /// and converts the results to <see cref="CatalogEntry"/> instances.
    /// </remarks>
    public override async Task<IReadOnlyList<CatalogEntry>> SearchAsync(
        PackageSearchCriteria criteria, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(criteria);

        if (string.IsNullOrWhiteSpace(criteria.Name))
        {
            Logger.LogDebug("No search name provided for NPM search; returning empty list");
            return [];
        }

        string url = $"{BaseUrl}/-/v1/search?text={Uri.EscapeDataString(criteria.Name)}";

        Logger.LogInformation("Searching NPM registry at {Url}", url);

        NpmSearchResponse? response = await GetJsonAsync<NpmSearchResponse>(url, cancellationToken)
            .ConfigureAwait(false);

        if (response?.Objects is null or { Count: 0 })
        {
            Logger.LogDebug("NPM search returned no results");
            return [];
        }

        List<CatalogEntry> entries = response.Objects
            .Where(o => o.Package is not null)
            .Select(o => new CatalogEntry
            {
                Name = o.Package!.Name!,
                Description = o.Package.Description,
                Version = o.Package.Version,
            })
            .ToList();

        Logger.LogDebug("NPM search returned {Count} results", entries.Count);

        return entries.AsReadOnly();
    }

    /// <inheritdoc />
    /// <remarks>
    /// Calls <c>GET {baseUrl}/{packageId}</c> using the standard NPM package document format.
    /// </remarks>
    public override async Task<PackageListing?> GetPackageListingAsync(
        string packageId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packageId);

        string url = $"{BaseUrl}/{Uri.EscapeDataString(packageId)}";

        Logger.LogInformation("Fetching NPM package listing for {PackageId} from {Url}", packageId, url);

        PackageListing? listing = await GetJsonAsync<PackageListing>(url, cancellationToken)
            .ConfigureAwait(false);

        if (listing is not null)
        {
            listing = WithSourceProvenance(listing);
            Logger.LogDebug(
                "NPM package {PackageId} has {VersionCount} version(s), latest = {Latest}",
                packageId,
                listing.Versions?.Count ?? 0,
                listing.LatestVersion ?? "(none)");
        }

        return listing;
    }

    /// <inheritdoc />
    /// <remarks>
    /// Resolution follows the same strategy as <see cref="FhirNpmRegistryClient"/>:
    /// exact lookup, dist-tags for latest, <see cref="FhirSemVer.MaxSatisfying"/> for
    /// wildcards, and <see cref="FhirSemVer.SatisfyingRange"/> for ranges.
    /// </remarks>
    public override async Task<ResolvedDirective?> ResolveAsync(
        PackageDirective directive,
        VersionResolveOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(directive);

        Logger.LogInformation(
            "Resolving {PackageId} ({VersionType}: {Version}) on NPM registry",
            directive.PackageId, directive.VersionType, directive.RequestedVersion ?? "latest");

        PackageListing? listing = await GetPackageListingAsync(directive.PackageId, cancellationToken)
            .ConfigureAwait(false);

        if (listing?.Versions is null or { Count: 0 })
        {
            Logger.LogWarning("Package {PackageId} not found or has no versions on NPM", directive.PackageId);
            return null;
        }

        string? resolvedVersion = ResolveVersion(directive, listing, options);

        if (resolvedVersion is null)
        {
            Logger.LogWarning(
                "No NPM version satisfying {VersionType} '{Specifier}' found for {PackageId}",
                directive.VersionType, directive.RequestedVersion, directive.PackageId);
            return null;
        }

        if (!listing.Versions.TryGetValue(resolvedVersion, out PackageVersionInfo? versionInfo))
        {
            Logger.LogWarning(
                "Resolved version {Version} is not in the NPM versions dictionary for {PackageId}",
                resolvedVersion, directive.PackageId);
            return null;
        }

        string tarballUrl = versionInfo.Distribution?.TarballUrl
            ?? $"{BaseUrl}/{Uri.EscapeDataString(directive.PackageId)}" +
               $"/-/{Uri.EscapeDataString(directive.PackageId)}-{resolvedVersion}.tgz";

        Logger.LogInformation(
            "Resolved {PackageId} to version {Version} on NPM (tarball: {Tarball})",
            directive.PackageId, resolvedVersion, tarballUrl);

        return new ResolvedDirective
        {
            Reference = new PackageReference(directive.PackageId, resolvedVersion),
            TarballUri = new Uri(tarballUrl),
            ShaSum = versionInfo.Distribution?.ShaSum,
            Integrity = versionInfo.Distribution?.Integrity,
            SourceRegistry = Endpoint.ToProvenance(),
            SourceClient = this,
            PublicationDate = DateTime.TryParse(versionInfo.PublicationDate, out DateTime pubDate) ? pubDate : null,
        };
    }

    /// <inheritdoc />
    /// <remarks>
    /// Downloads the tarball from the URI in the resolved directive.
    /// The caller must dispose the returned <see cref="PackageDownloadResult"/>.
    /// </remarks>
    public override async Task<PackageDownloadResult?> DownloadAsync(
        ResolvedDirective resolved, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(resolved);

        string url = resolved.TarballUri.ToString();
        Logger.LogInformation("Downloading tarball from NPM: {Url}", url);

        HttpResponseMessage? response = await GetResponseAsync(url, cancellationToken).ConfigureAwait(false);

        if (response is null)
        {
            Logger.LogWarning("NPM tarball not found at {Url}", url);
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
    /// Sends a standard NPM package document to <c>PUT {baseUrl}/{name}</c>.
    /// Requires authentication to be configured on the endpoint.
    /// </remarks>
    public override async Task<PublishResult> PublishAsync(
        PackageReference reference, Stream tarballStream, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tarballStream);
        ArgumentException.ThrowIfNullOrWhiteSpace(reference.Name);
        ArgumentException.ThrowIfNullOrWhiteSpace(reference.Version);
        ValidateNpmPackageName(reference);
        ValidateNpmVersion(reference);

        string url = $"{BaseUrl}/{EncodeDocumentName(reference.Name)}";

        Logger.LogInformation(
            "Publishing {PackageId}@{Version} to NPM registry at {Url}",
            reference.Name, reference.Version, url);

        string? workspacePath = null;
        try
        {
            workspacePath = _createTempDirectory();
            NpmPublishPreparation preparation =
                await PreparePublishAsync(
                        reference,
                        tarballStream,
                        workspacePath,
                        cancellationToken)
                    .ConfigureAwait(false);
            using NpmPackumentContent content = CreatePackumentContent(
                reference,
                preparation);
            using HttpResponseMessage response = await PutContentAsync(
                    url,
                    content,
                    cancellationToken)
                .ConfigureAwait(false);

            Logger.LogInformation(
                "Published {PackageId}@{Version} to NPM successfully ({StatusCode})",
                reference.Name, reference.Version, (int)response.StatusCode);

            return new PublishResult
            {
                Success = true,
                StatusCode = response.StatusCode,
                Message = $"Package {reference.Name}@{reference.Version} published to NPM.",
            };
        }
        catch (HttpRequestException ex)
        {
            Logger.LogError(
                ex, "Failed to publish {PackageId}@{Version} to NPM", reference.Name, reference.Version);

            return new PublishResult
            {
                Success = false,
                StatusCode = ex.StatusCode ?? HttpStatusCode.InternalServerError,
                Message = ex.Message,
            };
        }
        finally
        {
            DeleteWorkspace(workspacePath);
        }
    }

    private async Task<NpmPublishPreparation> PreparePublishAsync(
        PackageReference reference,
        Stream tarballStream,
        string workspacePath,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(workspacePath);
        string archivePath = Path.Combine(workspacePath, "package.tgz");
        string extractionPath = Path.Combine(workspacePath, "extracted");
        long archiveLength = 0;

        using IncrementalHash sha1 =
            IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        using IncrementalHash sha512 =
            IncrementalHash.CreateHash(HashAlgorithmName.SHA512);
        await using (FileStream spool = new FileStream(
            archivePath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            CopyBufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            byte[] buffer = new byte[CopyBufferSize];
            while (true)
            {
                int read = await tarballStream.ReadAsync(
                        buffer.AsMemory(),
                        cancellationToken)
                    .ConfigureAwait(false);
                if (read == 0)
                    break;

                if (read > _installLimits.MaxCompressedBytes - archiveLength)
                {
                    throw new PackageInstallException(
                        PackageInstallErrorCode.CompressedSizeLimitExceeded,
                        PackageInstallStage.Acquisition,
                        $"Package publish exceeds the configured compressed size limit of {_installLimits.MaxCompressedBytes} bytes.",
                        reference.FhirDirective);
                }

                sha1.AppendData(buffer, 0, read);
                sha512.AppendData(buffer, 0, read);
                await spool.WriteAsync(
                        buffer.AsMemory(0, read),
                        cancellationToken)
                    .ConfigureAwait(false);
                archiveLength += read;
            }

            await spool.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        await using (FileStream archive = new FileStream(
            archivePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            CopyBufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            ArchiveExtractionMetrics metrics =
                await TarballExtractor.ExtractAsync(
                        archive,
                        extractionPath,
                        _installLimits,
                        reference.FhirDirective,
                        cancellationToken)
                    .ConfigureAwait(false);
            PackageArchiveInventory inventory = metrics.Inventory
                ?? throw InvalidArchive(
                    reference,
                    "Package archive inventory was not produced.");
            PackageArchiveLayoutResult layout =
                TarballExtractor.ValidateAndNormalizePackageStructure(
                    extractionPath,
                    inventory,
                    reference.FhirDirective);
            if (layout.Layout != PackageArchiveLayout.Standard)
            {
                throw InvalidArchive(
                    reference,
                    "Standard NPM publication requires a package/package.json archive layout.");
            }

            long manifestLimit = Math.Min(
                MaxPublishManifestBytes,
                _installLimits.MaxEntryBytes);
            long manifestLength =
                new FileInfo(layout.ManifestPath).Length;
            if (manifestLength > manifestLimit)
            {
                throw InvalidArchive(
                    reference,
                    $"Package manifest exceeds the NPM publish limit of {manifestLimit} bytes.");
            }

            PackageIdentityExpectation expectation =
                PackageIdentityValidator.CreateExpectation(
                    reference,
                    reference.FhirDirective);
            _ = await PackageIdentityValidator.ValidateExpectedAsync(
                    layout.ManifestPath,
                    expectation,
                    reference.FhirDirective,
                    cancellationToken)
                .ConfigureAwait(false);

            await using FileStream manifestStream = new FileStream(
                layout.ManifestPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                16_384,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            JsonObject manifest =
                await JsonNode.ParseAsync(
                        manifestStream,
                        cancellationToken: cancellationToken)
                    .ConfigureAwait(false) as JsonObject
                ?? throw InvalidArchive(
                    reference,
                    "Package manifest JSON did not contain an object.");
            ValidatePublishManifest(reference, manifest);
            return new NpmPublishPreparation(
                archivePath,
                archiveLength,
                Convert.ToHexString(sha1.GetHashAndReset())
                    .ToLowerInvariant(),
                $"sha512-{Convert.ToBase64String(sha512.GetHashAndReset())}",
                manifest);
        }
    }

    private NpmPackumentContent CreatePackumentContent(
        PackageReference reference,
        NpmPublishPreparation preparation)
    {
        string version = reference.Version!;
        string attachmentName = CreateAttachmentName(reference);
        string tarballUrl = CreateTarballUrl(reference, attachmentName);
        JsonObject versionMetadata = preparation.Manifest;
        versionMetadata.Remove("_id");
        versionMetadata.Remove("dist");
        versionMetadata.Remove("dist-tags");
        versionMetadata.Remove("versions");
        versionMetadata.Remove("_attachments");
        versionMetadata.Remove("patchedDependencies");
        versionMetadata["name"] = reference.Name;
        versionMetadata["version"] = version;
        versionMetadata["_id"] = $"{reference.Name}@{version}";
        versionMetadata["dist"] = new JsonObject
        {
            ["tarball"] = tarballUrl,
            ["shasum"] = preparation.Sha1,
            ["integrity"] = preparation.Integrity,
        };

        string marker = $"__fhirpkg_{Guid.NewGuid():N}__";
        JsonObject packument = new()
        {
            ["_id"] = reference.Name,
            ["name"] = reference.Name,
            ["description"] =
                versionMetadata["description"]?.DeepClone(),
            ["dist-tags"] = new JsonObject
            {
                ["latest"] = version,
            },
            ["versions"] = new JsonObject
            {
                [version] = versionMetadata,
            },
            ["_attachments"] = new JsonObject
            {
                [attachmentName] = new JsonObject
                {
                    ["content_type"] = "application/octet-stream",
                    ["length"] = preparation.ArchiveLength,
                    ["data"] = marker,
                },
            },
            ["access"] = null,
        };

        return new NpmPackumentContent(
            preparation.ArchivePath,
            preparation.ArchiveLength,
            packument,
            marker);
    }

    private string CreateTarballUrl(
        PackageReference reference,
        string attachmentName)
    {
        string packagePath = string.Join(
            '/',
            reference.Name
                .Split('/', StringSplitOptions.RemoveEmptyEntries)
                .Select(EncodePathSegment));
        string attachmentPath = string.Join(
            '/',
            attachmentName
                .Split('/', StringSplitOptions.RemoveEmptyEntries)
                .Select(EncodePathSegment));
        return $"{BaseUrl}/{packagePath}/-/{attachmentPath}";
    }

    private static string CreateAttachmentName(
        PackageReference reference)
    {
        return $"{reference.Name}-{reference.Version}.tgz";
    }

    private static string EncodeDocumentName(string packageName)
    {
        if (!packageName.StartsWith('@'))
            return Uri.EscapeDataString(packageName);

        int separator = packageName.IndexOf('/');
        if (separator <= 1 || separator == packageName.Length - 1)
        {
            throw new ArgumentException(
                "Scoped NPM package names must use the form '@scope/name'.",
                nameof(packageName));
        }

        string scope = Uri.EscapeDataString(packageName[1..separator]);
        string name = Uri.EscapeDataString(packageName[(separator + 1)..]);
        return $"@{scope}%2F{name}";
    }

    private static string EncodePathSegment(string segment) =>
        segment.StartsWith('@')
            ? $"@{Uri.EscapeDataString(segment[1..])}"
            : Uri.EscapeDataString(segment);

    private static void ValidateNpmVersion(
        PackageReference reference)
    {
        if (reference.Version is null
            || !IsStrictNpmSemanticVersion(reference.Version))
        {
            throw new PackageInstallException(
                PackageInstallErrorCode.InvalidPackageIdentity,
                PackageInstallStage.IdentityValidation,
                $"Package version '{reference.Version}' is not valid semantic version metadata for NPM publication.",
                reference.FhirDirective);
        }
    }

    private static void ValidateNpmPackageName(
        PackageReference reference)
    {
        string name = reference.Name;
        string leafName = name[
            (name.LastIndexOf('/') + 1)..];
        bool invalid =
            name.Length > 214
            || name != name.ToLowerInvariant()
            || name.StartsWith('.')
            || name.StartsWith('-')
            || name.StartsWith('_')
            || leafName.StartsWith('.')
            || leafName.IndexOfAny(['~', '\'', '!', '(', ')', '*']) >= 0
            || name.Equals(
                "node_modules",
                StringComparison.OrdinalIgnoreCase)
            || name.Equals(
                "favicon.ico",
                StringComparison.OrdinalIgnoreCase)
            || !IsValidNpmNameShape(name);
        if (invalid)
        {
            throw new PackageInstallException(
                PackageInstallErrorCode.InvalidPackageIdentity,
                PackageInstallStage.IdentityValidation,
                $"Package name '{name}' is not valid for NPM publication.",
                reference.FhirDirective);
        }
    }

    private static bool IsValidNpmNameShape(string name)
    {
        if (!name.StartsWith('@'))
        {
            return name.All(IsEncodeURIComponentSafe)
                && !name.Any(character =>
                    character is '/' or '@' or '+' or '%' or ':'
                    || char.IsWhiteSpace(character));
        }

        int separator = name.IndexOf('/');
        return separator > 1
            && separator == name.LastIndexOf('/')
            && separator < name.Length - 1
            && IsValidNpmNameComponent(
                name.AsSpan(1, separator - 1))
            && IsValidNpmNameComponent(
                name.AsSpan(separator + 1));
    }

    private static bool IsValidNpmNameComponent(
        ReadOnlySpan<char> value)
    {
        foreach (char character in value)
        {
            if (!IsEncodeURIComponentSafe(character))
                return false;
        }

        return !value.IsEmpty;
    }

    private static bool IsEncodeURIComponentSafe(char character) =>
        character is >= 'a' and <= 'z'
        or >= '0' and <= '9'
        or '-' or '_' or '.' or '!' or '~' or '*' or '\'' or '(' or ')';

    private static bool IsStrictNpmSemanticVersion(
        string value)
    {
        int plusIndex = value.IndexOf('+');
        if (plusIndex >= 0
            && value.IndexOf('+', plusIndex + 1) >= 0)
        {
            return false;
        }

        ReadOnlySpan<char> coreAndPreRelease = plusIndex >= 0
            ? value.AsSpan(0, plusIndex)
            : value.AsSpan();
        ReadOnlySpan<char> buildMetadata = plusIndex >= 0
            ? value.AsSpan(plusIndex + 1)
            : [];
        int dashIndex = coreAndPreRelease.IndexOf('-');
        ReadOnlySpan<char> core = dashIndex >= 0
            ? coreAndPreRelease[..dashIndex]
            : coreAndPreRelease;
        ReadOnlySpan<char> preRelease = dashIndex >= 0
            ? coreAndPreRelease[(dashIndex + 1)..]
            : [];
        int firstDot = core.IndexOf('.');
        if (firstDot <= 0)
            return false;

        ReadOnlySpan<char> afterFirst = core[(firstDot + 1)..];
        int secondDot = afterFirst.IndexOf('.');
        if (secondDot <= 0
            || afterFirst[(secondDot + 1)..].Contains('.'))
            return false;

        ReadOnlySpan<char> major = core[..firstDot];
        ReadOnlySpan<char> minor = afterFirst[..secondDot];
        ReadOnlySpan<char> patch = afterFirst[(secondDot + 1)..];
        if (!IsValidCoreIdentifier(major)
            || !IsValidCoreIdentifier(minor)
            || !IsValidCoreIdentifier(patch))
            return false;

        return (dashIndex < 0
                || IsValidSemVerIdentifiers(
                    preRelease,
                    rejectLeadingZeroNumbers: true))
            && (plusIndex < 0
                || IsValidSemVerIdentifiers(
                    buildMetadata,
                    rejectLeadingZeroNumbers: false));
    }

    private static bool IsValidSemVerIdentifiers(
        ReadOnlySpan<char> value,
        bool rejectLeadingZeroNumbers)
    {
        if (value.IsEmpty)
            return false;

        int start = 0;
        while (start <= value.Length)
        {
            int relativeDot = value[start..].IndexOf('.');
            int end = relativeDot < 0
                ? value.Length
                : start + relativeDot;
            ReadOnlySpan<char> identifier = value[start..end];
            if (!IsValidSemVerIdentifier(identifier))
            {
                return false;
            }

            if (rejectLeadingZeroNumbers
                && identifier.Length > 1
                && identifier[0] == '0'
                && IsNumericIdentifier(identifier))
            {
                return false;
            }

            if (relativeDot < 0)
                return true;

            start = end + 1;
        }

        return false;
    }

    private static bool IsValidCoreIdentifier(
        ReadOnlySpan<char> value) =>
        IsNumericIdentifier(value)
        && (value.Length == 1 || value[0] != '0');

    private static bool IsValidSemVerIdentifier(
        ReadOnlySpan<char> value)
    {
        if (value.IsEmpty)
            return false;

        foreach (char character in value)
        {
            if (character is not (>= '0' and <= '9')
                and not (>= 'A' and <= 'Z')
                and not (>= 'a' and <= 'z')
                and not '-')
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsNumericIdentifier(
        ReadOnlySpan<char> value)
    {
        if (value.IsEmpty)
            return false;

        foreach (char character in value)
        {
            if (character is not (>= '0' and <= '9'))
                return false;
        }

        return true;
    }

    private static void ValidatePublishManifest(
        PackageReference reference,
        JsonObject manifest)
    {
        if (manifest.TryGetPropertyValue(
                "private",
                out JsonNode? privateNode)
            && IsJsonTruthy(privateNode))
        {
            throw new PackageInstallException(
                PackageInstallErrorCode.InvalidPackageIdentity,
                PackageInstallStage.IdentityValidation,
                "Packages marked private cannot be published.",
                reference.FhirDirective);
        }

        if (manifest.ContainsKey("packageExtensions"))
        {
            throw new PackageInstallException(
                PackageInstallErrorCode.InvalidPackageIdentity,
                PackageInstallStage.IdentityValidation,
                "Package extensions are project policy and cannot be published.",
                reference.FhirDirective);
        }
    }

    private static bool IsJsonTruthy(JsonNode? node)
    {
        if (node is null)
            return false;

        if (node is JsonObject or JsonArray)
            return true;

        JsonValue value = (JsonValue)node;
        if (value.TryGetValue<bool>(out bool boolean))
            return boolean;
        if (value.TryGetValue<string>(out string? text))
            return !string.IsNullOrEmpty(text);
        if (value.TryGetValue<double>(out double number))
            return number != 0;

        return true;
    }

    private void DeleteWorkspace(string? workspacePath)
    {
        if (workspacePath is null || !Directory.Exists(workspacePath))
            return;

        try
        {
            Directory.Delete(workspacePath, recursive: true);
        }
        catch (Exception exception)
        {
            Logger.LogWarning(
                exception,
                "Failed to delete NPM publish workspace {WorkspacePath}",
                workspacePath);
        }
    }

    private static PackageInstallException InvalidArchive(
        PackageReference reference,
        string message) =>
        new(
            PackageInstallErrorCode.InvalidArchive,
            PackageInstallStage.ArchiveValidation,
            message,
            reference.FhirDirective);

    private sealed record NpmPublishPreparation(
        string ArchivePath,
        long ArchiveLength,
        string Sha1,
        string Integrity,
        JsonObject Manifest);

    // ── Internal DTOs for NPM search response ───────────────────────────

    /// <summary>Represents the top-level NPM search API response.</summary>
    private sealed class NpmSearchResponse
    {
        [JsonPropertyName("objects")]
        public List<NpmSearchObject>? Objects { get; set; }
    }

    /// <summary>Represents a single search result object from the NPM search API.</summary>
    private sealed class NpmSearchObject
    {
        [JsonPropertyName("package")]
        public NpmSearchPackage? Package { get; set; }
    }

    /// <summary>Represents the package metadata within an NPM search result.</summary>
    private sealed class NpmSearchPackage
    {
        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("version")]
        public string? Version { get; set; }

        [JsonPropertyName("description")]
        public string? Description { get; set; }
    }
}
