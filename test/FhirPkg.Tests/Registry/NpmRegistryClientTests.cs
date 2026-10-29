// Copyright (c) Gino Canessa. Licensed under the MIT License.

using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FhirPkg.Installation;
using FhirPkg.IO;
using FhirPkg.Models;
using FhirPkg.Registry;
using FhirPkg.Tests.Support;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;

namespace FhirPkg.Tests.Registry;

[Collection("EnvironmentVariable")]
public sealed class NpmRegistryClientTests : IDisposable
{
    private readonly string _tempRoot = Path.Combine(
        Path.GetTempPath(),
        $"fhirpkg-npm-tests-{Guid.NewGuid():N}");

    public NpmRegistryClientTests()
    {
        Directory.CreateDirectory(_tempRoot);
    }

    [Fact]
    public async Task NpmSpool_PartialWriteDoesNotRereadOrDoubleHash()
    {
        byte[] resource = new byte[220_000];
        new Random(11).NextBytes(resource);
        byte[] archive = CreateArchive("example.package", "1.0.0", resource: resource);
        ScriptedFileSystemPrimitives primitives = PublicationTestSupport.SystemFiles();
        using TrackingReadStream source = new(new MemoryStream(archive), canSeek: false);
        primitives.Enqueue<bool>("WriteAtAsync", call =>
        {
            Path.GetFileName(call.Path).ShouldBe("package.tgz");
            call.Offset.ShouldBe(0);
            source.BytesRead.ShouldBe(81_920);
            RandomAccess.Write(call.Handle!, call.Content.Span[..7], call.Offset!.Value);
            return ScriptedFileSystemPrimitives.NativeFailure<bool>(1117, "spool write", call.Path);
        });
        primitives.OnWait = () =>
        {
            source.ReadCalls.ShouldBe(1);
            source.BytesRead.ShouldBe(81_920);
        };
        CaptureHandler handler = new();
        using HttpClient http = CreateHttpClient(handler);
        NpmRegistryClient client = CreateClient(http, context: primitives.CreateContext(retries: 1));

        PublishResult result = await client.PublishAsync(
            new PackageReference("example.package", "1.0.0"),
            source,
            TestContext.Current.CancellationToken);

        result.Success.ShouldBeTrue();
        handler.RequestCount.ShouldBe(1);
        source.BytesRead.ShouldBe(archive.LongLength);
        source.ReadCalls.ShouldBe(((archive.Length + 81_919) / 81_920) + 1);
        source.WasDisposed.ShouldBeFalse();
        source.PositionWasRead.ShouldBeFalse();
        FileSystemPrimitiveCall[] spoolWrites = primitives.Calls
            .Where(call => call.Operation == "WriteAtAsync" && Path.GetFileName(call.Path) == "package.tgz")
            .ToArray();
        spoolWrites.Length.ShouldBe(((archive.Length + 81_919) / 81_920) + 1);
        spoolWrites[0].Offset.ShouldBe(0);
        spoolWrites[1].Offset.ShouldBe(0);
        spoolWrites[0].Handle.ShouldBeSameAs(spoolWrites[1].Handle);
        spoolWrites[0].Content.Equals(spoolWrites[1].Content).ShouldBeTrue();
        spoolWrites[1].Content.ToArray().ShouldBe(archive[..81_920]);
        primitives.AsyncWaits.ShouldBe([10]);
        primitives.Waits.ShouldBeEmpty();
        using JsonDocument document = JsonDocument.Parse(handler.Body);
        JsonElement version = document.RootElement.GetProperty("versions").GetProperty("1.0.0");
        version.GetProperty("dist").GetProperty("shasum").GetString().ShouldBe(
            Convert.ToHexString(SHA1.HashData(archive)).ToLowerInvariant());
        version.GetProperty("dist").GetProperty("integrity").GetString().ShouldBe(
            $"sha512-{Convert.ToBase64String(SHA512.HashData(archive))}");
        JsonElement attachment = document.RootElement.GetProperty("_attachments")
            .GetProperty("example.package-1.0.0.tgz");
        attachment.GetProperty("length").GetInt64().ShouldBe(archive.LongLength);
        Convert.FromBase64String(attachment.GetProperty("data").GetString()!).ShouldBe(archive);
        AssertNoPublishWorkspaces();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PublishAsync_ManifestIoExhaustionStopsBeforeRequest(bool failLength)
    {
        byte[] archive = CreateArchive("example.package", "1.0.0");
        ScriptedFileSystemPrimitives primitives = PublicationTestSupport.SystemFiles();
        IOException failure = new("manifest filesystem failure");
        if (failLength)
        {
            primitives.Set<long>("GetLength", call =>
                ScriptedFileSystemPrimitives.Fail<long>(failure, "manifest length", call.Path));
        }
        else
        {
            primitives.Set<int>("ReadAtAsync", call =>
                Path.GetFileName(call.Path) == "package.json"
                    ? ScriptedFileSystemPrimitives.Fail<int>(failure, "manifest read", call.Path)
                    : SystemFileSystemPrimitives.Instance.ReadAt(
                        call.Handle!, call.Buffer, call.Offset!.Value, call.Path!));
        }
        CaptureHandler handler = new();
        using HttpClient http = CreateHttpClient(handler);
        NpmRegistryClient client = CreateClient(http, context: primitives.CreateContext(retries: 1));
        using MemoryStream source = new(archive);

        Exception exception;
        if (failLength)
        {
            exception = await Should.ThrowAsync<IOException>(() => client.PublishAsync(
                new PackageReference("example.package", "1.0.0"), source, TestContext.Current.CancellationToken));
            exception.ShouldBeSameAs(failure);
        }
        else
        {
            PackageInstallException installationException = await Should.ThrowAsync<PackageInstallException>(
                () => client.PublishAsync(
                    new PackageReference("example.package", "1.0.0"), source, TestContext.Current.CancellationToken));
            installationException.ErrorCode.ShouldBe(PackageInstallErrorCode.InvalidArchive);
            installationException.InnerException.ShouldBeSameAs(failure);
            exception = installationException;
        }

        handler.RequestCount.ShouldBe(0);
        FileSystemFailureContext.TryGetTerminal(exception, out FileSystemFailureContext? context).ShouldBeTrue();
        context!.RetriesUsed.ShouldBe(1);
        primitives.Calls.Count(call => call.Operation == (failLength ? "GetLength" : "ReadAtAsync")
            && Path.GetFileName(call.Path) == "package.json").ShouldBe(2);
        primitives.AsyncWaits.ShouldBe([10]);
        primitives.Waits.ShouldBeEmpty();
        AssertNoPublishWorkspaces();
    }

    [Fact]
    public async Task PublishAsync_TempDiscoveryExhaustionDoesNotRestart()
    {
        ScriptedFileSystemPrimitives primitives = PublicationTestSupport.SystemFiles();
        IOException failure = new("system temp unavailable");
        primitives.Set<string>("GetTempPath", _ =>
            ScriptedFileSystemPrimitives.Fail<string>(failure, "system temp"));
        CaptureHandler handler = new();
        using HttpClient http = CreateHttpClient(handler);
        NpmRegistryClient client = new(
            PublicationTestSupport.Transport(http),
            PublicationTestSupport.Endpoint("npm", RegistryType.Npm),
            NullLogger<NpmRegistryClient>.Instance,
            new PackageInstallLimits(),
            primitives.CreateContext(retries: 2));
        using MemoryStream source = new(CreateArchive("example.package", "1.0.0"));

        IOException exception = await Should.ThrowAsync<IOException>(() => client.PublishAsync(
            new PackageReference("example.package", "1.0.0"), source, TestContext.Current.CancellationToken));

        exception.ShouldBeSameAs(failure);
        primitives.Count("GetTempPath").ShouldBe(3);
        primitives.Count("CreateDirectory").ShouldBe(0);
        primitives.Count("OpenFileHandle").ShouldBe(0);
        primitives.Count("OpenOwnedDescriptor").ShouldBe(0);
        primitives.AsyncWaits.ShouldBe([10, 10]);
        primitives.Waits.ShouldBeEmpty();
        handler.RequestCount.ShouldBe(0);
        source.Position.ShouldBe(0);
        source.CanRead.ShouldBeTrue();
    }

    [Fact]
    public async Task PublishAsync_DeferredAttachmentRetainsClientContextAfterEnvironmentChanges()
    {
        string[] variables =
        [
            FileSystemRetryOptions.RetryCountEnvironmentVariable,
            FileSystemRetryOptions.MinimumDelayMillisecondsEnvironmentVariable,
            FileSystemRetryOptions.MaximumJitterMillisecondsEnvironmentVariable,
        ];
        string?[] originals = variables.Select(Environment.GetEnvironmentVariable).ToArray();
        try
        {
            byte[] archive = CreateArchive("example.package", "1.0.0");
            ScriptedFileSystemPrimitives primitives = PublicationTestSupport.SystemFiles();
            int callsBeforeSerialization = 0;
            CaptureHandler handler = new()
            {
                BeforeSerialization = () =>
                {
                    callsBeforeSerialization = primitives.Calls.Count;
                    primitives.Enqueue("ReadAtAsync",
                        ScriptedFileSystemPrimitives.NativeFailure<int>(1117, "deferred attachment"));
                },
            };
            using HttpClient http = CreateHttpClient(handler);
            NpmRegistryClient client = CreateClient(http, context: primitives.CreateContext(retries: 1));
            foreach (string variable in variables)
                Environment.SetEnvironmentVariable(variable, "invalid-after-client-construction");
            using MemoryStream source = new(archive);

            PublishResult result = await client.PublishAsync(
                new PackageReference("example.package", "1.0.0"), source, TestContext.Current.CancellationToken);

            result.Success.ShouldBeTrue();
            handler.RequestCount.ShouldBe(1);
            primitives.AsyncWaits.ShouldBe([10]);
            primitives.Waits.ShouldBeEmpty();
            FileSystemPrimitiveCall[] deferredReads = primitives.Calls.Skip(callsBeforeSerialization)
                .Where(call => call.Operation == "ReadAtAsync")
                .ToArray();
            deferredReads.Length.ShouldBe(3);
            deferredReads[0].Offset.ShouldBe(0);
            deferredReads[1].Offset.ShouldBe(0);
            deferredReads[0].Handle.ShouldBeSameAs(deferredReads[1].Handle);
            using JsonDocument document = JsonDocument.Parse(handler.Body);
            Convert.FromBase64String(document.RootElement.GetProperty("_attachments")
                .GetProperty("example.package-1.0.0.tgz").GetProperty("data").GetString()!).ShouldBe(archive);
            source.CanRead.ShouldBeTrue();
            AssertNoPublishWorkspaces();
        }
        finally
        {
            for (int index = 0; index < variables.Length; index++)
                Environment.SetEnvironmentVariable(variables[index], originals[index]);
        }
    }

    [Fact]
    public async Task PublishAsync_TerminalCleanupFailureStopsFallbackAfterFailedHttpResult()
    {
        byte[] archive = CreateArchive("example.package", "1.0.0");
        ScriptedFileSystemPrimitives primitives = PublicationTestSupport.SystemFiles();
        primitives.Set<bool>("DeleteEntry", call =>
            ScriptedFileSystemPrimitives.NativeFailure<bool>(1117, "workspace cleanup", call.Path));
        FileSystemContext context = primitives.CreateContext(retries: 1);
        CaptureHandler handler = new(HttpStatusCode.InternalServerError);
        using HttpClient http = CreateHttpClient(handler);
        NpmRegistryClient first = CreateClient(http, context: context);
        FhirNpmRegistryClient second = new(
            PublicationTestSupport.Transport(http),
            PublicationTestSupport.Endpoint("second"),
            NullLogger<FhirNpmRegistryClient>.Instance,
            context);
        RedundantRegistryClient composite = new([first, second], 3, null, context);
        using MemoryStream source = new(archive);

        PublishResult result = await composite.PublishAsync(
            new PackageReference("example.package", "1.0.0"), source, TestContext.Current.CancellationToken);

        result.Success.ShouldBeFalse();
        result.StatusCode.ShouldBe(HttpStatusCode.InternalServerError);
        handler.RequestCount.ShouldBe(1);
        primitives.Count("DeleteEntry").ShouldBe(2);
        primitives.AsyncWaits.ShouldBe([10]);
        primitives.Waits.ShouldBeEmpty();
        FileSystemFailureContext.TryGetResult(result, out FileSystemFailureContext? failure).ShouldBeTrue();
        failure!.Operation.ShouldBe("workspace cleanup");
        failure.RetriesUsed.ShouldBe(1);
        result.Message!.ShouldContain(failure.Format());
        Directory.GetDirectories(_tempRoot).Length.ShouldBe(1);
    }

    [Fact]
    public async Task PublishAsync_ExpectedMissingWorkspaceCleanupDoesNotWait()
    {
        byte[] archive = CreateArchive("example.package", "1.0.0");
        ScriptedFileSystemPrimitives primitives = PublicationTestSupport.SystemFiles();
        string? removedWorkspace = null;
        CaptureHandler handler = new(HttpStatusCode.InternalServerError)
        {
            AfterSerialization = () =>
            {
                removedWorkspace = Directory.GetDirectories(_tempRoot).Single();
                Directory.Delete(removedWorkspace, recursive: true);
            },
        };
        using HttpClient http = CreateHttpClient(handler);
        NpmRegistryClient client = CreateClient(http, context: primitives.CreateContext(retries: 2));
        using MemoryStream source = new(archive);

        PublishResult result = await client.PublishAsync(
            new PackageReference("example.package", "1.0.0"), source, TestContext.Current.CancellationToken);

        result.Success.ShouldBeFalse();
        FileSystemFailureContext.TryGetResult(result, out _).ShouldBeFalse();
        handler.RequestCount.ShouldBe(1);
        primitives.Calls.Count(call => call.Operation == "Probe"
            && call.Path == removedWorkspace && call.Flags == 0).ShouldBe(1);
        primitives.Count("DeleteEntry").ShouldBe(0);
        primitives.AsyncWaits.ShouldBeEmpty();
        primitives.Waits.ShouldBeEmpty();
        AssertNoPublishWorkspaces();
    }

    [Fact]
    public async Task PublishAsync_SendsStandardPackumentWithExactAttachment()
    {
        byte[] resource = new byte[100_000];
        new Random(42).NextBytes(resource);
        byte[] archive = CreateArchive(
            "example.package",
            "1.2.3",
            """
            {
              "description": "Example",
              "customField": { "value": 42 },
              "patchedDependencies": { "a@1.0.0": "patches/a.patch" }
            }
            """,
            resource);
        CaptureHandler handler = new();
        using HttpClient httpClient = CreateHttpClient(handler);
        NpmRegistryClient client = CreateClient(httpClient);

        using MemoryStream stream = new(archive);
        PublishResult result = await client.PublishAsync(
            new PackageReference("example.package", "1.2.3"),
            stream,
            TestContext.Current.CancellationToken);

        result.Success.ShouldBeTrue();
        handler.RequestCount.ShouldBe(1);
        handler.RequestUri.ShouldBe(
            new Uri("https://registry.example/npm/example.package"));
        handler.ContentType.ShouldBe("application/json");
        handler.ContentLength.ShouldBe(handler.Body.LongLength);
        handler.TransferEncodingChunked.ShouldNotBe(true);
        handler.Authorization.ShouldBe("Bearer publish-token");

        using JsonDocument document = JsonDocument.Parse(handler.Body);
        JsonElement root = document.RootElement;
        root.GetProperty("_id").GetString().ShouldBe("example.package");
        root.GetProperty("name").GetString().ShouldBe("example.package");
        root.GetProperty("description").GetString().ShouldBe("Example");
        root.GetProperty("access").ValueKind.ShouldBe(
            JsonValueKind.Null);
        root.GetProperty("dist-tags")
            .GetProperty("latest")
            .GetString()
            .ShouldBe("1.2.3");
        JsonElement version = root.GetProperty("versions")
            .GetProperty("1.2.3");
        version.GetProperty("name").GetString().ShouldBe("example.package");
        version.GetProperty("version").GetString().ShouldBe("1.2.3");
        version.GetProperty("_id").GetString()
            .ShouldBe("example.package@1.2.3");
        version.GetProperty("description").GetString().ShouldBe("Example");
        version.GetProperty("customField")
            .GetProperty("value")
            .GetInt32()
            .ShouldBe(42);
        version.TryGetProperty(
            "patchedDependencies",
            out _)
            .ShouldBeFalse();
        JsonElement distribution = version.GetProperty("dist");
        distribution.GetProperty("tarball").GetString().ShouldBe(
            "https://registry.example/npm/example.package/-/example.package-1.2.3.tgz");
        distribution.GetProperty("shasum").GetString().ShouldBe(
            Convert.ToHexString(SHA1.HashData(archive))
                .ToLowerInvariant());
        distribution.GetProperty("integrity").GetString().ShouldBe(
            $"sha512-{Convert.ToBase64String(SHA512.HashData(archive))}");
        JsonElement attachment = root.GetProperty("_attachments")
            .GetProperty("example.package-1.2.3.tgz");
        attachment.GetProperty("content_type").GetString()
            .ShouldBe("application/octet-stream");
        attachment.GetProperty("length").GetInt64()
            .ShouldBe(archive.LongLength);
        Convert.FromBase64String(
                attachment.GetProperty("data").GetString()!)
            .ShouldBe(archive);
        AssertNoPublishWorkspaces();
    }

    [Fact]
    public async Task PublishAsync_ScopedPackageUsesNpmEncoding()
    {
        byte[] archive = CreateArchive(
            "@scope/example",
            "1.0.0");
        CaptureHandler handler = new();
        using HttpClient httpClient = CreateHttpClient(handler);
        NpmRegistryClient client = CreateClient(httpClient);

        using MemoryStream stream = new(archive);
        PublishResult result = await client.PublishAsync(
            new PackageReference(
                "@scope/example",
                "1.0.0",
                "@scope"),
            stream,
            TestContext.Current.CancellationToken);

        result.Success.ShouldBeTrue();
        handler.RequestUri!.OriginalString.ShouldBe(
            "https://registry.example/npm/@scope%2Fexample");
        using JsonDocument document = JsonDocument.Parse(handler.Body);
        JsonElement root = document.RootElement;
        JsonElement attachment = root.GetProperty("_attachments")
            .GetProperty("@scope/example-1.0.0.tgz");
        Convert.FromBase64String(
                attachment.GetProperty("data").GetString()!)
            .ShouldBe(archive);
        root.GetProperty("versions")
            .GetProperty("1.0.0")
            .GetProperty("dist")
            .GetProperty("tarball")
            .GetString()
            .ShouldBe(
                "https://registry.example/npm/@scope/example/-/@scope/example-1.0.0.tgz");
        AssertNoPublishWorkspaces();
    }

    [Fact]
    public async Task PublishAsync_PrivateManifestSendsNoRequest()
    {
        byte[] archive = CreateArchive(
            "example.package",
            "1.0.0",
            """{ "private": true }""");
        CaptureHandler handler = new();
        using HttpClient httpClient = CreateHttpClient(handler);
        NpmRegistryClient client = CreateClient(httpClient);

        using MemoryStream stream = new(archive);
        PackageInstallException exception =
            await Should.ThrowAsync<PackageInstallException>(
                () => client.PublishAsync(
                    new PackageReference(
                        "example.package",
                        "1.0.0"),
                    stream,
                    TestContext.Current.CancellationToken));

        exception.ErrorCode.ShouldBe(
            PackageInstallErrorCode.InvalidPackageIdentity);
        handler.RequestCount.ShouldBe(0);
        AssertNoPublishWorkspaces();
    }

    [Fact]
    public async Task PublishAsync_TruthyNonBooleanPrivateSendsNoRequest()
    {
        byte[] archive = CreateArchive(
            "example.package",
            "1.0.0",
            """{ "private": "true" }""");
        CaptureHandler handler = new();
        using HttpClient httpClient = CreateHttpClient(handler);
        NpmRegistryClient client = CreateClient(httpClient);

        using MemoryStream stream = new(archive);
        await Should.ThrowAsync<PackageInstallException>(
            () => client.PublishAsync(
                new PackageReference(
                    "example.package",
                    "1.0.0"),
                stream,
                TestContext.Current.CancellationToken));

        handler.RequestCount.ShouldBe(0);
        AssertNoPublishWorkspaces();
    }

    [Fact]
    public async Task PublishAsync_PackageExtensionsSendsNoRequest()
    {
        byte[] archive = CreateArchive(
            "example.package",
            "1.0.0",
            """
            {
              "packageExtensions": {
                "a@1.0.0": { "dependencies": { "b": "1.0.0" } }
              }
            }
            """);
        CaptureHandler handler = new();
        using HttpClient httpClient = CreateHttpClient(handler);
        NpmRegistryClient client = CreateClient(httpClient);

        using MemoryStream stream = new(archive);
        await Should.ThrowAsync<PackageInstallException>(
            () => client.PublishAsync(
                new PackageReference(
                    "example.package",
                    "1.0.0"),
                stream,
                TestContext.Current.CancellationToken));

        handler.RequestCount.ShouldBe(0);
        AssertNoPublishWorkspaces();
    }

    [Theory]
    [InlineData("notsemver")]
    [InlineData("1.0.0-01")]
    [InlineData("1.0.0-alpha..1")]
    [InlineData("1.0.0-alph\u00E1")]
    public async Task PublishAsync_InvalidSemVerSendsNoRequest(
        string invalidVersion)
    {
        byte[] archive = CreateArchive(
            "example.package",
            invalidVersion);
        CaptureHandler handler = new();
        using HttpClient httpClient = CreateHttpClient(handler);
        NpmRegistryClient client = CreateClient(httpClient);

        using MemoryStream stream = new(archive);
        PackageInstallException exception =
            await Should.ThrowAsync<PackageInstallException>(
                () => client.PublishAsync(
                    new PackageReference(
                        "example.package",
                        invalidVersion),
                    stream,
                    TestContext.Current.CancellationToken));

        exception.ErrorCode.ShouldBe(
            PackageInstallErrorCode.InvalidPackageIdentity);
        handler.RequestCount.ShouldBe(0);
        Directory.EnumerateFileSystemEntries(_tempRoot)
            .ShouldBeEmpty();
    }

    [Theory]
    [InlineData("node_modules")]
    [InlineData("has space")]
    [InlineData("Uppercase")]
    public async Task PublishAsync_InvalidPackageNameSendsNoRequest(
        string invalidName)
    {
        byte[] archive = CreateArchive(
            invalidName,
            "1.0.0");
        CaptureHandler handler = new();
        using HttpClient httpClient = CreateHttpClient(handler);
        NpmRegistryClient client = CreateClient(httpClient);

        using MemoryStream stream = new(archive);
        PackageInstallException exception =
            await Should.ThrowAsync<PackageInstallException>(
                () => client.PublishAsync(
                    new PackageReference(
                        invalidName,
                        "1.0.0"),
                    stream,
                    TestContext.Current.CancellationToken));

        exception.ErrorCode.ShouldBe(
            PackageInstallErrorCode.InvalidPackageIdentity);
        handler.RequestCount.ShouldBe(0);
        AssertNoPublishWorkspaces();
    }

    [Fact]
    public async Task PublishAsync_NonSeekableInputReadsCurrentContentOnceAndLeavesOpen()
    {
        byte[] archive = CreateArchive(
            "example.package",
            "1.0.0");
        CaptureHandler handler = new();
        using HttpClient httpClient = CreateHttpClient(handler);
        NpmRegistryClient client = CreateClient(httpClient);
        TrackingReadStream stream = new(
            new MemoryStream(archive),
            canSeek: false);

        PublishResult result = await client.PublishAsync(
            new PackageReference("example.package", "1.0.0"),
            stream,
            TestContext.Current.CancellationToken);

        result.Success.ShouldBeTrue();
        stream.BytesRead.ShouldBe(archive.LongLength);
        stream.PositionWasRead.ShouldBeFalse();
        stream.WasDisposed.ShouldBeFalse();
        AssertNoPublishWorkspaces();
    }

    [Fact]
    public async Task PublishAsync_CompressedLimitExceededSendsNoRequest()
    {
        byte[] archive = CreateArchive(
            "example.package",
            "1.0.0");
        CaptureHandler handler = new();
        using HttpClient httpClient = CreateHttpClient(handler);
        PackageInstallLimits limits = new()
        {
            MaxCompressedBytes = archive.LongLength - 1,
        };
        NpmRegistryClient client = CreateClient(
            httpClient,
            limits);

        using MemoryStream stream = new(archive);
        PackageInstallException exception =
            await Should.ThrowAsync<PackageInstallException>(
                () => client.PublishAsync(
                    new PackageReference(
                        "example.package",
                        "1.0.0"),
                    stream,
                    TestContext.Current.CancellationToken));

        exception.ErrorCode.ShouldBe(
            PackageInstallErrorCode.CompressedSizeLimitExceeded);
        handler.RequestCount.ShouldBe(0);
        AssertNoPublishWorkspaces();
    }

    [Fact]
    public async Task PublishAsync_OversizedManifestSendsNoRequest()
    {
        string description = new('a', (1024 * 1024) + 1);
        string manifest = JsonSerializer.Serialize(new
        {
            name = "example.package",
            version = "1.0.0",
            description,
        });
        using MemoryStream archiveStream =
            ArbitraryTarBuilder.Create(
                ArbitraryTarBuilder.File(
                    "package/package.json",
                    manifest));
        byte[] archive = archiveStream.ToArray();
        CaptureHandler handler = new();
        using HttpClient httpClient = CreateHttpClient(handler);
        NpmRegistryClient client = CreateClient(httpClient);

        using MemoryStream stream = new(archive);
        PackageInstallException exception =
            await Should.ThrowAsync<PackageInstallException>(
                () => client.PublishAsync(
                    new PackageReference(
                        "example.package",
                        "1.0.0"),
                    stream,
                    TestContext.Current.CancellationToken));

        exception.ErrorCode.ShouldBe(
            PackageInstallErrorCode.InvalidArchive);
        handler.RequestCount.ShouldBe(0);
        AssertNoPublishWorkspaces();
    }

    [Theory]
    [InlineData("different.package", "1.0.0")]
    [InlineData("example.package", "2.0.0")]
    public async Task PublishAsync_ManifestMismatchSendsNoRequest(
        string manifestName,
        string manifestVersion)
    {
        byte[] archive = CreateArchive(
            manifestName,
            manifestVersion);
        CaptureHandler handler = new();
        using HttpClient httpClient = CreateHttpClient(handler);
        NpmRegistryClient client = CreateClient(httpClient);

        using MemoryStream stream = new(archive);
        PackageInstallException exception =
            await Should.ThrowAsync<PackageInstallException>(
                () => client.PublishAsync(
                    new PackageReference(
                        "example.package",
                        "1.0.0"),
                    stream,
                    TestContext.Current.CancellationToken));

        exception.ErrorCode.ShouldBe(
            PackageInstallErrorCode.InvalidPackageIdentity);
        handler.RequestCount.ShouldBe(0);
        AssertNoPublishWorkspaces();
    }

    [Fact]
    public async Task PublishAsync_LegacyLayoutSendsNoRequest()
    {
        using MemoryStream archiveStream = ArbitraryTarBuilder.Create(
            ArbitraryTarBuilder.File(
                "package.json",
                """
                {
                  "name": "example.package",
                  "version": "1.0.0"
                }
                """));
        byte[] archive = archiveStream.ToArray();
        CaptureHandler handler = new();
        using HttpClient httpClient = CreateHttpClient(handler);
        NpmRegistryClient client = CreateClient(httpClient);

        using MemoryStream stream = new(archive);
        PackageInstallException exception =
            await Should.ThrowAsync<PackageInstallException>(
                () => client.PublishAsync(
                    new PackageReference(
                        "example.package",
                        "1.0.0"),
                    stream,
                    TestContext.Current.CancellationToken));

        exception.ErrorCode.ShouldBe(
            PackageInstallErrorCode.InvalidArchive);
        handler.RequestCount.ShouldBe(0);
        AssertNoPublishWorkspaces();
    }

    [Fact]
    public async Task PublishAsync_HttpFailureCleansWorkspace()
    {
        byte[] archive = CreateArchive(
            "example.package",
            "1.0.0");
        CaptureHandler handler = new(HttpStatusCode.InternalServerError);
        using HttpClient httpClient = CreateHttpClient(handler);
        NpmRegistryClient client = CreateClient(httpClient);

        using MemoryStream stream = new(archive);
        PublishResult result = await client.PublishAsync(
            new PackageReference("example.package", "1.0.0"),
            stream,
            TestContext.Current.CancellationToken);

        result.Success.ShouldBeFalse();
        handler.RequestCount.ShouldBe(1);
        AssertNoPublishWorkspaces();
    }

    [Fact]
    public async Task PublishAsync_CancellationCleansWorkspace()
    {
        byte[] archive = CreateArchive(
            "example.package",
            "1.0.0");
        CaptureHandler handler = new();
        using HttpClient httpClient = CreateHttpClient(handler);
        NpmRegistryClient client = CreateClient(httpClient);
        using CancellationTokenSource cancellationSource = new();
        CancelOnReadStream stream = new(
            new MemoryStream(archive),
            cancellationSource);

        await Should.ThrowAsync<OperationCanceledException>(
            () => client.PublishAsync(
                new PackageReference(
                    "example.package",
                    "1.0.0"),
                stream,
                cancellationSource.Token));

        stream.WasDisposed.ShouldBeFalse();
        handler.RequestCount.ShouldBe(0);
        AssertNoPublishWorkspaces();
    }

    [Fact]
    public async Task FhirNpmPublishAsync_RemainsRawGzip()
    {
        byte[] archive = CreateArchive(
            "example.package",
            "1.0.0");
        CaptureHandler handler = new();
        using HttpClient httpClient = CreateHttpClient(handler);
        RegistryHttpTransport transport =
            RegistryHttpTransport.CreateRedirectControlled(
                httpClient,
                TimeSpan.FromSeconds(5),
                maxRedirects: 2);
        FhirNpmRegistryClient client = new(
            transport,
            new RegistryEndpoint
            {
                Url = "https://registry.example/fhir/",
                Type = RegistryType.FhirNpm,
            },
            NullLogger<FhirNpmRegistryClient>.Instance);

        using MemoryStream stream = new(archive);
        PublishResult result = await client.PublishAsync(
            new PackageReference("example.package", "1.0.0"),
            stream,
            TestContext.Current.CancellationToken);

        result.Success.ShouldBeTrue();
        handler.ContentType.ShouldBe("application/gzip");
        handler.Body.ShouldBe(archive);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempRoot))
        {
            Directory.Delete(_tempRoot, recursive: true);
        }
    }

    private NpmRegistryClient CreateClient(
        HttpClient httpClient,
        PackageInstallLimits? limits = null,
        FileSystemContext? context = null)
    {
        RegistryHttpTransport transport =
            RegistryHttpTransport.CreateRedirectControlled(
                httpClient,
                TimeSpan.FromSeconds(5),
                maxRedirects: 2);
        return new NpmRegistryClient(
            transport,
            new RegistryEndpoint
            {
                Url = "https://registry.example/npm/",
                Type = RegistryType.Npm,
                AuthHeaderValue = "Bearer publish-token",
            },
            NullLogger<NpmRegistryClient>.Instance,
            limits ?? new PackageInstallLimits(),
            context ?? FileSystemContext.CreateDefault(),
            CreatePublishWorkspace);
    }

    private string CreatePublishWorkspace()
    {
        string workspace = Path.Combine(
            _tempRoot,
            $"workspace-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workspace);
        return workspace;
    }

    private void AssertNoPublishWorkspaces() =>
        Directory.EnumerateFileSystemEntries(_tempRoot)
            .ShouldBeEmpty();

    private static HttpClient CreateHttpClient(
        CaptureHandler handler) =>
        new(handler)
        {
            Timeout = System.Threading.Timeout.InfiniteTimeSpan,
        };

    private static byte[] CreateArchive(
        string name,
        string version,
        string additionalManifestProperties = "",
        byte[]? resource = null)
    {
        string additional = string.IsNullOrWhiteSpace(
            additionalManifestProperties)
            ? string.Empty
            : $",{additionalManifestProperties.Trim()[1..^1]}";
        string manifest =
            $$"""
              {
                "name": "{{name}}",
                "version": "{{version}}"
                {{additional}}
              }
              """;
        using MemoryStream archive = resource is null
            ? ArbitraryTarBuilder.Create(
                ArbitraryTarBuilder.File(
                    "package/package.json",
                    manifest))
            : ArbitraryTarBuilder.Create(
                ArbitraryTarBuilder.File(
                    "package/package.json",
                    manifest),
                ArbitraryTarBuilder.File(
                    "package/resource.bin",
                    resource));
        return archive.ToArray();
    }

    private sealed class CaptureHandler(
        HttpStatusCode statusCode = HttpStatusCode.OK)
        : HttpMessageHandler
    {
        internal int RequestCount { get; private set; }

        internal Uri? RequestUri { get; private set; }

        internal string? Authorization { get; private set; }

        internal string? ContentType { get; private set; }

        internal long? ContentLength { get; private set; }

        internal bool? TransferEncodingChunked { get; private set; }

        internal byte[] Body { get; private set; } = [];

        internal Action? BeforeSerialization { get; init; }

        internal Action? AfterSerialization { get; init; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestCount++;
            RequestUri = request.RequestUri;
            Authorization = request.Headers.TryGetValues(
                "Authorization",
                out IEnumerable<string>? values)
                ? values.Single()
                : null;
            ContentType =
                request.Content?.Headers.ContentType?.MediaType;
            ContentLength =
                request.Content?.Headers.ContentLength;
            TransferEncodingChunked =
                request.Headers.TransferEncodingChunked;
            BeforeSerialization?.Invoke();
            Body = request.Content is null
                ? []
                : await request.Content
                    .ReadAsByteArrayAsync(cancellationToken)
                    .ConfigureAwait(false);
            AfterSerialization?.Invoke();
            return new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(
                    statusCode == HttpStatusCode.OK
                        ? "{}"
                        : "publish failed"),
            };
        }
    }

    private class TrackingReadStream : Stream
    {
        private readonly Stream _inner;
        private readonly bool _canSeek;

        internal TrackingReadStream(
            Stream inner,
            bool canSeek)
        {
            _inner = inner;
            _canSeek = canSeek;
        }

        internal long BytesRead { get; private set; }

        internal int ReadCalls { get; private set; }

        internal bool WasDisposed { get; private set; }

        internal bool PositionWasRead { get; private set; }

        public override bool CanRead => true;

        public override bool CanSeek => _canSeek;

        public override bool CanWrite => false;

        public override long Length => _inner.Length;

        public override long Position
        {
            get
            {
                PositionWasRead = true;
                if (!_canSeek)
                    throw new NotSupportedException();

                return _inner.Position;
            }
            set
            {
                if (!_canSeek)
                    throw new NotSupportedException();

                _inner.Position = value;
            }
        }

        public override void Flush() =>
            throw new NotSupportedException();

        public override int Read(
            byte[] buffer,
            int offset,
            int count)
        {
            ReadCalls++;
            int read = _inner.Read(buffer, offset, count);
            BytesRead += read;
            return read;
        }

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            ReadCalls++;
            int read = await _inner.ReadAsync(
                    buffer,
                    cancellationToken)
                .ConfigureAwait(false);
            BytesRead += read;
            return read;
        }

        public override long Seek(
            long offset,
            SeekOrigin origin) =>
            throw new NotSupportedException();

        public override void SetLength(long value) =>
            throw new NotSupportedException();

        public override void Write(
            byte[] buffer,
            int offset,
            int count) =>
            throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            WasDisposed = true;
            base.Dispose(disposing);
        }
    }

    private sealed class CancelOnReadStream : TrackingReadStream
    {
        private readonly CancellationTokenSource _cancellationSource;

        internal CancelOnReadStream(
            Stream inner,
            CancellationTokenSource cancellationSource)
            : base(inner, canSeek: false)
        {
            _cancellationSource = cancellationSource;
        }

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            _cancellationSource.Cancel();
            return ValueTask.FromCanceled<int>(
                cancellationToken);
        }
    }
}
