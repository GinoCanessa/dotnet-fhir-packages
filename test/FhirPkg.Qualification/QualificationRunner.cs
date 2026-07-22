// Copyright (c) Gino Canessa. Licensed under the MIT License.

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using FhirPkg.Installation;
using FhirPkg.Models;
using FhirPkg.Qualification.Models;
using FhirPkg.Registry;

namespace FhirPkg.Qualification;

internal sealed class QualificationRunner
{
    private readonly QualificationArguments _arguments;
    private readonly QualificationCorpus _corpus;
    private readonly QualificationBuildSnapshot _build;
    private readonly ReportSanitizer _sanitizer;
    private readonly QualificationReport _report;
    private readonly Dictionary<string, DownloadedArtifact> _artifacts =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, QualificationArtifactResult>
        _artifactResults = new(StringComparer.Ordinal);
    private readonly string _workspaceRoot;
    private readonly string _downloadRoot;
    private readonly string _fixtureRoot;

    internal QualificationRunner(
        QualificationArguments arguments,
        QualificationCorpus corpus,
        QualificationBuildSnapshot build,
        ReportSanitizer sanitizer,
        DateTimeOffset startedUtc,
        string corpusSha256)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentNullException.ThrowIfNull(corpus);
        ArgumentNullException.ThrowIfNull(sanitizer);
        _arguments = arguments;
        _corpus = corpus;
        _build = build;
        _sanitizer = sanitizer;
        _workspaceRoot = Path.Combine(
            arguments.CacheRoot,
            ".qualification");
        _downloadRoot = Path.Combine(_workspaceRoot, "downloads");
        _fixtureRoot = Path.Combine(_workspaceRoot, "fixtures");
        _report = new QualificationReport
        {
            Mode = build.Mode,
            ValidationOnly = arguments.ValidateOnly,
            RequestedPackageVersion =
                build.RequestedPackageVersion,
            PackageVersion = build.PackageVersion,
            FhirPkgAssemblyVersion =
                build.FhirPkgAssemblyVersion,
            FhirPkgInformationalVersion =
                build.FhirPkgInformationalVersion,
            CorpusSha256 = corpusSha256,
            CorpusHashAlgorithm =
                QualificationCorpusHash.Algorithm,
            Framework = RuntimeInformation.FrameworkDescription,
            OperatingSystem = RuntimeInformation.OSDescription,
            StartedUtc = startedUtc,
            CompletedUtc = startedUtc,
            Success = false,
            Artifacts = [],
            Cases = [],
            Summary = new QualificationSummary
            {
                ArtifactCount = 0,
                ArtifactFailures = 0,
                CaseCount = 0,
                CaseFailures = 0
            }
        };
    }

    internal async Task<QualificationReport> RunAsync(
        CancellationToken cancellationToken)
    {
        EnsureEmptyRoot();
        Directory.CreateDirectory(_downloadRoot);
        Directory.CreateDirectory(_fixtureRoot);

        await AcquireLocalFixtureAsync(cancellationToken)
            .ConfigureAwait(false);
        using (ArtifactDownloader downloader = new(
            _downloadRoot,
            _arguments.HttpTimeout))
        {
            foreach (ImmutableArtifactDefinition artifact in
                _corpus.ImmutableArtifacts.OrderBy(
                    artifact => artifact.Id,
                    StringComparer.Ordinal))
            {
                await AcquireRemoteArtifactAsync(
                        downloader,
                        "immutable",
                        artifact.Id,
                        artifact.Uri,
                        artifact.Sha256,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            foreach (MutableArtifactDefinition artifact in
                _corpus.MutableArtifacts.OrderBy(
                    artifact => artifact.Id,
                    StringComparer.Ordinal))
            {
                await AcquireRemoteArtifactAsync(
                        downloader,
                        "mutable",
                        artifact.Id,
                        artifact.Uri,
                        expectedSha256: null,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        await RunCaseAsync(
            "01-immutable-expected-stream",
            InstallImmutableCorpusAsync,
            cancellationToken).ConfigureAwait(false);
        await RunCaseAsync(
            "02-directive-exact",
            InstallDirectiveExactAsync,
            cancellationToken).ConfigureAwait(false);
        await RunCaseAsync(
            "03-uri-sources",
            InstallUriSourcesAsync,
            cancellationToken).ConfigureAwait(false);
        await RunCaseAsync(
            "04-discovery-stream-checksum",
            ImportStreamAndRejectChecksumAsync,
            cancellationToken).ConfigureAwait(false);
        await RunCaseAsync(
            "05-dependencies-closure",
            InstallDependencyClosureAsync,
            cancellationToken).ConfigureAwait(false);
        await RunCaseAsync(
            "06-downstream-direct-streams",
            InstallDownstreamCorpusAsync,
            cancellationToken).ConfigureAwait(false);
        await RunCaseAsync(
            "07-mutable-aliases",
            InstallMutableAliasesAsync,
            cancellationToken).ConfigureAwait(false);
        await RunCaseAsync(
            "08-dev-local-only",
            InstallDevAsync,
            cancellationToken).ConfigureAwait(false);
        await RunCaseAsync(
            "09-cancellation",
            VerifyCancellationAsync,
            cancellationToken).ConfigureAwait(false);
        await RunCaseAsync(
            "10-corrupt-cache",
            VerifyCorruptCacheAsync,
            cancellationToken).ConfigureAwait(false);
        await RunCaseAsync(
            "11-in-process-concurrency",
            VerifyInProcessConcurrencyAsync,
            cancellationToken).ConfigureAwait(false);
        await RunCaseAsync(
            "12-process-host",
            VerifyProcessHostAsync,
            cancellationToken).ConfigureAwait(false);

        return FinalizeReport();
    }

    internal async Task<QualificationReport> ValidateOnlyAsync(
        CancellationToken cancellationToken)
    {
        await RunCaseAsync(
                "validate-only",
                ValidateOnlyCoreAsync,
                cancellationToken)
            .ConfigureAwait(false);
        return FinalizeReport();
    }

    private QualificationReport FinalizeReport()
    {
        _report.Artifacts.AddRange(
            _artifactResults.Values
                .OrderBy(
                    result => result.Kind,
                    StringComparer.Ordinal)
                .ThenBy(
                    result => result.Id,
                    StringComparer.Ordinal));
        int artifactFailures = _report.Artifacts.Count(
            result => !result.Success);
        int caseFailures = _report.Cases.Count(
            result => !result.Success);
        _report.Summary = new QualificationSummary
        {
            ArtifactCount = _report.Artifacts.Count,
            ArtifactFailures = artifactFailures,
            CaseCount = _report.Cases.Count,
            CaseFailures = caseFailures
        };
        _report.Success = artifactFailures == 0
            && caseFailures == 0;
        _report.CompletedUtc = DateTimeOffset.UtcNow;
        return _report;
    }

    private async Task ValidateOnlyCoreAsync(
        QualificationCaseContext context,
        CancellationToken cancellationToken)
    {
        LocalFixtureDefinition definition =
            _corpus.LocalFixture;
        QualificationArtifactResult result = new()
        {
            Kind = "local",
            Id = definition.Id,
            SourceUri = "generated:deterministic-local-dev",
            ExpectedSha256 = definition.Sha256,
            Success = false
        };
        _artifactResults[result.Id] = result;
        byte[] content = DeterministicPackageArchive.Create(
            definition.Name,
            definition.ManifestVersion,
            "local-dev");
        string actualSha256 = Convert.ToHexString(
                SHA256.HashData(content))
            .ToLowerInvariant();
        await using MemoryStream archive = new(
            content,
            writable: false);
        ArchiveMetrics metrics =
            await ArchiveMetricsInspector.InspectAsync(
                    archive,
                    cancellationToken)
                .ConfigureAwait(false);
        bool hashMatch = string.Equals(
            definition.Sha256,
            actualSha256,
            StringComparison.Ordinal);
        result.ActualSha256 = actualSha256;
        result.HashMatch = hashMatch;
        result.CompressedBytes = content.LongLength;
        result.ExpandedBytes = metrics.ExpandedBytes;
        result.LargestEntryBytes =
            metrics.LargestEntryBytes;
        result.EntryCount = metrics.EntryCount;
        result.ManifestName = definition.Name;
        result.ManifestVersion =
            definition.ManifestVersion;
        result.Success = hashMatch;
        QualificationAssert.True(
            hashMatch,
            "The deterministic local fixture hash did not match the corpus pin.");
        QualificationCorpusHash
            .VerifyCanonicalizationRegression();
        context.Detail(
            "buildMode",
            _build.Mode);
        context.Detail(
            "corpusImmutableArtifacts",
            _corpus.ImmutableArtifacts.Count);
        context.Detail(
            "corpusHashAlgorithm",
            QualificationCorpusHash.Algorithm);
        context.Detail(
            "fixtureSha256",
            actualSha256);
        context.Detail(
            "loadedFhirPkgAssemblyVersion",
            _build.FhirPkgAssemblyVersion);
        context.Detail(
            "packageVersion",
            _build.PackageVersion ?? "source");
        context.Detail("schemas", "valid");
    }

    private void EnsureEmptyRoot()
    {
        if (Directory.Exists(_arguments.CacheRoot)
            && Directory.EnumerateFileSystemEntries(
                _arguments.CacheRoot).Any())
        {
            throw new QualificationInvariantException(
                "The qualification cache root must be empty.");
        }

        Directory.CreateDirectory(_arguments.CacheRoot);
    }

    private async Task AcquireLocalFixtureAsync(
        CancellationToken cancellationToken)
    {
        LocalFixtureDefinition definition = _corpus.LocalFixture;
        QualificationArtifactResult result = new()
        {
            Kind = "local",
            Id = definition.Id,
            SourceUri = "generated:deterministic-local-dev",
            ExpectedSha256 = definition.Sha256,
            Success = false
        };
        _artifactResults[result.Id] = result;
        try
        {
            byte[] content = DeterministicPackageArchive.Create(
                definition.Name,
                definition.ManifestVersion,
                "local-dev");
            string path = Path.Combine(
                _fixtureRoot,
                "local-dev.tgz");
            await File.WriteAllBytesAsync(
                    path,
                    content,
                    cancellationToken)
                .ConfigureAwait(false);
            string actualSha256 = Convert.ToHexString(
                    SHA256.HashData(content))
                .ToLowerInvariant();
            ArchiveMetrics metrics =
                await ArchiveMetricsInspector.InspectAsync(
                        path,
                        cancellationToken)
                    .ConfigureAwait(false);
            bool hashMatch = string.Equals(
                definition.Sha256,
                actualSha256,
                StringComparison.Ordinal);
            result.ActualSha256 = actualSha256;
            result.HashMatch = hashMatch;
            result.CompressedBytes = content.LongLength;
            result.ExpandedBytes = metrics.ExpandedBytes;
            result.LargestEntryBytes = metrics.LargestEntryBytes;
            result.EntryCount = metrics.EntryCount;
            result.Success = hashMatch;
            _artifacts[definition.Id] = new DownloadedArtifact(
                definition.Id,
                path,
                actualSha256,
                content.LongLength,
                metrics,
                FinalUri: null,
                PublicationDate: null);
            if (!hashMatch)
            {
                result.Failure = new QualificationFailure
                {
                    Code = "LocalFixtureHashMismatch",
                    ExceptionType =
                        nameof(QualificationInvariantException),
                    Message =
                        $"Generated local fixture SHA-256 was {actualSha256}."
                };
            }
        }
        catch (Exception exception)
        {
            result.Failure = CreateFailure(exception);
        }
    }

    private async Task AcquireRemoteArtifactAsync(
        ArtifactDownloader downloader,
        string kind,
        string id,
        string sourceUri,
        string? expectedSha256,
        CancellationToken cancellationToken)
    {
        QualificationArtifactResult result = new()
        {
            Kind = kind,
            Id = id,
            SourceUri = sourceUri,
            ExpectedSha256 = expectedSha256,
            Success = false
        };
        _artifactResults[id] = result;
        try
        {
            DownloadedArtifact artifact =
                await downloader.DownloadAsync(
                        id,
                        new Uri(sourceUri),
                        cancellationToken)
                    .ConfigureAwait(false);
            bool? hashMatch = expectedSha256 is null
                ? null
                : string.Equals(
                    expectedSha256,
                    artifact.ActualSha256,
                    StringComparison.Ordinal);
            result.FinalUri = artifact.FinalUri?.AbsoluteUri;
            result.ActualSha256 = artifact.ActualSha256;
            result.HashMatch = hashMatch;
            result.CompressedBytes = artifact.CompressedBytes;
            result.ExpandedBytes = artifact.Metrics.ExpandedBytes;
            result.LargestEntryBytes =
                artifact.Metrics.LargestEntryBytes;
            result.EntryCount = artifact.Metrics.EntryCount;
            result.PublicationUtc = artifact.PublicationDate?
                .ToUniversalTime()
                .ToString("O");
            result.Success = hashMatch is not false;
            if (result.Success)
            {
                _artifacts[id] = artifact;
            }
            else
            {
                result.Failure = new QualificationFailure
                {
                    Code = "ImmutableHashMismatch",
                    ExceptionType =
                        nameof(QualificationInvariantException),
                    Message =
                        $"Immutable artifact SHA-256 was {artifact.ActualSha256}."
                };
            }
        }
        catch (Exception exception)
        {
            result.Failure = CreateFailure(exception);
        }
    }

    private async Task RunCaseAsync(
        string id,
        Func<QualificationCaseContext, CancellationToken, Task> action,
        CancellationToken cancellationToken)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        QualificationCaseResult result = new()
        {
            Id = id,
            Success = false,
            DurationMilliseconds = 0,
            Details = [],
            Failures = []
        };
        QualificationCaseContext context = new(
            id,
            this,
            result);
        try
        {
            await action(context, cancellationToken)
                .ConfigureAwait(false);
            result.Success = true;
        }
        catch (Exception exception)
        {
            result.Failures.Add(CreateFailure(exception));
        }
        finally
        {
            stopwatch.Stop();
            result.DurationMilliseconds =
                stopwatch.ElapsedMilliseconds;
            result.Details.Sort(
                static (left, right) =>
                    StringComparer.Ordinal.Compare(
                        left.Name,
                        right.Name));
            _report.Cases.Add(result);
        }
    }

    private async Task InstallImmutableCorpusAsync(
        QualificationCaseContext context,
        CancellationToken cancellationToken)
    {
        string cachePath = context.CreateEmptyCache();
        using FhirPackageManager manager = CreateManager(cachePath);
        QualificationProgress progress = new();
        int installed = 0;
        foreach (ImmutableArtifactDefinition definition in
            _corpus.ImmutableArtifacts.OrderBy(
                artifact => artifact.Id,
                StringComparer.Ordinal))
        {
            DownloadedArtifact artifact =
                context.RequireArtifact(definition.Id);
            await using FileStream source = OpenArtifact(artifact);
            PackageRecord record = await manager.InstallAsync(
                    new PackageReference(
                        definition.Name,
                        definition.Version),
                    source,
                    new PackageSourceInstallOptions
                    {
                        ExpectedSha256 = definition.Sha256,
                        Progress = progress
                    },
                    cancellationToken)
                .ConfigureAwait(false);
            QualificationAssert.Equal(
                definition.Name,
                record.Manifest.Name,
                $"Manifest name mismatch for {definition.Id}.");
            QualificationAssert.Equal(
                definition.Version,
                record.Manifest.Version,
                $"Manifest version mismatch for {definition.Id}.");
            installed++;
        }

        IReadOnlyList<PackageRecord> records =
            await manager.ListCachedAsync(
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);
        QualificationAssert.Equal(
            _corpus.ImmutableArtifacts.Count,
            records.Count,
            "The immutable corpus cache count did not match.");
        PackageProgressPhase[] requiredPhases =
        [
            PackageProgressPhase.Acquiring,
            PackageProgressPhase.WaitingForLock,
            PackageProgressPhase.Extracting,
            PackageProgressPhase.Validating,
            PackageProgressPhase.Committing,
            PackageProgressPhase.Complete
        ];
        IReadOnlyList<PackageProgressPhase> observed = progress.Phases;
        foreach (PackageProgressPhase phase in requiredPhases)
        {
            QualificationAssert.True(
                observed.Contains(phase),
                $"Progress did not report {phase}.");
        }

        context.Detail("installedArtifacts", installed);
        context.Detail(
            "progressPhases",
            string.Join(
                ",",
                observed.Distinct()));
    }

    private async Task InstallDirectiveExactAsync(
        QualificationCaseContext context,
        CancellationToken cancellationToken)
    {
        string cachePath = context.CreateEmptyCache();
        using FhirPackageManager manager =
            CreateSecondaryRegistryManager(cachePath);
        QualificationProgress progress = new();
        PackageRecord? record = await manager.InstallAsync(
                "hl7.fhir.r4.core#4.0.1",
                new InstallOptions
                {
                    Progress = progress
                },
                cancellationToken)
            .ConfigureAwait(false);
        QualificationAssert.True(
            record is not null,
            "Exact directive installation returned no package.");
        QualificationAssert.Equal(
            "4.0.1",
            record!.Manifest.Version,
            "Exact directive installed the wrong version.");
        QualificationAssert.True(
            progress.Phases.Contains(PackageProgressPhase.Resolving),
            "Directive progress did not report resolution.");
        context.Detail(
            "reference",
            record.Reference.FhirDirective);
    }

    private async Task InstallUriSourcesAsync(
        QualificationCaseContext context,
        CancellationToken cancellationToken)
    {
        ImmutableArtifactDefinition expectedDefinition =
            _corpus.GetImmutable("hl7.fhir.r4b.core#4.3.0");
        string expectedCache = context.CreateEmptyCache("expected");
        using (FhirPackageManager manager = CreateManager(expectedCache))
        {
            QualificationProgress progress = new();
            PackageRecord record = await manager.InstallAsync(
                    new PackageReference(
                        expectedDefinition.Name,
                        expectedDefinition.Version),
                    new Uri(expectedDefinition.Uri),
                    new PackageSourceInstallOptions
                    {
                        ExpectedSha256 =
                            expectedDefinition.Sha256,
                        Progress = progress
                    },
                    cancellationToken)
                .ConfigureAwait(false);
            QualificationAssert.Equal(
                expectedDefinition.Id,
                record.Reference.FhirDirective,
                "Expected URI returned the wrong identity.");
            QualificationAssert.True(
                progress.Phases.FirstOrDefault()
                    == PackageProgressPhase.Downloading,
                "Expected URI did not begin with downloading progress.");
            QualificationAssert.Equal(
                1,
                progress.Phases.Count(
                    phase => phase
                        == PackageProgressPhase.Complete),
                "Expected URI did not complete exactly once.");
            QualificationAssert.True(
                !progress.Phases.Contains(
                    PackageProgressPhase.Failed),
                "Expected URI reported a failure phase.");
        }

        ImmutableArtifactDefinition discoveryDefinition =
            _corpus.GetImmutable("hl7.terminology#7.2.0");
        string discoveryCache =
            context.CreateEmptyCache("discovery");
        using (FhirPackageManager manager = CreateManager(discoveryCache))
        {
            PackageRecord record = await manager.ImportAsync(
                    new Uri(discoveryDefinition.Uri),
                    new PackageSourceInstallOptions
                    {
                        ExpectedSha256 =
                            discoveryDefinition.Sha256
                    },
                    cancellationToken)
                .ConfigureAwait(false);
            QualificationAssert.Equal(
                discoveryDefinition.Id,
                record.Reference.FhirDirective,
                "Discovery URI returned the wrong identity.");
        }

        context.Detail(
            "expectedUri",
            expectedDefinition.Id);
        context.Detail(
            "discoveryUri",
            discoveryDefinition.Id);
    }

    private async Task ImportStreamAndRejectChecksumAsync(
        QualificationCaseContext context,
        CancellationToken cancellationToken)
    {
        ImmutableArtifactDefinition definition =
            _corpus.GetImmutable("hl7.fhir.r5.core#5.0.0");
        DownloadedArtifact artifact =
            context.RequireArtifact(definition.Id);
        string discoveryCache =
            context.CreateEmptyCache("discovery");
        using (FhirPackageManager manager = CreateManager(discoveryCache))
        await using (FileStream source = OpenArtifact(artifact))
        {
            PackageRecord record = await manager.ImportAsync(
                    source,
                    new PackageSourceInstallOptions
                    {
                        ExpectedSha256 = definition.Sha256
                    },
                    cancellationToken)
                .ConfigureAwait(false);
            QualificationAssert.Equal(
                definition.Id,
                record.Reference.FhirDirective,
                "Discovery stream returned the wrong identity.");
        }

        DownloadedArtifact local =
            context.RequireArtifact(_corpus.LocalFixture.Id);
        string checksumCache =
            context.CreateEmptyCache("checksum-failure");
        using (FhirPackageManager manager = CreateManager(checksumCache))
        await using (FileStream source = OpenArtifact(local))
        {
            PackageInstallException exception;
            try
            {
                await manager.InstallAsync(
                        new PackageReference(
                            _corpus.LocalFixture.Name,
                            _corpus.LocalFixture.ManifestVersion),
                        source,
                        new PackageSourceInstallOptions
                        {
                            ExpectedSha256 =
                                new string('f', 64)
                        },
                        cancellationToken)
                    .ConfigureAwait(false);
                throw new QualificationInvariantException(
                    "Checksum mismatch was accepted.");
            }
            catch (PackageInstallException caught)
            {
                exception = caught;
            }

            QualificationAssert.Equal(
                PackageInstallErrorCode.ChecksumMismatch,
                exception.ErrorCode,
                "Checksum failure used the wrong error code.");
            IReadOnlyList<PackageRecord> records =
                await manager.ListCachedAsync(
                        cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
            QualificationAssert.Equal(
                0,
                records.Count,
                "Checksum failure left a visible package.");
        }

        context.Detail(
            "discoveryStream",
            definition.Id);
        context.Detail(
            "checksumFailureCode",
            PackageInstallErrorCode.ChecksumMismatch);
    }

    private async Task InstallDependencyClosureAsync(
        QualificationCaseContext context,
        CancellationToken cancellationToken)
    {
        string cachePath = context.CreateEmptyCache();
        using FhirPackageManager manager =
            CreateSecondaryRegistryManager(
                cachePath,
                verifyChecksums: false,
                maxParallelRegistryQueries: 1);
        string projectPath =
            context.CreateWorkspace("restore-project");
        await File.WriteAllTextAsync(
                Path.Combine(projectPath, "package.json"),
                """
                {
                  "name": "local.qualification.restore",
                  "version": "1.0.0",
                  "dependencies": {
                    "hl7.fhir.us.core": "6.0.0",
                    "hl7.fhir.uv.ips": "1.1.0"
                  }
                }
                """,
                cancellationToken)
            .ConfigureAwait(false);
        PackageClosure closure = await manager.RestoreAsync(
                projectPath,
                new RestoreOptions
                {
                    WriteLockFile = false
                },
                cancellationToken)
            .ConfigureAwait(false);
        QualificationAssert.Equal(
            0,
            closure.Missing.Count,
            "Dependency resolver reported missing packages.");
        string[] resolved = closure.Resolved.Values
            .Select(reference => reference.FhirDirective)
            .Order(StringComparer.Ordinal)
            .ToArray();
        AssertExactReferences(
            _corpus.DownstreamClosure.ResolverMembers,
            resolved,
            "Resolved transitive closure");

        IReadOnlyList<PackageRecord> afterDependencies =
            await manager.ListCachedAsync(
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);
        string[] installed = afterDependencies
            .Select(record => record.Reference.FhirDirective)
            .Order(StringComparer.Ordinal)
            .ToArray();
        AssertExactReferences(
            _corpus.DownstreamClosure.ResolverMembers,
            installed,
            "Resolver-installed transitive closure");
        context.Detail(
            "resolverMembers",
            resolved.Length);
    }

    private async Task InstallDownstreamCorpusAsync(
        QualificationCaseContext context,
        CancellationToken cancellationToken)
    {
        string cachePath = context.CreateEmptyCache();
        using FhirPackageManager manager = CreateManager(cachePath);
        foreach (string memberId in
            _corpus.DownstreamClosure.RequiredMembers
                .Order(StringComparer.Ordinal))
        {
            ImmutableArtifactDefinition definition =
                _corpus.GetImmutable(memberId);
            DownloadedArtifact artifact =
                context.RequireArtifact(memberId);
            await using FileStream source = OpenArtifact(artifact);
            PackageRecord record = await manager.InstallAsync(
                    new PackageReference(
                        definition.Name,
                        definition.Version),
                    source,
                    new PackageSourceInstallOptions
                    {
                        ExpectedSha256 = definition.Sha256
                    },
                    cancellationToken)
                .ConfigureAwait(false);
            QualificationAssert.Equal(
                memberId,
                record.Reference.FhirDirective,
                "Direct downstream installation returned the wrong identity.");
        }

        IReadOnlyList<PackageRecord> records =
            await manager.ListCachedAsync(
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);
        string[] installed = records
            .Select(record => record.Reference.FhirDirective)
            .Order(StringComparer.Ordinal)
            .ToArray();
        AssertExactReferences(
            _corpus.DownstreamClosure.RequiredMembers,
            installed,
            "Direct downstream corpus");

        context.Detail(
            "directMembers",
            installed.Length);
    }

    private async Task InstallMutableAliasesAsync(
        QualificationCaseContext context,
        CancellationToken cancellationToken)
    {
        foreach (MutableArtifactDefinition definition in
            _corpus.MutableArtifacts.OrderBy(
                artifact => artifact.Id,
                StringComparer.Ordinal))
        {
            DownloadedArtifact artifact =
                context.RequireArtifact(definition.Id);
            string cachePath = context.CreateEmptyCache(
                $"stream-{definition.Id}");
            using FhirPackageManager manager = CreateManager(cachePath);
            await using FileStream source = OpenArtifact(artifact);
            PackageReference expected = new(
                definition.Name,
                definition.Selector);
            PackageRecord record = await manager.InstallAsync(
                    expected,
                    source,
                    options: null,
                    cancellationToken)
                .ConfigureAwait(false);
            QualificationAssert.Equal(
                expected,
                record.Reference,
                $"Mutable alias '{definition.Id}' returned the wrong display identity.");
            QualificationAssert.Equal(
                definition.Name,
                record.Manifest.Name,
                $"Mutable alias '{definition.Id}' manifest name mismatch.");
            QualificationAssert.True(
                !string.IsNullOrWhiteSpace(record.Manifest.Version),
                $"Mutable alias '{definition.Id}' manifest version was empty.");
            QualificationArtifactResult result =
                _artifactResults[definition.Id];
            result.ManifestName = record.Manifest.Name;
            result.ManifestVersion = record.Manifest.Version;
            result.ManifestDate = record.Manifest.Date;
            QualificationAssert.True(
                result.PublicationUtc is not null
                    || result.ManifestDate is not null,
                $"Mutable alias '{definition.Id}' had no publication metadata.");
            context.Detail(
                $"{definition.Id}.streamManifestVersion",
                record.Manifest.Version);

            string directiveCache = context.CreateEmptyCache(
                $"directive-{definition.Id}");
            using FhirPackageManager directiveManager =
                CreateManager(directiveCache);
            QualificationProgress directiveProgress = new();
            PackageRecord? directiveRecord =
                await directiveManager.InstallAsync(
                        expected.FhirDirective,
                        new InstallOptions
                        {
                            Progress = directiveProgress
                        },
                        cancellationToken)
                    .ConfigureAwait(false);
            QualificationAssert.True(
                directiveRecord is not null,
                $"Mutable directive '{definition.Id}' did not resolve.");
            QualificationAssert.Equal(
                expected,
                directiveRecord!.Reference,
                $"Mutable directive '{definition.Id}' returned the wrong alias identity.");
            QualificationAssert.Equal(
                definition.Name,
                directiveRecord.Manifest.Name,
                $"Mutable directive '{definition.Id}' returned the wrong manifest name.");
            QualificationAssert.True(
                directiveProgress.Phases.Contains(
                    PackageProgressPhase.Resolving)
                && directiveProgress.Phases.Contains(
                    PackageProgressPhase.Downloading),
                $"Mutable directive '{definition.Id}' did not exercise CI resolution and URI acquisition.");
            context.Detail(
                $"{definition.Id}.directiveManifestVersion",
                directiveRecord.Manifest.Version);
        }
    }

    private async Task InstallDevAsync(
        QualificationCaseContext context,
        CancellationToken cancellationToken)
    {
        LocalFixtureDefinition definition = _corpus.LocalFixture;
        DownloadedArtifact artifact =
            context.RequireArtifact(definition.Id);
        string cachePath = context.CreateEmptyCache("installed");
        using (FhirPackageManager manager = CreateManager(cachePath))
        {
            await using FileStream source = OpenArtifact(artifact);
            PackageRecord record = await manager.InstallAsync(
                    new PackageReference(
                        definition.Name,
                        definition.Selector),
                    source,
                    new PackageSourceInstallOptions
                    {
                        ExpectedSha256 = definition.Sha256
                    },
                    cancellationToken)
                .ConfigureAwait(false);
            QualificationAssert.Equal(
                definition.ManifestVersion,
                record.Manifest.Version,
                "The dev fixture manifest version was not preserved.");
            PackageRecord? cached = await manager.InstallAsync(
                    definition.Id,
                    new InstallOptions
                    {
                        OverwriteExisting = true
                    },
                    cancellationToken)
                .ConfigureAwait(false);
            QualificationAssert.True(
                cached is not null,
                "Installed dev package was not returned from local cache.");
        }

        string missingCache = context.CreateEmptyCache("missing");
        using (FhirPackageManager manager = CreateManager(missingCache))
        {
            PackageRecord? missing = await manager.InstallAsync(
                    "local.qualification.missing#dev",
                    options: null,
                    cancellationToken)
                .ConfigureAwait(false);
            QualificationAssert.True(
                missing is null,
                "Missing dev package unexpectedly resolved remotely.");
        }

        QualificationArtifactResult result =
            _artifactResults[definition.Id];
        result.ManifestName = definition.Name;
        result.ManifestVersion = definition.ManifestVersion;
        context.Detail("devHash", artifact.ActualSha256);
    }

    private async Task VerifyCancellationAsync(
        QualificationCaseContext context,
        CancellationToken cancellationToken)
    {
        string cachePath = context.CreateEmptyCache();
        using FhirPackageManager manager = CreateManager(cachePath);
        await using BlockingReadStream source = new();
        using CancellationTokenSource cancellationSource =
            CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken);
        cancellationSource.CancelAfter(
            TimeSpan.FromMilliseconds(100));
        bool cancelled = false;
        try
        {
            await manager.InstallAsync(
                    new PackageReference(
                        "local.qualification.cancel",
                        "1.0.0"),
                    source,
                    options: null,
                    cancellationSource.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
            when (cancellationSource.IsCancellationRequested)
        {
            cancelled = true;
        }

        QualificationAssert.True(
            cancelled,
            "Caller cancellation was not preserved.");
        QualificationAssert.True(
            !source.WasDisposed,
            "Manager disposed the caller-owned cancellation stream.");
        IReadOnlyList<PackageRecord> records =
            await manager.ListCachedAsync(
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);
        QualificationAssert.Equal(
            0,
            records.Count,
            "Cancelled installation left a visible package.");
        context.Detail("cancelled", true);
    }

    private async Task VerifyCorruptCacheAsync(
        QualificationCaseContext context,
        CancellationToken cancellationToken)
    {
        LocalFixtureDefinition definition = _corpus.LocalFixture;
        PackageReference reference = new(
            definition.Name,
            definition.ManifestVersion);
        string strictCache = context.CreateEmptyCache("strict");
        CreateCorruptTarget(strictCache, reference);
        using (FhirPackageManager manager = CreateManager(
            strictCache,
            CorruptCacheBehavior.Strict))
        {
            FailOnReadStream source = new();
            PackageInstallException exception;
            try
            {
                await manager.InstallAsync(
                        reference,
                        source,
                        new PackageSourceInstallOptions
                        {
                            CorruptCacheBehavior =
                                CorruptCacheBehavior.Strict
                        },
                        cancellationToken)
                    .ConfigureAwait(false);
                throw new QualificationInvariantException(
                    "Strict corruption policy accepted a corrupt cache.");
            }
            catch (PackageInstallException caught)
            {
                exception = caught;
            }

            QualificationAssert.Equal(
                PackageInstallErrorCode.CorruptCache,
                exception.ErrorCode,
                "Strict corruption used the wrong error code.");
            QualificationAssert.Equal(
                0,
                source.ReadCount,
                "Strict corruption consumed replacement source bytes.");
        }

        string repairCache = context.CreateEmptyCache("repair");
        CreateCorruptTarget(repairCache, reference);
        using (FhirPackageManager manager = CreateManager(repairCache))
        {
            DownloadedArtifact artifact =
                context.RequireArtifact(definition.Id);
            await using FileStream source = OpenArtifact(artifact);
            PackageRecord record = await manager.InstallAsync(
                    reference,
                    source,
                    new PackageSourceInstallOptions
                    {
                        CorruptCacheBehavior =
                            CorruptCacheBehavior.Repair,
                        ExpectedSha256 = definition.Sha256
                    },
                    cancellationToken)
                .ConfigureAwait(false);
            QualificationAssert.Equal(
                definition.ManifestVersion,
                record.Manifest.Version,
                "Corrupt cache repair installed the wrong manifest.");
        }

        context.Detail(
            "strictError",
            PackageInstallErrorCode.CorruptCache);
        context.Detail("repair", "valid");
    }

    private async Task VerifyInProcessConcurrencyAsync(
        QualificationCaseContext context,
        CancellationToken cancellationToken)
    {
        await VerifySameIdentityConcurrencyAsync(
                context,
                cancellationToken)
            .ConfigureAwait(false);
        await VerifyDifferentIdentityConcurrencyAsync(
                context,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task VerifySameIdentityConcurrencyAsync(
        QualificationCaseContext context,
        CancellationToken cancellationToken)
    {
        string cachePath =
            context.CreateEmptyCache("same-identity");
        using FhirPackageManager manager = CreateManager(cachePath);
        byte[] archive = DeterministicPackageArchive.Create(
            "local.qualification.same",
            "1.0.0",
            "same");
        TaskCompletionSource release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        await using CoordinatedReadStream owner = new(
            new MemoryStream(archive, writable: false),
            release.Task);
        await using CountingReadStream waiter = new(
            new MemoryStream(archive, writable: false));
        QualificationProgress waiterProgress = new();
        PackageReference reference = new(
            "local.qualification.same",
            "1.0.0");
        Task<PackageRecord> ownerTask = manager.InstallAsync(
            reference,
            owner,
            options: null,
            cancellationToken);
        try
        {
            await owner.Started.WaitAsync(
                    TimeSpan.FromSeconds(10),
                    cancellationToken)
                .ConfigureAwait(false);
            Task<PackageRecord> waiterTask = manager.InstallAsync(
                reference,
                waiter,
                new PackageSourceInstallOptions
                {
                    Progress = waiterProgress
                },
                cancellationToken);
            await waiterProgress.WaitingForLock.WaitAsync(
                    TimeSpan.FromSeconds(10),
                    cancellationToken)
                .ConfigureAwait(false);
            release.TrySetResult();
            PackageRecord[] records = await Task.WhenAll(
                    ownerTask,
                    waiterTask)
                .ConfigureAwait(false);
            QualificationAssert.Equal(
                2,
                records.Length,
                "Same-identity concurrency did not return both callers.");
        }
        finally
        {
            release.TrySetResult();
        }

        QualificationAssert.True(
            owner.BytesRead > 0,
            "Same-identity owner did not consume its source.");
        QualificationAssert.Equal(
            0L,
            waiter.BytesRead,
            "Same-identity waiter consumed duplicate source bytes.");
        context.Detail(
            "sameIdentityWaiterBytes",
            waiter.BytesRead);
    }

    private async Task VerifyDifferentIdentityConcurrencyAsync(
        QualificationCaseContext context,
        CancellationToken cancellationToken)
    {
        string cachePath =
            context.CreateEmptyCache("different-identities");
        using FhirPackageManager manager = CreateManager(cachePath);
        byte[] firstArchive = DeterministicPackageArchive.Create(
            "local.qualification.first",
            "1.0.0",
            "first");
        byte[] secondArchive = DeterministicPackageArchive.Create(
            "local.qualification.second",
            "1.0.0",
            "second");
        TaskCompletionSource release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        await using CoordinatedReadStream first = new(
            new MemoryStream(firstArchive, writable: false),
            release.Task);
        await using CoordinatedReadStream second = new(
            new MemoryStream(secondArchive, writable: false),
            release.Task);
        Task<PackageRecord> firstTask = manager.InstallAsync(
            new PackageReference(
                "local.qualification.first",
                "1.0.0"),
            first,
            options: null,
            cancellationToken);
        Task<PackageRecord> secondTask = manager.InstallAsync(
            new PackageReference(
                "local.qualification.second",
                "1.0.0"),
            second,
            options: null,
            cancellationToken);
        try
        {
            await Task.WhenAll(
                    first.Started,
                    second.Started)
                .WaitAsync(
                    TimeSpan.FromSeconds(10),
                    cancellationToken)
                .ConfigureAwait(false);
            release.TrySetResult();
            await Task.WhenAll(firstTask, secondTask)
                .ConfigureAwait(false);
        }
        finally
        {
            release.TrySetResult();
        }

        QualificationAssert.True(
            first.BytesRead > 0 && second.BytesRead > 0,
            "Different identities did not acquire concurrently.");
        context.Detail("differentIdentityOverlap", true);
    }

    private async Task VerifyProcessHostAsync(
        QualificationCaseContext context,
        CancellationToken cancellationToken)
    {
        ProcessQualificationResult result =
            await ProcessQualification.RunAsync(
                    ResolveProcessHostPath(),
                    context.CreateWorkspace("process"),
                    context.CreateEmptyCache("process-cache-root"),
                    cancellationToken)
                .ConfigureAwait(false);
        QualificationAssert.True(
            result.SameIdentityWinnerBytes > 0,
            "Process owner did not read its source.");
        QualificationAssert.Equal(
            0L,
            result.SameIdentityWaiterBytes,
            "Process waiter consumed duplicate source bytes.");
        QualificationAssert.True(
            result.DifferentIdentityOverlap,
            "Different process identities did not overlap acquisition.");
        context.Detail(
            "sameIdentityWaiterBytes",
            result.SameIdentityWaiterBytes);
        context.Detail(
            "differentIdentityOverlap",
            result.DifferentIdentityOverlap);
    }

    private FhirPackageManager CreateManager(
        string cachePath,
        CorruptCacheBehavior corruptCacheBehavior =
            CorruptCacheBehavior.Repair) =>
        new(new FhirPackageManagerOptions
        {
            CachePath = cachePath,
            CorruptCacheBehavior = corruptCacheBehavior,
            HttpTimeout = _arguments.HttpTimeout,
            MaxParallelRegistryQueries = 3,
            ResourceCacheSize = 0
        });

    private FhirPackageManager CreateSecondaryRegistryManager(
        string cachePath,
        bool verifyChecksums = true,
        int maxParallelRegistryQueries = 3)
    {
        FhirPackageManagerOptions options = new()
        {
            CachePath = cachePath,
            IncludeCiBuilds = false,
            IncludeHl7WebsiteFallback = false,
            HttpTimeout = _arguments.HttpTimeout,
            MaxParallelRegistryQueries =
                maxParallelRegistryQueries,
            ResourceCacheSize = 0,
            VerifyChecksums = verifyChecksums
        };
        options.Registries.Add(RegistryEndpoint.FhirSecondary);
        return new FhirPackageManager(options);
    }

    private static FileStream OpenArtifact(
        DownloadedArtifact artifact) =>
        new(
            artifact.FilePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 65_536,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

    private static void AssertExactReferences(
        IEnumerable<string> expected,
        IEnumerable<string> actual,
        string description)
    {
        string[] expectedValues = expected
            .Order(StringComparer.Ordinal)
            .ToArray();
        string[] actualValues = actual
            .Order(StringComparer.Ordinal)
            .ToArray();
        if (expectedValues.SequenceEqual(
                actualValues,
                StringComparer.Ordinal))
        {
            return;
        }

        string missing = string.Join(
            ",",
            expectedValues.Except(
                actualValues,
                StringComparer.Ordinal));
        string unexpected = string.Join(
            ",",
            actualValues.Except(
                expectedValues,
                StringComparer.Ordinal));
        throw new QualificationInvariantException(
            $"{description} differed from the pinned set; missing=[{missing}], unexpected=[{unexpected}].");
    }

    private void CreateCorruptTarget(
        string cachePath,
        PackageReference reference)
    {
        PackageCacheKey cacheKey = PackageCacheKey.Create(reference);
        string contentPath = Path.Combine(
            cacheKey.GetPackageDirectoryPath(cachePath),
            "package");
        Directory.CreateDirectory(contentPath);
        File.WriteAllText(
            Path.Combine(contentPath, "package.json"),
            "{invalid-json");
    }

    private string ResolveProcessHostPath()
    {
        if (_arguments.ProcessHostPath is string configured)
        {
            QualificationAssert.True(
                File.Exists(configured),
                "The configured process host does not exist.");
            return configured;
        }

        DirectoryInfo frameworkDirectory =
            new(AppContext.BaseDirectory);
        string framework = frameworkDirectory.Name;
        string configuration =
            frameworkDirectory.Parent?.Name
            ?? "Release";
        string candidate = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..",
            "..",
            "..",
            "..",
            "FhirPkg.ProcessTestHost",
            "bin",
            configuration,
            framework,
            "FhirPkg.ProcessTestHost.dll"));
        QualificationAssert.True(
            File.Exists(candidate),
            "The process test host was not built.");
        return candidate;
    }

    private QualificationFailure CreateFailure(Exception exception)
    {
        if (exception is PackageInstallException installException)
        {
            return new QualificationFailure
            {
                Code = installException.ErrorCode.ToString(),
                Stage = installException.Stage.ToString(),
                ExceptionType = exception.GetType().Name,
                Message = _sanitizer.Sanitize(exception.Message)
            };
        }

        return new QualificationFailure
        {
            ExceptionType = exception.GetType().Name,
            Message = _sanitizer.Sanitize(exception.Message)
        };
    }

    internal sealed class QualificationCaseContext
    {
        private readonly string _id;
        private readonly QualificationRunner _runner;
        private readonly QualificationCaseResult _result;

        internal QualificationCaseContext(
            string id,
            QualificationRunner runner,
            QualificationCaseResult result)
        {
            _id = id;
            _runner = runner;
            _result = result;
        }

        internal DownloadedArtifact RequireArtifact(string id)
        {
            if (_runner._artifacts.TryGetValue(
                id,
                out DownloadedArtifact? artifact))
            {
                return artifact;
            }

            throw new QualificationInvariantException(
                $"Required artifact '{id}' was unavailable.");
        }

        internal string CreateEmptyCache(string? suffix = null)
        {
            string name = suffix is null
                ? _id
                : $"{_id}-{suffix}";
            string path = Path.Combine(
                _runner._arguments.CacheRoot,
                "caches",
                name);
            if (Directory.Exists(path)
                && Directory.EnumerateFileSystemEntries(path).Any())
            {
                throw new QualificationInvariantException(
                    $"Qualification child cache '{name}' was not empty.");
            }

            Directory.CreateDirectory(path);
            return path;
        }

        internal string CreateWorkspace(string suffix)
        {
            string path = Path.Combine(
                _runner._workspaceRoot,
                $"{_id}-{suffix}");
            if (Directory.Exists(path))
            {
                throw new QualificationInvariantException(
                    $"Qualification workspace '{suffix}' already existed.");
            }

            Directory.CreateDirectory(path);
            return path;
        }

        internal void Detail(string name, object value)
        {
            _result.Details.Add(new QualificationDetail
            {
                Name = name,
                Value = value switch
                {
                    bool boolean => boolean ? "true" : "false",
                    IFormattable formattable =>
                        formattable.ToString(
                            null,
                            System.Globalization.CultureInfo.InvariantCulture),
                    _ => value.ToString() ?? string.Empty
                }
            });
        }
    }
}
