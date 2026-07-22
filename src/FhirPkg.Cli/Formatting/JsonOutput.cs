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
        Write(ProjectInstallResult(result));
    }

    /// <summary>
    /// Writes multiple install results as a JSON array.
    /// </summary>
    /// <param name="results">The install results to serialize.</param>
    public static void WriteInstallResults(IReadOnlyList<PackageInstallResult> results)
    {
        InstallResultSummary summary =
            InstallResultPresentation.Summarize(results);
        Write(new
        {
            Results = results.Select(ProjectInstallResult),
            Summary = new
            {
                summary.Total,
                Installed = summary.CoarseInstalled,
                summary.AlreadyCached,
                summary.Failed,
                Dispositions = new
                {
                    Installed = summary.DispositionInstalled,
                    summary.Updated,
                    summary.AlreadyCurrent,
                    summary.Refreshed
                }
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
        HashSet<string?> cachedVersions = cached?
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

    private static IReadOnlyDictionary<string, object?> ProjectInstallResult(
        PackageInstallResult result)
    {
        Dictionary<string, object?> projection = new()
        {
            ["directive"] = result.Directive,
            ["status"] = result.Status.ToString(),
            ["dependencyFailures"] = result.DependencyFailures
                .Select(ProjectDependencyFailure)
                .ToArray()
        };
        if (result.ErrorMessage is not null)
            projection["errorMessage"] = result.ErrorMessage;
        if (result.ErrorCode is not null)
            projection["errorCode"] = result.ErrorCode;
        if (result.ErrorStage is not null)
            projection["errorStage"] = result.ErrorStage;
        if (result.Package is PackageRecord package)
            projection["package"] = ProjectPackage(package);
        if (result.Status == PackageInstallStatus.Installed
            && result.Disposition is PackageInstallDisposition disposition)
        {
            projection["disposition"] = disposition.ToString();
            projection["previousManifestDate"] =
                result.PreviousManifestDate;
            projection["manifestDate"] = result.ManifestDate;
        }

        return projection;
    }

    private static IReadOnlyDictionary<string, object?>
        ProjectDependencyFailure(PackageInstallResult failure)
    {
        Dictionary<string, object?> projection = new()
        {
            ["directive"] = failure.Directive,
            ["status"] = failure.Status.ToString()
        };
        if (failure.ErrorMessage is not null)
            projection["errorMessage"] = failure.ErrorMessage;
        if (failure.ErrorCode is not null)
            projection["errorCode"] = failure.ErrorCode;
        if (failure.ErrorStage is not null)
            projection["errorStage"] = failure.ErrorStage;
        return projection;
    }

    private static IReadOnlyDictionary<string, object?> ProjectPackage(
        PackageRecord package)
    {
        Dictionary<string, object?> projection = new()
        {
            ["name"] = package.Reference.Name,
            ["directoryPath"] = package.DirectoryPath
        };
        if (package.Reference.Version is not null)
            projection["version"] = package.Reference.Version;
        if (package.SizeBytes is not null)
            projection["sizeBytes"] = package.SizeBytes;
        return projection;
    }

    private static void Write<T>(T value)
    {
        Console.Out.WriteLine(JsonSerializer.Serialize(value, s_options));
    }
}
