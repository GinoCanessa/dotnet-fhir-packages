// Copyright (c) Gino Canessa. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System.Collections.Concurrent;
using System.Text.Json;

using Microsoft.Extensions.Logging;

namespace FhirPkg.Indexing;

/// <summary>
/// Indexes FHIR package content directories by scanning JSON resource files and building
/// an in-memory searchable index. Supports reading existing <c>.index.json</c> files
/// and generating new indexes from scratch.
/// </summary>
/// <remarks>
/// <para>
/// When a package is indexed, its resources are registered in an in-memory store keyed by
/// package identity. Subsequent calls to <see cref="FindResources"/>, <see cref="FindByCanonicalUrl"/>,
/// and <see cref="FindByResourceType"/> search across all indexed packages.
/// </para>
/// <para>
/// For StructureDefinition resources, the indexer classifies the flavor:
/// </para>
/// <list type="bullet">
///   <item><description><b>Profile</b>: derivation == "constraint" and kind == "resource"</description></item>
///   <item><description><b>Extension</b>: type == "Extension"</description></item>
///   <item><description><b>Logical</b>: derivation == "specialization" and kind == "logical"</description></item>
///   <item><description><b>Type</b>: kind == "primitive-type" or kind == "complex-type"</description></item>
///   <item><description><b>Resource</b>: kind == "resource" and derivation == "specialization"</description></item>
/// </list>
/// </remarks>
public class PackageIndexer : IPackageIndexer
{
    private const string IndexFileName = ".index.json";

    private static readonly JsonSerializerOptions s_readOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    private static readonly JsonSerializerOptions s_writeOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    /// <summary>
    /// In-memory store of indexed resources, keyed by package identity ("name#version" or content path).
    /// Thread-safe for concurrent indexing.
    /// </summary>
    private readonly ConcurrentDictionary<string, IReadOnlyList<ResourceInfo>> _indexedPackages = new(StringComparer.OrdinalIgnoreCase);

    private readonly ILogger _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="PackageIndexer"/> class.
    /// </summary>
    /// <param name="logger">Logger for diagnostic output.</param>
    public PackageIndexer(ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<PackageIndex> IndexPackageAsync(
        string packageContentPath,
        IndexingOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(packageContentPath);

        if (!Directory.Exists(packageContentPath))
            throw new DirectoryNotFoundException(
                $"Package content directory not found: {packageContentPath}");

        options ??= new IndexingOptions();

        // Try reading existing .index.json unless force reindex is requested
        if (!options.ForceReindex)
        {
            var existingIndex = await TryReadExistingIndexAsync(packageContentPath, cancellationToken)
                .ConfigureAwait(false);
            if (existingIndex is not null)
            {
                _logger.LogDebug("Using existing .index.json from '{Path}'.", packageContentPath);
                RegisterIndex(packageContentPath, existingIndex);
                return existingIndex;
            }
        }

        // Build a new index by scanning all .json files in the directory
        _logger.LogInformation("Generating index for package at '{Path}'.", packageContentPath);
        var index = await BuildIndexAsync(packageContentPath, cancellationToken).ConfigureAwait(false);

        RegisterIndex(packageContentPath, index);

        return index;
    }

    /// <inheritdoc />
    public IReadOnlyList<ResourceInfo> FindResources(ResourceSearchCriteria criteria)
    {
        ArgumentNullException.ThrowIfNull(criteria);

        IEnumerable<ResourceInfo> results = _indexedPackages.Values.SelectMany(r => r);

        // Package scope filter
        if (!string.IsNullOrEmpty(criteria.PackageScope))
        {
            results = ApplyPackageScopeFilter(results, criteria.PackageScope);
        }

        // Key filter: match by canonical URL or resource type
        if (!string.IsNullOrEmpty(criteria.Key))
        {
            var key = criteria.Key;
            results = results.Where(r =>
                string.Equals(r.Url, key, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(r.ResourceType, key, StringComparison.OrdinalIgnoreCase));
        }

        // Resource type filter
        if (criteria.ResourceTypes is { Count: > 0 })
        {
            var types = new HashSet<string>(criteria.ResourceTypes, StringComparer.OrdinalIgnoreCase);
            results = results.Where(r => types.Contains(r.ResourceType));
        }

        // SD flavor filter
        if (criteria.SdFlavors is { Count: > 0 })
        {
            var flavors = new HashSet<string>(criteria.SdFlavors, StringComparer.OrdinalIgnoreCase);
            results = results.Where(r => r.SdFlavor is not null && flavors.Contains(r.SdFlavor));
        }

        // Limit
        if (criteria.Limit is > 0)
        {
            results = results.Take(criteria.Limit.Value);
        }

        return results.ToList().AsReadOnly();
    }

    /// <inheritdoc />
    public ResourceInfo? FindByCanonicalUrl(string canonicalUrl)
    {
        ArgumentNullException.ThrowIfNull(canonicalUrl);

        return _indexedPackages.Values
            .SelectMany(r => r)
            .FirstOrDefault(r => string.Equals(r.Url, canonicalUrl, StringComparison.OrdinalIgnoreCase));
    }

    /// <inheritdoc />
    public IReadOnlyList<ResourceInfo> FindByResourceType(string resourceType, string? packageScope = null)
    {
        ArgumentNullException.ThrowIfNull(resourceType);

        IEnumerable<ResourceInfo> results = _indexedPackages.Values.SelectMany(r => r);

        results = results.Where(r =>
            string.Equals(r.ResourceType, resourceType, StringComparison.OrdinalIgnoreCase));

        if (!string.IsNullOrEmpty(packageScope))
        {
            results = ApplyPackageScopeFilter(results, packageScope);
        }

        return results.ToList().AsReadOnly();
    }

    // ── Private helpers ──────────────────────────────────────────────────

    /// <summary>
    /// Attempts to read and deserialize an existing <c>.index.json</c> from the package directory.
    /// </summary>
    private async Task<PackageIndex?> TryReadExistingIndexAsync(
        string packageContentPath,
        CancellationToken cancellationToken)
    {
        var indexPath = Path.Combine(packageContentPath, IndexFileName);
        if (!File.Exists(indexPath))
            return null;

        try
        {
            await using var stream = File.OpenRead(indexPath);
            var index = await JsonSerializer.DeserializeAsync<PackageIndex>(stream, s_readOptions, cancellationToken)
                .ConfigureAwait(false);
            return index;
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Failed to deserialize .index.json at '{Path}'. Will regenerate.", indexPath);
            return null;
        }
    }

    /// <summary>
    /// Scans all <c>.json</c> files (non-recursive) in the package content directory and builds
    /// a <see cref="PackageIndex"/>.
    /// </summary>
    private async Task<PackageIndex> BuildIndexAsync(
        string packageContentPath,
        CancellationToken cancellationToken)
    {
        var entries = new List<ResourceIndexEntry>();
        var jsonFiles = Directory.GetFiles(packageContentPath, "*.json", SearchOption.TopDirectoryOnly);

        foreach (var filePath in jsonFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var fileName = Path.GetFileName(filePath);

            // Skip the index file itself, package.json, and hidden files
            if (fileName.StartsWith('.') ||
                string.Equals(fileName, "package.json", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var entry = await TryIndexFileAsync(filePath, fileName, cancellationToken).ConfigureAwait(false);
            if (entry is not null)
            {
                entries.Add(entry);
            }
        }

        _logger.LogDebug("Indexed {Count} resources from '{Path}'.", entries.Count, packageContentPath);

        return new PackageIndex
        {
            IndexVersion = 2,
            Date = DateTime.UtcNow,
            Files = entries.AsReadOnly(),
        };
    }

    /// <summary>
    /// Attempts to read a single JSON file and extract a <see cref="ResourceIndexEntry"/>.
    /// Returns <c>null</c> if the file is not a valid FHIR resource or cannot be parsed.
    /// </summary>
    private async Task<ResourceIndexEntry?> TryIndexFileAsync(
        string filePath,
        string fileName,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = File.OpenRead(filePath);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return null;

            var resourceType = GetStringProperty(root, "resourceType");
            if (string.IsNullOrEmpty(resourceType))
                return null;

            var id = GetStringProperty(root, "id");
            var url = GetStringProperty(root, "url");
            var version = GetStringProperty(root, "version");
            var name = GetStringProperty(root, "name");
            var title = GetStringProperty(root, "title");
            var description = GetStringProperty(root, "description");

            // StructureDefinition-specific fields
            string? sdKind = null;
            string? sdDerivation = null;
            string? sdType = null;
            string? sdBaseDefinition = null;
            bool? sdAbstract = null;
            string? sdFlavor = null;
            bool? hasSnapshot = null;

            if (string.Equals(resourceType, "StructureDefinition", StringComparison.OrdinalIgnoreCase))
            {
                sdKind = GetStringProperty(root, "kind");
                sdDerivation = GetStringProperty(root, "derivation");
                sdType = GetStringProperty(root, "type");
                sdBaseDefinition = GetStringProperty(root, "baseDefinition");
                sdAbstract = GetBoolProperty(root, "abstract");
                hasSnapshot = root.TryGetProperty("snapshot", out _);

                sdFlavor = ClassifyStructureDefinitionFlavor(sdKind, sdDerivation, sdType);
            }

            // ValueSet-specific
            bool? hasExpansion = null;
            if (string.Equals(resourceType, "ValueSet", StringComparison.OrdinalIgnoreCase))
            {
                hasExpansion = root.TryGetProperty("expansion", out _);
            }

            return new ResourceIndexEntry
            {
                Filename = fileName,
                ResourceType = resourceType,
                Id = id,
                Url = url,
                Version = version,
                Name = name,
                Title = title,
                Description = description,
                SdKind = sdKind,
                SdDerivation = sdDerivation,
                SdType = sdType,
                SdBaseDefinition = sdBaseDefinition,
                SdAbstract = sdAbstract,
                SdFlavor = sdFlavor,
                HasSnapshot = hasSnapshot,
                HasExpansion = hasExpansion,
            };
        }
        catch (JsonException ex)
        {
            _logger.LogDebug(ex, "Skipping non-JSON or malformed file '{FileName}'.", fileName);
            return null;
        }
        catch (IOException ex)
        {
            _logger.LogWarning(ex, "Could not read file '{FileName}'.", fileName);
            return null;
        }
    }

    /// <summary>
    /// Classifies a StructureDefinition into a human-readable flavor based on its kind,
    /// derivation, and type properties.
    /// </summary>
    /// <param name="kind">The SD kind (e.g. "resource", "complex-type", "primitive-type", "logical").</param>
    /// <param name="derivation">The SD derivation (e.g. "specialization", "constraint").</param>
    /// <param name="type">The SD type (e.g. "Patient", "Extension").</param>
    /// <returns>
    /// A flavor string: "Profile", "Extension", "Logical", "Type", or "Resource".
    /// Returns <c>null</c> if classification is indeterminate.
    /// </returns>
    internal static string? ClassifyStructureDefinitionFlavor(
        string? kind, string? derivation, string? type)
    {
        // Extension: type is "Extension" regardless of other fields
        if (string.Equals(type, "Extension", StringComparison.OrdinalIgnoreCase))
            return "Extension";

        // Profile: derivation == "constraint" and kind == "resource"
        if (string.Equals(derivation, "constraint", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(kind, "resource", StringComparison.OrdinalIgnoreCase))
            return "Profile";

        // Logical model: derivation == "specialization" and kind == "logical"
        if (string.Equals(derivation, "specialization", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(kind, "logical", StringComparison.OrdinalIgnoreCase))
            return "Logical";

        // Data type: kind == "primitive-type" or kind == "complex-type"
        if (string.Equals(kind, "primitive-type", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(kind, "complex-type", StringComparison.OrdinalIgnoreCase))
            return "Type";

        // Resource definition: kind == "resource" and derivation == "specialization"
        if (string.Equals(kind, "resource", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(derivation, "specialization", StringComparison.OrdinalIgnoreCase))
            return "Resource";

        // Constraint on a type (e.g. profile on a data type)
        if (string.Equals(derivation, "constraint", StringComparison.OrdinalIgnoreCase))
            return "Profile";

        return null;
    }

    /// <summary>
    /// Registers an index's entries in the in-memory store, converting them to <see cref="ResourceInfo"/>
    /// enriched with package metadata extracted from the content path.
    /// </summary>
    private void RegisterIndex(string packageContentPath, PackageIndex index)
    {
        var (packageName, packageVersion) = ExtractPackageIdentity(packageContentPath);
        var key = packageVersion is not null ? $"{packageName}#{packageVersion}" : packageContentPath;

        var resources = index.Files.Select(entry => new ResourceInfo
        {
            ResourceType = entry.ResourceType,
            Id = entry.Id,
            Url = entry.Url,
            Name = entry.Name,
            Version = entry.Version,
            PackageName = packageName,
            PackageVersion = packageVersion,
            FilePath = entry.Filename,
            SdFlavor = entry.SdFlavor,
        }).ToList().AsReadOnly();

        _indexedPackages[key] = resources;

        _logger.LogDebug("Registered {Count} resources for package '{Key}'.", resources.Count, key);
    }

    /// <summary>
    /// Extracts the package name and version from a content path.
    /// The FHIR cache convention is: <c>{cache}/name#version/package/</c>.
    /// </summary>
    private static (string? PackageName, string? PackageVersion) ExtractPackageIdentity(string contentPath)
    {
        // Walk up from the package/ directory to the parent (name#version)
        var directoryName = Path.GetFileName(Path.GetDirectoryName(contentPath));
        if (string.IsNullOrEmpty(directoryName))
            return (null, null);

        var hashIndex = directoryName.IndexOf('#');
        if (hashIndex <= 0)
            return (directoryName, null);

        return (directoryName[..hashIndex], directoryName[(hashIndex + 1)..]);
    }

    /// <summary>
    /// Applies a package scope filter to a sequence of resource info entries.
    /// The scope can be "name#version" (exact match) or just "name" (prefix match on package name).
    /// </summary>
    private static IEnumerable<ResourceInfo> ApplyPackageScopeFilter(
        IEnumerable<ResourceInfo> results, string packageScope)
    {
        var hashIndex = packageScope.IndexOf('#');
        if (hashIndex > 0)
        {
            var name = packageScope[..hashIndex];
            var version = packageScope[(hashIndex + 1)..];
            return results.Where(r =>
                string.Equals(r.PackageName, name, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(r.PackageVersion, version, StringComparison.OrdinalIgnoreCase));
        }

        return results.Where(r =>
            string.Equals(r.PackageName, packageScope, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Safely reads a string property from a <see cref="JsonElement"/>, returning <c>null</c>
    /// if the property does not exist or is not a string.
    /// </summary>
    private static string? GetStringProperty(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var prop) && prop.ValueKind == JsonValueKind.String
            ? prop.GetString()
            : null;
    }

    /// <summary>
    /// Safely reads a boolean property from a <see cref="JsonElement"/>, returning <c>null</c>
    /// if the property does not exist or is not a boolean.
    /// </summary>
    private static bool? GetBoolProperty(JsonElement element, string propertyName)
    {
        if (element.TryGetProperty(propertyName, out var prop))
        {
            if (prop.ValueKind == JsonValueKind.True) return true;
            if (prop.ValueKind == JsonValueKind.False) return false;
        }
        return null;
    }
}
