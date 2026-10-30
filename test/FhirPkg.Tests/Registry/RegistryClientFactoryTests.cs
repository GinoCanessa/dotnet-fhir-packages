// Copyright (c) Gino Canessa. Licensed under the MIT License.

using FhirPkg;
using FhirPkg.Installation;
using FhirPkg.IO;
using FhirPkg.Models;
using FhirPkg.Registry;
using FhirPkg.Tests.Support;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;

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
        ];

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
