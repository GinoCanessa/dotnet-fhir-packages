// Copyright (c) Gino Canessa. Licensed under the MIT License.

using FhirPkg.Models;
using Spectre.Console;

namespace FhirPkg.Cli.Formatting;

/// <summary>
/// Provides rich console output formatting using Spectre.Console markup and tables.
/// </summary>
internal static class ConsoleOutput
{
    /// <summary>
    /// Writes the result of a single package install operation to the console.
    /// </summary>
    /// <param name="result">The install result to display.</param>
    public static void WriteInstallResult(PackageInstallResult result)
    {
        string statusMarkup = result.Status switch
        {
            PackageInstallStatus.Installed => "[green]✓ installed[/]",
            PackageInstallStatus.AlreadyCached => "[yellow]● already cached[/]",
            PackageInstallStatus.NotFound => "[red]✗ not found[/]",
            PackageInstallStatus.Failed => "[red]✗ failed[/]",
            _ => "[grey]? unknown[/]"
        };

        AnsiConsole.MarkupLine($"  {Markup.Escape(result.Directive),-40} {statusMarkup}");

        if (result.ErrorMessage is not null)
        {
            AnsiConsole.MarkupLine($"    [red]{Markup.Escape(result.ErrorMessage)}[/]");
        }

        if (result.Package is { } pkg)
        {
            AnsiConsole.MarkupLine($"    [grey]→ {Markup.Escape(pkg.DirectoryPath)}[/]");
        }
    }

    /// <summary>
    /// Writes the results of installing multiple packages to the console.
    /// </summary>
    /// <param name="results">The collection of install results to display.</param>
    public static void WriteInstallResults(IReadOnlyList<PackageInstallResult> results)
    {
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[bold]Install results:[/]");
        AnsiConsole.WriteLine();

        foreach (PackageInstallResult result in results)
        {
            WriteInstallResult(result);
        }

        AnsiConsole.WriteLine();

        int installed = results.Count(r => r.Status == PackageInstallStatus.Installed);
        int cached = results.Count(r => r.Status == PackageInstallStatus.AlreadyCached);
        int failed = results.Count(r => r.Status is PackageInstallStatus.Failed or PackageInstallStatus.NotFound);

        AnsiConsole.MarkupLine(
            $"[bold]Summary:[/] [green]{installed} installed[/], " +
            $"[yellow]{cached} already cached[/], " +
            $"[red]{failed} failed[/]");
    }

    /// <summary>
    /// Writes the result of a restore operation to the console.
    /// </summary>
    /// <param name="closure">The package closure resulting from the restore.</param>
    public static void WriteRestoreResult(PackageClosure closure)
    {
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[bold]Restore results:[/]");
        AnsiConsole.WriteLine();

        if (closure.Resolved.Count > 0)
        {
            Table table = new Table()
                .Border(TableBorder.Rounded)
                .AddColumn("[bold]Package[/]")
                .AddColumn("[bold]Version[/]");

            foreach ((string? id, PackageReference reference) in closure.Resolved.OrderBy(kvp => kvp.Key))
            {
                table.AddRow(
                    Markup.Escape(id),
                    Markup.Escape(reference.Version ?? "latest"));
            }

            AnsiConsole.Write(table);
        }

        if (closure.Missing.Count > 0)
        {
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine("[red bold]Missing packages:[/]");

            foreach ((string? id, string? reason) in closure.Missing.OrderBy(kvp => kvp.Key))
            {
                AnsiConsole.MarkupLine($"  [red]✗[/] {Markup.Escape(id)}: {Markup.Escape(reason)}");
            }
        }

        AnsiConsole.WriteLine();

        if (closure.IsComplete)
        {
            AnsiConsole.MarkupLine($"[green]✓ Restore complete — {closure.Resolved.Count} package(s) resolved.[/]");
        }
        else
        {
            AnsiConsole.MarkupLine(
                $"[yellow]⚠ Restore incomplete — {closure.Resolved.Count} resolved, " +
                $"{closure.Missing.Count} missing.[/]");
        }
    }

    /// <summary>
    /// Writes a list of cached packages as a formatted table.
    /// </summary>
    /// <param name="packages">The cached packages to display.</param>
    /// <param name="showSize">Whether to include package size in the output.</param>
    public static void WritePackageList(IReadOnlyList<PackageRecord> packages, bool showSize)
    {
        if (packages.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No packages found in cache.[/]");
            return;
        }

        Table table = new Table()
            .Border(TableBorder.Rounded)
            .AddColumn("[bold]Package[/]")
            .AddColumn("[bold]Version[/]")
            .AddColumn("[bold]FHIR[/]")
            .AddColumn("[bold]Installed[/]");

        if (showSize)
        {
            table.AddColumn("[bold]Size[/]");
        }

        foreach (PackageRecord pkg in packages)
        {
            string fhirVersion = pkg.Manifest?.FhirVersions?.FirstOrDefault() ?? "-";
            string installedAt = pkg.InstalledAt?.ToString("yyyy-MM-dd HH:mm") ?? "-";

            List<string> columns = new List<string>
            {
                Markup.Escape(pkg.Reference.Name),
                Markup.Escape(pkg.Reference.Version ?? "?"),
                Markup.Escape(fhirVersion),
                installedAt
            };

            if (showSize)
            {
                columns.Add(FormatBytes(pkg.SizeBytes));
            }

            table.AddRow(columns.ToArray());
        }

        AnsiConsole.Write(table);
        AnsiConsole.MarkupLine($"\n[grey]{packages.Count} package(s) in cache.[/]");
    }

    /// <summary>
    /// Writes search results from a registry query as a formatted table.
    /// </summary>
    /// <param name="entries">The catalog entries to display.</param>
    public static void WriteSearchResults(IReadOnlyList<CatalogEntry> entries)
    {
        if (entries.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No packages matched the search criteria.[/]");
            return;
        }

        Table table = new Table()
            .Border(TableBorder.Rounded)
            .AddColumn("[bold]Name[/]")
            .AddColumn("[bold]Version[/]")
            .AddColumn("[bold]FHIR[/]")
            .AddColumn("[bold]Description[/]");

        foreach (CatalogEntry entry in entries)
        {
            table.AddRow(
                Markup.Escape(entry.Name),
                Markup.Escape(entry.Version ?? "-"),
                Markup.Escape(entry.FhirVersion ?? "-"),
                Markup.Escape(Truncate(entry.Description, 60)));
        }

        AnsiConsole.Write(table);
        AnsiConsole.MarkupLine($"\n[grey]{entries.Count} result(s).[/]");
    }

    /// <summary>
    /// Writes detailed information about a package listing.
    /// </summary>
    /// <param name="listing">The package listing from the registry.</param>
    /// <param name="cached">Optional list of locally cached records for cross-referencing.</param>
    public static void WritePackageInfo(PackageListing listing, IReadOnlyList<PackageRecord>? cached)
    {
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine($"[bold]{Markup.Escape(listing.PackageId)}[/]");

        if (listing.Description is not null)
        {
            AnsiConsole.MarkupLine($"  [grey]{Markup.Escape(listing.Description)}[/]");
        }

        if (listing.LatestVersion is not null)
        {
            AnsiConsole.MarkupLine($"  [green]Latest:[/] {Markup.Escape(listing.LatestVersion)}");
        }

        if (listing.DistTags is { Count: > 0 })
        {
            AnsiConsole.MarkupLine("  [bold]Dist tags:[/]");
            foreach ((string? tag, string? version) in listing.DistTags)
            {
                AnsiConsole.MarkupLine($"    {Markup.Escape(tag)}: {Markup.Escape(version)}");
            }
        }

        if (listing.Versions.Count > 0)
        {
            AnsiConsole.WriteLine();

            HashSet<string?> cachedVersions = cached?
                .Where(c => string.Equals(c.Reference.Name, listing.PackageId, StringComparison.OrdinalIgnoreCase))
                .Select(c => c.Reference.Version)
                .ToHashSet(StringComparer.OrdinalIgnoreCase) ?? [];

            Table table = new Table()
                .Border(TableBorder.Rounded)
                .AddColumn("[bold]Version[/]")
                .AddColumn("[bold]Cached[/]");

            foreach ((string? version, PackageVersionInfo _) in listing.Versions.OrderByDescending(v => v.Key))
            {
                bool isCached = cachedVersions.Contains(version);
                table.AddRow(
                    Markup.Escape(version),
                    isCached ? "[green]✓[/]" : "[grey]-[/]");
            }

            AnsiConsole.Write(table);
        }

        AnsiConsole.MarkupLine($"\n[grey]{listing.Versions.Count} version(s) available.[/]");
    }

    /// <summary>
    /// Writes the result of a directive resolution to the console.
    /// </summary>
    /// <param name="resolved">The resolved directive information.</param>
    public static void WriteResolveResult(ResolvedDirective resolved)
    {
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[bold]Resolved:[/]");
        AnsiConsole.MarkupLine($"  [bold]Package:[/]    {Markup.Escape(resolved.Reference.Name)}");
        AnsiConsole.MarkupLine($"  [bold]Version:[/]    {Markup.Escape(resolved.Reference.Version ?? "?")}");
        AnsiConsole.MarkupLine($"  [bold]Tarball:[/]    {Markup.Escape(resolved.TarballUri.ToString())}");

        if (resolved.ShaSum is not null)
        {
            AnsiConsole.MarkupLine($"  [bold]SHA:[/]        {Markup.Escape(resolved.ShaSum)}");
        }

        if (resolved.SourceRegistry is not null)
        {
            AnsiConsole.MarkupLine($"  [bold]Registry:[/]   {Markup.Escape(resolved.SourceRegistry.Url)}");
        }

        if (resolved.PublicationDate is not null)
        {
            AnsiConsole.MarkupLine($"  [bold]Published:[/]  {resolved.PublicationDate:yyyy-MM-dd}");
        }
    }

    /// <summary>
    /// Writes the result of a publish operation to the console.
    /// </summary>
    /// <param name="result">The publish result to display.</param>
    public static void WritePublishResult(PublishResult result)
    {
        if (result.Success)
        {
            WriteSuccess(result.Message ?? "Package published successfully.");
        }
        else
        {
            WriteError(result.Message ?? $"Publish failed (HTTP {result.StatusCode}).");
        }
    }

    /// <summary>
    /// Writes a success message to the console with green markup.
    /// </summary>
    /// <param name="message">The success message.</param>
    public static void WriteSuccess(string message)
    {
        AnsiConsole.MarkupLine($"[green]✓ {Markup.Escape(message)}[/]");
    }

    /// <summary>
    /// Writes an error message to the console with red markup.
    /// </summary>
    /// <param name="message">The error message.</param>
    public static void WriteError(string message)
    {
        AnsiConsole.MarkupLine($"[red]✗ {Markup.Escape(message)}[/]");
    }

    /// <summary>
    /// Writes a warning message to the console with yellow markup.
    /// </summary>
    /// <param name="message">The warning message.</param>
    public static void WriteWarning(string message)
    {
        AnsiConsole.MarkupLine($"[yellow]⚠ {Markup.Escape(message)}[/]");
    }

    /// <summary>
    /// Writes a verbose/debug message to the console with grey markup.
    /// </summary>
    /// <param name="message">The verbose message.</param>
    public static void WriteVerbose(string message)
    {
        AnsiConsole.MarkupLine($"[grey]{Markup.Escape(message)}[/]");
    }

    private static string FormatBytes(long? bytes)
    {
        if (bytes is null) return "-";

        return bytes.Value switch
        {
            < 1024 => $"{bytes} B",
            < 1024 * 1024 => $"{bytes.Value / 1024.0:F1} KB",
            < 1024 * 1024 * 1024 => $"{bytes.Value / (1024.0 * 1024):F1} MB",
            _ => $"{bytes.Value / (1024.0 * 1024 * 1024):F2} GB"
        };
    }

    private static string Truncate(string? value, int maxLength)
    {
        if (value is null) return "-";
        return value.Length <= maxLength ? value : string.Concat(value.AsSpan(0, maxLength - 1), "…");
    }
}
