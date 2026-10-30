// Copyright (c) Gino Canessa. Licensed under the MIT License.

using System.Globalization;
using FhirPkg.Installation;
using FhirPkg.IO;
using Shouldly;
using Xunit;

namespace FhirPkg.Tests;

[Collection("EnvironmentVariable")]
public class FileSystemRetryOptionsTests : IDisposable
{
    private static readonly string[] s_environmentVariables =
    [
        FileSystemRetryOptions.RetryCountEnvironmentVariable,
        FileSystemRetryOptions.MinimumDelayMillisecondsEnvironmentVariable,
        FileSystemRetryOptions.MaximumJitterMillisecondsEnvironmentVariable,
    ];

    private readonly Dictionary<string, string?> _originalEnvironment = [];

    public FileSystemRetryOptionsTests()
    {
        foreach (string variableName in s_environmentVariables)
        {
            _originalEnvironment[variableName] = Environment.GetEnvironmentVariable(variableName);
            Environment.SetEnvironmentVariable(variableName, null);
        }
    }

    public void Dispose()
    {
        foreach ((string variableName, string? value) in _originalEnvironment)
            Environment.SetEnvironmentVariable(variableName, value);

        GC.SuppressFinalize(this);
    }

    public static TheoryData<string, string> InvalidEnvironmentValues
    {
        get
        {
            string[] invalidValues =
            [
                "", " ", "\t", "-1", "-2147483648", "-0", "+0", "+1",
                " 1", "1 ", "1.0", "1,000", "1e2", "not-a-number",
                "2147483648", "4294967295", "9223372036854775808", "１",
            ];
            TheoryData<string, string> cases = new TheoryData<string, string>();
            foreach (string variableName in s_environmentVariables)
            {
                foreach (string value in invalidValues)
                    cases.Add(variableName, value);
            }

            return cases;
        }
    }

    [Fact]
    public void Defaults_AreDocumentedAndUnspecified()
    {
        FileSystemRetryOptions options = new FileSystemRetryOptions();

        FileSystemRetryOptions.DefaultRetryCount.ShouldBe(5);
        FileSystemRetryOptions.DefaultMinimumDelayMilliseconds.ShouldBe(10);
        FileSystemRetryOptions.DefaultMaximumJitterMilliseconds.ShouldBe(10);
        FileSystemRetryOptions.RetryCountEnvironmentVariable.ShouldBe("FHIRPKG_FS_RETRY_COUNT");
        FileSystemRetryOptions.MinimumDelayMillisecondsEnvironmentVariable.ShouldBe("FHIRPKG_FS_MIN_DELAY_MS");
        FileSystemRetryOptions.MaximumJitterMillisecondsEnvironmentVariable.ShouldBe("FHIRPKG_FS_JITTER_MAX_MS");
        options.RetryCount.ShouldBe(5);
        options.MinimumDelayMilliseconds.ShouldBe(10);
        options.MaximumJitterMilliseconds.ShouldBe(10);
        options.ExplicitRetryCount.ShouldBeNull();
        options.ExplicitMinimumDelayMilliseconds.ShouldBeNull();
        options.ExplicitMaximumJitterMilliseconds.ShouldBeNull();
        options.HasExplicitValues.ShouldBeFalse();

        FileSystemRetryPolicy policy = FileSystemRetryPolicy.Resolve(options, out bool hasExplicitValues);

        policy.RetryCount.ShouldBe(5);
        policy.MinimumDelayMilliseconds.ShouldBe(10);
        policy.MaximumJitterMilliseconds.ShouldBe(10);
        policy.MaximumAttemptCount.ShouldBe(6L);
        policy.MaximumDelayMilliseconds.ShouldBe(20);
        hasExplicitValues.ShouldBeFalse();
        options.HasExplicitValues.ShouldBeFalse();
    }

    [Fact]
    public void NullConfiguredPolicy_FailsBeforeReadingEnvironmentOrStartingWork()
    {
        int environmentReads = 0;
        int workCalls = 0;

        PackageInstallException exception = Should.Throw<PackageInstallException>(() =>
        {
            FileSystemRetryPolicy.Resolve(
                null,
                _ =>
                {
                    environmentReads++;
                    return null;
                },
                out _);
            workCalls++;
        });

        AssertInvalidPolicy(exception);
        exception.Message.ShouldContain("FileSystemRetry");
        environmentReads.ShouldBe(0);
        workCalls.ShouldBe(0);
    }

    [Fact]
    public void Environment_ResolvesEachValueWithoutBecomingExplicit()
    {
        SetEnvironment(7, 17, 19);
        FileSystemRetryOptions options = new FileSystemRetryOptions();

        FileSystemRetryPolicy policy = FileSystemRetryPolicy.Resolve(options, out bool hasExplicitValues);

        policy.RetryCount.ShouldBe(7);
        policy.MinimumDelayMilliseconds.ShouldBe(17);
        policy.MaximumJitterMilliseconds.ShouldBe(19);
        policy.MaximumDelayMilliseconds.ShouldBe(36);
        hasExplicitValues.ShouldBeFalse();
        options.HasExplicitValues.ShouldBeFalse();
        options.RetryCount.ShouldBe(5);
        options.MinimumDelayMilliseconds.ShouldBe(10);
        options.MaximumJitterMilliseconds.ShouldBe(10);
    }

    [Theory]
    [InlineData(nameof(FileSystemRetryOptions.RetryCount), 5)]
    [InlineData(nameof(FileSystemRetryOptions.RetryCount), 0)]
    [InlineData(nameof(FileSystemRetryOptions.MinimumDelayMilliseconds), 10)]
    [InlineData(nameof(FileSystemRetryOptions.MinimumDelayMilliseconds), 0)]
    [InlineData(nameof(FileSystemRetryOptions.MaximumJitterMilliseconds), 10)]
    [InlineData(nameof(FileSystemRetryOptions.MaximumJitterMilliseconds), 0)]
    public void DefaultOrZeroAssignment_IsExplicitAndOverridesOnlyItsEnvironment(
        string propertyName,
        int value)
    {
        SetEnvironment(7, 17, 19);
        Environment.SetEnvironmentVariable(EnvironmentName(propertyName), "malformed");
        FileSystemRetryOptions options = new FileSystemRetryOptions();
        Assign(options, propertyName, value);

        FileSystemRetryPolicy policy = FileSystemRetryPolicy.Resolve(options, out bool hasExplicitValues);

        hasExplicitValues.ShouldBeTrue();
        options.HasExplicitValues.ShouldBeTrue();
        policy.RetryCount.ShouldBe(propertyName == nameof(options.RetryCount) ? value : 7);
        policy.MinimumDelayMilliseconds.ShouldBe(
            propertyName == nameof(options.MinimumDelayMilliseconds) ? value : 17);
        policy.MaximumJitterMilliseconds.ShouldBe(
            propertyName == nameof(options.MaximumJitterMilliseconds) ? value : 19);
    }

    [Theory]
    [InlineData(nameof(FileSystemRetryOptions.RetryCount), nameof(FileSystemRetryOptions.MinimumDelayMilliseconds))]
    [InlineData(nameof(FileSystemRetryOptions.MinimumDelayMilliseconds), nameof(FileSystemRetryOptions.MaximumJitterMilliseconds))]
    [InlineData(nameof(FileSystemRetryOptions.MaximumJitterMilliseconds), nameof(FileSystemRetryOptions.RetryCount))]
    public void ExplicitValue_DoesNotHideAnUnrelatedInvalidEnvironmentValue(
        string explicitProperty,
        string invalidProperty)
    {
        FileSystemRetryOptions options = new FileSystemRetryOptions();
        Assign(options, explicitProperty, 0);
        Environment.SetEnvironmentVariable(EnvironmentName(invalidProperty), "invalid");
        int workCalls = 0;

        PackageInstallException exception = Should.Throw<PackageInstallException>(() =>
        {
            FileSystemRetryPolicy.Resolve(options);
            workCalls++;
        });

        AssertInvalidPolicy(exception);
        exception.Message.ShouldContain(EnvironmentName(invalidProperty));
        workCalls.ShouldBe(0);
    }

    [Theory]
    [MemberData(nameof(InvalidEnvironmentValues))]
    public void InvalidEffectiveEnvironment_RejectsEveryFieldAndTextBoundary(
        string variableName,
        string rawValue)
    {
        int workCalls = 0;

        PackageInstallException exception = Should.Throw<PackageInstallException>(() =>
        {
            // Inject the read so an empty value is tested even on runtimes where
            // SetEnvironmentVariable(name, "") deletes the variable.
            FileSystemRetryPolicy.Resolve(
                new FileSystemRetryOptions(),
                name => name == variableName ? rawValue : null,
                out _);
            workCalls++;
        });

        AssertInvalidPolicy(exception);
        exception.Message.ShouldContain(variableName);
        workCalls.ShouldBe(0);
    }

    [Theory]
    [InlineData(nameof(FileSystemRetryOptions.RetryCount), -1)]
    [InlineData(nameof(FileSystemRetryOptions.RetryCount), int.MinValue)]
    [InlineData(nameof(FileSystemRetryOptions.MinimumDelayMilliseconds), -1)]
    [InlineData(nameof(FileSystemRetryOptions.MinimumDelayMilliseconds), int.MinValue)]
    [InlineData(nameof(FileSystemRetryOptions.MaximumJitterMilliseconds), -1)]
    [InlineData(nameof(FileSystemRetryOptions.MaximumJitterMilliseconds), int.MinValue)]
    public void NegativeExplicitValue_RejectsEveryField(string propertyName, int value)
    {
        FileSystemRetryOptions options = new FileSystemRetryOptions();
        Assign(options, propertyName, value);
        Environment.SetEnvironmentVariable(EnvironmentName(propertyName), "0");

        PackageInstallException exception = Should.Throw<PackageInstallException>(
            () => FileSystemRetryPolicy.Resolve(options));

        AssertInvalidPolicy(exception);
        exception.Message.ShouldContain(propertyName);
    }

    [Theory]
    [InlineData("0", 0)]
    [InlineData("0001", 1)]
    [InlineData("2147483647", int.MaxValue)]
    public void InvariantUnsignedIntegerSyntax_AcceptsZeroAndFullInt32Range(string rawValue, int expected)
    {
        foreach (string variableName in s_environmentVariables)
        {
            FileSystemRetryPolicy policy = FileSystemRetryPolicy.Resolve(
                new FileSystemRetryOptions(),
                name => name == variableName ? rawValue : "0",
                out bool hasExplicitValues);

            policy.RetryCount.ShouldBe(
                variableName == FileSystemRetryOptions.RetryCountEnvironmentVariable ? expected : 0);
            policy.MinimumDelayMilliseconds.ShouldBe(
                variableName == FileSystemRetryOptions.MinimumDelayMillisecondsEnvironmentVariable ? expected : 0);
            policy.MaximumJitterMilliseconds.ShouldBe(
                variableName == FileSystemRetryOptions.MaximumJitterMillisecondsEnvironmentVariable ? expected : 0);
            hasExplicitValues.ShouldBeFalse();
        }
    }

    [Theory]
    [InlineData(0, 0, 0)]
    [InlineData(int.MaxValue, 0, 0)]
    [InlineData(0, int.MaxValue, 0)]
    [InlineData(0, 0, int.MaxValue)]
    [InlineData(int.MaxValue, int.MaxValue, 0)]
    [InlineData(int.MaxValue, int.MaxValue - 10, 10)]
    public void ValidInt32Boundaries_HaveNoSmallerCap(int retries, int minimum, int jitter)
    {
        SetEnvironment(retries, minimum, jitter);
        FileSystemRetryPolicy environmentPolicy = FileSystemRetryPolicy.Resolve(
            new FileSystemRetryOptions(),
            out bool environmentIsExplicit);
        FileSystemRetryPolicy explicitPolicy = FileSystemRetryPolicy.Resolve(
            new FileSystemRetryOptions
            {
                RetryCount = retries,
                MinimumDelayMilliseconds = minimum,
                MaximumJitterMilliseconds = jitter,
            },
            out bool assignedIsExplicit);

        environmentPolicy.ShouldBe(explicitPolicy);
        explicitPolicy.MaximumAttemptCount.ShouldBe((long)retries + 1);
        explicitPolicy.MaximumDelayMilliseconds.ShouldBe((int)((long)minimum + jitter));
        environmentIsExplicit.ShouldBeFalse();
        assignedIsExplicit.ShouldBeTrue();
    }

    [Theory]
    [InlineData(int.MaxValue, 1)]
    [InlineData(1, int.MaxValue)]
    [InlineData(int.MaxValue, int.MaxValue)]
    [InlineData(int.MaxValue - 9, 10)]
    public void DelaySumBeyondInt32_RejectsExplicitAndEnvironmentPolicies(int minimum, int jitter)
    {
        SetEnvironment(0, minimum, jitter);

        PackageInstallException environmentException = Should.Throw<PackageInstallException>(
            () => FileSystemRetryPolicy.Resolve(new FileSystemRetryOptions()));
        PackageInstallException explicitException = Should.Throw<PackageInstallException>(
            () => FileSystemRetryPolicy.Resolve(new FileSystemRetryOptions
            {
                RetryCount = 0,
                MinimumDelayMilliseconds = minimum,
                MaximumJitterMilliseconds = jitter,
            }));

        AssertInvalidPolicy(environmentException);
        AssertInvalidPolicy(explicitException);
        environmentException.Message.ShouldContain("Int32.MaxValue");
        explicitException.Message.ShouldBe(environmentException.Message);
    }

    [Fact]
    public void FullyExplicitPolicy_DoesNotReadLowerPriorityEnvironment()
    {
        int reads = 0;

        FileSystemRetryPolicy policy = FileSystemRetryPolicy.Resolve(
            new FileSystemRetryOptions
            {
                RetryCount = 5,
                MinimumDelayMilliseconds = 10,
                MaximumJitterMilliseconds = 10,
            },
            _ =>
            {
                reads++;
                return "invalid";
            },
            out bool hasExplicitValues);

        reads.ShouldBe(0);
        hasExplicitValues.ShouldBeTrue();
        policy.MaximumAttemptCount.ShouldBe(6L);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Snapshot_IsImmutableAndRetainsOriginalExplicitnessSeparately(bool assignDefault)
    {
        SetEnvironment(7, 17, 19);
        FileSystemRetryOptions options = new FileSystemRetryOptions();
        if (assignDefault)
            options.RetryCount = FileSystemRetryOptions.DefaultRetryCount;

        FileSystemRetryPolicy policy = FileSystemRetryPolicy.Resolve(options, out bool hasExplicitValues);
        FileSystemRetryOptions snapshot = policy.ToOptionsSnapshot();
        FileSystemRetryPolicy equalValues = FileSystemRetryPolicy.Resolve(
            snapshot,
            out bool snapshotHasAssignments);
        int originalHash = policy.GetHashCode();

        hasExplicitValues.ShouldBe(assignDefault);
        snapshotHasAssignments.ShouldBeTrue();
        equalValues.ShouldBe(policy);
        equalValues.GetHashCode().ShouldBe(originalHash);
        snapshot.RetryCount.ShouldBe(assignDefault ? 5 : 7);
        snapshot.MinimumDelayMilliseconds.ShouldBe(17);
        snapshot.MaximumJitterMilliseconds.ShouldBe(19);

        options.RetryCount = 99;
        options.MinimumDelayMilliseconds = 99;
        options.MaximumJitterMilliseconds = 99;
        snapshot.RetryCount = 0;
        snapshot.MinimumDelayMilliseconds = 0;
        snapshot.MaximumJitterMilliseconds = 0;
        foreach (string variableName in s_environmentVariables)
            Environment.SetEnvironmentVariable(variableName, "invalid");

        policy.RetryCount.ShouldBe(assignDefault ? 5 : 7);
        policy.MinimumDelayMilliseconds.ShouldBe(17);
        policy.MaximumJitterMilliseconds.ShouldBe(19);
        policy.GetHashCode().ShouldBe(originalHash);
        hasExplicitValues.ShouldBe(assignDefault);
        equalValues.ShouldBe(policy);
    }

    private static void AssertInvalidPolicy(PackageInstallException exception)
    {
        exception.ErrorCode.ShouldBe(PackageInstallErrorCode.InvalidPolicy);
        exception.Stage.ShouldBe(PackageInstallStage.PolicyValidation);
    }

    private static void SetEnvironment(int retries, int minimum, int jitter)
    {
        Environment.SetEnvironmentVariable(
            FileSystemRetryOptions.RetryCountEnvironmentVariable,
            retries.ToString(CultureInfo.InvariantCulture));
        Environment.SetEnvironmentVariable(
            FileSystemRetryOptions.MinimumDelayMillisecondsEnvironmentVariable,
            minimum.ToString(CultureInfo.InvariantCulture));
        Environment.SetEnvironmentVariable(
            FileSystemRetryOptions.MaximumJitterMillisecondsEnvironmentVariable,
            jitter.ToString(CultureInfo.InvariantCulture));
    }

    private static string EnvironmentName(string propertyName) => propertyName switch
    {
        nameof(FileSystemRetryOptions.RetryCount) => FileSystemRetryOptions.RetryCountEnvironmentVariable,
        nameof(FileSystemRetryOptions.MinimumDelayMilliseconds) => FileSystemRetryOptions.MinimumDelayMillisecondsEnvironmentVariable,
        nameof(FileSystemRetryOptions.MaximumJitterMilliseconds) => FileSystemRetryOptions.MaximumJitterMillisecondsEnvironmentVariable,
        _ => throw new ArgumentOutOfRangeException(nameof(propertyName)),
    };

    private static void Assign(FileSystemRetryOptions options, string propertyName, int value)
    {
        switch (propertyName)
        {
            case nameof(FileSystemRetryOptions.RetryCount):
                options.RetryCount = value;
                break;
            case nameof(FileSystemRetryOptions.MinimumDelayMilliseconds):
                options.MinimumDelayMilliseconds = value;
                break;
            case nameof(FileSystemRetryOptions.MaximumJitterMilliseconds):
                options.MaximumJitterMilliseconds = value;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(propertyName));
        }
    }
}
