// Copyright (c) Gino Canessa. Licensed under the MIT License.

using System.CommandLine;
using FhirPkg.Cache;
using FhirPkg.Cli;
using FhirPkg.Models;
using Shouldly;
using Xunit;

namespace FhirPkg.IntegrationTests;

[Collection("InstallOutput")]
[Trait("Category", "Integration")]
public sealed class UnmodifiedCliOptionsTests : IntegrationTestBase
{
    [Theory]
    [InlineData("", null)]
    [InlineData("--unmodified", true)]
    [InlineData("--unmodified true", true)]
    [InlineData("--unmodified false", false)]
    public void UnmodifiedOption_ParsesAtRootAndAroundCommands(
        string optionArguments,
        bool? expected)
    {
        RootCommand root = Program.BuildRootCommand();
        string[] optionTokens = optionArguments.Split(
            ' ', StringSplitOptions.RemoveEmptyEntries);
        string[][] placements =
        [
            [.. optionTokens, "--help"],
            [.. optionTokens, "install", "forwarded.package#1.0.0"],
            ["install", "forwarded.package#1.0.0", .. optionTokens],
            [.. optionTokens, "restore", "project"],
            ["restore", "project", .. optionTokens],
        ];

        foreach (string[] arguments in placements)
        {
            // Do not let an inherited NO_COLOR value mutate the test process.
            ParseResult parsed = root.Parse([.. arguments, "--no-color", "false"]);

            parsed.Errors.ShouldBeEmpty();
            parsed.GetGlobalOptions().Unmodified.ShouldBe(expected);
        }

        GlobalOptionsBinder.UnmodifiedOption.Recursive.ShouldBeTrue();
        new GlobalOptions().Unmodified.ShouldBeNull();
        new ConfigFile().Unmodified.ShouldBeNull();
    }

    public static TheoryData<string, string, bool, bool> HandlerCases
    {
        get
        {
            TheoryData<string, string, bool, bool> cases = [];
            foreach (string command in new[] { "install", "restore" })
            {
                foreach ((string arguments, bool expected) in new[]
                {
                    (string.Empty, false),
                    ("--unmodified", true),
                    ("--unmodified true", true),
                    ("--unmodified false", false),
                })
                {
                    cases.Add(command, arguments, expected, true);
                    cases.Add(command, arguments, expected, false);
                }
            }

            return cases;
        }
    }

    [Theory]
    [MemberData(nameof(HandlerCases))]
    public async Task UnmodifiedOption_ForwardsThroughInstallAndRestoreHandlers(
        string command,
        string optionArguments,
        bool expected,
        bool beforeCommand)
    {
        string operand;
        if (command == "install")
        {
            PackageReference reference = new("forwarded.package", "1.0.0");
            using DiskPackageCache cache = new(TempCacheDir);
            using Stream archive = CreateTestTarball(reference.Name, reference.Version!);
            await cache.InstallAsync(
                reference, archive, ct: TestContext.Current.CancellationToken);
            operand = reference.FhirDirective;
        }
        else
        {
            // This checks forwarding; real dependency payload coverage is in
            // FhirPackageManagerUnmodifiedTests, not this empty closure.
            operand = CreateTestProject(
                """{"name":"forwarded.project","version":"1.0.0","dependencies":{}}""");
        }

        Func<FhirPackageManagerOptions, FhirPackageManager>? originalFactory =
            ManagerFactory.FactoryOverride;
        Func<ConfigFile?>? originalLoader = GlobalOptions.ConfigLoaderOverride;
        FhirPackageManager? manager = null;
        FhirPackageManagerOptions? receivedOptions = null;
        int factoryCalls = 0;
        int configLoads = 0;
        try
        {
            GlobalOptions.ConfigLoaderOverride = () =>
            {
                configLoads++;
                return null;
            };
            ManagerFactory.FactoryOverride = options =>
            {
                factoryCalls++;
                receivedOptions = options;
                manager = new FhirPackageManager(options);
                return manager;
            };

            RootCommand root = Program.BuildRootCommand();
            string[] optionTokens = optionArguments.Split(
                ' ', StringSplitOptions.RemoveEmptyEntries);
            List<string> arguments =
            [
                "--package-cache-folder", TempCacheDir,
                "--no-color", "false",
                "--verbose", "false",
                "--json", "false",
                "--quiet",
            ];
            if (beforeCommand)
                arguments.AddRange(optionTokens);
            arguments.Add(command);
            arguments.Add(operand);
            if (!beforeCommand)
                arguments.AddRange(optionTokens);
            ParseResult parsed = root.Parse([.. arguments]);
            parsed.Errors.ShouldBeEmpty();

            int exitCode = await parsed.InvokeAsync(
                new InvocationConfiguration(), TestContext.Current.CancellationToken);

            exitCode.ShouldBe(ExitCodes.Success);
            configLoads.ShouldBe(1);
            factoryCalls.ShouldBe(1);
            receivedOptions.ShouldNotBeNull();
            receivedOptions.Unmodified.ShouldBe(expected);
            receivedOptions.CachePath.ShouldBe(TempCacheDir);
        }
        finally
        {
            ManagerFactory.FactoryOverride = originalFactory;
            GlobalOptions.ConfigLoaderOverride = originalLoader;
            manager?.Dispose();
        }
    }
}
