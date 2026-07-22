// Copyright (c) Gino Canessa. Licensed under the MIT License.

using System.Formats.Tar;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Text;
using FhirPkg.Cli;
using FhirPkg.Cli.Commands;
using FhirPkg.Cache;
using FhirPkg.Installation;
using FhirPkg.Models;
using FhirPkg.Utilities;
using Shouldly;
using Xunit;

namespace FhirPkg.IntegrationTests;

[Trait("Category", "Integration")]
public sealed class HardenedInstallationIntegrationTests
{
    [Fact]
    public async Task ManagerSourceModes_InstallThroughOneValidatedContract()
    {
        using TestWorkspace workspace = new();
        using FhirPackageManager manager = new(
            new FhirPackageManagerOptions
            {
                CachePath = workspace.CachePath,
                IncludeCiBuilds = false,
                IncludeHl7WebsiteFallback = false
            });

        await using MemoryStream expectedStream =
            CreateArchive("source.expected.stream", "1.0.0");
        PackageRecord expectedStreamRecord =
            await manager.InstallAsync(
                new PackageReference(
                    "source.expected.stream",
                    "1.0.0"),
                expectedStream,
                options: null,
                TestContext.Current.CancellationToken);

        await using MemoryStream importStream =
            CreateArchive("source.import.stream", "1.0.0");
        PackageRecord importStreamRecord =
            await manager.ImportAsync(
                importStream,
                options: null,
                TestContext.Current.CancellationToken);

        byte[] expectedUriArchive =
            CreateArchive("source.expected.uri", "1.0.0").ToArray();
        await using SingleResponseHttpServer expectedServer =
            await SingleResponseHttpServer.StartAsync(
                expectedUriArchive,
                TestContext.Current.CancellationToken);
        PackageRecord expectedUriRecord =
            await manager.InstallAsync(
                new PackageReference(
                    "source.expected.uri",
                    "1.0.0"),
                expectedServer.Uri,
                options: null,
                TestContext.Current.CancellationToken);

        byte[] importUriArchive =
            CreateArchive("source.import.uri", "1.0.0").ToArray();
        await using SingleResponseHttpServer importServer =
            await SingleResponseHttpServer.StartAsync(
                importUriArchive,
                TestContext.Current.CancellationToken);
        PackageRecord importUriRecord =
            await manager.ImportAsync(
                importServer.Uri,
                options: null,
                TestContext.Current.CancellationToken);

        expectedStreamRecord.Reference.Name.ShouldBe(
            "source.expected.stream");
        importStreamRecord.Reference.Name.ShouldBe(
            "source.import.stream");
        expectedUriRecord.Reference.Name.ShouldBe(
            "source.expected.uri");
        importUriRecord.Reference.Name.ShouldBe(
            "source.import.uri");
        IReadOnlyList<PackageRecord> cached =
            await manager.ListCachedAsync(
                cancellationToken:
                    TestContext.Current.CancellationToken);
        cached.Count.ShouldBe(4);
    }

    [Theory]
    [InlineData("current")]
    [InlineData("current$feature")]
    public async Task MutableAlias_OverwritePublishesSecondGeneration(
        string alias)
    {
        using TestWorkspace workspace = new();
        using FhirPackageManager manager = new(
            new FhirPackageManagerOptions
            {
                CachePath = workspace.CachePath,
                IncludeCiBuilds = false,
                IncludeHl7WebsiteFallback = false
            });
        PackageReference aliasReference =
            new("alias.package", alias);
        await using MemoryStream first =
            CreateArchive(
                "alias.package",
                "1.0.0",
                "generation.txt",
                "first");
        await manager.InstallAsync(
            aliasReference,
            first,
            options: null,
            TestContext.Current.CancellationToken);
        await using MemoryStream second =
            CreateArchive(
                "alias.package",
                "2.0.0",
                "generation.txt",
                "second");

        PackageRecord record = await manager.InstallAsync(
            aliasReference,
            second,
            new PackageSourceInstallOptions
            {
                OverwriteExisting = true
            },
            TestContext.Current.CancellationToken);

        record.Reference.ShouldBe(aliasReference);
        record.Manifest.Version.ShouldBe("2.0.0");
        string generation = await File.ReadAllTextAsync(
            Path.Combine(
                record.ContentPath,
                "generation.txt"),
            TestContext.Current.CancellationToken);
        generation.ShouldBe("second");
    }

    [Fact]
    public async Task DevDirective_RemainsLocalAuthoritativeWithOverwrite()
    {
        using TestWorkspace workspace = new();
        DiskPackageCache cache = new(workspace.CachePath);
        await using MemoryStream archive =
            CreateArchive("dev.package", "9.9.9");
        await cache.InstallAsync(
            new PackageReference("dev.package", "dev"),
            archive,
            ct:
                TestContext.Current.CancellationToken);
        using FhirPackageManager manager = new(
            new FhirPackageManagerOptions
            {
                CachePath = workspace.CachePath,
                IncludeCiBuilds = false,
                IncludeHl7WebsiteFallback = false
            });

        PackageRecord? record = await manager.InstallAsync(
            "dev.package#dev",
            new InstallOptions
            {
                OverwriteExisting = true
            },
            TestContext.Current.CancellationToken);

        record.ShouldNotBeNull();
        record!.Reference.Version.ShouldBe("dev");
        record.Manifest.Version.ShouldBe("9.9.9");
    }

    [Fact]
    public async Task Clear_RemovesMetadataOnlyManagedEntryAndPreservesUnknownSection()
    {
        using TestWorkspace workspace = new();
        using DiskPackageCache cache = new(workspace.CachePath);
        PackageReference reference =
            new("metadata.only", "1.0.0");
        PackageCacheKey cacheKey = PackageCacheKey.Create(reference);
        await cache.UpdateMetadataAsync(
            reference,
            new CacheMetadataEntry
            {
                DownloadDateTime = DateTime.UtcNow,
                SizeBytes = 42,
                SourcePublicationDate =
                    new DateTimeOffset(
                        2026,
                        7,
                        17,
                        12,
                        0,
                        0,
                        TimeSpan.Zero),
                ArchiveSha256 = new string('a', 64)
            },
            TestContext.Current.CancellationToken);
        string metadataPath = Path.Combine(
            workspace.CachePath,
            "packages.ini");
        await File.AppendAllTextAsync(
            metadataPath,
            $"{Environment.NewLine}[external]{Environment.NewLine}keep = value{Environment.NewLine}",
            TestContext.Current.CancellationToken);

        int removed = await cache.ClearAsync(
            TestContext.Current.CancellationToken);

        removed.ShouldBe(0);
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>>
            sections = IniParser.ParseFile(metadataPath);
        sections["packages"].ContainsKey(
            cacheKey.MetadataKey).ShouldBeFalse();
        sections["package-sizes"].ContainsKey(
            cacheKey.MetadataKey).ShouldBeFalse();
        sections["package-source-publication-dates"].ContainsKey(
            cacheKey.MetadataKey).ShouldBeFalse();
        sections["package-archive-sha256"].ContainsKey(
            cacheKey.MetadataKey).ShouldBeFalse();
        sections["external"]["keep"].ShouldBe("value");
    }

    [Fact]
    public async Task Clear_RemovesMalformedLegacyAndNonCanonicalManagedKeys()
    {
        using TestWorkspace workspace = new();
        using DiskPackageCache cache = new(workspace.CachePath);
        string metadataPath = Path.Combine(
            workspace.CachePath,
            "packages.ini");
        await File.WriteAllTextAsync(
            metadataPath,
            """
            [cache]
            version = 3

            [packages]
            legacy.package@1.0.0 = 20260717120000
            UPPER.package#1.0.0 = 20260717120000
            ../malformed#1.0.0 = 20260717120000
            canonical.package#1.0.0 = 20260717120000

            [package-sizes]
            legacy.package@1.0.0 = 1
            @scope\package#1.0.0 = 2

            [package-source-publication-dates]
            UPPER.package#1.0.0 = 2026-07-17T12:00:00Z

            [package-archive-sha256]
            ../malformed#1.0.0 = abc

            [external]
            keep = value
            """,
            TestContext.Current.CancellationToken);

        int removed = await cache.ClearAsync(
            TestContext.Current.CancellationToken);

        removed.ShouldBe(0);
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>>
            sections = IniParser.ParseFile(metadataPath);
        sections["packages"].ShouldBeEmpty();
        sections["package-sizes"].ShouldBeEmpty();
        sections["package-source-publication-dates"].ShouldBeEmpty();
        sections["package-archive-sha256"].ShouldBeEmpty();
        sections["external"]["keep"].ShouldBe("value");
    }

    [Fact]
    public void InstallExitCode_NotFoundRequiresNotFoundStatus()
    {
        int notFound = InstallCommand.GetExitCode(
        [
            new PackageInstallResult
            {
                Directive = "installed.package#1.0.0",
                Status = PackageInstallStatus.Installed
            },
            new PackageInstallResult
            {
                Directive = "missing.package#1.0.0",
                Status = PackageInstallStatus.NotFound,
                ErrorCode = PackageInstallErrorCode.ResolutionFailed,
                ErrorStage = PackageInstallStage.Resolution
            }
        ]);
        int failedResolution = InstallCommand.GetExitCode(
        [
            new PackageInstallResult
            {
                Directive = "network.package#1.0.0",
                Status = PackageInstallStatus.Failed,
                ErrorCode = PackageInstallErrorCode.ResolutionFailed,
                ErrorStage = PackageInstallStage.Resolution
            }
        ]);

        notFound.ShouldBe(ExitCodes.NotFound);
        failedResolution.ShouldBe(ExitCodes.NetworkError);
    }

    [Fact]
    public void InstallExitCode_MixedResultsUseStablePrecedence()
    {
        PackageInstallResult notFound = new()
        {
            Directive = "missing.package#1.0.0",
            Status = PackageInstallStatus.NotFound,
            ErrorCode = PackageInstallErrorCode.ResolutionFailed,
            ErrorStage = PackageInstallStage.Resolution
        };
        PackageInstallResult networkFailure = new()
        {
            Directive = "network.package#1.0.0",
            Status = PackageInstallStatus.Failed,
            ErrorCode = PackageInstallErrorCode.ResolutionFailed,
            ErrorStage = PackageInstallStage.Resolution
        };
        PackageInstallResult checksumFailure = new()
        {
            Directive = "checksum.package#1.0.0",
            Status = PackageInstallStatus.Failed,
            ErrorCode = PackageInstallErrorCode.ChecksumMismatch,
            ErrorStage = PackageInstallStage.ChecksumValidation
        };
        PackageInstallResult dependencyFailure = new()
        {
            Directive = "root.package#1.0.0",
            Status = PackageInstallStatus.Failed,
            ErrorCode =
                PackageInstallErrorCode.DependencyInstallationFailed,
            ErrorStage = PackageInstallStage.DependencyInstallation,
            DependencyFailures =
            [
                new PackageInstallResult
                {
                    Directive = "dependency.package#1.0.0",
                    Status = PackageInstallStatus.NotFound,
                    ErrorCode =
                        PackageInstallErrorCode.ResolutionFailed,
                    ErrorStage = PackageInstallStage.Resolution,
                }
            ],
        };

        InstallCommand.GetExitCode(
            [notFound, networkFailure])
            .ShouldBe(ExitCodes.NetworkError);
        InstallCommand.GetExitCode(
            [networkFailure, checksumFailure])
            .ShouldBe(ExitCodes.ChecksumFail);
        InstallCommand.GetExitCode(
            [networkFailure, dependencyFailure])
            .ShouldBe(ExitCodes.DependencyResolutionFail);
    }

    private static MemoryStream CreateArchive(
        string name,
        string version,
        string? extraName = null,
        string? extraContent = null)
    {
        MemoryStream archive = new();
        using (GZipStream gzip = new(
            archive,
            CompressionLevel.Fastest,
            leaveOpen: true))
        using (TarWriter writer = new(
            gzip,
            TarEntryFormat.Pax,
            leaveOpen: true))
        {
            writer.WriteEntry(new PaxTarEntry(
                TarEntryType.Directory,
                "package/"));
            WriteEntry(
                writer,
                "package/package.json",
                $$"""{"name":"{{name}}","version":"{{version}}"}""");
            if (extraName is not null)
            {
                WriteEntry(
                    writer,
                    $"package/{extraName}",
                    extraContent ?? string.Empty);
            }
        }

        archive.Position = 0;
        return archive;
    }

    private static void WriteEntry(
        TarWriter writer,
        string path,
        string content)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(content);
        writer.WriteEntry(new PaxTarEntry(
            TarEntryType.RegularFile,
            path)
        {
            DataStream = new MemoryStream(bytes)
        });
    }

    private sealed class TestWorkspace : IDisposable
    {
        internal TestWorkspace()
        {
            RootPath = Path.Combine(
                AppContext.BaseDirectory,
                "phase5-hardened-integration",
                Guid.NewGuid().ToString("N"));
            CachePath = Path.Combine(RootPath, "cache");
            Directory.CreateDirectory(CachePath);
        }

        internal string RootPath { get; }
        internal string CachePath { get; }

        public void Dispose()
        {
            if (Directory.Exists(RootPath))
                Directory.Delete(RootPath, recursive: true);
        }
    }

    private sealed class SingleResponseHttpServer :
        IAsyncDisposable
    {
        private readonly TcpListener _listener;
        private readonly Task _serverTask;

        private SingleResponseHttpServer(
            TcpListener listener,
            Task serverTask,
            Uri uri)
        {
            _listener = listener;
            _serverTask = serverTask;
            Uri = uri;
        }

        internal Uri Uri { get; }

        internal static Task<SingleResponseHttpServer> StartAsync(
            byte[] content,
            CancellationToken cancellationToken)
        {
            TcpListener listener = new(
                IPAddress.Loopback,
                port: 0);
            listener.Start();
            int port =
                ((IPEndPoint)listener.LocalEndpoint).Port;
            Task serverTask = ServeAsync(
                listener,
                content,
                cancellationToken);
            SingleResponseHttpServer server = new(
                listener,
                serverTask,
                new Uri($"http://127.0.0.1:{port}/package.tgz"));
            return Task.FromResult(server);
        }

        private static async Task ServeAsync(
            TcpListener listener,
            byte[] content,
            CancellationToken cancellationToken)
        {
            using TcpClient client =
                await listener.AcceptTcpClientAsync(
                        cancellationToken)
                    .ConfigureAwait(false);
            await using NetworkStream stream =
                client.GetStream();
            byte[] requestBuffer = new byte[4096];
            int total = 0;
            while (total < requestBuffer.Length)
            {
                int read = await stream.ReadAsync(
                        requestBuffer.AsMemory(total),
                        cancellationToken)
                    .ConfigureAwait(false);
                if (read == 0)
                    break;

                total += read;
                if (Encoding.ASCII
                    .GetString(requestBuffer, 0, total)
                    .Contains(
                        "\r\n\r\n",
                        StringComparison.Ordinal))
                {
                    break;
                }
            }

            byte[] headers = Encoding.ASCII.GetBytes(
                "HTTP/1.1 200 OK\r\n" +
                "Content-Type: application/gzip\r\n" +
                $"Content-Length: {content.LongLength}\r\n" +
                "Connection: close\r\n\r\n");
            await stream.WriteAsync(
                    headers,
                    cancellationToken)
                .ConfigureAwait(false);
            await stream.WriteAsync(
                    content,
                    cancellationToken)
                .ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken)
                .ConfigureAwait(false);
        }

        public async ValueTask DisposeAsync()
        {
            _listener.Stop();
            try
            {
                await _serverTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            catch (SocketException exception)
                when (exception.SocketErrorCode
                    == SocketError.OperationAborted)
            {
            }
        }
    }
}
