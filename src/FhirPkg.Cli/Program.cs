// Copyright (c) Gino Canessa. Licensed under the MIT License.

using System.CommandLine;
using System.Text.Json;
using FhirPkg.Cli.Commands;
using FhirPkg.Cli.Formatting;
using FhirPkg.Models;
using FhirPkg.Registry;

namespace FhirPkg.Cli;

/// <summary>
/// Entry point for the <c>fhir-pkg</c> CLI tool.
/// Configures the root command, global options, subcommands, config loading, and exit-code mapping.
/// </summary>
internal static class Program
{
    /// <summary>
    /// Application entry point. Builds the command tree and invokes the parser.
    /// </summary>
    /// <param name="args">Command-line arguments.</param>
    /// <returns>Process exit code.</returns>
    public static async Task<int> Main(string[] args)
    {
        var rootCommand = BuildRootCommand();
        var parseResult = rootCommand.Parse(args);
        return await parseResult.InvokeAsync(new InvocationConfiguration(), CancellationToken.None);
    }

    /// <summary>
    /// Builds the root <c>fhir-pkg</c> command with all global options and subcommands.
    /// </summary>
    /// <returns>A fully configured <see cref="RootCommand"/>.</returns>
    internal static RootCommand BuildRootCommand()
    {
        var rootCommand = new RootCommand(
            "fhir-pkg — CLI tool for managing FHIR packages: install, restore, list, search, and cache management.");

        // Global options (Recursive = true makes them available to all subcommands)
        var cachePathOption = new Option<string?>("--cache-path", "-c")
        {
            Description = "Path to the local FHIR package cache directory.",
            DefaultValueFactory = _ => Environment.GetEnvironmentVariable("FHIR_PACKAGE_CACHE"),
            Recursive = true
        };

        var verboseOption = new Option<bool>("--verbose", "-v")
        {
            Description = "Enable verbose output.",
            DefaultValueFactory = _ => IsTruthy(Environment.GetEnvironmentVariable("FHIR_PKG_VERBOSE")),
            Recursive = true
        };

        var quietOption = new Option<bool>("--quiet", "-q")
        {
            Description = "Suppress all non-essential output.",
            Recursive = true
        };

        var noColorOption = new Option<bool>("--no-color")
        {
            Description = "Disable colored output.",
            DefaultValueFactory = _ => IsTruthy(Environment.GetEnvironmentVariable("NO_COLOR")),
            Recursive = true
        };

        var jsonOption = new Option<bool>("--json")
        {
            Description = "Output results as JSON for machine consumption.",
            DefaultValueFactory = _ => IsTruthy(Environment.GetEnvironmentVariable("FHIR_PKG_JSON")),
            Recursive = true
        };

        rootCommand.Add(cachePathOption);
        rootCommand.Add(verboseOption);
        rootCommand.Add(quietOption);
        rootCommand.Add(noColorOption);
        rootCommand.Add(jsonOption);

        // Store option references so command handlers can retrieve them
        GlobalOptionsBinder.CachePathOption = cachePathOption;
        GlobalOptionsBinder.VerboseOption = verboseOption;
        GlobalOptionsBinder.QuietOption = quietOption;
        GlobalOptionsBinder.NoColorOption = noColorOption;
        GlobalOptionsBinder.JsonOption = jsonOption;

        // Add subcommands
        rootCommand.Add(InstallCommand.Build());
        rootCommand.Add(RestoreCommand.Build());
        rootCommand.Add(ListCommand.Build());
        rootCommand.Add(RemoveCommand.Build());
        rootCommand.Add(CleanCommand.Build());
        rootCommand.Add(SearchCommand.Build());
        rootCommand.Add(InfoCommand.Build());
        rootCommand.Add(ResolveCommand.Build());
        rootCommand.Add(PublishCommand.Build());

        return rootCommand;
    }

    private static bool IsTruthy(string? value) =>
        value is "1" or "true" or "yes" or "True" or "TRUE" or "YES";
}

// ─────────────────────────────────────────────────────────────
//  Shared infrastructure types
// ─────────────────────────────────────────────────────────────

/// <summary>
/// Standard exit codes for the <c>fhir-pkg</c> CLI tool.
/// </summary>
internal static class ExitCodes
{
    /// <summary>Operation completed successfully.</summary>
    public const int Success = 0;

    /// <summary>General / unclassified error.</summary>
    public const int GeneralError = 1;

    /// <summary>Invalid command-line arguments.</summary>
    public const int InvalidArgs = 2;

    /// <summary>Requested resource (package, file) not found.</summary>
    public const int NotFound = 3;

    /// <summary>Network / HTTP error communicating with a registry.</summary>
    public const int NetworkError = 4;

    /// <summary>Checksum verification failed.</summary>
    public const int ChecksumFail = 5;

    /// <summary>Dependency resolution could not complete.</summary>
    public const int DependencyResolutionFail = 6;

    /// <summary>Error reading from or writing to the local cache.</summary>
    public const int CacheError = 7;

    /// <summary>Authentication or authorization failure.</summary>
    public const int AuthError = 8;
}

/// <summary>
/// Holds resolved global option values for use by command handlers.
/// </summary>
internal sealed record GlobalOptions
{
    /// <summary>Custom package cache directory, or <c>null</c> for the default.</summary>
    public string? CachePath { get; init; }

    /// <summary>Whether verbose output is enabled.</summary>
    public bool Verbose { get; init; }

    /// <summary>Whether to suppress non-essential output.</summary>
    public bool Quiet { get; init; }

    /// <summary>Whether colored output is disabled.</summary>
    public bool NoColor { get; init; }

    /// <summary>Whether to produce JSON output.</summary>
    public bool Json { get; init; }

    /// <summary>
    /// Builds a <see cref="FhirPackageManagerOptions"/> instance by merging global CLI flags
    /// with optional <c>.fhir-pkg.json</c> config files.
    /// </summary>
    /// <returns>A configured <see cref="FhirPackageManagerOptions"/> instance.</returns>
    public FhirPackageManagerOptions BuildManagerOptions()
    {
        var options = new FhirPackageManagerOptions();

        // Load optional config file (current directory first, then home directory)
        var configFromFile = LoadConfigFile();
        if (configFromFile is not null)
        {
            MergeConfig(options, configFromFile);
        }

        // CLI flags take precedence over config file
        if (CachePath is not null)
        {
            options.CachePath = CachePath;
        }

        return options;
    }

    private static ConfigFile? LoadConfigFile()
    {
        var candidates = new[]
        {
            Path.Combine(Directory.GetCurrentDirectory(), ".fhir-pkg.json"),
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".fhir-pkg.json")
        };

        foreach (var path in candidates)
        {
            if (!File.Exists(path)) continue;

            try
            {
                var json = File.ReadAllText(path);
                return JsonSerializer.Deserialize<ConfigFile>(json, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });
            }
            catch
            {
                // Silently ignore malformed config files
            }
        }

        return null;
    }

    private static void MergeConfig(FhirPackageManagerOptions target, ConfigFile config)
    {
        if (config.CachePath is not null)
        {
            target.CachePath = config.CachePath;
        }

        if (config.HttpTimeout is > 0)
        {
            target.HttpTimeout = TimeSpan.FromSeconds(config.HttpTimeout.Value);
        }

        if (config.IncludeCiBuilds is not null)
        {
            target.IncludeCiBuilds = config.IncludeCiBuilds.Value;
        }

        if (config.VerifyChecksums is not null)
        {
            target.VerifyChecksums = config.VerifyChecksums.Value;
        }

        if (config.Registries is { Count: > 0 })
        {
            target.Registries.Clear();
            foreach (var reg in config.Registries)
            {
                if (reg.Url is null) continue;

                _ = Enum.TryParse<RegistryType>(reg.Type, ignoreCase: true, out var regType);
                target.Registries.Add(new RegistryEndpoint
                {
                    Url = reg.Url,
                    Type = regType,
                    AuthHeaderValue = reg.Auth
                });
            }
        }
    }
}

/// <summary>
/// Deserialization model for the optional <c>.fhir-pkg.json</c> configuration file.
/// </summary>
internal sealed class ConfigFile
{
    /// <summary>Custom package cache path.</summary>
    public string? CachePath { get; set; }

    /// <summary>HTTP timeout in seconds.</summary>
    public int? HttpTimeout { get; set; }

    /// <summary>Whether to include CI builds.</summary>
    public bool? IncludeCiBuilds { get; set; }

    /// <summary>Whether to verify checksums.</summary>
    public bool? VerifyChecksums { get; set; }

    /// <summary>Custom registry definitions.</summary>
    public List<ConfigRegistry>? Registries { get; set; }
}

/// <summary>
/// Registry entry within a <see cref="ConfigFile"/>.
/// </summary>
internal sealed class ConfigRegistry
{
    /// <summary>Registry base URL.</summary>
    public string? Url { get; set; }

    /// <summary>Registry type (FhirNpm, FhirCiBuild, FhirHttp, Npm).</summary>
    public string? Type { get; set; }

    /// <summary>Authentication header value.</summary>
    public string? Auth { get; set; }
}

/// <summary>
/// Static holder for global option references, enabling retrieval from <see cref="ParseResult"/>.
/// </summary>
internal static class GlobalOptionsBinder
{
    /// <summary>The --cache-path / -c option.</summary>
    public static Option<string?> CachePathOption { get; set; } = null!;

    /// <summary>The --verbose / -v option.</summary>
    public static Option<bool> VerboseOption { get; set; } = null!;

    /// <summary>The --quiet / -q option.</summary>
    public static Option<bool> QuietOption { get; set; } = null!;

    /// <summary>The --no-color option.</summary>
    public static Option<bool> NoColorOption { get; set; } = null!;

    /// <summary>The --json option.</summary>
    public static Option<bool> JsonOption { get; set; } = null!;
}

/// <summary>
/// Extension methods for extracting global options from <see cref="ParseResult"/>.
/// </summary>
internal static class ParseResultExtensions
{
    /// <summary>
    /// Retrieves all global options from the current parse result.
    /// </summary>
    /// <param name="parseResult">The parse result.</param>
    /// <returns>A populated <see cref="GlobalOptions"/> record.</returns>
    public static GlobalOptions GetGlobalOptions(this ParseResult parseResult)
    {
        var noColor = parseResult.GetValue(GlobalOptionsBinder.NoColorOption);
        if (noColor)
        {
            // Disable Spectre.Console ANSI formatting
            Environment.SetEnvironmentVariable("NO_COLOR", "1");
        }

        return new GlobalOptions
        {
            CachePath = parseResult.GetValue(GlobalOptionsBinder.CachePathOption),
            Verbose = parseResult.GetValue(GlobalOptionsBinder.VerboseOption),
            Quiet = parseResult.GetValue(GlobalOptionsBinder.QuietOption),
            NoColor = noColor,
            Json = parseResult.GetValue(GlobalOptionsBinder.JsonOption)
        };
    }
}
