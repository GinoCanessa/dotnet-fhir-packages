// Copyright (c) Gino Canessa. Licensed under the MIT License.

using System.Text.Json;
using System.Text.Json.Serialization;
using FhirPkg.Models;

namespace FhirPkg.Cli.Formatting;

/// <summary>
/// Provides JSON-formatted output for machine-readable consumption.
/// All methods serialize objects using <see cref="System.Text.Json"/> and write to <see cref="Console.Out"/>.
/// </summary>
internal static class JsonOutput
{
    private static readonly JsonSerializerOptions s_options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    /// <summary>
    /// Writes a single install result as JSON.
    /// </summary>
    /// <param name="result">The install result to serialize.</param>
    public static void WriteInstallResult(PackageInstallResult result)
    {
        Write(new
        {
            Directive = result.Directive,
            Status = result.Status.ToString(),
            result.ErrorMessage,
            Package = result.Package is { } pkg
                ? new { pkg.Reference.Name, pkg.Reference.Version, pkg.DirectoryPath, pkg.SizeBytes }
                : null
        });
    }

    /// <summary>
    /// Writes multiple install results as a JSON array.
    /// </summary>
    /// <param name="results">The install results to serialize.</param>
    public static void WriteInstallResults(IReadOnlyList<PackageInstallResult> results)
    {
        Write(new
        {
            Results = results.Select(r => new
            {
                Directive = r.Directive,
                Status = r.Status.ToString(),
                r.ErrorMessage,
                Package = r.Package is { } pkg
                    ? new { pkg.Reference.Name, pkg.Reference.Version, pkg.DirectoryPath, pkg.SizeBytes }
                    : null
            }),
            Summary = new
            {
                Total = results.Count,
                Installed = results.Count(r => r.Status == PackageInstallStatus.Installed),
                AlreadyCached = results.Count(r => r.Status == PackageInstallStatus.AlreadyCached),
                Failed = results.Count(r => r.Status is PackageInstallStatus.Failed or PackageInstallStatus.NotFound)
            }
        });
    }

    /// <summary>
    /// Writes a restore result (package closure) as JSON.
    /// </summary>
    /// <param name="closure">The package closure to serialize.</param>
    public static void WriteRestoreResult(PackageClosure closure)
    {
        Write(new
        {
            closure.Timestamp,
            closure.IsComplete,
            Resolved = closure.Resolved.ToDictionary(
                kvp => kvp.Key,
                kvp => new { kvp.Value.Name, kvp.Value.Version }),
            closure.Missing
        });
    }

    /// <summary>
    /// Writes a list of cached packages as JSON.
    /// </summary>
    /// <param name="packages">The cached packages to serialize.</param>
    /// <param name="showSize">Whether to include size information.</param>
    public static void WritePackageList(IReadOnlyList<PackageRecord> packages, bool showSize)
    {
        Write(new
        {
            Count = packages.Count,
            Packages = packages.Select(p => new
            {
                p.Reference.Name,
                p.Reference.Version,
                FhirVersion = p.Manifest?.FhirVersions?.FirstOrDefault(),
                p.InstalledAt,
                Size = showSize ? p.SizeBytes : null
            })
        });
    }

    /// <summary>
    /// Writes search results as JSON.
    /// </summary>
    /// <param name="entries">The catalog entries to serialize.</param>
    public static void WriteSearchResults(IReadOnlyList<CatalogEntry> entries)
    {
        Write(new
        {
            Count = entries.Count,
            Results = entries.Select(e => new
            {
                e.Name,
                e.Version,
                e.FhirVersion,
                e.Description,
                e.Canonical,
                e.Kind,
                e.Date,
                e.Url
            })
        });
    }

    /// <summary>
    /// Writes package listing information as JSON.
    /// </summary>
    /// <param name="listing">The package listing to serialize.</param>
    /// <param name="cached">Optional list of locally cached records.</param>
    public static void WritePackageInfo(PackageListing listing, IReadOnlyList<PackageRecord>? cached)
    {
        var cachedVersions = cached?
            .Where(c => string.Equals(c.Reference.Name, listing.PackageId, StringComparison.OrdinalIgnoreCase))
            .Select(c => c.Reference.Version)
            .ToHashSet(StringComparer.OrdinalIgnoreCase) ?? [];

        Write(new
        {
            listing.PackageId,
            listing.Description,
            listing.LatestVersion,
            listing.DistTags,
            Versions = listing.Versions.Keys.Select(v => new
            {
                Version = v,
                Cached = cachedVersions.Contains(v)
            })
        });
    }

    /// <summary>
    /// Writes a resolved directive as JSON.
    /// </summary>
    /// <param name="resolved">The resolved directive to serialize.</param>
    public static void WriteResolveResult(ResolvedDirective resolved)
    {
        Write(new
        {
            resolved.Reference.Name,
            resolved.Reference.Version,
            Tarball = resolved.TarballUri.ToString(),
            resolved.ShaSum,
            Registry = resolved.SourceRegistry?.Url,
            resolved.PublicationDate
        });
    }

    /// <summary>
    /// Writes a publish result as JSON.
    /// </summary>
    /// <param name="result">The publish result to serialize.</param>
    public static void WritePublishResult(PublishResult result)
    {
        Write(new
        {
            result.Success,
            result.Message,
            result.StatusCode
        });
    }

    /// <summary>
    /// Writes an error object as JSON.
    /// </summary>
    /// <param name="message">The error message.</param>
    public static void WriteError(string message)
    {
        Write(new { Error = message });
    }

    /// <summary>
    /// Writes a success object as JSON.
    /// </summary>
    /// <param name="message">The success message.</param>
    public static void WriteSuccess(string message)
    {
        Write(new { Success = true, Message = message });
    }

    private static void Write<T>(T value)
    {
        Console.Out.WriteLine(JsonSerializer.Serialize(value, s_options));
    }
}
