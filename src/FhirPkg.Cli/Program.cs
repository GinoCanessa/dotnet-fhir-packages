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
        RootCommand rootCommand = BuildRootCommand();
        ParseResult parseResult = rootCommand.Parse(args);
        return await parseResult.InvokeAsync(new InvocationConfiguration(), CancellationToken.None);
    }

    /// <summary>
    /// Builds the root <c>fhir-pkg</c> command with all global options and subcommands.
    /// </summary>
    /// <returns>A fully configured <see cref="RootCommand"/>.</returns>
    internal static RootCommand BuildRootCommand()
    {
        RootCommand rootCommand = new RootCommand(
            "fhir-pkg — CLI tool for managing FHIR packages: install, restore, list, search, and cache management.");

        // Global options (Recursive = true makes them available to all subcommands)
        Option<string?> cachePathOption = new Option<string?>("--package-cache-folder")
        {
            Description = "Path to the local FHIR package cache directory.",
            DefaultValueFactory = _ => Environment.GetEnvironmentVariable("PACKAGE_CACHE_FOLDER"),
            Recursive = true
        };

        Option<bool> verboseOption = new Option<bool>("--verbose", "-v")
        {
            Description = "Enable verbose output.",
            DefaultValueFactory = _ => isTruthy(Environment.GetEnvironmentVariable("FHIR_PKG_VERBOSE")),
            Recursive = true
        };

        Option<bool> quietOption = new Option<bool>("--quiet", "-q")
        {
            Description = "Suppress all non-essential output.",
            Recursive = true
        };

        Option<bool> noColorOption = new Option<bool>("--no-color")
        {
            Description = "Disable colored output.",
            DefaultValueFactory = _ => isTruthy(Environment.GetEnvironmentVariable("NO_COLOR")),
            Recursive = true
        };

        Option<bool> jsonOption = new Option<bool>("--json")
        {
            Description = "Output results as JSON for machine consumption.",
            DefaultValueFactory = _ => isTruthy(Environment.GetEnvironmentVariable("FHIR_PKG_JSON")),
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

    private static bool isTruthy(string? value) =>
        value is "1" ||
        string.Equals(value, "true", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(value, "yes", StringComparison.OrdinalIgnoreCase);
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
        FhirPackageManagerOptions options = new FhirPackageManagerOptions();

        // Load optional config file (current directory first, then home directory)
        ConfigFile? configFromFile = loadConfigFile();
        if (configFromFile is not null)
        {
            mergeConfig(options, configFromFile);
        }

        // CLI flags take precedence over config file
        if (CachePath is not null)
        {
            options.CachePath = CachePath;
        }

        return options;
    }

    private static ConfigFile? loadConfigFile()
    {
        string[] candidates =
        [
            Path.Combine(Directory.GetCurrentDirectory(), ".fhir-pkg.json"),
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".fhir-pkg.json")
        ];

        JsonSerializerOptions jso = new()
        {
            PropertyNameCaseInsensitive = true
        };

        foreach (string? path in candidates)
        {
            if (!File.Exists(path)) continue;

            try
            {
                string json = File.ReadAllText(path);
                return JsonSerializer.Deserialize<ConfigFile>(json, jso);
            }
            catch (Exception ex)
            {
                // Log warning for malformed config files when verbose output is available
                Console.Error.WriteLine($"Warning: Failed to parse config file '{path}': {ex.Message}");
            }
        }

        return null;
    }

    private static void mergeConfig(FhirPackageManagerOptions target, ConfigFile config)
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
            foreach (ConfigRegistry reg in config.Registries)
            {
                if (reg.Url is null) continue;

                _ = Enum.TryParse<RegistryType>(reg.Type, ignoreCase: true, out RegistryType regType);
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
/// Holds the global option instances created during root command construction.
/// Initialized once by <see cref="Program.BuildRootCommand"/> and used by
/// <see cref="ParseResultExtensions.GetGlobalOptions"/> to extract values from parse results.
/// </summary>
internal static class GlobalOptionsBinder
{
    private static Option<string?>? _cachePathOption;
    private static Option<bool>? _verboseOption;
    private static Option<bool>? _quietOption;
    private static Option<bool>? _noColorOption;
    private static Option<bool>? _jsonOption;

    /// <summary>The --package-cache-folder option.</summary>
    public static Option<string?> CachePathOption
    {
        get => _cachePathOption ?? throw new InvalidOperationException("GlobalOptionsBinder has not been initialized. Call BuildRootCommand first.");
        set => _cachePathOption = value;
    }

    /// <summary>The --verbose / -v option.</summary>
    public static Option<bool> VerboseOption
    {
        get => _verboseOption ?? throw new InvalidOperationException("GlobalOptionsBinder has not been initialized. Call BuildRootCommand first.");
        set => _verboseOption = value;
    }

    /// <summary>The --quiet / -q option.</summary>
    public static Option<bool> QuietOption
    {
        get => _quietOption ?? throw new InvalidOperationException("GlobalOptionsBinder has not been initialized. Call BuildRootCommand first.");
        set => _quietOption = value;
    }

    /// <summary>The --no-color option.</summary>
    public static Option<bool> NoColorOption
    {
        get => _noColorOption ?? throw new InvalidOperationException("GlobalOptionsBinder has not been initialized. Call BuildRootCommand first.");
        set => _noColorOption = value;
    }

    /// <summary>The --json option.</summary>
    public static Option<bool> JsonOption
    {
        get => _jsonOption ?? throw new InvalidOperationException("GlobalOptionsBinder has not been initialized. Call BuildRootCommand first.");
        set => _jsonOption = value;
    }
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
        bool noColor = parseResult.GetValue(GlobalOptionsBinder.NoColorOption);
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
