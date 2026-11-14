// Copyright (c) Gino Canessa. Licensed under the MIT License.

using System.Globalization;
using System.Net;
using System.Reflection;
using System.Text.Json;
using FhirPkg;
using FhirPkg.Cache;
using FhirPkg.Installation;
using FhirPkg.IO;
using FhirPkg.Models;
using FhirPkg.Registry;
using FhirPkg.Tests.Support;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;
using DirectPolicyTestSupport = FhirPkg.Tests.PackageInstallLimitsTests.DirectPolicyTestSupport;

namespace FhirPkg.Tests.Registry;

/// <summary>
/// Tests for <see cref="RegistryClientFactory"/>'s credential-safety guard: a
/// caller-supplied GitHub token is refused on any transport that is not
/// redirect-controlled, and is accepted on one that is.
/// </summary>
[Collection("EnvironmentVariable")]
public class RegistryClientFactoryTests
{
    private const string TestToken = "gho_test-token";

    private static readonly RegistryType[] s_endpointTypes =
        [RegistryType.FhirNpm, RegistryType.Npm, RegistryType.FhirCiBuild, RegistryType.FhirHttp];

    public static IEnumerable<object[]> CompleteEndpointPolicies()
    {
        foreach (RegistryType type in s_endpointTypes)
        {
            foreach (bool controlled in new[] { false, true })
            {
                foreach (object[] policyCase in PackageInstallLimitsTests.CompletePolicyCases())
                    yield return [type, controlled, policyCase[0], policyCase[1]];
            }
        }
    }

    public static IEnumerable<object[]> PartialEndpointPolicies()
    {
        foreach (RegistryType type in s_endpointTypes)
        {
            foreach (bool controlled in new[] { false, true })
            {
                foreach (object[] policyCase in PackageInstallLimitsTests.PartialPolicyCases())
                    yield return [type, controlled, policyCase[0], policyCase[1]];
            }
        }
    }

    public static IEnumerable<object[]> InvalidEndpointPolicies()
    {
        foreach (RegistryType type in s_endpointTypes)
        {
            foreach (bool controlled in new[] { false, true })
            {
                foreach (object[] policyCase in PackageInstallLimitsTests.InvalidPolicyCases())
                    yield return [type, controlled, policyCase[0], policyCase[1]];
            }
        }
    }

    public static IEnumerable<object[]> EndpointRetryPolicies()
    {
        foreach (RegistryType type in s_endpointTypes)
        {
            foreach (bool controlled in new[] { false, true })
            {
                foreach (string invalid in new[] { "null", "count", "delay", "jitter", "overflow" })
                    yield return [type, controlled, invalid];
            }
        }
    }

    public static IEnumerable<object[]> NpmSnapshotPolicies()
    {
        foreach (bool controlled in new[] { false, true })
        {
            foreach (string field in DirectPolicyTestSupport.Fields)
                yield return [controlled, field];
        }
    }

    public static IEnumerable<object[]> EndpointFamilies()
    {
        foreach (RegistryType type in s_endpointTypes)
        {
            yield return [type, false];
            yield return [type, true];
        }
    }

    public static IEnumerable<object[]> MixedEndpointPolicies()
    {
        foreach (object[] family in EndpointFamilies())
        {
            foreach (string field in DirectPolicyTestSupport.Fields)
                yield return [family[0], family[1], field];
        }
    }

    [Theory]
    [MemberData(nameof(CompleteEndpointPolicies))]
    public async Task EndpointPolicyOverloads_CompleteExplicitPolicy_AvoidsAmbientParsing(
        RegistryType type, bool controlledTransport, string field, bool invalidAmbient)
    {
        using DirectPolicyTestSupport.EnvironmentScope environment = new();
        DirectPolicyTestSupport fixture = new();
        foreach (string selected in field == "all" ? DirectPolicyTestSupport.Fields : [field])
        {
            DirectPolicyTestSupport.SetEnvironment(
                selected, invalidAmbient ? "malformed" : (fixture.Quantity(selected) - 1).ToString(CultureInfo.InvariantCulture));
        }
        OfflinePutHandler handler = new(fixture.Archive);
        using HttpClient http = new(handler);
        RegistryEndpoint endpoint = PublicationTestSupport.Endpoint("policy", type);
        FileSystemRetryOptions retry = new()
        {
            RetryCount = 2,
            MinimumDelayMilliseconds = 3,
            MaximumJitterMilliseconds = 4
        };
        IRegistryClient client = PolicyClient(endpoint, http, controlledTransport, fixture.MeasuredLimits(), retry);
        AssertEndpointType(client, type);
        RegistryClientBase builtIn = client.ShouldBeAssignableTo<RegistryClientBase>();
        retry.RetryCount = -1;
        retry.MinimumDelayMilliseconds = -1;
        retry.MaximumJitterMilliseconds = -1;
        builtIn.FileSystemContext.Policy.RetryCount.ShouldBe(2);
        builtIn.FileSystemContext.Policy.MinimumDelayMilliseconds.ShouldBe(3);
        builtIn.FileSystemContext.Policy.MaximumJitterMilliseconds.ShouldBe(4);
        builtIn.FileSystemContext.HasExplicitValues.ShouldBeTrue();
        builtIn.Endpoint.ShouldBeSameAs(endpoint);
        handler.RequestCount.ShouldBe(0);
        if (type == RegistryType.Npm)
            await AssertPreparationAcceptedAsync(client, fixture, handler, controlledTransport);
    }

    [Theory]
    [MemberData(nameof(PartialEndpointPolicies))]
    public async Task EndpointPolicyOverloads_PartialAndInvalidPolicy_RetainValidation(
        RegistryType type, bool controlledTransport, string field, string mode)
    {
        using DirectPolicyTestSupport.EnvironmentScope environment = new();
        DirectPolicyTestSupport fixture = new();
        PackageInstallLimits limits = fixture.MeasuredLimits(except: field);
        if (mode == "explicit")
        {
            DirectPolicyTestSupport.Set(limits, field, fixture.Quantity(field));
            DirectPolicyTestSupport.SetEnvironment(field, "malformed");
        }
        else if (mode == "environment")
        {
            DirectPolicyTestSupport.SetEnvironment(
                field, fixture.Quantity(field).ToString(CultureInfo.InvariantCulture));
        }
        else if (mode == "unresolved-invalid")
        {
            DirectPolicyTestSupport.SetEnvironment(field, "malformed");
        }
        else if (field == "entry")
        {
            limits.MaxExpandedBytes = PackageInstallLimits.DefaultMaxExpandedBytes;
        }

        OfflinePutHandler handler = new(fixture.Archive);
        using HttpClient http = new(handler);
        RegistryEndpoint endpoint = PublicationTestSupport.Endpoint("partial", type);
        if (mode == "unresolved-invalid")
        {
            DirectPolicyTestSupport.AssertInvalidPolicy(Should.Throw<PackageInstallException>(
                () => PolicyClient(endpoint, http, controlledTransport, limits)));
            handler.RequestCount.ShouldBe(0);
            return;
        }

        IRegistryClient client = PolicyClient(endpoint, http, controlledTransport, limits);
        AssertEndpointType(client, type);
        handler.RequestCount.ShouldBe(0);
        if (type == RegistryType.Npm)
        {
            await AssertPreparationAcceptedAsync(client, fixture, handler, controlledTransport);
            if (mode != "explicit")
            {
                DirectPolicyTestSupport.SetEnvironment(
                    field, (fixture.Quantity(field) - 1).ToString(CultureInfo.InvariantCulture));
                IRegistryClient tighter = PolicyClient(endpoint, http, controlledTransport, limits);
                await AssertPreparationRejectedAsync(tighter, fixture, handler, field);
                await AssertPreparationAcceptedAsync(client, fixture, handler, controlledTransport);
            }
        }
    }

    [Theory]
    [MemberData(nameof(InvalidEndpointPolicies))]
    public void EndpointPolicyOverloads_InvalidExplicitPolicy_FailsBeforeHttp(
        RegistryType type, bool controlledTransport, string invalidCase, bool invalidAmbient)
    {
        using DirectPolicyTestSupport.EnvironmentScope environment = new();
        DirectPolicyTestSupport fixture = new();
        foreach (string field in DirectPolicyTestSupport.Fields)
        {
            DirectPolicyTestSupport.SetEnvironment(
                field, invalidAmbient ? "malformed" : (fixture.Quantity(field) + 1).ToString(CultureInfo.InvariantCulture));
        }
        OfflinePutHandler handler = new([]);
        using HttpClient http = new(handler);
        DirectPolicyTestSupport.AssertInvalidPolicy(Should.Throw<PackageInstallException>(
            () => PolicyClient(
                PublicationTestSupport.Endpoint("invalid", type), http, controlledTransport,
                DirectPolicyTestSupport.InvalidLimits(invalidCase)!)));
        handler.RequestCount.ShouldBe(0);
    }

    [Theory]
    [MemberData(nameof(EndpointRetryPolicies))]
    public void EndpointPolicyOverloads_InvalidRetry_FailsBeforeHttp(
        RegistryType type, bool controlledTransport, string invalidCase)
    {
        using DirectPolicyTestSupport.EnvironmentScope environment = new();
        OfflinePutHandler handler = new([]);
        using HttpClient http = new(handler);
        DirectPolicyTestSupport.AssertInvalidPolicy(Should.Throw<PackageInstallException>(
            () => PolicyClient(
                PublicationTestSupport.Endpoint("invalid-retry", type), http, controlledTransport,
                new DirectPolicyTestSupport().MeasuredLimits(),
                DirectPolicyTestSupport.InvalidRetry(invalidCase), useDefaultRetry: false)));
        handler.RequestCount.ShouldBe(0);
    }

    [Theory]
    [MemberData(nameof(EndpointFamilies))]
    public async Task EndpointPolicyOverloads_HigherFiniteConstructionPolicy_IsSupported(
        RegistryType type, bool controlledTransport)
    {
        using DirectPolicyTestSupport.EnvironmentScope environment = new();
        DirectPolicyTestSupport fixture = new();
        OfflinePutHandler handler = new(fixture.Archive);
        using HttpClient http = new(handler);
        IRegistryClient client = PolicyClient(
            PublicationTestSupport.Endpoint("higher", type), http, controlledTransport, DirectPolicyTestSupport.HigherLimits());
        AssertEndpointType(client, type);
        handler.RequestCount.ShouldBe(0);
        if (type == RegistryType.Npm)
            await AssertPreparationAcceptedAsync(client, fixture, handler, controlledTransport);
    }

    [Theory]
    [MemberData(nameof(MixedEndpointPolicies))]
    public async Task EndpointPolicyOverloads_PartialPolicy_MixesExplicitEnvironmentAndDefaults(
        RegistryType type, bool controlledTransport, string field)
    {
        using DirectPolicyTestSupport.EnvironmentScope environment = new();
        DirectPolicyTestSupport fixture = new();
        string ambientField = DirectPolicyTestSupport.Fields[(Array.IndexOf(DirectPolicyTestSupport.Fields, field) + 1) % 6];
        PackageInstallLimits partial = DirectPolicyTestSupport.Single(field, fixture.Quantity(field));
        if ((field == "expanded" || ambientField == "expanded") && field != "entry" && ambientField != "entry")
            partial.MaxEntryBytes = fixture.Quantity("entry");
        DirectPolicyTestSupport.SetEnvironment(field, "malformed");
        DirectPolicyTestSupport.SetEnvironment(
            ambientField, fixture.Quantity(ambientField).ToString(CultureInfo.InvariantCulture));
        OfflinePutHandler handler = new(fixture.Archive);
        using HttpClient http = new(handler);
        RegistryEndpoint endpoint = PublicationTestSupport.Endpoint("mixed", type);
        IRegistryClient client = PolicyClient(endpoint, http, controlledTransport, partial);
        AssertEndpointType(client, type);
        handler.RequestCount.ShouldBe(0);
        if (type == RegistryType.Npm)
        {
            await AssertPreparationAcceptedAsync(client, fixture, handler, controlledTransport);
            DirectPolicyTestSupport.SetEnvironment(
                ambientField, (fixture.Quantity(ambientField) - 1).ToString(CultureInfo.InvariantCulture));
            IRegistryClient tighter = PolicyClient(endpoint, http, controlledTransport, partial);
            await AssertPreparationRejectedAsync(tighter, fixture, handler, ambientField);
            await AssertPreparationAcceptedAsync(client, fixture, handler, controlledTransport);
        }
    }

    [Theory]
    [MemberData(nameof(EndpointFamilies))]
    public async Task EndpointPolicyOverloads_RetrySnapshot_IgnoresOverriddenAndLaterAmbientValues(
        RegistryType type, bool controlledTransport)
    {
        using DirectPolicyTestSupport.EnvironmentScope environment = new(includeRetry: true);
        foreach (string variable in DirectPolicyTestSupport.RetryEnvironmentVariables)
            Environment.SetEnvironmentVariable(variable, "malformed");
        DirectPolicyTestSupport fixture = new();
        OfflinePutHandler handler = new(fixture.Archive);
        using HttpClient http = new(handler);
        RegistryEndpoint endpoint = PublicationTestSupport.Endpoint("retry-snapshot", type);
        FileSystemRetryOptions retry = DirectPolicyTestSupport.Retry();
        IRegistryClient client = PolicyClient(endpoint, http, controlledTransport, fixture.MeasuredLimits(), retry);
        retry.RetryCount = -1;
        retry.MinimumDelayMilliseconds = -1;
        retry.MaximumJitterMilliseconds = -1;
        foreach (string variable in DirectPolicyTestSupport.RetryEnvironmentVariables)
            Environment.SetEnvironmentVariable(variable, "-1");
        RegistryClientBase builtIn = client.ShouldBeAssignableTo<RegistryClientBase>();
        builtIn.FileSystemContext.Policy.RetryCount.ShouldBe(0);
        builtIn.FileSystemContext.Policy.MinimumDelayMilliseconds.ShouldBe(0);
        builtIn.FileSystemContext.Policy.MaximumJitterMilliseconds.ShouldBe(0);
        DirectPolicyTestSupport.AssertInvalidPolicy(Should.Throw<PackageInstallException>(
            () => PolicyClient(endpoint, http, controlledTransport, fixture.MeasuredLimits(), new FileSystemRetryOptions())));
        handler.RequestCount.ShouldBe(0);
        if (type == RegistryType.Npm)
            await AssertPreparationAcceptedAsync(client, fixture, handler, controlledTransport);
    }

    [Theory]
    [MemberData(nameof(NpmSnapshotPolicies))]
    public void LegacyNpmEndpointFactories_UnresolvedInvalidEnvironment_RetainsTypedFailure(
        bool controlledTransport, string field)
    {
        using DirectPolicyTestSupport.EnvironmentScope environment = new();
        DirectPolicyTestSupport.SetEnvironment(field, "malformed");
        NoRequestsHandler handler = new();
        using HttpClient http = new(handler);
        RegistryEndpoint endpoint = PublicationTestSupport.Endpoint("legacy-policy", RegistryType.Npm);
        RegistryHttpTransport transport = PublicationTestSupport.Transport(http);
        Action[] constructors = controlledTransport
            ?
            [
                () => RegistryClientFactory.CreateClientForEndpoint(endpoint, transport, NullLoggerFactory.Instance),
                () => RegistryClientFactory.CreateClientForEndpoint(endpoint, transport, NullLoggerFactory.Instance, null, DirectPolicyTestSupport.Retry())
            ]
            :
            [
                () => RegistryClientFactory.CreateClientForEndpoint(endpoint, http, NullLoggerFactory.Instance),
                () => RegistryClientFactory.CreateClientForEndpoint(endpoint, http, NullLoggerFactory.Instance, default, DirectPolicyTestSupport.Retry())
            ];
        foreach (Action construct in constructors)
            DirectPolicyTestSupport.AssertInvalidPolicy(Should.Throw<PackageInstallException>(construct));
        handler.RequestCount.ShouldBe(0);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EndpointPolicyOverloads_OrdinaryArguments_PrecedeTypedPolicyValidation(bool controlledTransport)
    {
        using DirectPolicyTestSupport.EnvironmentScope environment = new();
        NoRequestsHandler handler = new();
        using HttpClient http = new(handler);
        RegistryEndpoint endpoint = PublicationTestSupport.Endpoint("arguments");
        RegistryHttpTransport transport = PublicationTestSupport.Transport(http);
        FileSystemRetryOptions retry = DirectPolicyTestSupport.Retry();
        Action[] invalidArguments = controlledTransport
            ?
            [
                () => RegistryClientFactory.CreateClientForEndpoint(null!, transport, NullLoggerFactory.Instance, null, retry, null!),
                () => RegistryClientFactory.CreateClientForEndpoint(endpoint, (RegistryHttpTransport)null!, NullLoggerFactory.Instance, null, retry, null!),
                () => RegistryClientFactory.CreateClientForEndpoint(endpoint, transport, null!, null, retry, null!)
            ]
            :
            [
                () => RegistryClientFactory.CreateClientForEndpoint(null!, http, NullLoggerFactory.Instance, null, retry, null!),
                () => RegistryClientFactory.CreateClientForEndpoint(endpoint, (HttpClient)null!, NullLoggerFactory.Instance, null, retry, null!),
                () => RegistryClientFactory.CreateClientForEndpoint(endpoint, http, null!, null, retry, null!)
            ];
        foreach (Action construct in invalidArguments)
            Should.Throw<ArgumentNullException>(construct);
        Should.Throw<ArgumentOutOfRangeException>(() => PolicyClient(
            PublicationTestSupport.Endpoint("unsupported", (RegistryType)999),
            http, controlledTransport, new DirectPolicyTestSupport().MeasuredLimits()));
        handler.RequestCount.ShouldBe(0);
    }

    [Theory]
    [MemberData(nameof(NpmSnapshotPolicies))]
    public async Task EndpointPolicyOverloads_NpmSnapshots_ControlPublishPreparation(
        bool controlledTransport, string field)
    {
        using DirectPolicyTestSupport.EnvironmentScope environment = new();
        DirectPolicyTestSupport fixture = new();
        PackageInstallLimits acceptedPolicy = fixture.MeasuredLimits();
        PackageInstallLimits rejectedPolicy = fixture.MeasuredLimits();
        DirectPolicyTestSupport.Set(rejectedPolicy, field, fixture.Quantity(field) - 1);
        OfflinePutHandler handler = new(fixture.Archive);
        using HttpClient http = new(handler);
        RegistryEndpoint endpoint = PublicationTestSupport.Endpoint("npm-snapshot", RegistryType.Npm);
        IRegistryClient accepted = PolicyClient(endpoint, http, controlledTransport, acceptedPolicy);
        IRegistryClient rejected = PolicyClient(endpoint, http, controlledTransport, rejectedPolicy);
        await AssertPreparationAcceptedAsync(accepted, fixture, handler, controlledTransport);
        await AssertPreparationRejectedAsync(rejected, fixture, handler, field);
        foreach (string selected in DirectPolicyTestSupport.Fields)
        {
            DirectPolicyTestSupport.Set(acceptedPolicy, selected, 0);
            DirectPolicyTestSupport.Set(rejectedPolicy, selected, long.MaxValue);
            DirectPolicyTestSupport.SetEnvironment(selected, "malformed");
        }

        // HttpClient remains unverified: successful local preparation reaches the
        // retained body-transport guard, not a PUT. Supplied controlled transport
        // reaches only our offline counting handler, never an actual registry.
        await AssertPreparationAcceptedAsync(accepted, fixture, handler, controlledTransport);
        await AssertPreparationRejectedAsync(rejected, fixture, handler, field);
        await AssertPreparationAcceptedAsync(accepted, fixture, handler, controlledTransport);
        await AssertPreparationRejectedAsync(rejected, fixture, handler, field);
        handler.RequestCount.ShouldBe(controlledTransport ? 3 : 0);
    }

    private static IRegistryClient PolicyClient(
        RegistryEndpoint endpoint, HttpClient http, bool controlledTransport,
        PackageInstallLimits limits, FileSystemRetryOptions? retry = null, bool useDefaultRetry = true) =>
        controlledTransport
            ? RegistryClientFactory.CreateClientForEndpoint(
                endpoint, PublicationTestSupport.Transport(http), NullLoggerFactory.Instance,
                null, useDefaultRetry ? retry ?? DirectPolicyTestSupport.Retry() : retry!, limits)
            : RegistryClientFactory.CreateClientForEndpoint(
                endpoint, http, NullLoggerFactory.Instance,
                default, useDefaultRetry ? retry ?? DirectPolicyTestSupport.Retry() : retry!, limits);

    private static void AssertEndpointType(IRegistryClient client, RegistryType type)
    {
        client.GetType().ShouldBe(type switch
        {
            RegistryType.FhirNpm => typeof(FhirNpmRegistryClient),
            RegistryType.Npm => typeof(NpmRegistryClient),
            RegistryType.FhirCiBuild => typeof(FhirCiBuildClient),
            RegistryType.FhirHttp => typeof(Hl7WebsiteClient),
            _ => throw new ArgumentOutOfRangeException(nameof(type))
        });
    }

    private static async Task AssertPreparationAcceptedAsync(
        IRegistryClient client, DirectPolicyTestSupport fixture, OfflinePutHandler handler, bool controlled)
    {
        TestContext.Current.TestOutputHelper?.WriteLine(
            $"Local NPM preparation accepted ({(controlled ? "offline PUT" : "unverified body guard")}): {fixture.DescribeQuantities()}");
        int before = handler.RequestCount;
        using MemoryStream source = new(fixture.Archive);
        if (controlled)
        {
            PublishResult result = await client.PublishAsync(
                fixture.Reference, source, TestContext.Current.CancellationToken);
            result.Success.ShouldBeTrue();
            handler.RequestCount.ShouldBe(before + 1);
        }
        else
        {
            InvalidOperationException exception = await Should.ThrowAsync<InvalidOperationException>(
                () => client.PublishAsync(fixture.Reference, source, TestContext.Current.CancellationToken));
            exception.GetType().ShouldBe(typeof(InvalidOperationException));
            exception.Message.ShouldBe("Registry requests with a body require a redirect-controlled transport.");
            handler.RequestCount.ShouldBe(before);
        }
        source.Position.ShouldBe(fixture.Archive.LongLength);
        source.CanRead.ShouldBeTrue();
        source.ToArray().ShouldBe(fixture.Archive);
    }

    private static async Task AssertPreparationRejectedAsync(
        IRegistryClient client, DirectPolicyTestSupport fixture, OfflinePutHandler handler, string field)
    {
        int before = handler.RequestCount;
        using MemoryStream source = new(fixture.Archive);
        PackageInstallException exception = await Should.ThrowAsync<PackageInstallException>(
            () => client.PublishAsync(fixture.Reference, source, TestContext.Current.CancellationToken));
        exception.ErrorCode.ShouldBe(DirectPolicyTestSupport.Error(field));
        exception.Stage.ShouldBe(field == "compressed"
            ? PackageInstallStage.Acquisition
            : PackageInstallStage.ArchiveValidation);
        TestContext.Current.TestOutputHelper?.WriteLine(
            $"Local NPM preparation rejected without PUT: {field} quantity={fixture.Quantity(field)}, ceiling={fixture.Quantity(field) - 1}");
        handler.RequestCount.ShouldBe(before);
        source.CanRead.ShouldBeTrue();
        source.ToArray().ShouldBe(fixture.Archive);
    }

    private sealed class OfflinePutHandler(byte[] expectedArchive) : HttpMessageHandler
    {
        internal int RequestCount { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            request.Method.ShouldBe(HttpMethod.Put);
            request.RequestUri!.Host.ShouldEndWith(".example");
            request.Content!.Headers.ContentType!.MediaType.ShouldBe("application/json");
            using JsonDocument body = JsonDocument.Parse(
                await request.Content.ReadAsByteArrayAsync(cancellationToken));
            JsonElement attachment = body.RootElement.GetProperty("_attachments").EnumerateObject().Single().Value;
            attachment.GetProperty("length").GetInt64().ShouldBe(expectedArchive.LongLength);
            Convert.FromBase64String(attachment.GetProperty("data").GetString()!).ShouldBe(expectedArchive);
            return new HttpResponseMessage(HttpStatusCode.Created)
            {
                Content = new StringContent("""{"ok":true}""")
            };
        }
    }

    [Theory]
    [InlineData(RegistryType.FhirNpm, false)]
    [InlineData(RegistryType.Npm, false)]
    [InlineData(RegistryType.FhirCiBuild, false)]
    [InlineData(RegistryType.FhirHttp, false)]
    [InlineData(RegistryType.FhirNpm, true)]
    [InlineData(RegistryType.Npm, true)]
    [InlineData(RegistryType.FhirCiBuild, true)]
    [InlineData(RegistryType.FhirHttp, true)]
    public void EndpointOptionsOverloads_RetainSnapshot(
        RegistryType type,
        bool controlledTransport)
    {
        NoRequestsHandler handler = new();
        using HttpClient http = new(handler);
        RegistryEndpoint endpoint = PublicationTestSupport.Endpoint("configured", type);
        FileSystemRetryOptions options = new()
        {
            RetryCount = 2,
            MinimumDelayMilliseconds = 3,
            MaximumJitterMilliseconds = 4,
        };
        IRegistryClient client = controlledTransport
            ? RegistryClientFactory.CreateClientForEndpoint(
                endpoint, PublicationTestSupport.Transport(http), NullLoggerFactory.Instance, null, options)
            : RegistryClientFactory.CreateClientForEndpoint(
                endpoint, http, NullLoggerFactory.Instance, default, options);
        RegistryClientBase builtIn = client.ShouldBeAssignableTo<RegistryClientBase>();
        options.RetryCount = 0;
        options.MinimumDelayMilliseconds = 0;
        options.MaximumJitterMilliseconds = 0;

        builtIn.FileSystemContext.Policy.RetryCount.ShouldBe(2);
        builtIn.FileSystemContext.Policy.MinimumDelayMilliseconds.ShouldBe(3);
        builtIn.FileSystemContext.Policy.MaximumJitterMilliseconds.ShouldBe(4);
        builtIn.FileSystemContext.HasExplicitValues.ShouldBeTrue();
        builtIn.Endpoint.ShouldBeSameAs(endpoint);
        handler.RequestCount.ShouldBe(0);
    }

    [Theory]
    [InlineData(RegistryType.FhirNpm)]
    [InlineData(RegistryType.Npm)]
    [InlineData(RegistryType.FhirCiBuild)]
    [InlineData(RegistryType.FhirHttp)]
    public async Task EndpointContextForwarding_RetainsSuppliedExecutor(RegistryType type)
    {
        ScriptedFileSystemPrimitives primitives = PublicationTestSupport.SystemFiles();
        primitives.Enqueue("GetTempPath", ScriptedFileSystemPrimitives.NativeFailure<string>(1117, "temp discovery"));
        primitives.Set("GetTempPath", FileSystemReplayOutcome<string>.Complete("fixture-temp"));
        FileSystemContext context = primitives.CreateContext(retries: 1);
        NoRequestsHandler handler = new();
        using HttpClient http = new(handler);
        IRegistryClient client = RegistryClientFactory.CreateClientForEndpoint(
            PublicationTestSupport.Endpoint("configured", type),
            PublicationTestSupport.Transport(http),
            NullLoggerFactory.Instance,
            new PackageInstallLimits(),
            null,
            null,
            context);
        RegistryClientBase builtIn = client.ShouldBeAssignableTo<RegistryClientBase>();

        string path = await builtIn.FileSystemContext.FileSystem.GetTempPathAsync(TestContext.Current.CancellationToken);

        path.ShouldBe("fixture-temp");
        builtIn.FileSystemContext.ShouldBeSameAs(context);
        primitives.Count("GetTempPath").ShouldBe(2);
        primitives.AsyncWaits.ShouldBe([10]);
        primitives.Waits.ShouldBeEmpty();
        handler.RequestCount.ShouldBe(0);
    }

    [Fact]
    public void BuildRegistryClient_AllBuiltInLeavesShareSuppliedContext()
    {
        ScriptedFileSystemPrimitives primitives = PublicationTestSupport.SystemFiles();
        FileSystemContext context = primitives.CreateContext(retries: 2);
        NoRequestsHandler handler = new();
        using HttpClient http = new(handler);
        FhirPackageManagerOptions options = new()
        {
            Registries =
            [
                PublicationTestSupport.Endpoint("fhir"),
                PublicationTestSupport.Endpoint("npm", RegistryType.Npm),
            ],
            IncludeCiBuilds = true,
            IncludeHl7WebsiteFallback = true,
        };

        IRegistryClient client = RegistryClientFactory.BuildRegistryClient(
            options, PublicationTestSupport.Transport(http), NullLoggerFactory.Instance, null, context);

        RedundantRegistryClient composite = client.ShouldBeOfType<RedundantRegistryClient>();
        composite.FileSystemContext.ShouldBeSameAs(context);
        RegistryClientBase[] leaves = composite.BuiltInClients.ToArray();
        leaves.Select(leaf => leaf.GetType()).ShouldBe(
            [typeof(FhirNpmRegistryClient), typeof(NpmRegistryClient), typeof(FhirCiBuildClient), typeof(Hl7WebsiteClient)]);
        foreach (RegistryClientBase leaf in leaves)
            leaf.FileSystemContext.ShouldBeSameAs(context);
        primitives.Calls.ShouldBeEmpty();
        handler.RequestCount.ShouldBe(0);
    }

    [Fact]
    public void CompositeFactory_PreservesLegacyConstructorBindings()
    {
        NoRequestsHandler handler = new();
        using HttpClient http = new(handler);
        FhirNpmRegistryClient first = new(
            http, PublicationTestSupport.Endpoint("first"), NullLogger<FhirNpmRegistryClient>.Instance);
        NpmRegistryClient second = new(
            http, PublicationTestSupport.Endpoint("second", RegistryType.Npm), NullLogger<NpmRegistryClient>.Instance);
        Should.Throw<ArgumentException>(() => new RedundantRegistryClient());
        RedundantRegistryClient one = new(first);
        IEnumerable<IRegistryClient> clients = [first, second];
        RedundantRegistryClient enumerable = new(clients);
        RedundantRegistryClient nullLogger = new(clients, null);
        RedundantRegistryClient defaultLogger = new(clients, default);
        RedundantRegistryClient expandedTwo = new(first, second);
        RedundantRegistryClient expandedThree = new(first, second, first);
        RedundantRegistryClient nested = new(one, second);
        FileSystemRetryOptions options = new()
        {
            RetryCount = 2,
            MinimumDelayMilliseconds = 3,
            MaximumJitterMilliseconds = 4,
        };
        RedundantRegistryClient configured = RedundantRegistryClient.CreateWithFileSystemRetry(
            clients, options, null);
        options.RetryCount = 0;

        // These deliberately invalid-client expressions must compile as legacy
        // params calls; executing them would dereference the null leaf clients.
        _ = (Func<RedundantRegistryClient>)(() => new RedundantRegistryClient(null!, null!, default!));
        _ = (Func<RedundantRegistryClient>)(() => new RedundantRegistryClient(null!));
        _ = (Func<RedundantRegistryClient>)(() => new RedundantRegistryClient(default!));

        one.BuiltInClients.ShouldBe([first]);
        foreach (RedundantRegistryClient composite in new[]
            { enumerable, nullLogger, defaultLogger, expandedTwo, nested, configured })
        {
            composite.Endpoint.ShouldBeSameAs(first.Endpoint);
            composite.BuiltInClients.ShouldBe([first, second]);
            composite.SupportedNameTypes.ShouldBe(first.SupportedNameTypes);
        }
        expandedThree.BuiltInClients.ShouldBe([first, second, first]);
        configured.FileSystemContext.Policy.RetryCount.ShouldBe(2);
        configured.FileSystemContext.Policy.MinimumDelayMilliseconds.ShouldBe(3);
        configured.FileSystemContext.Policy.MaximumJitterMilliseconds.ShouldBe(4);
        handler.RequestCount.ShouldBe(0);
    }

    [Fact]
    public void StandaloneConstructorsAndLegacyEndpointFactories_PreserveBindings()
    {
        using DirectPolicyTestSupport.EnvironmentScope environment = new();
        NoRequestsHandler handler = new();
        using HttpClient http = new(handler);
        RegistryEndpoint endpoint = PublicationTestSupport.Endpoint("legacy");
        RegistryHttpTransport transport = PublicationTestSupport.Transport(http);
        FileSystemRetryOptions options = new()
        {
            RetryCount = 2,
            MinimumDelayMilliseconds = 0,
            MaximumJitterMilliseconds = 0,
        };
        FhirNpmRegistryClient fhir = new(http, endpoint, NullLogger<FhirNpmRegistryClient>.Instance, options);
        NpmRegistryClient npm = new(http, endpoint, NullLogger<NpmRegistryClient>.Instance, options);
        options.RetryCount = 0;
        ILoggerFactory loggers = NullLoggerFactory.Instance;
        IRegistryClient[] clients =
        [
            RegistryClientFactory.CreateClientForEndpoint(endpoint, http, loggers),
            RegistryClientFactory.CreateClientForEndpoint(endpoint, http, loggers, null),
            RegistryClientFactory.CreateClientForEndpoint(endpoint, http, loggers, default),
            RegistryClientFactory.CreateClientForEndpoint(endpoint, transport, loggers),
            RegistryClientFactory.CreateClientForEndpoint(endpoint, transport, loggers, timeProvider: null),
            RegistryClientFactory.CreateClientForEndpoint(endpoint, transport, loggers, timeProvider: default),
            RegistryClientFactory.CreateClientForEndpoint(endpoint, http, loggers, null, options),
            RegistryClientFactory.CreateClientForEndpoint(endpoint, http, loggers, default, options),
            RegistryClientFactory.CreateClientForEndpoint(
                endpoint: endpoint, httpClient: http, loggerFactory: loggers, timeProvider: default, fileSystemRetry: options),
            RegistryClientFactory.CreateClientForEndpoint(endpoint, transport, loggers, null, options),
            RegistryClientFactory.CreateClientForEndpoint(endpoint, transport, loggers, default, options),
            RegistryClientFactory.CreateClientForEndpoint(
                endpoint: endpoint, transport: transport, loggerFactory: loggers, timeProvider: null, fileSystemRetry: options),
            RegistryClientFactory.CreateClientForEndpoint(endpoint, http, loggers, null, options, new PackageInstallLimits()),
            RegistryClientFactory.CreateClientForEndpoint(endpoint, http, loggers, default, options, new PackageInstallLimits()),
            RegistryClientFactory.CreateClientForEndpoint(
                endpoint: endpoint, httpClient: http, loggerFactory: loggers, timeProvider: default,
                fileSystemRetry: options, installLimits: new PackageInstallLimits()),
            RegistryClientFactory.CreateClientForEndpoint(endpoint, transport, loggers, null, options, new PackageInstallLimits()),
            RegistryClientFactory.CreateClientForEndpoint(endpoint, transport, loggers, default, options, new PackageInstallLimits()),
            RegistryClientFactory.CreateClientForEndpoint(
                endpoint: endpoint, transport: transport, loggerFactory: loggers, timeProvider: null,
                fileSystemRetry: options, installLimits: new PackageInstallLimits()),
        ];

        // Compile-time examples are in a friend assembly. They are not external
        // consumer or packaged-binary binding proof; public-only metadata below
        // establishes the local candidate set, leaving that later proof to Q2.
        Func<IRegistryClient>[] typedNullFamilies =
        [
            () => RegistryClientFactory.CreateClientForEndpoint(endpoint, (HttpClient)null!, loggers),
            () => RegistryClientFactory.CreateClientForEndpoint(endpoint, (RegistryHttpTransport)null!, loggers),
            () => RegistryClientFactory.CreateClientForEndpoint(endpoint, (HttpClient)default!, loggers, default),
            () => RegistryClientFactory.CreateClientForEndpoint(endpoint, (RegistryHttpTransport)default!, loggers, null),
            () => RegistryClientFactory.CreateClientForEndpoint(endpoint, (HttpClient)null!, loggers, null, options),
            () => RegistryClientFactory.CreateClientForEndpoint(endpoint, (RegistryHttpTransport)null!, loggers, default, options),
            () => RegistryClientFactory.CreateClientForEndpoint(endpoint, (HttpClient)null!, loggers, null, options, new PackageInstallLimits()),
            () => RegistryClientFactory.CreateClientForEndpoint(endpoint, (RegistryHttpTransport)null!, loggers, default, options, new PackageInstallLimits())
        ];
        foreach (Func<IRegistryClient> construct in typedNullFamilies)
            Should.Throw<ArgumentNullException>(() => construct());

        Action[] typedNullPolicies =
        [
            () => RegistryClientFactory.CreateClientForEndpoint(endpoint, http, loggers, null, options, (PackageInstallLimits)null!),
            () => RegistryClientFactory.CreateClientForEndpoint(endpoint, transport, loggers, default, options, default!),
            () => RegistryClientFactory.CreateClientForEndpoint(endpoint, http, loggers, default, (FileSystemRetryOptions)null!, new PackageInstallLimits()),
            () => RegistryClientFactory.CreateClientForEndpoint(endpoint, transport, loggers, null, default!, new PackageInstallLimits()),
            () => RegistryClientFactory.CreateClientForEndpoint(endpoint, http, loggers, null, (FileSystemRetryOptions)null!),
            () => RegistryClientFactory.CreateClientForEndpoint(endpoint, transport, loggers, default, (FileSystemRetryOptions)default!)
        ];
        foreach (Action construct in typedNullPolicies)
            DirectPolicyTestSupport.AssertInvalidPolicy(Should.Throw<PackageInstallException>(construct));

        Should.Throw<ArgumentNullException>(() =>
            RegistryClientFactory.CreateClientForEndpoint(endpoint, http, (ILoggerFactory)null!, null, options, new PackageInstallLimits()));
        Should.Throw<ArgumentNullException>(() =>
            RegistryClientFactory.CreateClientForEndpoint(endpoint, transport, default!, default, options, new PackageInstallLimits()));

        string root = Path.Combine(Path.GetTempPath(), $"fhirpkg-binding-{Guid.NewGuid():N}");
        string? originalRoot = Environment.GetEnvironmentVariable("PACKAGE_CACHE_FOLDER");
        List<DiskPackageCache> caches = [];
        try
        {
            Environment.SetEnvironmentVariable("PACKAGE_CACHE_FOLDER", root);
            caches.AddRange(
            [
                new DiskPackageCache(),
                new DiskPackageCache(null),
                new DiskPackageCache(default),
                new DiskPackageCache(root, null),
                new DiskPackageCache(root, default),
                new DiskPackageCache(root, null, null),
                new DiskPackageCache(root, default, default),
                new DiskPackageCache(cacheDirectory: root, logger: null, timeProvider: default),
                new DiskPackageCache(root, null, null, options),
                new DiskPackageCache(root, default, default, options),
                new DiskPackageCache(cacheDirectory: root, logger: default, timeProvider: null, fileSystemRetry: options),
                new DiskPackageCache(root, null, null, options, new PackageInstallLimits()),
                new DiskPackageCache(root, default, default, options, new PackageInstallLimits()),
                new DiskPackageCache(
                    cacheDirectory: root, logger: null, timeProvider: default,
                    fileSystemRetry: options, installLimits: new PackageInstallLimits())
            ]);
            foreach (DiskPackageCache cache in caches)
                cache.CacheDirectory.ShouldBe(root);
            Action[] nullCachePolicies =
            [
                () => new DiskPackageCache(root, null, null, options, (PackageInstallLimits)null!),
                () => new DiskPackageCache(root, default, default, options, default!),
                () => new DiskPackageCache(root, null, default, (FileSystemRetryOptions)null!, new PackageInstallLimits()),
                () => new DiskPackageCache(root, default, null, default!, new PackageInstallLimits()),
                () => new DiskPackageCache(root, null, null, (FileSystemRetryOptions)null!)
            ];
            foreach (Action construct in nullCachePolicies)
                DirectPolicyTestSupport.AssertInvalidPolicy(Should.Throw<PackageInstallException>(construct));
        }
        finally
        {
            foreach (DiskPackageCache cache in caches)
                cache.Dispose();
            Environment.SetEnvironmentVariable("PACKAGE_CACHE_FOLDER", originalRoot);
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }

        fhir.FileSystemContext.Policy.RetryCount.ShouldBe(2);
        npm.FileSystemContext.Policy.ShouldBe(fhir.FileSystemContext.Policy);
        foreach (IRegistryClient client in clients)
        {
            client.ShouldBeOfType<FhirNpmRegistryClient>();
            client.Endpoint.ShouldBeSameAs(endpoint);
        }
        handler.RequestCount.ShouldBe(0);
    }

    [Fact]
    public void PublicPolicySurface_AdditionalRequiredArity_PreservesLegacySignatures()
    {
        // Public-only inspection is essential: this test assembly can also see
        // internal overloads, which are deliberately not consumer candidates.
        ConstructorInfo[] constructors = typeof(DiskPackageCache)
            .GetConstructors(BindingFlags.Public | BindingFlags.Instance);
        constructors.Length.ShouldBe(3);
        Type[] cacheTypes =
            [typeof(string), typeof(ILogger<DiskPackageCache>), typeof(TimeProvider), typeof(FileSystemRetryOptions), typeof(PackageInstallLimits)];
        string[] cacheNames = ["cacheDirectory", "logger", "timeProvider", "fileSystemRetry", "installLimits"];
        AssertParameters(constructors.Single(c => c.GetParameters().Length == 3), cacheTypes[..3], cacheNames[..3], optionalStart: 0);
        AssertParameters(constructors.Single(c => c.GetParameters().Length == 4), cacheTypes[..4], cacheNames[..4]);
        AssertParameters(constructors.Single(c => c.GetParameters().Length == 5), cacheTypes, cacheNames);

        MethodInfo[] factories = typeof(RegistryClientFactory)
            .GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly);
        factories.Length.ShouldBe(8);
        MethodInfo[] endpointFactories = factories.Where(m => m.Name == "CreateClientForEndpoint").ToArray();
        endpointFactories.Length.ShouldBe(6);
        foreach ((Type transportType, string transportName) in new[]
            { (typeof(HttpClient), "httpClient"), (typeof(RegistryHttpTransport), "transport") })
        {
            Type[] types =
                [typeof(RegistryEndpoint), transportType, typeof(ILoggerFactory), typeof(TimeProvider), typeof(FileSystemRetryOptions), typeof(PackageInstallLimits)];
            string[] names = ["endpoint", transportName, "loggerFactory", "timeProvider", "fileSystemRetry", "installLimits"];
            MethodInfo[] family = endpointFactories.Where(m => m.GetParameters()[1].ParameterType == transportType).ToArray();
            family.Length.ShouldBe(3);
            foreach (MethodInfo method in family)
                method.ReturnType.ShouldBe(typeof(IRegistryClient));
            AssertParameters(family.Single(m => m.GetParameters().Length == 4), types[..4], names[..4], optionalStart: 3);
            AssertParameters(family.Single(m => m.GetParameters().Length == 5), types[..5], names[..5]);
            AssertParameters(family.Single(m => m.GetParameters().Length == 6), types, names);

            MethodInfo composite = factories.Single(m =>
                m.Name == "BuildRegistryClient" && m.GetParameters()[1].ParameterType == transportType);
            composite.ReturnType.ShouldBe(typeof(IRegistryClient));
            AssertParameters(composite,
                [typeof(FhirPackageManagerOptions), transportType, typeof(ILoggerFactory), typeof(TimeProvider)],
                ["options", transportName, "loggerFactory", "timeProvider"], optionalStart: 3);
        }
    }

    private static void AssertParameters(
        MethodBase member, Type[] types, string[] names, int optionalStart = int.MaxValue)
    {
        ParameterInfo[] parameters = member.GetParameters();
        parameters.Select(p => p.ParameterType).ShouldBe(types);
        parameters.Select(p => p.Name).ShouldBe(names);
        for (int index = 0; index < parameters.Length; index++)
        {
            parameters[index].IsOptional.ShouldBe(index >= optionalStart);
            parameters[index].HasDefaultValue.ShouldBe(index >= optionalStart);
            if (index >= optionalStart)
                parameters[index].DefaultValue.ShouldBeNull();
        }
    }

    [Fact]
    public void ConfiguredEndpoints_InvalidPolicyPrecedesAnyRequest()
    {
        NoRequestsHandler handler = new();
        using HttpClient http = new(handler);
        RegistryEndpoint endpoint = PublicationTestSupport.Endpoint("invalid");
        FileSystemRetryOptions invalid = new()
        {
            RetryCount = -1,
            MinimumDelayMilliseconds = 0,
            MaximumJitterMilliseconds = 0,
        };
        Action[] constructors =
        [
            () => RegistryClientFactory.CreateClientForEndpoint(
                endpoint, http, NullLoggerFactory.Instance, null, invalid),
            () => RegistryClientFactory.CreateClientForEndpoint(
                endpoint, PublicationTestSupport.Transport(http), NullLoggerFactory.Instance, null, invalid),
            () => new NpmRegistryClient(http, endpoint, NullLogger<NpmRegistryClient>.Instance, invalid),
            () => new FhirNpmRegistryClient(http, endpoint, NullLogger<FhirNpmRegistryClient>.Instance, invalid),
        ];

        foreach (Action construct in constructors)
        {
            PackageInstallException exception = Should.Throw<PackageInstallException>(construct);
            exception.ErrorCode.ShouldBe(PackageInstallErrorCode.InvalidPolicy);
            exception.Stage.ShouldBe(PackageInstallStage.PolicyValidation);
        }
        handler.RequestCount.ShouldBe(0);
    }

    private sealed class NoRequestsHandler : HttpMessageHandler
    {
        internal int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestCount++;
            throw new InvalidOperationException("Constructor and factory tests must not make HTTP requests.");
        }
    }

    [Fact]
    public void BuildRegistryClient_HttpClientOverloadWithGitHubToken_Throws()
    {
        FhirPackageManagerOptions options = new() { GitHubToken = TestToken };
        using HttpClient httpClient = new();

        InvalidOperationException exception = Should.Throw<InvalidOperationException>(
            () => RegistryClientFactory.BuildRegistryClient(
                options,
                httpClient,
                NullLoggerFactory.Instance));

        exception.Message.ShouldContain("GitHubToken");
        exception.Message.ShouldContain("redirect-controlled");
    }

    [Fact]
    public void BuildRegistryClient_HttpClientOverloadWithoutGitHubToken_BuildsClient()
    {
        FhirPackageManagerOptions options = new();
        using HttpClient httpClient = new();

        options.GitHubToken.ShouldBeNull();

        IRegistryClient client = RegistryClientFactory.BuildRegistryClient(
            options,
            httpClient,
            NullLoggerFactory.Instance);

        client.ShouldNotBeNull();
    }

    [Fact]
    public void BuildRegistryClient_RedirectControlledTransportWithGitHubToken_BuildsClient()
    {
        FhirPackageManagerOptions options = new() { GitHubToken = TestToken };

        options.IncludeCiBuilds.ShouldBeTrue();

        using HttpClient httpClient = new(new HttpClientHandler { AllowAutoRedirect = false });
        RegistryHttpTransport transport = RegistryHttpTransport.CreateRedirectControlled(
            httpClient,
            TimeSpan.FromSeconds(30),
            maxRedirects: 5);

        IRegistryClient client = RegistryClientFactory.BuildRegistryClient(
            options,
            transport,
            NullLoggerFactory.Instance);

        client.ShouldNotBeNull();
    }

    [Fact]
    public void BuildRegistryClient_UnverifiedTransportWithGitHubToken_Throws()
    {
        FhirPackageManagerOptions options = new() { GitHubToken = TestToken };

        options.IncludeCiBuilds.ShouldBeTrue();

        using HttpClient httpClient = new();
        RegistryHttpTransport transport = RegistryHttpTransport.CreateUnverified(httpClient);

        InvalidOperationException exception = Should.Throw<InvalidOperationException>(
            () => RegistryClientFactory.BuildRegistryClient(
                options,
                transport,
                NullLoggerFactory.Instance));

        exception.Message.ShouldContain("Authenticated GitHub repository lookups");
        exception.Message.ShouldContain("redirect-controlled");
    }
}
