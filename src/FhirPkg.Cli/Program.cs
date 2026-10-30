// Copyright (c) Gino Canessa. Licensed under the MIT License.

using System.CommandLine;
using System.CommandLine.Invocation;
using System.CommandLine.Parsing;
using FhirPkg.Cli.Commands;
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
        bool retryOptionError = parseResult.Action is ParseErrorAction
            && parseResult.Errors.Any(IsRetryOptionError);
        int exitCode = await parseResult.InvokeAsync(new InvocationConfiguration(), CancellationToken.None);
        return retryOptionError ? ExitCodes.InvalidArgs : exitCode;
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

        Option<bool?> unmodifiedOption = new Option<bool?>("--unmodified")
        {
            Description = "Preserve distributed package paths and keep generated resource indexes in memory.",
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

        Option<string?> gitHubTokenOption = new Option<string?>("--github-token")
        {
            Description = "GitHub token used only for api.github.com repository lookups when choosing the canonical CI build repository.",
            Recursive = true
        };

        Option<int?> fsRetryCountOption = new Option<int?>("--fs-retry-count")
        {
            Description = "Filesystem retries after the first attempt (flag overrides environment, then SDK default).",
            Recursive = true
        };

        Option<int?> fsMinimumDelayOption = new Option<int?>("--fs-min-delay-ms")
        {
            Description = "Minimum filesystem retry delay in milliseconds.",
            Recursive = true
        };

        Option<int?> fsMaximumJitterOption = new Option<int?>("--fs-jitter-max-ms")
        {
            Description = "Inclusive maximum additive filesystem retry jitter in milliseconds.",
            Recursive = true
        };

        rootCommand.Add(cachePathOption);
        rootCommand.Add(unmodifiedOption);
        rootCommand.Add(verboseOption);
        rootCommand.Add(quietOption);
        rootCommand.Add(noColorOption);
        rootCommand.Add(jsonOption);
        rootCommand.Add(gitHubTokenOption);
        rootCommand.Add(fsRetryCountOption);
        rootCommand.Add(fsMinimumDelayOption);
        rootCommand.Add(fsMaximumJitterOption);

        // Store option references so command handlers can retrieve them
        GlobalOptionsBinder.CachePathOption = cachePathOption;
        GlobalOptionsBinder.UnmodifiedOption = unmodifiedOption;
        GlobalOptionsBinder.VerboseOption = verboseOption;
        GlobalOptionsBinder.QuietOption = quietOption;
        GlobalOptionsBinder.NoColorOption = noColorOption;
        GlobalOptionsBinder.JsonOption = jsonOption;
        GlobalOptionsBinder.GitHubTokenOption = gitHubTokenOption;
        GlobalOptionsBinder.FileSystemRetryCountOption = fsRetryCountOption;
        GlobalOptionsBinder.FileSystemMinimumDelayOption = fsMinimumDelayOption;
        GlobalOptionsBinder.FileSystemMaximumJitterOption = fsMaximumJitterOption;

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

    private static bool IsRetryOptionError(ParseError error)
    {
        // Conversion errors belong to an argument below the option; missing-value
        // errors belong to the option itself. Do not remap unrelated parse errors.
        for (SymbolResult? result = error.SymbolResult; result is not null; result = result.Parent)
        {
            if (result is OptionResult optionResult
                && optionResult.Option.Name is "--fs-retry-count"
                    or "--fs-min-delay-ms"
                    or "--fs-jitter-max-ms")
            {
                return true;
            }
        }

        return false;
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

    /// <summary>Whether to preserve package payloads, or <c>null</c> to use configuration defaults.</summary>
    public bool? Unmodified { get; init; }

    /// <summary>Whether verbose output is enabled.</summary>
    public bool Verbose { get; init; }

    /// <summary>Whether to suppress non-essential output.</summary>
    public bool Quiet { get; init; }

    /// <summary>Whether colored output is disabled.</summary>
    public bool NoColor { get; init; }

    /// <summary>Whether to produce JSON output.</summary>
    public bool Json { get; init; }

    /// <summary>
    /// Optional GitHub token used only for the <c>api.github.com</c> repository lookups
    /// behind canonical CI build repository selection, or <c>null</c> for unauthenticated
    /// requests.
    /// </summary>
    public string? GitHubToken { get; init; }

    /// <summary>Explicit filesystem retry count, or <c>null</c> when unspecified.</summary>
    public int? FileSystemRetryCount { get; init; }

    /// <summary>Explicit minimum retry delay, or <c>null</c> when unspecified.</summary>
    public int? FileSystemMinimumDelayMilliseconds { get; init; }

    /// <summary>Explicit maximum additive jitter, or <c>null</c> when unspecified.</summary>
    public int? FileSystemMaximumJitterMilliseconds { get; init; }

    /// <summary>Optional config loader override for isolated command tests; <c>null</c> restores file loading.</summary>
    internal static Func<ConfigFile?>? ConfigLoaderOverride { get; set; }

    /// <summary>
    /// Builds a <see cref="FhirPackageManagerOptions"/> instance by merging global CLI flags
    /// with optional <c>.fhir-pkg.json</c> config files.
    /// </summary>
    /// <returns>A configured <see cref="FhirPackageManagerOptions"/> instance.</returns>
    public FhirPackageManagerOptions BuildManagerOptions() =>
        CliInvocationContext.Create(this).CreateManagerOptions();

    internal FhirPackageManagerOptions MergeManagerOptions(ConfigFile? configFromFile)
    {
        FhirPackageManagerOptions options = new FhirPackageManagerOptions();

        if (configFromFile is not null)
        {
            mergeConfig(options, configFromFile);
        }

        // CLI flags take precedence over config file
        if (CachePath is not null)
        {
            options.CachePath = CachePath;
        }

        if (Unmodified is not null)
        {
            options.Unmodified = Unmodified.Value;
        }

        if (GitHubToken is not null)
        {
            options.GitHubToken = GitHubToken;
        }

        return options;
    }

    private static void mergeConfig(FhirPackageManagerOptions target, ConfigFile config)
    {
        if (config.CachePath is not null)
        {
            target.CachePath = config.CachePath;
        }

        if (config.Unmodified is not null)
        {
            target.Unmodified = config.Unmodified.Value;
        }

        if (config.HttpTimeout is > 0)
        {
            target.HttpTimeout = TimeSpan.FromSeconds(config.HttpTimeout.Value);
        }

        if (config.IncludeCiBuilds is not null)
        {
            target.IncludeCiBuilds = config.IncludeCiBuilds.Value;
        }

        if (config.GitHubToken is not null)
        {
            target.GitHubToken = config.GitHubToken;
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

    /// <summary>Whether to preserve distributed payloads and avoid resource-index writes.</summary>
    public bool? Unmodified { get; set; }

    /// <summary>HTTP timeout in seconds.</summary>
    public int? HttpTimeout { get; set; }

    /// <summary>Whether to include CI builds.</summary>
    public bool? IncludeCiBuilds { get; set; }

    /// <summary>
    /// GitHub token used only for the <c>api.github.com</c> repository lookups behind
    /// canonical CI build repository selection.
    /// </summary>
    public string? GitHubToken { get; set; }

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
    private static Option<bool?>? _unmodifiedOption;
    private static Option<bool>? _verboseOption;
    private static Option<bool>? _quietOption;
    private static Option<bool>? _noColorOption;
    private static Option<bool>? _jsonOption;
    private static Option<string?>? _gitHubTokenOption;
    private static Option<int?>? _fileSystemRetryCountOption;
    private static Option<int?>? _fileSystemMinimumDelayOption;
    private static Option<int?>? _fileSystemMaximumJitterOption;

    /// <summary>The --package-cache-folder option.</summary>
    public static Option<string?> CachePathOption
    {
        get => _cachePathOption ?? throw new InvalidOperationException("GlobalOptionsBinder has not been initialized. Call BuildRootCommand first.");
        set => _cachePathOption = value;
    }

    /// <summary>The --unmodified option.</summary>
    public static Option<bool?> UnmodifiedOption
    {
        get => _unmodifiedOption ?? throw new InvalidOperationException("GlobalOptionsBinder has not been initialized. Call BuildRootCommand first.");
        set => _unmodifiedOption = value;
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

    /// <summary>The --github-token option.</summary>
    public static Option<string?> GitHubTokenOption
    {
        get => _gitHubTokenOption ?? throw new InvalidOperationException("GlobalOptionsBinder has not been initialized. Call BuildRootCommand first.");
        set => _gitHubTokenOption = value;
    }

    /// <summary>The --fs-retry-count option.</summary>
    public static Option<int?> FileSystemRetryCountOption
    {
        get => _fileSystemRetryCountOption ?? throw new InvalidOperationException("GlobalOptionsBinder has not been initialized. Call BuildRootCommand first.");
        set => _fileSystemRetryCountOption = value;
    }

    /// <summary>The --fs-min-delay-ms option.</summary>
    public static Option<int?> FileSystemMinimumDelayOption
    {
        get => _fileSystemMinimumDelayOption ?? throw new InvalidOperationException("GlobalOptionsBinder has not been initialized. Call BuildRootCommand first.");
        set => _fileSystemMinimumDelayOption = value;
    }

    /// <summary>The --fs-jitter-max-ms option.</summary>
    public static Option<int?> FileSystemMaximumJitterOption
    {
        get => _fileSystemMaximumJitterOption ?? throw new InvalidOperationException("GlobalOptionsBinder has not been initialized. Call BuildRootCommand first.");
        set => _fileSystemMaximumJitterOption = value;
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
            Unmodified = parseResult.GetValue(GlobalOptionsBinder.UnmodifiedOption),
            Verbose = parseResult.GetValue(GlobalOptionsBinder.VerboseOption),
            Quiet = parseResult.GetValue(GlobalOptionsBinder.QuietOption),
            NoColor = noColor,
            Json = parseResult.GetValue(GlobalOptionsBinder.JsonOption),
            GitHubToken = parseResult.GetValue(GlobalOptionsBinder.GitHubTokenOption),
            FileSystemRetryCount = parseResult.GetValue(GlobalOptionsBinder.FileSystemRetryCountOption),
            FileSystemMinimumDelayMilliseconds = parseResult.GetValue(GlobalOptionsBinder.FileSystemMinimumDelayOption),
            FileSystemMaximumJitterMilliseconds = parseResult.GetValue(GlobalOptionsBinder.FileSystemMaximumJitterOption)
        };
    }
}
