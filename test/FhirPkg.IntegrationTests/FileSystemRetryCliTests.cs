// Copyright (c) Gino Canessa. Licensed under the MIT License.

using System.CommandLine;
using System.ComponentModel;
using System.Formats.Tar;
using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using FhirPkg.Cache;
using FhirPkg.Cli;
using FhirPkg.Indexing;
using FhirPkg.Installation;
using FhirPkg.IO;
using FhirPkg.Models;
using FhirPkg.Registry;
using FhirPkg.Resolution;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Win32.SafeHandles;
using Shouldly;
using Spectre.Console;
using Xunit;

namespace FhirPkg.IntegrationTests;

[Collection("InstallOutput")]
[Trait("Category", "Integration")]
public sealed class FileSystemRetryCliTests : IDisposable
{
    private const string FactoryStop = "Factory stopped after bootstrap.";
    private static readonly string[] s_variables =
    [
        FileSystemRetryOptions.RetryCountEnvironmentVariable,
        FileSystemRetryOptions.MinimumDelayMillisecondsEnvironmentVariable,
        FileSystemRetryOptions.MaximumJitterMillisecondsEnvironmentVariable,
    ];

    private readonly string?[] _originalEnvironment = s_variables.Select(Environment.GetEnvironmentVariable).ToArray();
    private readonly Func<ConfigFile?>? _originalLoader = GlobalOptions.ConfigLoaderOverride;
    private readonly Func<FhirPackageManagerOptions, FhirPackageManager>? _originalFactory = ManagerFactory.FactoryOverride;
    private readonly Func<FileSystemContext, FileSystemContext>? _originalContextFactory =
        CliInvocationContext.FileSystemContextOverride;
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"fhir-cli-retry-{Guid.NewGuid():N}");
    private readonly CliFileSystemPrimitives _files;
    private readonly List<FileSystemContext> _contexts = [];
    private int _factoryCalls;
    private FhirPackageManagerOptions? _receivedOptions;

    public FileSystemRetryCliTests()
    {
        Directory.CreateDirectory(CurrentDirectory);
        Directory.CreateDirectory(HomeDirectory);
        Directory.CreateDirectory(Path.Combine(_root, "temp"));
        _files = new CliFileSystemPrimitives(CurrentDirectory, HomeDirectory, Path.Combine(_root, "temp"));
        SetEnvironment(null, null, null);
        GlobalOptions.ConfigLoaderOverride = null;
        ManagerFactory.FactoryOverride = null;
        CliInvocationContext.FileSystemContextOverride = resolved =>
        {
            FileSystemContext context = new(
                resolved.Policy, resolved.HasExplicitValues, _files, _files.CreateExecutor());
            _contexts.Add(context);
            return context;
        };
    }

    private string CurrentDirectory => Path.Combine(_root, "cwd");
    private string HomeDirectory => Path.Combine(_root, "home");
    private string CurrentConfig => Path.Combine(CurrentDirectory, ".fhir-pkg.json");
    private string HomeConfig => Path.Combine(HomeDirectory, ".fhir-pkg.json");
    private string CachePath => Path.Combine(_root, "cache");

    public static TheoryData<string> PrecedenceCases =>
    [
        "defaults",
        "environment",
        "count",
        "minimum",
        "jitter",
        "explicit-defaults",
    ];

    [Theory]
    [MemberData(nameof(PrecedenceCases))]
    public async Task Flags_OverrideEnvironmentPerValue(string setting)
    {
        string[] flags;
        int count;
        int minimum;
        int jitter;
        bool explicitlyConfigured = true;
        switch (setting)
        {
            case "defaults":
                flags = [];
                count = FileSystemRetryOptions.DefaultRetryCount;
                minimum = FileSystemRetryOptions.DefaultMinimumDelayMilliseconds;
                jitter = FileSystemRetryOptions.DefaultMaximumJitterMilliseconds;
                explicitlyConfigured = false;
                break;
            case "environment":
                SetEnvironment("2", "7", "3");
                flags = [];
                count = 2;
                minimum = 7;
                jitter = 3;
                explicitlyConfigured = false;
                break;
            case "count":
                SetEnvironment("invalid", "7", "3");
                flags = ["--fs-retry-count", "0"];
                count = 0;
                minimum = 7;
                jitter = 3;
                break;
            case "minimum":
                SetEnvironment("2", "invalid", "3");
                flags = ["--fs-min-delay-ms", "0"];
                count = 2;
                minimum = 0;
                jitter = 3;
                break;
            case "jitter":
                SetEnvironment("2", "7", "invalid");
                flags = ["--fs-jitter-max-ms", "0"];
                count = 2;
                minimum = 7;
                jitter = 0;
                break;
            default:
                SetEnvironment("invalid", "invalid", "invalid");
                count = FileSystemRetryOptions.DefaultRetryCount;
                minimum = FileSystemRetryOptions.DefaultMinimumDelayMilliseconds;
                jitter = FileSystemRetryOptions.DefaultMaximumJitterMilliseconds;
                flags = RetryFlags(count, minimum, jitter);
                break;
        }

        for (int attempt = 0; attempt <= count; attempt++)
            _files.Enqueue("Probe", CurrentConfig, Failure<FileSystemProbeResult>("Probe", CurrentConfig, attempt));
        StopAtFactory();

        InvocationOutput output = await RunMainAsync(["list", .. flags]);

        output.ExitCode.ShouldBe(ExitCodes.GeneralError, output.StandardError);
        output.StandardOutput.ShouldContain(FactoryStop);
        _factoryCalls.ShouldBe(1);
        _receivedOptions.ShouldNotBeNull();
        _receivedOptions.FileSystemRetry.RetryCount.ShouldBe(count);
        _receivedOptions.FileSystemRetry.MinimumDelayMilliseconds.ShouldBe(minimum);
        _receivedOptions.FileSystemRetry.MaximumJitterMilliseconds.ShouldBe(jitter);
        FileSystemContext context = _contexts.ShouldHaveSingleItem();
        context.HasExplicitValues.ShouldBe(explicitlyConfigured);
        _receivedOptions.FileSystemRetry.HasExplicitValues.ShouldBeTrue();
        _files.Count("GetCurrentDirectory").ShouldBe(1);
        _files.Count("GetHomeDirectory").ShouldBe(1);
        _files.Count("GetTempPath").ShouldBe(0);
        _files.Count("Probe", CurrentConfig).ShouldBe(count + 1);
        _files.Count("Probe", HomeConfig).ShouldBe(1);
        _files.Calls.Count.ShouldBe(count + 4);
        AssertAsyncWaits(Enumerable.Repeat(minimum + jitter, count).ToArray(), jitter);
        Directory.Exists(CachePath).ShouldBeFalse();
    }

    public static TheoryData<string, string, bool> InvalidHandlerCases
    {
        get
        {
            TheoryData<string, string, bool> cases = [];
            foreach (string handler in new[] { "clean", "info", "install", "list", "publish", "remove", "resolve", "restore", "search" })
            {
                foreach (string invalid in new[]
                {
                    "count", "minimum", "jitter", "sum", "empty-environment",
                    "malformed-environment", "overflow-environment", "negative-environment", "unshadowed-environment"
                })
                {
                    cases.Add(handler, invalid, false);
                    cases.Add(handler, invalid, true);
                }
            }

            return cases;
        }
    }

    [Theory]
    [MemberData(nameof(InvalidHandlerCases))]
    public async Task AllHandlers_InvalidPolicyExitsTwoBeforeFileSystemWork(
        string handler,
        string invalid,
        bool json)
    {
        string[] flags = invalid switch
        {
            "count" => ["--fs-retry-count", "-1"],
            "minimum" => ["--fs-min-delay-ms", "-1"],
            "jitter" => ["--fs-jitter-max-ms", "-1"],
            "sum" => ["--fs-min-delay-ms", "2147483647", "--fs-jitter-max-ms", "1"],
            "unshadowed-environment" => ["--fs-retry-count", "0"],
            _ => [],
        };
        switch (invalid)
        {
            case "empty-environment":
                SetVariable(s_variables[0], string.Empty);
                Environment.GetEnvironmentVariable(s_variables[0]).ShouldBe(string.Empty);
                break;
            case "malformed-environment":
                SetEnvironment(null, "invalid", null);
                break;
            case "overflow-environment":
                SetEnvironment(null, null, "2147483648");
                break;
            case "negative-environment":
                SetEnvironment("-1", null, null);
                break;
            case "unshadowed-environment":
                SetEnvironment("ignored", "invalid", null);
                break;
        }

        int configLoads = 0;
        GlobalOptions.ConfigLoaderOverride = () =>
        {
            configLoads++;
            throw new InvalidOperationException("Configuration must not be loaded.");
        };
        StopAtFactory();

        InvocationOutput output = await RunMainAsync([.. HandlerArguments(handler), .. flags], json);

        output.ExitCode.ShouldBe(ExitCodes.InvalidArgs, output.StandardError);
        configLoads.ShouldBe(0);
        _factoryCalls.ShouldBe(0);
        _contexts.ShouldBeEmpty();
        _files.Calls.ShouldBeEmpty();
        AssertAsyncWaits([], 0);
        Directory.Exists(CachePath).ShouldBeFalse();
        output.StandardError.ShouldBeEmpty();
        if (json)
        {
            using JsonDocument document = JsonDocument.Parse(output.StandardOutput);
            document.RootElement.EnumerateObject().Select(property => property.Name).ShouldBe(["error"]);
            document.RootElement.GetProperty("error").GetString().ShouldNotBeNullOrWhiteSpace();
        }
        else
        {
            output.StandardOutput.ShouldContain("✗ ");
            output.StandardOutput.ShouldNotContain("\"error\"");
        }
    }

    [Theory]
    [InlineData("probe")]
    [InlineData("cwd")]
    [InlineData("home")]
    public async Task FirstConfigProbe_UsesResolvedRetryPolicy(string fault)
    {
        SetEnvironment("0", "0", "0");
        string configPath = fault == "home" ? HomeConfig : CurrentConfig;
        await File.WriteAllTextAsync(configPath, """{"unmodified":true}""", TestContext.Current.CancellationToken);
        string operation = fault switch
        {
            "cwd" => "GetCurrentDirectory",
            "home" => "GetHomeDirectory",
            _ => "Probe",
        };
        string? path = fault == "probe" ? CurrentConfig : null;
        for (int attempt = 0; attempt < 2; attempt++)
        {
            if (fault == "probe")
                _files.Enqueue(operation, path, Failure<FileSystemProbeResult>(operation, path, attempt));
            else
                _files.Enqueue(operation, path, Failure<string>(operation, path, attempt));
        }
        StopAtFactory();

        InvocationOutput output = await RunMainAsync(["list", .. RetryFlags(2, 7, 3)]);

        output.ExitCode.ShouldBe(ExitCodes.GeneralError, output.StandardError);
        output.StandardError.ShouldBeEmpty();
        _factoryCalls.ShouldBe(1);
        _receivedOptions!.Unmodified.ShouldBeTrue();
        _files.Count(operation, path).ShouldBe(3);
        _files.Count("GetCurrentDirectory").ShouldBe(fault == "cwd" ? 3 : 1);
        _files.Count("GetHomeDirectory").ShouldBe(fault == "home" ? 3 : 0);
        _files.Count("Probe", CurrentConfig).ShouldBe(fault == "probe" ? 3 : 1);
        _files.Count("Probe", HomeConfig).ShouldBe(fault == "home" ? 1 : 0);
        _files.OpenCount(configPath).ShouldBe(1);
        _files.Count("ReadAtAsync", configPath).ShouldBe(2);
        _files.Count("ReadAt", configPath).ShouldBe(0);
        _files.Count("CloseFile", configPath).ShouldBe(1);
        AssertAsyncWaits([10, 10], 3);
        Directory.Exists(CachePath).ShouldBeFalse();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PublishTarballProbe_UsesFlagsBeforeManagerConstruction(bool exhaust)
    {
        SetEnvironment("0", "0", "0");
        int configLoads = 0;
        GlobalOptions.ConfigLoaderOverride = () =>
        {
            configLoads++;
            return null;
        };
        string tarball = Path.Combine(_root, "missing.tgz");
        int failures = exhaust ? 3 : 2;
        for (int attempt = 0; attempt < failures; attempt++)
            _files.Enqueue("Probe", tarball, Failure<FileSystemProbeResult>("Probe", tarball, attempt));
        StopAtFactory();

        InvocationOutput output = await RunMainAsync(
        [
            "publish", tarball, "--registry", "https://registry.invalid/", "--auth", "test-only",
            .. RetryFlags(2, 7, 3),
        ]);

        output.ExitCode.ShouldBe(exhaust ? ExitCodes.GeneralError : ExitCodes.NotFound);
        configLoads.ShouldBe(1);
        _factoryCalls.ShouldBe(0);
        _files.Count("Probe", tarball).ShouldBe(3);
        _files.Calls.Count.ShouldBe(3);
        AssertAsyncWaits([10, 10], 3);
        output.StandardError.ShouldBeEmpty();
        output.StandardOutput.ShouldContain(exhaust ? "attempts 3" : "Tarball not found:");
        output.StandardOutput.ShouldContain(tarball.Replace("\\", "\\\\", StringComparison.Ordinal));
        Directory.Exists(CachePath).ShouldBeFalse();
    }

    [Fact]
    public async Task Publish_ExistingTarballUsesSameContextAndStubHttpOnly()
    {
        string tarball = Path.Combine(_root, "publish.tgz");
        byte[] archive = CreateArchive();
        await File.WriteAllBytesAsync(tarball, archive, TestContext.Current.CancellationToken);
        _files.Enqueue("Probe", tarball, Failure<FileSystemProbeResult>("Probe", tarball, 0));
        _files.Enqueue("Probe", tarball, Failure<FileSystemProbeResult>("Probe", tarball, 1));
        int configLoads = 0;
        GlobalOptions.ConfigLoaderOverride = () =>
        {
            configLoads++;
            return null;
        };
        StubPublishHandler handler = new();
        using HttpClient http = new(handler);
        FhirPackageManager? manager = null;
        DiskPackageCache? cache = null;
        ManagerFactory.FactoryOverride = options =>
        {
            _factoryCalls++;
            _files.Count("Probe", tarball).ShouldBe(3);
            FileSystemContext context = _contexts.ShouldHaveSingleItem();
            SetEnvironment("invalid-after-bootstrap", "invalid-after-bootstrap", "invalid-after-bootstrap");
            FhirPackageManagerConfiguration configuration = FhirPackageManagerConfiguration.CreateResolved(options, context);
            cache = new DiskPackageCache(
                CachePath, null, null, configuration.InstallLimits, context,
                new SystemPackageCacheFileOperations(context),
                NullPackageCacheFaultObserver.Instance, NullPackageCacheContentionObserver.Instance);
            RegistryEndpoint endpoint = new() { Url = "https://registry.invalid/", Type = RegistryType.FhirNpm };
            FhirNpmRegistryClient registry = new(
                RegistryHttpTransport.CreateRedirectControlled(http, options.HttpTimeout, options.MaxRedirects),
                endpoint, NullLogger<FhirNpmRegistryClient>.Instance, context);
            VersionResolver versionResolver = new(registry, NullLogger.Instance);
            DependencyResolver dependencyResolver = new(registry, versionResolver, cache, NullLogger.Instance);
            manager = FhirPackageManager.CreateWithConfiguration(
                cache, registry, versionResolver, dependencyResolver, new PackageIndexer(NullLogger.Instance, context),
                configuration, NullLogger<FhirPackageManager>.Instance, null, http, redirectsControlled: true);
            return manager;
        };
        try
        {
            InvocationOutput output = await RunMainAsync(
            [
                "publish", tarball, "--registry", "https://registry.invalid/", "--auth", "test-only",
                .. RetryFlags(2, 7, 3),
            ]);

            output.ExitCode.ShouldBe(ExitCodes.Success, output.StandardOutput + output.StandardError);
            output.StandardError.ShouldBeEmpty();
            configLoads.ShouldBe(1);
            _factoryCalls.ShouldBe(1);
            handler.Requests.ShouldBe(1);
            handler.Payload.ShouldBe(archive);
            _files.Count("Probe", tarball).ShouldBe(4);
            _files.OpenCount(tarball).ShouldBe(1);
            _files.Count("CloseFile", tarball).ShouldBe(1);
            _files.Count("GetTempPath").ShouldBe(1);
            AssertAsyncWaits([10, 10], 3);
        }
        finally
        {
            manager?.Dispose();
            cache?.Dispose();
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("\0")]
    public async Task Publish_InvalidPathRemainsNotFoundWithoutPhysicalProbe(string path)
    {
        int configLoads = 0;
        GlobalOptions.ConfigLoaderOverride = () =>
        {
            configLoads++;
            return null;
        };
        StopAtFactory();

        InvocationOutput output = await RunMainAsync(
            ["publish", path, "--registry", "https://registry.invalid/", "--auth", "test-only"]);

        output.ExitCode.ShouldBe(ExitCodes.NotFound, output.StandardError);
        output.StandardOutput.ShouldContain("Tarball not found:");
        configLoads.ShouldBe(1);
        _factoryCalls.ShouldBe(0);
        _files.Count("Probe").ShouldBe(0);
        _files.Calls.Count.ShouldBe(path == "\0" ? 1 : 0);
        AssertAsyncWaits([], 0);
        Directory.Exists(CachePath).ShouldBeFalse();
    }

    public static TheoryData<string, string?, bool> MalformedFlags
    {
        get
        {
            TheoryData<string, string?, bool> cases = [];
            foreach (string flag in new[] { "--fs-retry-count", "--fs-min-delay-ms", "--fs-jitter-max-ms" })
            {
                foreach (string? value in new[] { "not-an-integer", "2147483648", "-2147483649", string.Empty, null })
                {
                    cases.Add(flag, value, false);
                    cases.Add(flag, value, true);
                }
            }

            return cases;
        }
    }

    [Theory]
    [MemberData(nameof(MalformedFlags))]
    public async Task MalformedRetryFlag_MainReturnsTwo(string flag, string? value, bool beforeCommand)
    {
        int configLoads = 0;
        GlobalOptions.ConfigLoaderOverride = () =>
        {
            configLoads++;
            return null;
        };
        StopAtFactory();
        string[] flagTokens = value is null ? [flag] : [flag, value];
        string[] arguments = beforeCommand ? [.. flagTokens, "list"] : ["list", .. flagTokens];

        InvocationOutput output = await RunMainAsync(arguments);

        output.ExitCode.ShouldBe(ExitCodes.InvalidArgs, output.StandardError);
        output.StandardError.ShouldNotBeNullOrWhiteSpace();
        configLoads.ShouldBe(0);
        _factoryCalls.ShouldBe(0);
        _contexts.ShouldBeEmpty();
        _files.Calls.ShouldBeEmpty();
        AssertAsyncWaits([], 0);
        Directory.Exists(CachePath).ShouldBeFalse();
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("operand")]
    [InlineData("integer")]
    [InlineData("validator")]
    [InlineData("required")]
    public async Task UnrelatedParseFailure_WithValidRetryFlag_PreservesParserExitCode(string error)
    {
        string[] arguments = error switch
        {
            "unknown" => ["nonexistent-command"],
            "operand" => ["install"],
            "integer" => ["restore", "--max-depth", "not-an-integer"],
            "validator" => ["install", "test.package#1.0.0", "--pre-release", "--no-pre-release"],
            _ => ["publish", Path.Combine(_root, "missing.tgz")],
        };
        InvocationOutput baseline = await RunParserAsync(arguments);
        InvocationOutput output = await RunMainAsync([.. arguments, "--fs-retry-count", "0"]);

        baseline.ExitCode.ShouldBe(ExitCodes.GeneralError);
        output.ExitCode.ShouldBe(baseline.ExitCode);
        output.StandardError.ShouldBe(baseline.StandardError);
        output.StandardOutput.ShouldBe(baseline.StandardOutput);
        _contexts.ShouldBeEmpty();
        _files.Calls.ShouldBeEmpty();
        AssertAsyncWaits([], 0);
    }

    [Fact]
    public async Task ConfigMalformedJson_IsNotRetried()
    {
        await File.WriteAllTextAsync(CurrentConfig, "{ not json", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(
            HomeConfig,
            """{"unmodified":true,"fileSystemRetry":{"retryCount":-1},"fsRetryCount":-1}""",
            TestContext.Current.CancellationToken);
        StopAtFactory();

        InvocationOutput output = await RunMainAsync(["list", .. RetryFlags(3, 7, 0)]);

        output.ExitCode.ShouldBe(ExitCodes.GeneralError);
        output.StandardError.Split("Warning:", StringSplitOptions.None).Length.ShouldBe(2);
        output.StandardError.ShouldContain(CurrentConfig);
        output.StandardError.ShouldNotContain("Filesystem operation");
        _factoryCalls.ShouldBe(1);
        _receivedOptions!.Unmodified.ShouldBeTrue();
        _receivedOptions.FileSystemRetry.RetryCount.ShouldBe(3);
        _files.Count("GetCurrentDirectory").ShouldBe(1);
        _files.Count("GetHomeDirectory").ShouldBe(1);
        foreach (string path in new[] { CurrentConfig, HomeConfig })
        {
            _files.Count("Probe", path).ShouldBe(1);
            _files.OpenCount(path).ShouldBe(1);
            _files.Count("ReadAtAsync", path).ShouldBe(2);
            _files.Count("ReadAt", path).ShouldBe(0);
            _files.Count("CloseFile", path).ShouldBe(1);
        }
        AssertAsyncWaits([], 0);
    }

    [Fact]
    public async Task ConfigurationWarningFallback_RetainsFinalIoContext()
    {
        await File.WriteAllTextAsync(CurrentConfig, "{}", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(HomeConfig, """{"unmodified":true}""", TestContext.Current.CancellationToken);
        for (int attempt = 0; attempt < 3; attempt++)
            _files.Enqueue("ReadAtAsync", CurrentConfig, Failure<int>("ReadAtAsync", CurrentConfig, attempt));
        StopAtFactory();

        InvocationOutput output = await RunMainAsync(["list", .. RetryFlags(2, 7, 0)]);

        output.ExitCode.ShouldBe(ExitCodes.GeneralError);
        _factoryCalls.ShouldBe(1);
        _receivedOptions!.Unmodified.ShouldBeTrue();
        output.StandardError.Split("Warning:", StringSplitOptions.None).Length.ShouldBe(2);
        output.StandardError.ShouldContain(CurrentConfig);
        output.StandardError.ShouldContain("failure 2");
        output.StandardError.ShouldNotContain("failure 0");
        output.StandardError.ShouldNotContain("failure 1");
        output.StandardError.ShouldContain("Filesystem operation 'ReadAtAsync'");
        output.StandardError.ShouldContain("attempts 3, retries 2, termination Exhausted");
        output.StandardError.ShouldContain("native error 5");
        output.StandardError.ShouldContain("native flags 0x123");
        _files.Count("GetCurrentDirectory").ShouldBe(1);
        _files.Count("GetHomeDirectory").ShouldBe(1);
        _files.Count("Probe", CurrentConfig).ShouldBe(1);
        _files.Count("Probe", HomeConfig).ShouldBe(1);
        _files.OpenCount(CurrentConfig).ShouldBe(1);
        _files.OpenCount(HomeConfig).ShouldBe(1);
        _files.Count("ReadAtAsync", CurrentConfig).ShouldBe(3);
        _files.Count("ReadAtAsync", HomeConfig).ShouldBe(2);
        _files.Count("CloseFile", CurrentConfig).ShouldBe(1);
        _files.Count("CloseFile", HomeConfig).ShouldBe(1);
        AssertAsyncWaits([7, 7], 0);
    }

    public static TheoryData<string, string, bool> PhysicalErrorCases
    {
        get
        {
            TheoryData<string, string, bool> cases = [];
            string[] handlers = ["clean", "list", "remove", "publish", "restore"];
            foreach (string handler in handlers)
            {
                string[] exceptions = handler switch
                {
                    "publish" => ["access"],
                    "restore" => ["missing"],
                    _ => ["io", "access"],
                };
                foreach (string exception in exceptions)
                {
                    cases.Add(handler, exception, false);
                    cases.Add(handler, exception, true);
                }
            }
            return cases;
        }
    }

    [Theory]
    [MemberData(nameof(PhysicalErrorCases))]
    public async Task PhysicalErrorCatches_PreserveFinalContextAndExitCode(
        string handler,
        string exceptionKind,
        bool json)
    {
        GlobalOptions.ConfigLoaderOverride = () => null;
        string[] arguments = HandlerArguments(handler);
        bool cacheFailure = handler is "clean" or "list" or "remove";
        string operation = handler == "restore" ? "ReadAtAsync" : "Probe";
        string path = cacheFailure
            ? CachePath
            : handler == "restore"
                ? Path.Combine(arguments[1], "package.json")
                : arguments[1];
        if (handler == "restore")
        {
            Directory.CreateDirectory(arguments[1]);
            await File.WriteAllTextAsync(
                path,
                """{"name":"retry.project","version":"1.0.0","dependencies":{}}""",
                TestContext.Current.CancellationToken);
        }
        for (int attempt = 0; attempt < 3; attempt++)
        {
            if (operation == "Probe")
                _files.Enqueue(operation, path, Failure<FileSystemProbeResult>(operation, path, attempt, exceptionKind));
            else
                _files.Enqueue(operation, path, Failure<int>(operation, path, attempt, exceptionKind));
        }

        InvocationOutput output = await RunMainAsync([.. arguments, .. RetryFlags(2, 7, 0)], json);

        int expectedExitCode = handler switch
        {
            "publish" => ExitCodes.AuthError,
            "restore" => ExitCodes.NotFound,
            _ => ExitCodes.CacheError,
        };
        string prefix = handler switch
        {
            "publish" => "Authentication error: ",
            "restore" => "Manifest not found: ",
            _ => "Cache error: ",
        };
        output.ExitCode.ShouldBe(expectedExitCode, output.StandardOutput + output.StandardError);
        output.StandardError.ShouldBeEmpty();
        string message;
        if (json)
        {
            using JsonDocument document = JsonDocument.Parse(output.StandardOutput);
            document.RootElement.EnumerateObject().Select(property => property.Name).ShouldBe(["error"]);
            message = document.RootElement.GetProperty("error").GetString().ShouldNotBeNull();
            message.ShouldStartWith(prefix);
        }
        else
        {
            output.StandardOutput.ShouldStartWith($"✗ {prefix}");
            message = output.StandardOutput.Replace("\r", string.Empty, StringComparison.Ordinal)
                .Replace("\n", string.Empty, StringComparison.Ordinal);
        }
        message.ShouldContain("Scripted physical failure 2.");
        message.ShouldNotContain("Scripted physical failure 0.");
        message.ShouldNotContain("Scripted physical failure 1.");
        string phase = cacheFailure
            ? "InspectDirectoryAncestors"
            : handler == "restore" ? "ReadFileBlock" : "ProbeFollowing";
        message.ShouldContain($"Filesystem operation '{operation}', phase '{phase}'");
        int attempts = cacheFailure ? 4 : 3;
        message.ShouldContain($"attempts {attempts}, retries 2, termination Exhausted");
        message.ShouldContain($"path '{path}'");
        message.ShouldContain("native error 5, native flags 0x123");
        _files.Count(operation, path).ShouldBe(3);
        _contexts.ShouldHaveSingleItem().Policy.RetryCount.ShouldBe(2);
        if (cacheFailure)
        {
            // Manager construction is a synchronous compatibility boundary.
            _files.SyncWaits.ShouldBe([7, 7]);
            _files.AsyncWaits.ShouldBeEmpty();
            _files.JitterBounds.ShouldBe([1L, 1L]);
            Directory.Exists(CachePath).ShouldBeFalse();
        }
        else
        {
            AssertAsyncWaits([7, 7], 0);
            if (handler == "restore")
            {
                _files.OpenCount(path).ShouldBe(1);
                _files.Count("CloseFile", path).ShouldBe(1);
            }
            else
            {
                _factoryCalls.ShouldBe(0);
                Directory.Exists(CachePath).ShouldBeFalse();
            }
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Invocation_LoadsConfigOnceAndRetainsPolicy(bool explicitFlag)
    {
        SetEnvironment("2", "7", "0");
        await File.WriteAllTextAsync(CurrentConfig, """{"includeCiBuilds":false}""", TestContext.Current.CancellationToken);
        string projectPath = Path.Combine(CurrentDirectory, "project");
        Directory.CreateDirectory(projectPath);
        string manifestPath = Path.Combine(projectPath, "package.json");
        await File.WriteAllTextAsync(
            manifestPath,
            """{"name":"retry.project","version":"1.0.0","dependencies":{}}""",
            TestContext.Current.CancellationToken);
        int mutations = 0;
        _files.BeforeCall = (operation, path) =>
        {
            if (operation == "ReadAtAsync" && path == CurrentConfig)
            {
                mutations++;
                SetEnvironment("invalid-after-bootstrap", "invalid-after-bootstrap", "invalid-after-bootstrap");
            }
        };
        _files.Enqueue("GetFullPath", "project", Failure<string>("GetFullPath", "project", 0));
        _files.Enqueue("GetFullPath", "project", Failure<string>("GetFullPath", "project", 1));
        _files.Enqueue("ReadAtAsync", manifestPath, Failure<int>("ReadAtAsync", manifestPath, 0));
        string[] flags = explicitFlag ? ["--fs-retry-count", "2"] : [];

        // Use the production resolved manager path, not a factory that reconstructs
        // a manager from populated options and accidentally changes explicitness.
        InvocationOutput output = await RunMainAsync(["restore", "project", .. flags], json: false, verbose: true);

        output.ExitCode.ShouldBe(ExitCodes.Success, output.StandardOutput + output.StandardError);
        output.StandardError.ShouldBeEmpty();
        output.StandardOutput.ShouldContain(projectPath);
        FileSystemContext context = _contexts.ShouldHaveSingleItem();
        context.Policy.RetryCount.ShouldBe(2);
        context.Policy.MinimumDelayMilliseconds.ShouldBe(7);
        context.Policy.MaximumJitterMilliseconds.ShouldBe(0);
        context.HasExplicitValues.ShouldBe(explicitFlag);
        context.ToOptionsSnapshot().HasExplicitValues.ShouldBeTrue();
        mutations.ShouldBe(2);
        _files.Count("GetCurrentDirectory").ShouldBe(1);
        _files.Count("GetHomeDirectory").ShouldBe(0);
        _files.Count("Probe", CurrentConfig).ShouldBe(1);
        _files.OpenCount(CurrentConfig).ShouldBe(1);
        _files.Count("ReadAtAsync", CurrentConfig).ShouldBe(2);
        _files.Count("GetFullPath", "project").ShouldBe(4);
        _files.Count("Probe", manifestPath).ShouldBe(1);
        _files.OpenCount(manifestPath).ShouldBe(1);
        _files.Count("ReadAtAsync", manifestPath).ShouldBe(3);
        _files.Count("CloseFile", CurrentConfig).ShouldBe(1);
        _files.Count("CloseFile", manifestPath).ShouldBe(1);
        AssertAsyncWaits([7, 7, 7], 0);
        Directory.Exists(CachePath).ShouldBeTrue();
    }

    [Theory]
    [InlineData("")]
    [InlineData("install")]
    [InlineData("restore")]
    [InlineData("list")]
    [InlineData("remove")]
    [InlineData("clean")]
    [InlineData("search")]
    [InlineData("info")]
    [InlineData("resolve")]
    [InlineData("publish")]
    public async Task Help_InvalidEnvironmentDoesNotBootstrap(string command)
    {
        SetEnvironment("invalid", "invalid", "invalid");

        InvocationOutput output = await RunMainAsync(command.Length == 0 ? ["--help"] : [command, "--help"]);

        output.ExitCode.ShouldBe(ExitCodes.Success, output.StandardError);
        output.StandardError.ShouldBeEmpty();
        output.StandardOutput.ShouldContain("--fs-retry-count");
        output.StandardOutput.ShouldContain("--fs-min-delay-ms");
        output.StandardOutput.ShouldContain("--fs-jitter-max-ms");
        _contexts.ShouldBeEmpty();
        _files.Calls.ShouldBeEmpty();
        AssertAsyncWaits([], 0);
        Directory.Exists(CachePath).ShouldBeFalse();
    }

    [Fact]
    public async Task PopulatedOptionsCopies_PreserveInvocationExplicitnessAndIsolation()
    {
        SetEnvironment("2", "7", "0");
        int configLoads = 0;
        GlobalOptions.ConfigLoaderOverride = () =>
        {
            configLoads++;
            return new ConfigFile { CachePath = CachePath, GitHubToken = "test-only-token" };
        };
        CliInvocationContext invocation = await CliInvocationContext.CreateAsync(
            new GlobalOptions(), TestContext.Current.CancellationToken);
        FhirPackageManagerOptions copy = invocation.CreateManagerOptions();
        copy.FileSystemRetry.HasExplicitValues.ShouldBeTrue();
        invocation.Configuration.HasExplicitFileSystemRetry.ShouldBeFalse();
        copy.FileSystemRetry.RetryCount = 99;
        copy.CachePath = Path.Combine(_root, "mutated");
        copy.GitHubToken = "mutated";
        SetEnvironment("invalid", "invalid", "invalid");

        using FhirPackageManager manager = ManagerFactory.Create(invocation);
        (await manager.ListCachedSummariesAsync(
            cancellationToken: TestContext.Current.CancellationToken)).ShouldBeEmpty();

        configLoads.ShouldBe(1);
        _contexts.ShouldHaveSingleItem().ShouldBeSameAs(invocation.FileSystemContext);
        invocation.Configuration.HasExplicitFileSystemRetry.ShouldBeFalse();
        invocation.Configuration.FileSystemContext.Policy.RetryCount.ShouldBe(2);
        FhirPackageManagerOptions anotherCopy = invocation.CreateManagerOptions();
        anotherCopy.FileSystemRetry.RetryCount.ShouldBe(2);
        anotherCopy.CachePath.ShouldBe(CachePath);
        anotherCopy.GitHubToken.ShouldBe("test-only-token");
        Directory.Exists(copy.CachePath).ShouldBeFalse();
        _files.Count("GetCurrentDirectory").ShouldBe(0);
        _files.Count("GetHomeDirectory").ShouldBe(0);
        AssertAsyncWaits([], 0);
    }

    [Fact]
    public void BuildManagerOptions_UsesSynchronousCompatibilityWithoutBlockingAsyncRetries()
    {
        File.WriteAllText(CurrentConfig, """{"unmodified":true}""");
        _files.Enqueue("Probe", CurrentConfig, Failure<FileSystemProbeResult>("Probe", CurrentConfig, 0));

        FhirPackageManagerOptions options = new GlobalOptions
        {
            CachePath = CachePath,
            FileSystemRetryCount = 1,
            FileSystemMinimumDelayMilliseconds = 7,
            FileSystemMaximumJitterMilliseconds = 0,
        }.BuildManagerOptions();

        options.Unmodified.ShouldBeTrue();
        options.FileSystemRetry.RetryCount.ShouldBe(1);
        _files.Count("GetCurrentDirectory").ShouldBe(1);
        _files.Count("GetHomeDirectory").ShouldBe(0);
        _files.Count("Probe", CurrentConfig).ShouldBe(2);
        _files.OpenCount(CurrentConfig).ShouldBe(1);
        _files.Count("ReadAt", CurrentConfig).ShouldBe(2);
        _files.Count("ReadAtAsync", CurrentConfig).ShouldBe(0);
        _files.Count("CloseFile", CurrentConfig).ShouldBe(1);
        _files.SyncWaits.ShouldBe([7]);
        _files.AsyncWaits.ShouldBeEmpty();
        _files.JitterBounds.ShouldBe([1L]);
        Directory.Exists(CachePath).ShouldBeFalse();
    }

    private void StopAtFactory()
    {
        ManagerFactory.FactoryOverride = options =>
        {
            _factoryCalls++;
            _receivedOptions = options;
            throw new InvalidOperationException(FactoryStop);
        };
    }

    private string[] HandlerArguments(string handler) => handler switch
    {
        "install" => ["install", "test.package#1.0.0"],
        "info" => ["info", "test.package"],
        "publish" =>
        [
            "publish", Path.Combine(_root, "not-to-be-probed.tgz"),
            "--registry", "https://registry.invalid/", "--auth", "test-only",
        ],
        "remove" => ["remove", "test.package#1.0.0", "--force"],
        "resolve" => ["resolve", "test.package#latest"],
        "restore" => ["restore", Path.Combine(_root, "not-to-be-probed-project")],
        "clean" => ["clean", "--force"],
        _ => [handler],
    };

    private static string[] RetryFlags(int count, int minimum, int jitter) =>
    [
        "--fs-retry-count", count.ToString(CultureInfo.InvariantCulture),
        "--fs-min-delay-ms", minimum.ToString(CultureInfo.InvariantCulture),
        "--fs-jitter-max-ms", jitter.ToString(CultureInfo.InvariantCulture),
    ];

    private void AssertAsyncWaits(int[] expected, int jitter)
    {
        _files.AsyncWaits.ShouldBe(expected);
        _files.SyncWaits.ShouldBeEmpty();
        _files.JitterBounds.ShouldBe(Enumerable.Repeat((long)jitter + 1, expected.Length).ToArray());
    }

    private static FileSystemReplayOutcome<T> Failure<T>(
        string operation,
        string? path,
        int attempt,
        string exceptionKind = "io")
    {
        string message = $"Scripted physical failure {attempt}.";
        Exception exception = exceptionKind switch
        {
            "access" => new UnauthorizedAccessException(message, new Win32Exception(5)),
            "missing" => new FileNotFoundException(message, path, new Win32Exception(5)),
            _ => new IOException(message, new Win32Exception(5)),
        };
        return FileSystemReplayOutcome<T>.Failed(FileSystemFailureContext.CapturePrimitiveFailure(
            exception,
            FileSystemReplaySafety.Safe,
            operation,
            path,
            nativeErrorCode: 5,
            nativeFlags: 0x123));
    }

    private Task<InvocationOutput> RunMainAsync(string[] arguments, bool json = true, bool verbose = false) =>
        CaptureAsync(() => Program.Main(WithGlobalOptions(arguments, json, verbose)));

    private Task<InvocationOutput> RunParserAsync(string[] arguments) =>
        CaptureAsync(() => Program.BuildRootCommand().Parse(WithGlobalOptions(arguments, json: true, verbose: false))
            .InvokeAsync(new InvocationConfiguration(), TestContext.Current.CancellationToken));

    private string[] WithGlobalOptions(string[] arguments, bool json, bool verbose) =>
    [
        "--package-cache-folder", CachePath,
        "--no-color", "false",
        "--verbose", verbose ? "true" : "false",
        "--json", json ? "true" : "false",
        "--quiet",
        .. arguments,
    ];

    private static async Task<InvocationOutput> CaptureAsync(Func<Task<int>> invoke)
    {
        TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
        TextWriter originalOut = Console.Out;
        TextWriter originalError = Console.Error;
        IAnsiConsole originalConsole = AnsiConsole.Console;
        using StringWriter standardOutput = new(CultureInfo.InvariantCulture);
        using StringWriter standardError = new(CultureInfo.InvariantCulture);
        try
        {
            Console.SetOut(standardOutput);
            Console.SetError(standardError);
            AnsiConsole.Console = AnsiConsole.Create(new AnsiConsoleSettings
            {
                Ansi = AnsiSupport.No,
                ColorSystem = ColorSystemSupport.NoColors,
                Out = new AnsiConsoleOutput(standardOutput),
            });
            AnsiConsole.Console.Profile.Width = 240;
            int exitCode = await invoke();
            return new InvocationOutput(exitCode, standardOutput.ToString(), standardError.ToString());
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalError);
            AnsiConsole.Console = originalConsole;
        }
    }

    private static void SetEnvironment(string? count, string? minimum, string? jitter)
    {
        SetVariable(s_variables[0], count);
        SetVariable(s_variables[1], minimum);
        SetVariable(s_variables[2], jitter);
    }

    private static void SetVariable(string name, string? value)
    {
        if (value is not { Length: 0 })
        {
            Environment.SetEnvironmentVariable(name, value);
            return;
        }

        // The net8.0 managed setter deletes empty values. Create a real empty
        // process variable, and restore it the same way if it was originally empty.
        bool succeeded = OperatingSystem.IsWindows()
            ? SetWindowsEnvironmentVariable(name, value)
            : SetUnixEnvironmentVariable(name, value, overwrite: 1) == 0;
        if (!succeeded)
            throw new Win32Exception(Marshal.GetLastPInvokeError());
    }

    [DllImport("kernel32.dll", EntryPoint = "SetEnvironmentVariableW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowsEnvironmentVariable(string name, string value);

    [DllImport("libc", EntryPoint = "setenv", SetLastError = true)]
    private static extern int SetUnixEnvironmentVariable(string name, string value, int overwrite);

    private static byte[] CreateArchive()
    {
        using MemoryStream archive = new();
        using (GZipStream gzip = new(archive, CompressionLevel.Fastest, leaveOpen: true))
        {
            using TarWriter writer = new(gzip, TarEntryFormat.Pax, leaveOpen: true);
            using MemoryStream manifest = new(Encoding.UTF8.GetBytes("""{"name":"retry.publish","version":"1.0.0"}"""));
            writer.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, "package/package.json") { DataStream = manifest });
        }
        return archive.ToArray();
    }

    public void Dispose()
    {
        GlobalOptions.ConfigLoaderOverride = _originalLoader;
        ManagerFactory.FactoryOverride = _originalFactory;
        CliInvocationContext.FileSystemContextOverride = _originalContextFactory;
        for (int index = 0; index < s_variables.Length; index++)
            SetVariable(s_variables[index], _originalEnvironment[index]);
        Directory.Delete(_root, recursive: true);
    }

    private sealed record InvocationOutput(int ExitCode, string StandardOutput, string StandardError);

    private sealed class StubPublishHandler : HttpMessageHandler
    {
        internal int Requests { get; private set; }
        internal byte[] Payload { get; private set; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            request.RequestUri!.Host.ShouldBe("registry.invalid");
            request.Method.ShouldBe(HttpMethod.Put);
            Requests++;
            Payload = await request.Content!.ReadAsByteArrayAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.Created) { Content = new StringContent("""{"ok":true}""") };
        }
    }

    /// <summary>
    /// A raw port decorator: only throwaway fixture paths reach the system port.
    /// Fault queues and waits are deterministic; no retry is implemented here.
    /// </summary>
    private sealed class CliFileSystemPrimitives(
        string currentDirectory,
        string homeDirectory,
        string tempDirectory) : IFileSystemPrimitives
    {
        private readonly Dictionary<(string Operation, string? Path), Queue<object>> _outcomes = [];

        internal List<(string Operation, string? Path)> Calls { get; } = [];
        internal List<int> SyncWaits { get; } = [];
        internal List<int> AsyncWaits { get; } = [];
        internal List<long> JitterBounds { get; } = [];
        internal Action<string, string?>? BeforeCall { get; set; }

        public FileSystemPlatform Platform => SystemFileSystemPrimitives.Instance.Platform;

        internal int Count(string operation) => Calls.Count(call => call.Operation == operation);
        internal int Count(string operation, string? path) => Calls.Count(call => call.Operation == operation && call.Path == path);
        internal int OpenCount(string path) => Count("OpenFileHandle", path) + Count("OpenOwnedDescriptor", path);

        internal void Enqueue<T>(string operation, string? path, FileSystemReplayOutcome<T> outcome)
        {
            if (!_outcomes.TryGetValue((operation, path), out Queue<object>? outcomes))
            {
                outcomes = new Queue<object>();
                _outcomes.Add((operation, path), outcomes);
            }
            outcomes.Enqueue(outcome);
        }

        internal FileSystemRetryExecutor CreateExecutor() =>
            new(
                bound =>
                {
                    JitterBounds.Add(bound);
                    return bound - 1;
                },
                (delay, token) =>
                {
                    token.ThrowIfCancellationRequested();
                    SyncWaits.Add(delay);
                },
                (delay, token) =>
                {
                    token.ThrowIfCancellationRequested();
                    AsyncWaits.Add(delay);
                    return ValueTask.CompletedTask;
                });

        private bool TryScript<T>(string operation, string? path, out FileSystemReplayOutcome<T> outcome)
        {
            Calls.Add((operation, path));
            BeforeCall?.Invoke(operation, path);
            if (_outcomes.TryGetValue((operation, path), out Queue<object>? outcomes) && outcomes.TryDequeue(out object? scripted))
            {
                outcome = (FileSystemReplayOutcome<T>)scripted;
                return true;
            }
            outcome = default;
            return false;
        }

        private FileSystemReplayOutcome<T> Invoke<T>(
            string operation, string? path, Func<FileSystemReplayOutcome<T>> attempt) =>
            TryScript(operation, path, out FileSystemReplayOutcome<T> outcome) ? outcome : attempt();

        private ValueTask<FileSystemReplayOutcome<T>> InvokeAsync<T>(
            string operation, string? path, Func<ValueTask<FileSystemReplayOutcome<T>>> attempt) =>
            TryScript(operation, path, out FileSystemReplayOutcome<T> outcome)
                ? new ValueTask<FileSystemReplayOutcome<T>>(outcome)
                : attempt();

        public FileSystemReplayOutcome<string> GetCurrentDirectory() =>
            Invoke("GetCurrentDirectory", null, () => FileSystemReplayOutcome<string>.Complete(currentDirectory));
        public FileSystemReplayOutcome<string> GetTempPath() =>
            Invoke("GetTempPath", null, () => FileSystemReplayOutcome<string>.Complete(tempDirectory));
        public FileSystemReplayOutcome<string> GetHomeDirectory() =>
            Invoke("GetHomeDirectory", null, () => FileSystemReplayOutcome<string>.Complete(homeDirectory));
        public FileSystemReplayOutcome<string> GetFullPath(string path) =>
            Invoke("GetFullPath", path, () => FileSystemReplayOutcome<string>.Complete(Path.GetFullPath(path, currentDirectory)));
        public FileSystemReplayOutcome<FileSystemProbeResult> Probe(string path, bool followLinks = false) =>
            Invoke("Probe", path, () => SystemFileSystemPrimitives.Instance.Probe(path, followLinks));
        public FileSystemReplayOutcome<FileAttributes> GetAttributes(string path) =>
            Invoke("GetAttributes", path, () => SystemFileSystemPrimitives.Instance.GetAttributes(path));
        public FileSystemReplayOutcome<long> GetLength(string path) =>
            Invoke("GetLength", path, () => SystemFileSystemPrimitives.Instance.GetLength(path));
        public FileSystemReplayOutcome<IEnumerator<string>> CreateDirectoryEnumerator(string path) =>
            Invoke("CreateDirectoryEnumerator", path, () => SystemFileSystemPrimitives.Instance.CreateDirectoryEnumerator(path));
        public FileSystemReplayOutcome<bool> MoveNext(IEnumerator<string> enumerator, string path) =>
            Invoke("MoveNext", path, () => SystemFileSystemPrimitives.Instance.MoveNext(enumerator, path));
        public FileSystemReplayOutcome<string> GetCurrent(IEnumerator<string> enumerator, string path) =>
            Invoke("GetCurrent", path, () => SystemFileSystemPrimitives.Instance.GetCurrent(enumerator, path));
        public FileSystemReplayOutcome<bool> DisposeEnumerator(IEnumerator<string> enumerator, string path) =>
            Invoke("DisposeEnumerator", path, () => SystemFileSystemPrimitives.Instance.DisposeEnumerator(enumerator, path));
        public FileSystemReplayOutcome<FileStream> OpenFile(
            string path, FileMode mode, FileAccess access, FileShare share, int bufferSize, FileOptions options) =>
            Invoke("OpenFile", path, () => SystemFileSystemPrimitives.Instance.OpenFile(path, mode, access, share, bufferSize, options));
        public FileSystemReplayOutcome<SafeFileHandle> OpenFileHandle(
            string path, FileMode mode, FileAccess access, FileShare share, FileOptions options) =>
            Invoke("OpenFileHandle", path, () => SystemFileSystemPrimitives.Instance.OpenFileHandle(path, mode, access, share, options));
        public FileSystemReplayOutcome<int> OpenOwnedDescriptor(
            string path, FileMode mode, FileAccess access, FileShare share, FileOptions options) =>
            Invoke("OpenOwnedDescriptor", path, () => SystemFileSystemPrimitives.Instance.OpenOwnedDescriptor(path, mode, access, share, options));
        public FileSystemReplayOutcome<bool> ConfigureFileSharing(SafeFileHandle handle, FileShare share, string path) =>
            Invoke("ConfigureFileSharing", path, () => SystemFileSystemPrimitives.Instance.ConfigureFileSharing(handle, share, path));
        public FileSystemReplayOutcome<bool> AdviseFileAccess(SafeFileHandle handle, FileOptions options, string path) =>
            Invoke("AdviseFileAccess", path, () => SystemFileSystemPrimitives.Instance.AdviseFileAccess(handle, options, path));
        public FileSystemReplayOutcome<int> ReadAt(SafeFileHandle handle, Memory<byte> buffer, long offset, string path) =>
            Invoke("ReadAt", path, () => SystemFileSystemPrimitives.Instance.ReadAt(handle, buffer, offset, path));
        public ValueTask<FileSystemReplayOutcome<int>> ReadAtAsync(
            SafeFileHandle handle, Memory<byte> buffer, long offset, string path, CancellationToken cancellationToken) =>
            InvokeAsync("ReadAtAsync", path, () => SystemFileSystemPrimitives.Instance.ReadAtAsync(handle, buffer, offset, path, cancellationToken));
        public FileSystemReplayOutcome<bool> WriteAt(SafeFileHandle handle, ReadOnlyMemory<byte> buffer, long offset, string path) =>
            Invoke("WriteAt", path, () => SystemFileSystemPrimitives.Instance.WriteAt(handle, buffer, offset, path));
        public ValueTask<FileSystemReplayOutcome<bool>> WriteAtAsync(
            SafeFileHandle handle, ReadOnlyMemory<byte> buffer, long offset, string path, CancellationToken cancellationToken) =>
            InvokeAsync("WriteAtAsync", path, () => SystemFileSystemPrimitives.Instance.WriteAtAsync(handle, buffer, offset, path, cancellationToken));
        public FileSystemReplayOutcome<long> GetLength(SafeFileHandle handle, string path) =>
            Invoke("GetHandleLength", path, () => SystemFileSystemPrimitives.Instance.GetLength(handle, path));
        public FileSystemReplayOutcome<bool> SetLength(SafeFileHandle handle, long length, string path) =>
            Invoke("SetLength", path, () => SystemFileSystemPrimitives.Instance.SetLength(handle, length, path));
        public FileSystemReplayOutcome<bool> FlushFile(SafeFileHandle handle, string path) =>
            Invoke("FlushFile", path, () => SystemFileSystemPrimitives.Instance.FlushFile(handle, path));
        public FileSystemReplayOutcome<bool> CloseFile(SafeFileHandle handle, string path) =>
            Invoke("CloseFile", path, () => SystemFileSystemPrimitives.Instance.CloseFile(handle, path));
        public FileSystemReplayOutcome<BorrowedFileReadState> PrepareBorrowedRead(FileStream stream) =>
            Invoke("PrepareBorrowedRead", stream.Name, () => SystemFileSystemPrimitives.Instance.PrepareBorrowedRead(stream));
        public FileSystemReplayOutcome<long> GetPosition(FileStream stream) =>
            Invoke("GetPosition", stream.Name, () => SystemFileSystemPrimitives.Instance.GetPosition(stream));
        public FileSystemReplayOutcome<long> SetPosition(FileStream stream, long position) =>
            Invoke("SetPosition", stream.Name, () => SystemFileSystemPrimitives.Instance.SetPosition(stream, position));
        public FileSystemReplayOutcome<int> ReadBorrowed(FileStream stream, Memory<byte> buffer) =>
            Invoke("ReadBorrowed", stream.Name, () => SystemFileSystemPrimitives.Instance.ReadBorrowed(stream, buffer));
        public ValueTask<FileSystemReplayOutcome<int>> ReadBorrowedAsync(
            FileStream stream, Memory<byte> buffer, CancellationToken cancellationToken) =>
            InvokeAsync("ReadBorrowedAsync", stream.Name, () => SystemFileSystemPrimitives.Instance.ReadBorrowedAsync(stream, buffer, cancellationToken));
        public FileSystemReplayOutcome<bool> CreateDirectory(string path) =>
            Invoke("CreateDirectory", path, () => SystemFileSystemPrimitives.Instance.CreateDirectory(path));
        public FileSystemReplayOutcome<bool> MovePath(string sourcePath, string destinationPath, bool replace) =>
            Invoke("MovePath", sourcePath, () => SystemFileSystemPrimitives.Instance.MovePath(sourcePath, destinationPath, replace));
        public FileSystemReplayOutcome<bool> DeleteEntry(string path, FileSystemEntryKind kind) =>
            Invoke("DeleteEntry", path, () => SystemFileSystemPrimitives.Instance.DeleteEntry(path, kind));
        public FileSystemReplayOutcome<int> OpenDescriptor(string path, int flags, uint mode) =>
            Invoke("OpenDescriptor", path, () => SystemFileSystemPrimitives.Instance.OpenDescriptor(path, flags, mode));
        public FileSystemReplayOutcome<FileSystemProbeResult> InspectDescriptor(int descriptor, string? path = null) =>
            Invoke("InspectDescriptor", path, () => SystemFileSystemPrimitives.Instance.InspectDescriptor(descriptor, path));
        public FileSystemReplayOutcome<int> DuplicateDescriptor(int descriptor, int command, int minimum, string? path = null) =>
            Invoke("DuplicateDescriptor", path, () => SystemFileSystemPrimitives.Instance.DuplicateDescriptor(descriptor, command, minimum, path));
        public FileSystemReplayOutcome<FileSystemDirectorySyncOutcome> SynchronizeDescriptor(
            int descriptor, bool fullSync, string? path = null) =>
            Invoke("SynchronizeDescriptor", path, () => SystemFileSystemPrimitives.Instance.SynchronizeDescriptor(descriptor, fullSync, path));
        public FileSystemReplayOutcome<bool> CloseDescriptor(int descriptor, string? path = null) =>
            Invoke("CloseDescriptor", path, () => SystemFileSystemPrimitives.Instance.CloseDescriptor(descriptor, path));
        public FileSystemReplayOutcome<bool> TryLock(FileStream stream) =>
            Invoke("TryLock", stream.Name, () => SystemFileSystemPrimitives.Instance.TryLock(stream));
        public FileSystemReplayOutcome<bool> Unlock(FileStream stream) =>
            Invoke("Unlock", stream.Name, () => SystemFileSystemPrimitives.Instance.Unlock(stream));
    }
}
