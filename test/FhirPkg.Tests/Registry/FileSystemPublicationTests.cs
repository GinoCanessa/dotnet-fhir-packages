// Copyright (c) Gino Canessa. Licensed under the MIT License.

using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using FhirPkg.IO;
using FhirPkg.Models;
using FhirPkg.Registry;
using FhirPkg.Tests.Support;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Win32.SafeHandles;
using Shouldly;
using Xunit;

namespace FhirPkg.Tests.Registry;

[Collection("EnvironmentVariable")]
public sealed class FileSystemPublicationTests
{
    [Fact]
    public async Task DeferredAttachment_RetainsConstructionPolicyAfterEnvironmentChanges()
    {
        using PublicationWorkspace workspace = new();
        byte[] bytes = new byte[100_003];
        new Random(23).NextBytes(bytes);
        string path = workspace.CreateFile(bytes);
        string[] variables =
        [
            FileSystemRetryOptions.RetryCountEnvironmentVariable,
            FileSystemRetryOptions.MinimumDelayMillisecondsEnvironmentVariable,
            FileSystemRetryOptions.MaximumJitterMillisecondsEnvironmentVariable,
        ];
        string?[] originals = variables.Select(Environment.GetEnvironmentVariable).ToArray();
        try
        {
            Environment.SetEnvironmentVariable(variables[0], "2");
            Environment.SetEnvironmentVariable(variables[1], "7");
            Environment.SetEnvironmentVariable(variables[2], "0");
            ScriptedFileSystemPrimitives primitives = PublicationTestSupport.SystemFiles();
            FileSystemContext context = new(
                new FileSystemRetryOptions(),
                primitives,
                primitives.CreateExecutor());
            using NpmPackumentContent content = CreateContent(path, bytes.LongLength, context);

            foreach (string variable in variables)
                Environment.SetEnvironmentVariable(variable, "invalid-after-construction");
            primitives.Enqueue("ReadAtAsync", ScriptedFileSystemPrimitives.NativeFailure<int>(1117, "attachment read", path));
            primitives.Enqueue("ReadAtAsync", ScriptedFileSystemPrimitives.NativeFailure<int>(5, "attachment read", path));
            using RecordingDestination destination = new();

            await content.CopyToAsync(destination, TestContext.Current.CancellationToken);

            destination.ToArray().ShouldBe(Encoding.UTF8.GetBytes(
                $"{{\"data\":\"{Convert.ToBase64String(bytes)}\"}}"));
            content.Headers.ContentLength.ShouldBe(destination.Length);
            primitives.AsyncWaits.ShouldBe([7, 7]);
            primitives.Waits.ShouldBeEmpty();
            context.HasExplicitValues.ShouldBeFalse();
            FileSystemPrimitiveCall[] firstReads = primitives.Calls
                .Where(call => call.Operation == "ReadAtAsync" && call.Offset == 0)
                .ToArray();
            firstReads.Length.ShouldBe(3);
            firstReads.Select(call => call.Handle).Distinct().Count().ShouldBe(1);
            primitives.Count("CloseFile").ShouldBe(1);
            destination.Writes[0].ShouldBe("{\"data\":\""u8.ToArray());
            destination.Writes[^1].ShouldBe("\"}"u8.ToArray());
        }
        finally
        {
            for (int index = 0; index < variables.Length; index++)
                Environment.SetEnvironmentVariable(variables[index], originals[index]);
        }
    }

    [Fact]
    public async Task AttachmentReadRetry_EmitsEachBase64BlockOnce()
    {
        using PublicationWorkspace workspace = new();
        byte[] bytes = new byte[(49_152 * 2) + 17];
        new Random(71).NextBytes(bytes);
        string path = workspace.CreateFile(bytes);
        ScriptedFileSystemPrimitives primitives = PublicationTestSupport.SystemFiles();
        bool failed = false;
        primitives.Set<int>("ReadAtAsync", call =>
        {
            if (call.Offset == 49_152 && !failed)
            {
                failed = true;
                RandomAccess.Read(call.Handle!, call.Buffer.Span[..7], call.Offset.Value).ShouldBe(7);
                return ScriptedFileSystemPrimitives.NativeFailure<int>(1117, "attachment read", path);
            }

            return SystemFileSystemPrimitives.Instance.ReadAt(
                call.Handle!, call.Buffer, call.Offset!.Value, call.Path!);
        });
        using NpmPackumentContent content = CreateContent(path, bytes.LongLength, primitives.CreateContext(retries: 1));
        using RecordingDestination destination = new();

        await content.CopyToAsync(destination, TestContext.Current.CancellationToken);

        string encoded = Convert.ToBase64String(bytes);
        destination.ToArray().ShouldBe(Encoding.UTF8.GetBytes($"{{\"data\":\"{encoded}\"}}"));
        destination.Writes.Count.ShouldBe(6);
        destination.Writes[1].ShouldBe(Encoding.ASCII.GetBytes(encoded[..65_536]));
        destination.Writes[2].ShouldBe(Encoding.ASCII.GetBytes(encoded[65_536..131_072]));
        destination.Writes[3].Length.ShouldBe(20);
        destination.Writes[4].Length.ShouldBe(4);
        primitives.AsyncWaits.ShouldBe([10]);
        primitives.Waits.ShouldBeEmpty();
        FileSystemPrimitiveCall[] replayed = primitives.Calls
            .Where(call => call.Operation == "ReadAtAsync" && call.Offset == 49_152)
            .ToArray();
        replayed.Length.ShouldBe(2);
        replayed[0].Handle.ShouldBeSameAs(replayed[1].Handle);
        primitives.Count("PrepareBorrowedRead").ShouldBe(0);
        primitives.Count("CloseFile").ShouldBe(1);
    }

    [Fact]
    public async Task DeferredAttachment_OpenRetryDoesNotReemitPrefix()
    {
        using PublicationWorkspace workspace = new();
        byte[] bytes = "deferred archive bytes"u8.ToArray();
        string path = workspace.CreateFile(bytes);
        ScriptedFileSystemPrimitives primitives = PublicationTestSupport.SystemFiles();
        // Owned FileMode.Open uses the managed handle open on every host.
        const string openOperation = "OpenFileHandle";
        primitives.Enqueue(openOperation,
            ScriptedFileSystemPrimitives.NativeFailure<SafeFileHandle>(1117, "attachment open", path));
        using NpmPackumentContent content = CreateContent(path, bytes.LongLength, primitives.CreateContext(retries: 1));
        using RecordingDestination destination = new();

        await content.CopyToAsync(destination, TestContext.Current.CancellationToken);

        primitives.Count(openOperation).ShouldBe(2);
        primitives.Count("CloseFile").ShouldBe(1);
        primitives.AsyncWaits.ShouldBe([10]);
        primitives.Waits.ShouldBeEmpty();
        destination.Writes.Count.ShouldBe(4);
        destination.Writes[0].ShouldBe("{\"data\":\""u8.ToArray());
        destination.Writes[^1].ShouldBe("\"}"u8.ToArray());
        destination.ToArray().ShouldBe(Encoding.UTF8.GetBytes(
            $"{{\"data\":\"{Convert.ToBase64String(bytes)}\"}}"));
        FileSystemPrimitiveCall[] opens = primitives.Calls.Where(call => call.Operation == openOperation).ToArray();
        opens.Select(call => call.Mode).ShouldBe([FileMode.Open, FileMode.Open]);
        opens.Select(call => call.Access).ShouldBe([FileAccess.Read, FileAccess.Read]);
        opens.Select(call => call.Share).ShouldBe([FileShare.Read, FileShare.Read]);
        opens.Select(call => call.Options).ShouldBe(
            [FileOptions.Asynchronous | FileOptions.SequentialScan, FileOptions.Asynchronous | FileOptions.SequentialScan]);
    }

    [Fact]
    public async Task AttachmentDestinationFailure_DoesNotRetryEmittedBytes()
    {
        using PublicationWorkspace workspace = new();
        byte[] bytes = new byte[100_000];
        new Random(17).NextBytes(bytes);
        string path = workspace.CreateFile(bytes);
        ScriptedFileSystemPrimitives primitives = PublicationTestSupport.SystemFiles();
        using NpmPackumentContent content = CreateContent(path, bytes.LongLength, primitives.CreateContext());
        using RecordingDestination destination = new(failOnWrite: 2);

        HttpRequestException exception = await Should.ThrowAsync<HttpRequestException>(
            () => content.CopyToAsync(destination, TestContext.Current.CancellationToken));

        exception.InnerException.ShouldBeSameAs(destination.Failure);
        FileSystemFailureContext.TryGetOrigin(exception, out _).ShouldBeFalse();
        destination.Writes.Count.ShouldBe(2);
        destination.Length.ShouldBe("{\"data\":\""u8.Length + 3);
        primitives.Count("ReadAtAsync").ShouldBe(1);
        primitives.Count("CloseFile").ShouldBe(1);
        primitives.AsyncWaits.ShouldBeEmpty();
        primitives.Waits.ShouldBeEmpty();
    }

    [Fact]
    public async Task CompositeRewind_ReusesProtectedInputAndItsOriginalPolicy()
    {
        using PublicationWorkspace workspace = new();
        byte[] bytes = "complete raw gzip body"u8.ToArray();
        string path = workspace.CreateFile(bytes);
        using FileStream caller = File.OpenRead(path);
        caller.Position = 4;
        ScriptedFileSystemPrimitives original = PublicationTestSupport.SystemFiles();
        using Stream protectedInput = original.CreateContext(retries: 1).ProtectRead(caller);
        original.Enqueue("SetPosition", ScriptedFileSystemPrimitives.NativeFailure<long>(1117, "rewind", path));
        ScriptedFileSystemPrimitives compositeFiles = PublicationTestSupport.SystemFiles();
        ScriptedFileSystemPrimitives childFiles = PublicationTestSupport.SystemFiles();
        BodySequenceHandler handler = new();
        using HttpClient http = new(handler);
        RegistryHttpTransport transport = PublicationTestSupport.Transport(http);
        FileSystemContext childContext = childFiles.CreateContext(retries: 0);
        FhirNpmRegistryClient first = new(
            transport,
            PublicationTestSupport.Endpoint("first"),
            NullLogger<FhirNpmRegistryClient>.Instance,
            childContext);
        FhirNpmRegistryClient second = new(
            transport,
            PublicationTestSupport.Endpoint("second"),
            NullLogger<FhirNpmRegistryClient>.Instance,
            childContext);
        RedundantRegistryClient composite = new(
            [first, second],
            3,
            null,
            compositeFiles.CreateContext(retries: 0));

        PublishResult result = await composite.PublishAsync(
            new PackageReference("example.package", "1.0.0"),
            protectedInput,
            TestContext.Current.CancellationToken);

        result.Success.ShouldBeTrue();
        handler.Bodies.Count.ShouldBe(2);
        handler.Bodies[0].ShouldBe(bytes);
        handler.Bodies[1].ShouldBe(bytes);
        original.AsyncWaits.ShouldBe([10]);
        original.Waits.ShouldBeEmpty();
        original.Count("PrepareBorrowedRead").ShouldBe(1);
        original.Calls.Where(call => call.Operation == "SetPosition")
            .Select(call => call.Offset).ShouldBe([0L, 0L, 0L]);
        compositeFiles.Calls.ShouldBeEmpty();
        childFiles.Calls.ShouldBeEmpty();
        caller.Position.ShouldBe(bytes.LongLength);
        protectedInput.CanRead.ShouldBeTrue();
        caller.CanRead.ShouldBeTrue();
    }

    [Fact]
    public async Task CompositeRewind_ExhaustionStopsBeforeAnyRegistryRequest()
    {
        using PublicationWorkspace workspace = new();
        string path = workspace.CreateFile("payload"u8.ToArray());
        using FileStream caller = File.OpenRead(path);
        caller.Position = 3;
        ScriptedFileSystemPrimitives primitives = PublicationTestSupport.SystemFiles();
        IOException failure = new("rewind failed");
        primitives.Set<long>("SetPosition", call =>
            ScriptedFileSystemPrimitives.Fail<long>(failure, "rewind", call.Path));
        BodySequenceHandler handler = new();
        using HttpClient http = new(handler);
        FhirNpmRegistryClient child = new(
            PublicationTestSupport.Transport(http),
            PublicationTestSupport.Endpoint("first"),
            NullLogger<FhirNpmRegistryClient>.Instance);
        RedundantRegistryClient composite = new(
            [child, child],
            3,
            null,
            primitives.CreateContext(retries: 2));

        IOException exception = await Should.ThrowAsync<IOException>(() => composite.PublishAsync(
            new PackageReference("example.package", "1.0.0"),
            caller,
            TestContext.Current.CancellationToken));

        exception.ShouldBeSameAs(failure);
        handler.Bodies.ShouldBeEmpty();
        primitives.Count("SetPosition").ShouldBe(3);
        primitives.Count("PrepareBorrowedRead").ShouldBe(1);
        primitives.Count("ReadBorrowedAsync").ShouldBe(0);
        primitives.AsyncWaits.ShouldBe([10, 10]);
        primitives.Waits.ShouldBeEmpty();
        caller.Position.ShouldBe(3);
        caller.CanRead.ShouldBeTrue();
    }

    [Fact]
    public async Task RawGzip_TerminalPreparedLengthReturnsFailureWithoutRequestOrFallback()
    {
        using PublicationWorkspace workspace = new();
        using FileStream caller = File.OpenRead(workspace.CreateFile("raw gzip"u8.ToArray()));
        ScriptedFileSystemPrimitives files = PublicationTestSupport.SystemFiles();
        files.Set<long>("GetHandleLength", call =>
            ScriptedFileSystemPrimitives.NativeFailure<long>(5, "HTTP file length", call.Path));
        FileSystemContext context = files.CreateContext(retries: 2);
        BodySequenceHandler handler = new();
        using HttpClient http = new(handler);
        FhirNpmRegistryClient child = new(
            PublicationTestSupport.Transport(http), PublicationTestSupport.Endpoint("first"),
            NullLogger<FhirNpmRegistryClient>.Instance, context);
        RedundantRegistryClient composite = new([child, child], 3, null, context);

        PublishResult result = await composite.PublishAsync(
            new PackageReference("example.package", "1.0.0"), caller, TestContext.Current.CancellationToken);

        result.Success.ShouldBeFalse();
        result.StatusCode.ShouldBe(HttpStatusCode.InternalServerError);
        FileSystemFailureContext.TryGetResult(result, out FileSystemFailureContext? failure).ShouldBeTrue();
        failure!.Operation.ShouldBe("HTTP file length");
        failure.RetriesUsed.ShouldBe(2);
        result.Message.ShouldNotBeNull().ShouldContain("attempts 3, retries 2");
        handler.Requests.ShouldBe(0);
        handler.Bodies.ShouldBeEmpty();
        files.Count("GetHandleLength").ShouldBe(3);
        files.Count("SetPosition").ShouldBe(1);
        files.Count("ReadBorrowedAsync").ShouldBe(0);
        files.Count("PrepareBorrowedRead").ShouldBe(1);
        files.AsyncWaits.ShouldBe([10, 10]);
        files.Waits.ShouldBeEmpty();
        caller.CanRead.ShouldBeTrue();
        caller.Position.ShouldBe(0);
    }

    [Fact]
    public async Task RawGzip_HttpDestinationFailureDoesNotReplayFileReadsOrWrites()
    {
        using PublicationWorkspace workspace = new();
        byte[] bytes = new byte[300_003];
        new Random(31).NextBytes(bytes);
        using FileStream caller = File.OpenRead(workspace.CreateFile(bytes));
        ScriptedFileSystemPrimitives files = PublicationTestSupport.SystemFiles();
        using RecordingDestination destination = new(failOnWrite: 2);
        FailingDestinationHandler handler = new(destination);
        using HttpClient http = new(handler);
        FhirNpmRegistryClient client = new(
            PublicationTestSupport.Transport(http), PublicationTestSupport.Endpoint("destination"),
            NullLogger<FhirNpmRegistryClient>.Instance, files.CreateContext());

        PublishResult result = await client.PublishAsync(
            new PackageReference("example.package", "1.0.0"), caller, TestContext.Current.CancellationToken);

        result.Success.ShouldBeFalse();
        FileSystemFailureContext.TryGetResult(result, out _).ShouldBeFalse();
        handler.Requests.ShouldBe(1);
        destination.Writes.Count.ShouldBe(2);
        destination.Length.ShouldBe(destination.Writes[0].Length + 3);
        files.Count("ReadBorrowedAsync").ShouldBe(2);
        files.Count("SetPosition").ShouldBe(0);
        files.Count("CloseFile").ShouldBe(0);
        files.AsyncWaits.ShouldBeEmpty();
        files.Waits.ShouldBeEmpty();
        caller.CanRead.ShouldBeTrue();
        caller.Position.ShouldBe(destination.Writes.Sum(write => (long)write.Length));
    }

    private sealed class FailingDestinationHandler(Stream destination) : HttpMessageHandler
    {
        internal int Requests { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            await request.Content!.CopyToAsync(destination, cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }

    private static NpmPackumentContent CreateContent(
        string path,
        long length,
        FileSystemContext context) =>
        new(path, length, new JsonObject { ["data"] = "__attachment__" }, "__attachment__", context);

    private sealed class RecordingDestination(int? failOnWrite = null) : MemoryStream
    {
        internal List<byte[]> Writes { get; } = [];

        internal IOException Failure { get; } = new("HTTP destination failed after a partial write");

        public override ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Writes.Add(buffer.ToArray());
            if (Writes.Count == failOnWrite)
            {
                base.Write(buffer.Span[..3]);
                return ValueTask.FromException(Failure);
            }

            base.Write(buffer.Span);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class BodySequenceHandler : HttpMessageHandler
    {
        internal List<byte[]> Bodies { get; } = [];

        internal int Requests { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests++;
            Bodies.Add(await request.Content!.ReadAsByteArrayAsync(cancellationToken));
            return new HttpResponseMessage(
                Bodies.Count == 1 ? HttpStatusCode.InternalServerError : HttpStatusCode.OK);
        }
    }
}

internal sealed class PublicationWorkspace : IDisposable
{
    internal string Root { get; } = Path.Combine(
        Path.GetTempPath(), $"fhirpkg-publication-tests-{Guid.NewGuid():N}");

    internal PublicationWorkspace() => Directory.CreateDirectory(Root);

    internal string CreateFile(byte[] bytes)
    {
        string path = Path.Combine(Root, $"{Guid.NewGuid():N}.tgz");
        File.WriteAllBytes(path, bytes);
        return path;
    }

    public void Dispose() => Directory.Delete(Root, recursive: true);
}

internal static class PublicationTestSupport
{
    internal static ScriptedFileSystemPrimitives SystemFiles()
    {
        ScriptedFileSystemPrimitives primitives = new(SystemFileSystemPrimitives.Instance.Platform);
        primitives.UseSystemFiles();
        primitives.Set<IEnumerator<string>>(
            "CreateDirectoryEnumerator",
            call => SystemFileSystemPrimitives.Instance.CreateDirectoryEnumerator(call.Path!));
        return primitives;
    }

    internal static RegistryHttpTransport Transport(HttpClient http) =>
        RegistryHttpTransport.CreateRedirectControlled(http, TimeSpan.FromSeconds(5), maxRedirects: 2);

    internal static RegistryEndpoint Endpoint(string name, RegistryType type = RegistryType.FhirNpm) =>
        new() { Url = $"https://{name}.example/", Type = type };
}
