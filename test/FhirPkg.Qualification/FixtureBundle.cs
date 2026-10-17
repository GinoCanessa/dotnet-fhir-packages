// Copyright (c) Gino Canessa. Licensed under the MIT License.

using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using FhirPkg.Qualification.Models;

namespace FhirPkg.Qualification;

internal static class FixtureBundle
{
    internal const string ManifestFileName = "bundle-manifest.json";

    internal const string BlobDirectoryName = "blobs";

    internal const string ArtifactNamePrefix = "fixture-bundle-v1-";

    internal const int SchemaVersion = 1;

    internal const string ImmutableKind = "immutable";

    internal const string MutableKind = "mutable";

    internal const string FreezeCaseId = "bundle-freeze";

    internal const string VerifyCaseId = "bundle-verify";

    internal const string DeriveCaseId = "bundle-derive";

    internal const string BundleSha256DetailName = "bundleSha256";

    internal const string CorpusSha256DetailName = "corpusSha256";

    internal const string ApprovalsDetailName = "approvals";

    internal const string NoApprovals = "none";

    internal const string ApprovalDetailPrefix = "approval.";

    internal const string ApprovalArrow = "\u2192";

    internal const string BaseBundleSha256DetailName = "baseBundleSha256";

    internal const string DerivedBundleSha256DetailName =
        "derivedBundleSha256";

    internal const string BlockedArtifactsDetailName =
        "blockedArtifacts";

    internal static string GetBlobFileName(string sha256) =>
        $"{sha256}.tgz";

    internal static string GetApprovalDetailName(string artifact) =>
        $"{ApprovalDetailPrefix}{artifact}";

    // Renders "<pinned> → <approved> by <approvedBy> at <approvalCommit>".
    internal static string DescribeApproval(FixtureBundleApproval approval) =>
        $"{approval.PinnedSha256} {ApprovalArrow} {approval.ApprovedSha256} by {approval.ApprovedBy} at {approval.ApprovalCommit}";

    internal static bool IsSha256(string? value) =>
        IsLowercaseHex(value, 64);

    internal static bool IsCommit(string? value) =>
        IsLowercaseHex(value, 40);

    private static bool IsLowercaseHex(
        string? value,
        int length) =>
        value is not null
        && value.Length == length
        && value.All(character =>
            character is (>= '0' and <= '9')
                or (>= 'a' and <= 'f'));

    internal static string ComputeSha256(ReadOnlySpan<byte> content) =>
        Convert.ToHexString(SHA256.HashData(content))
            .ToLowerInvariant();

    internal static async Task<string> ComputeFileSha256Async(
        string path,
        CancellationToken cancellationToken)
    {
        await using FileStream stream = new(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 65_536,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        byte[] hash = await SHA256.HashDataAsync(
                stream,
                cancellationToken)
            .ConfigureAwait(false);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record FixtureBundleManifest
{
    public required int SchemaVersion { get; init; }

    public required string CorpusSha256 { get; init; }

    public required string CorpusHashAlgorithm { get; init; }

    public required DateTimeOffset RecordedUtc { get; init; }

    public FixtureBundleRecorder? RecordedBy { get; init; }

    public string? BaseBundleSha256 { get; init; }

    public required IReadOnlyList<FixtureBundleArtifact> Artifacts { get; init; }

    public IReadOnlyList<FixtureBundleApproval>? Approvals { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record FixtureBundleRecorder
{
    public required string Repository { get; init; }

    public required string RunId { get; init; }

    public required string Commit { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record FixtureBundleArtifact
{
    public required string Id { get; init; }

    public required string Kind { get; init; }

    public required string Uri { get; init; }

    public required string Sha256 { get; init; }

    public required long CompressedBytes { get; init; }

    public required long ExpandedBytes { get; init; }

    public required long LargestEntryBytes { get; init; }

    public required int EntryCount { get; init; }

    public DateTimeOffset? PublicationUtc { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record FixtureBundleApproval
{
    public required string Artifact { get; init; }

    public required string PinnedSha256 { get; init; }

    public required string ApprovedSha256 { get; init; }

    public required string Release { get; init; }

    public required string ApprovedBy { get; init; }

    public required string Reason { get; init; }

    public required string ApprovalCommit { get; init; }

    public DateTimeOffset? ObservedLastModified { get; init; }
}

internal sealed record VerifiedFixtureBundle(
    string Digest,
    FixtureBundleManifest Manifest,
    IReadOnlyDictionary<string, string> BlobPaths,
    QualificationCorpus EffectiveCorpus)
{
    internal IReadOnlyList<FixtureBundleApproval> Approvals =>
        Manifest.Approvals ?? [];

    internal FixtureBundleArtifact GetArtifact(string id) =>
        Manifest.Artifacts.FirstOrDefault(
            artifact => string.Equals(
                artifact.Id,
                id,
                StringComparison.Ordinal))
        ?? throw new QualificationInvariantException(
            $"The verified bundle lists no artifact '{id}'.");

    internal string GetBlobPath(string id) =>
        BlobPaths.TryGetValue(id, out string? path)
            ? path
            : throw new QualificationInvariantException(
                $"The verified bundle holds no blob for artifact '{id}'.");
}

internal sealed class FixtureBundleException(
    string code,
    string message) :
    Exception(message)
{
    internal string Code { get; } = code;
}

internal static class FixtureBundleWriter
{
    internal static Task<string> WriteFrozenAsync(
        string outputDirectory,
        QualificationCorpus corpus,
        string corpusSha256,
        IReadOnlyDictionary<string, DownloadedArtifact> artifacts,
        CancellationToken cancellationToken) =>
        WriteFrozenAsync(
            outputDirectory,
            corpus,
            corpusSha256,
            artifacts,
            Environment.GetEnvironmentVariable,
            cancellationToken);

    internal static async Task<string> WriteFrozenAsync(
        string outputDirectory,
        QualificationCorpus corpus,
        string corpusSha256,
        IReadOnlyDictionary<string, DownloadedArtifact> artifacts,
        Func<string, string?> getEnvironmentVariable,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        ArgumentNullException.ThrowIfNull(corpus);
        ArgumentException.ThrowIfNullOrWhiteSpace(corpusSha256);
        ArgumentNullException.ThrowIfNull(artifacts);
        ArgumentNullException.ThrowIfNull(getEnvironmentVariable);

        string root = Path.GetFullPath(outputDirectory);
        bool createdRoot = PrepareOutputDirectory(root, Blocked);
        try
        {
            string blobRoot = Path.Combine(
                root,
                FixtureBundle.BlobDirectoryName);
            Directory.CreateDirectory(blobRoot);
            HashSet<string> writtenBlobs = new(StringComparer.Ordinal);
            List<FixtureBundleArtifact> entries = [];
            foreach (ImmutableArtifactDefinition definition in
                corpus.ImmutableArtifacts)
            {
                entries.Add(await FreezeArtifactAsync(
                        blobRoot,
                        writtenBlobs,
                        FixtureBundle.ImmutableKind,
                        definition.Id,
                        definition.Uri,
                        definition.Sha256,
                        artifacts,
                        cancellationToken)
                    .ConfigureAwait(false));
            }

            foreach (MutableArtifactDefinition definition in
                corpus.MutableArtifacts)
            {
                entries.Add(await FreezeArtifactAsync(
                        blobRoot,
                        writtenBlobs,
                        FixtureBundle.MutableKind,
                        definition.Id,
                        definition.Uri,
                        pinnedSha256: null,
                        artifacts,
                        cancellationToken)
                    .ConfigureAwait(false));
            }

            FixtureBundleManifest manifest = new()
            {
                SchemaVersion = FixtureBundle.SchemaVersion,
                CorpusSha256 = corpusSha256,
                CorpusHashAlgorithm = QualificationCorpusHash.Algorithm,
                RecordedUtc = DateTimeOffset.UtcNow,
                RecordedBy = CreateRecorder(getEnvironmentVariable),
                Artifacts = entries
                    .OrderBy(
                        entry => entry.Kind,
                        StringComparer.Ordinal)
                    .ThenBy(
                        entry => entry.Id,
                        StringComparer.Ordinal)
                    .ToArray()
            };
            byte[] manifestBytes = JsonSerializer.SerializeToUtf8Bytes(
                manifest,
                QualificationJson.SerializerOptions);
            await File.WriteAllBytesAsync(
                    Path.Combine(root, FixtureBundle.ManifestFileName),
                    manifestBytes,
                    cancellationToken)
                .ConfigureAwait(false);
            return FixtureBundle.ComputeSha256(manifestBytes);
        }
        catch
        {
            DeleteWrittenContent(root, createdRoot);
            throw;
        }
    }

    internal static Task<string> WriteDerivedAsync(
        string outputDirectory,
        VerifiedFixtureBundle baseBundle,
        QualificationCorpus corpus,
        string corpusSha256,
        FixtureApproval approval,
        string approvalCommit,
        string releaseTag,
        IReadOnlyDictionary<string, DownloadedArtifact> approvedArtifacts,
        CancellationToken cancellationToken) =>
        WriteDerivedAsync(
            outputDirectory,
            baseBundle,
            corpus,
            corpusSha256,
            approval,
            approvalCommit,
            releaseTag,
            approvedArtifacts,
            Environment.GetEnvironmentVariable,
            cancellationToken);

    internal static async Task<string> WriteDerivedAsync(
        string outputDirectory,
        VerifiedFixtureBundle baseBundle,
        QualificationCorpus corpus,
        string corpusSha256,
        FixtureApproval approval,
        string approvalCommit,
        string releaseTag,
        IReadOnlyDictionary<string, DownloadedArtifact> approvedArtifacts,
        Func<string, string?> getEnvironmentVariable,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        ArgumentNullException.ThrowIfNull(baseBundle);
        ArgumentNullException.ThrowIfNull(corpus);
        ArgumentException.ThrowIfNullOrWhiteSpace(corpusSha256);
        ArgumentNullException.ThrowIfNull(approval);
        ArgumentException.ThrowIfNullOrWhiteSpace(approvalCommit);
        ArgumentException.ThrowIfNullOrWhiteSpace(releaseTag);
        ArgumentNullException.ThrowIfNull(approvedArtifacts);
        ArgumentNullException.ThrowIfNull(getEnvironmentVariable);

        IReadOnlyList<FixtureBundleApproval> approvals =
            approval.ToBundleApprovals(approvalCommit);
        string root = Path.GetFullPath(outputDirectory);
        bool createdRoot = PrepareOutputDirectory(
            root,
            static message => new QualificationInvariantException(message));
        try
        {
            Dictionary<string, FixtureBundleApproval> approvedById =
                new(StringComparer.Ordinal);
            foreach (FixtureBundleApproval applied in approvals)
            {
                if (!approvedById.TryAdd(applied.Artifact, applied))
                {
                    throw new QualificationInvariantException(
                        $"The approval lists artifact '{applied.Artifact}' more than once.");
                }
            }

            // Identical bytes share one blob; a base blob that no entry
            // references any more is not carried into the derived bundle.
            List<FixtureBundleArtifact> entries = [];
            Dictionary<string, string> blobSources =
                new(StringComparer.Ordinal);
            int repointed = 0;
            foreach (FixtureBundleArtifact entry in
                baseBundle.Manifest.Artifacts)
            {
                if (!approvedById.TryGetValue(
                        entry.Id,
                        out FixtureBundleApproval? applied))
                {
                    entries.Add(entry);
                    blobSources.TryAdd(
                        entry.Sha256,
                        baseBundle.GetBlobPath(entry.Id));
                    continue;
                }

                DownloadedArtifact artifact = GetApprovedArtifact(
                    approvedArtifacts,
                    applied);
                entries.Add(entry with
                {
                    Sha256 = artifact.ActualSha256,
                    CompressedBytes = artifact.CompressedBytes,
                    ExpandedBytes = artifact.Metrics.ExpandedBytes,
                    LargestEntryBytes = artifact.Metrics.LargestEntryBytes,
                    EntryCount = artifact.Metrics.EntryCount,
                    PublicationUtc =
                        artifact.PublicationDate?.ToUniversalTime()
                });
                blobSources.TryAdd(
                    artifact.ActualSha256,
                    artifact.FilePath);
                repointed++;
            }

            if (repointed != approvedById.Count)
            {
                throw new QualificationInvariantException(
                    "The approval names an artifact the base bundle does not hold.");
            }

            string blobRoot = Path.Combine(
                root,
                FixtureBundle.BlobDirectoryName);
            Directory.CreateDirectory(blobRoot);
            foreach ((string sha256, string sourcePath) in blobSources)
            {
                File.Copy(
                    sourcePath,
                    Path.Combine(
                        blobRoot,
                        FixtureBundle.GetBlobFileName(sha256)),
                    overwrite: false);
            }

            FixtureBundleManifest manifest = new()
            {
                SchemaVersion = FixtureBundle.SchemaVersion,
                CorpusSha256 = corpusSha256,
                CorpusHashAlgorithm = QualificationCorpusHash.Algorithm,
                RecordedUtc = DateTimeOffset.UtcNow,
                RecordedBy = CreateRecorder(getEnvironmentVariable),
                BaseBundleSha256 = baseBundle.Digest,
                Artifacts = entries
                    .OrderBy(
                        entry => entry.Kind,
                        StringComparer.Ordinal)
                    .ThenBy(
                        entry => entry.Id,
                        StringComparer.Ordinal)
                    .ToArray(),
                Approvals = approvals
            };
            byte[] manifestBytes = JsonSerializer.SerializeToUtf8Bytes(
                manifest,
                QualificationJson.SerializerOptions);
            await File.WriteAllBytesAsync(
                    Path.Combine(root, FixtureBundle.ManifestFileName),
                    manifestBytes,
                    cancellationToken)
                .ConfigureAwait(false);
            string digest = FixtureBundle.ComputeSha256(manifestBytes);

            // Re-hashes every copied blob and re-checks every approval.
            await FixtureBundleVerifier.VerifyAsync(
                    root,
                    digest,
                    corpus,
                    corpusSha256,
                    releaseTag,
                    cancellationToken)
                .ConfigureAwait(false);
            return digest;
        }
        catch
        {
            DeleteWrittenContent(root, createdRoot);
            throw;
        }
    }

    private static DownloadedArtifact GetApprovedArtifact(
        IReadOnlyDictionary<string, DownloadedArtifact> approvedArtifacts,
        FixtureBundleApproval approval)
    {
        if (!approvedArtifacts.TryGetValue(
                approval.Artifact,
                out DownloadedArtifact? artifact))
        {
            throw new FixtureBundleException(
                QualificationFailureCodes.ApprovedBytesUnavailable,
                $"The approved bytes for {approval.Artifact} were not acquired, so no bundle was derived.");
        }

        if (!string.Equals(
                artifact.ActualSha256,
                approval.ApprovedSha256,
                StringComparison.Ordinal))
        {
            throw new FixtureBundleException(
                QualificationFailureCodes.ApprovedBytesUnavailable,
                $"upstream no longer serves the approved bytes for {approval.Artifact}: observed {artifact.ActualSha256}");
        }

        return artifact;
    }

    private static async Task<FixtureBundleArtifact> FreezeArtifactAsync(
        string blobRoot,
        HashSet<string> writtenBlobs,
        string kind,
        string id,
        string uri,
        string? pinnedSha256,
        IReadOnlyDictionary<string, DownloadedArtifact> artifacts,
        CancellationToken cancellationToken)
    {
        if (!artifacts.TryGetValue(
                id,
                out DownloadedArtifact? artifact))
        {
            throw Blocked(
                $"Artifact '{id}' was not acquired, so no bundle was frozen.");
        }

        if (!FixtureBundle.IsSha256(artifact.ActualSha256))
        {
            throw Blocked(
                $"Artifact '{id}' has no valid SHA-256, so no bundle was frozen.");
        }

        if (pinnedSha256 is not null
            && !string.Equals(
                pinnedSha256,
                artifact.ActualSha256,
                StringComparison.Ordinal))
        {
            throw Blocked(
                $"Artifact '{id}' SHA-256 {artifact.ActualSha256} does not match its pin {pinnedSha256}, so no bundle was frozen.");
        }

        if (writtenBlobs.Add(artifact.ActualSha256))
        {
            string blobPath = Path.Combine(
                blobRoot,
                FixtureBundle.GetBlobFileName(artifact.ActualSha256));
            File.Copy(
                artifact.FilePath,
                blobPath,
                overwrite: false);
            string copiedSha256 = await FixtureBundle
                .ComputeFileSha256Async(
                    blobPath,
                    cancellationToken)
                .ConfigureAwait(false);
            if (!string.Equals(
                    copiedSha256,
                    artifact.ActualSha256,
                    StringComparison.Ordinal))
            {
                throw Blocked(
                    $"Artifact '{id}' changed after it was verified: its bytes now hash to {copiedSha256}, so no bundle was frozen.");
            }
        }

        return new FixtureBundleArtifact
        {
            Id = id,
            Kind = kind,
            Uri = uri,
            Sha256 = artifact.ActualSha256,
            CompressedBytes = artifact.CompressedBytes,
            ExpandedBytes = artifact.Metrics.ExpandedBytes,
            LargestEntryBytes = artifact.Metrics.LargestEntryBytes,
            EntryCount = artifact.Metrics.EntryCount,
            PublicationUtc = artifact.PublicationDate?.ToUniversalTime()
        };
    }

    private static FixtureBundleRecorder? CreateRecorder(
        Func<string, string?> getEnvironmentVariable)
    {
        string? repository = getEnvironmentVariable("GITHUB_REPOSITORY");
        string? runId = getEnvironmentVariable("GITHUB_RUN_ID");
        string? commit = getEnvironmentVariable("GITHUB_SHA");
        if (string.IsNullOrWhiteSpace(repository)
            || string.IsNullOrWhiteSpace(runId)
            || string.IsNullOrWhiteSpace(commit))
        {
            return null;
        }

        return new FixtureBundleRecorder
        {
            Repository = repository,
            RunId = runId,
            Commit = commit
        };
    }

    private static bool PrepareOutputDirectory(
        string root,
        Func<string, Exception> createException)
    {
        if (File.Exists(root))
        {
            throw createException(
                "The bundle output path is a file; it must be absent or an empty directory.");
        }

        if (!Directory.Exists(root))
        {
            Directory.CreateDirectory(root);
            return true;
        }

        if (Directory.EnumerateFileSystemEntries(root).Any())
        {
            throw createException(
                "The bundle output directory must be absent or empty.");
        }

        return false;
    }

    private static void DeleteWrittenContent(
        string root,
        bool createdRoot)
    {
        try
        {
            if (!Directory.Exists(root))
                return;

            if (createdRoot)
            {
                Directory.Delete(root, recursive: true);
                return;
            }

            foreach (string entry in Directory.GetFileSystemEntries(root))
            {
                if (Directory.Exists(entry))
                    Directory.Delete(entry, recursive: true);
                else
                    File.Delete(entry);
            }
        }
        catch (IOException)
        {
            // Cleanup is best effort; the caller rethrows the original failure.
        }
        catch (UnauthorizedAccessException)
        {
            // Cleanup is best effort; the caller rethrows the original failure.
        }
    }

    private static FixtureBundleException Blocked(string message) =>
        new(QualificationFailureCodes.BundleFreezeBlocked, message);
}

internal static class FixtureBundleVerifier
{
    internal static Task<VerifiedFixtureBundle> VerifyAsync(
        string bundleDirectory,
        string expectedSha256,
        QualificationCorpus corpus,
        string corpusSha256,
        CancellationToken cancellationToken) =>
        VerifyAsync(
            bundleDirectory,
            expectedSha256,
            corpus,
            corpusSha256,
            releaseTag: null,
            cancellationToken);

    internal static async Task<VerifiedFixtureBundle> VerifyAsync(
        string bundleDirectory,
        string expectedSha256,
        QualificationCorpus corpus,
        string corpusSha256,
        string? releaseTag,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(bundleDirectory);
        ArgumentNullException.ThrowIfNull(expectedSha256);
        ArgumentNullException.ThrowIfNull(corpus);
        ArgumentNullException.ThrowIfNull(corpusSha256);

        string root = Path.GetFullPath(bundleDirectory);
        if (!Directory.Exists(root))
            throw Failed("The bundle directory does not exist.");

        string manifestPath = Path.Combine(
            root,
            FixtureBundle.ManifestFileName);
        if (!File.Exists(manifestPath))
        {
            throw Failed(
                $"The bundle has no {FixtureBundle.ManifestFileName}.");
        }

        byte[] manifestBytes = await File.ReadAllBytesAsync(
                manifestPath,
                cancellationToken)
            .ConfigureAwait(false);
        string digest = FixtureBundle.ComputeSha256(manifestBytes);
        if (!string.Equals(
                digest,
                expectedSha256,
                StringComparison.Ordinal))
        {
            throw Failed(
                $"The bundle manifest SHA-256 was {digest}, not the expected {expectedSha256}.");
        }

        FixtureBundleManifest manifest = ParseManifest(manifestBytes);
        if (manifest.SchemaVersion != FixtureBundle.SchemaVersion)
        {
            throw Failed(
                $"The bundle manifest declares schema version {manifest.SchemaVersion}; only version {FixtureBundle.SchemaVersion} is supported.");
        }

        if (!string.Equals(
                manifest.CorpusHashAlgorithm,
                QualificationCorpusHash.Algorithm,
                StringComparison.Ordinal))
        {
            throw Failed(
                $"The bundle manifest declares corpus hash algorithm '{manifest.CorpusHashAlgorithm}', not '{QualificationCorpusHash.Algorithm}'.");
        }

        if (!string.Equals(
                manifest.CorpusSha256,
                corpusSha256,
                StringComparison.Ordinal))
        {
            throw Failed(
                $"The bundle was frozen for corpus {manifest.CorpusSha256}, not for corpus {corpusSha256}.");
        }

        VerifyBundleEntries(root);
        IReadOnlyDictionary<string, string> approvedPins = VerifyApprovals(
            manifest,
            corpus,
            releaseTag);
        VerifyArtifacts(manifest, corpus, approvedPins);
        IReadOnlyDictionary<string, string> blobPaths =
            await VerifyBlobsAsync(
                    root,
                    manifest,
                    cancellationToken)
                .ConfigureAwait(false);
        return new VerifiedFixtureBundle(
            digest,
            manifest,
            blobPaths,
            CreateEffectiveCorpus(corpus, approvedPins));
    }

    private static FixtureBundleManifest ParseManifest(
        byte[] manifestBytes)
    {
        FixtureBundleManifest? manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<FixtureBundleManifest>(
                manifestBytes,
                QualificationJson.SerializerOptions);
        }
        catch (JsonException exception)
        {
            throw Failed(
                $"The bundle manifest is not a valid schema version {FixtureBundle.SchemaVersion} manifest: {exception.Message}");
        }

        if (manifest is null
            || manifest.Artifacts is null
            || manifest.Artifacts.Any(artifact => artifact is null)
            || manifest.Approvals?.Any(approval => approval is null) == true)
        {
            throw Failed("The bundle manifest is incomplete.");
        }

        return manifest;
    }

    // Returns the approved SHA-256 of each approved artifact, by id.
    private static IReadOnlyDictionary<string, string> VerifyApprovals(
        FixtureBundleManifest manifest,
        QualificationCorpus corpus,
        string? releaseTag)
    {
        Dictionary<string, string> approvedPins =
            new(StringComparer.Ordinal);
        if (manifest.Approvals is null
            && manifest.BaseBundleSha256 is null)
        {
            return approvedPins;
        }

        // Approvals are scoped to one release, so a bundle carrying them is
        // accepted only for the release it was derived for.
        if (releaseTag is null)
        {
            throw Failed(
                "The bundle manifest carries approval data, which this harness does not accept.");
        }

        if (manifest.BaseBundleSha256 is null)
            throw Failed("The bundle manifest lists approvals without a base bundle.");

        if (manifest.Approvals is null || manifest.Approvals.Count == 0)
            throw Failed("The bundle manifest records a base bundle but no approvals.");

        if (!FixtureBundle.IsSha256(manifest.BaseBundleSha256))
        {
            throw Failed(
                "The bundle manifest records a base bundle SHA-256 that is not 64 lowercase hexadecimal characters.");
        }

        foreach (FixtureBundleApproval approval in manifest.Approvals)
        {
            try
            {
                FixtureApproval applied =
                    FixtureApproval.FromBundleApproval(approval);
                applied.ValidateStructure();
                applied.ValidateAgainst(corpus, releaseTag);
            }
            catch (FixtureBundleException exception)
                when (exception.Code
                    == QualificationFailureCodes.ApprovalInvalid)
            {
                throw Failed(
                    $"The bundle's approval for '{approval.Artifact}' is invalid: {exception.Message}");
            }

            if (!FixtureBundle.IsCommit(approval.ApprovalCommit))
            {
                throw Failed(
                    $"The bundle's approval for '{approval.Artifact}' records an approval commit that is not 40 lowercase hexadecimal characters.");
            }

            if (!approvedPins.TryAdd(
                    approval.Artifact,
                    approval.ApprovedSha256))
            {
                throw Failed(
                    $"The bundle approves artifact '{approval.Artifact}' more than once.");
            }
        }

        return approvedPins;
    }

    private static QualificationCorpus CreateEffectiveCorpus(
        QualificationCorpus corpus,
        IReadOnlyDictionary<string, string> approvedPins)
    {
        if (approvedPins.Count == 0)
            return corpus;

        return corpus with
        {
            ImmutableArtifacts = corpus.ImmutableArtifacts
                .Select(definition => approvedPins.TryGetValue(
                        definition.Id,
                        out string? approvedSha256)
                    ? definition with { Sha256 = approvedSha256 }
                    : definition)
                .ToArray()
        };
    }

    private static void VerifyBundleEntries(string root)
    {
        bool hasBlobDirectory = false;
        foreach (FileSystemInfo entry in
            new DirectoryInfo(root).EnumerateFileSystemInfos())
        {
            if (entry is FileInfo
                && string.Equals(
                    entry.Name,
                    FixtureBundle.ManifestFileName,
                    StringComparison.Ordinal))
            {
                continue;
            }

            if (entry is DirectoryInfo
                && string.Equals(
                    entry.Name,
                    FixtureBundle.BlobDirectoryName,
                    StringComparison.Ordinal))
            {
                hasBlobDirectory = true;
                continue;
            }

            throw Failed(
                $"The bundle contains the unexpected entry '{entry.Name}'; only {FixtureBundle.ManifestFileName} and {FixtureBundle.BlobDirectoryName}/ are allowed.");
        }

        if (!hasBlobDirectory)
        {
            throw Failed(
                $"The bundle has no {FixtureBundle.BlobDirectoryName}/ directory.");
        }
    }

    private static void VerifyArtifacts(
        FixtureBundleManifest manifest,
        QualificationCorpus corpus,
        IReadOnlyDictionary<string, string> approvedPins)
    {
        Dictionary<string, FixtureBundleArtifact> immutable =
            new(StringComparer.Ordinal);
        Dictionary<string, FixtureBundleArtifact> mutable =
            new(StringComparer.Ordinal);
        HashSet<string> ids = new(StringComparer.Ordinal);
        foreach (FixtureBundleArtifact artifact in manifest.Artifacts)
        {
            if (string.IsNullOrEmpty(artifact.Id))
                throw Failed("The bundle lists an artifact without an id.");

            if (!ids.Add(artifact.Id))
            {
                throw Failed(
                    $"The bundle lists artifact '{artifact.Id}' more than once.");
            }

            if (!FixtureBundle.IsSha256(artifact.Sha256))
            {
                throw Failed(
                    $"Bundle artifact '{artifact.Id}' does not record a valid SHA-256.");
            }

            switch (artifact.Kind)
            {
                case FixtureBundle.ImmutableKind:
                    immutable.Add(artifact.Id, artifact);
                    break;
                case FixtureBundle.MutableKind:
                    mutable.Add(artifact.Id, artifact);
                    break;
                default:
                    throw Failed(
                        $"Bundle artifact '{artifact.Id}' has the unsupported kind '{artifact.Kind}'.");
            }
        }

        VerifyIds(
            FixtureBundle.ImmutableKind,
            immutable.Keys,
            corpus.ImmutableArtifacts.Select(definition => definition.Id));
        foreach (ImmutableArtifactDefinition definition in
            corpus.ImmutableArtifacts)
        {
            FixtureBundleArtifact entry = immutable[definition.Id];
            VerifyUri(entry, definition.Uri);
            if (approvedPins.TryGetValue(
                    definition.Id,
                    out string? approvedSha256))
            {
                if (!string.Equals(
                        entry.Sha256,
                        approvedSha256,
                        StringComparison.Ordinal))
                {
                    throw Failed(
                        $"Bundle artifact '{definition.Id}' records SHA-256 {entry.Sha256}, not its approved SHA-256 {approvedSha256}.");
                }

                continue;
            }

            if (!string.Equals(
                    entry.Sha256,
                    definition.Sha256,
                    StringComparison.Ordinal))
            {
                throw Failed(
                    $"Bundle artifact '{definition.Id}' records SHA-256 {entry.Sha256}, not the corpus pin {definition.Sha256}.");
            }
        }

        VerifyIds(
            FixtureBundle.MutableKind,
            mutable.Keys,
            corpus.MutableArtifacts.Select(definition => definition.Id));
        foreach (MutableArtifactDefinition definition in
            corpus.MutableArtifacts)
        {
            VerifyUri(mutable[definition.Id], definition.Uri);
        }
    }

    private static void VerifyIds(
        string kind,
        IEnumerable<string> bundleIds,
        IEnumerable<string> corpusIds)
    {
        string[] actual = bundleIds
            .Order(StringComparer.Ordinal)
            .ToArray();
        string[] expected = corpusIds
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        if (actual.SequenceEqual(
                expected,
                StringComparer.Ordinal))
        {
            return;
        }

        string missing = string.Join(
            ",",
            expected.Except(
                actual,
                StringComparer.Ordinal));
        string unexpected = string.Join(
            ",",
            actual.Except(
                expected,
                StringComparer.Ordinal));
        throw Failed(
            $"The bundle's {kind} artifacts differ from the corpus; missing=[{missing}], unexpected=[{unexpected}].");
    }

    private static void VerifyUri(
        FixtureBundleArtifact entry,
        string corpusUri)
    {
        if (!string.Equals(
                entry.Uri,
                corpusUri,
                StringComparison.Ordinal))
        {
            throw Failed(
                $"Bundle artifact '{entry.Id}' records URI '{entry.Uri}', not the corpus URI '{corpusUri}'.");
        }
    }

    private static async Task<IReadOnlyDictionary<string, string>>
        VerifyBlobsAsync(
            string root,
            FixtureBundleManifest manifest,
            CancellationToken cancellationToken)
    {
        string blobRoot = Path.Combine(
            root,
            FixtureBundle.BlobDirectoryName);
        Dictionary<string, string> referencedBy =
            new(StringComparer.Ordinal);
        foreach (FixtureBundleArtifact artifact in manifest.Artifacts)
        {
            referencedBy.TryAdd(
                FixtureBundle.GetBlobFileName(artifact.Sha256),
                artifact.Id);
        }

        HashSet<string> present = new(StringComparer.Ordinal);
        foreach (FileSystemInfo entry in
            new DirectoryInfo(blobRoot).EnumerateFileSystemInfos())
        {
            if (entry is not FileInfo
                || !referencedBy.ContainsKey(entry.Name))
            {
                throw Failed(
                    $"The bundle's {FixtureBundle.BlobDirectoryName}/ directory contains the unexpected entry '{entry.Name}'.");
            }

            present.Add(entry.Name);
        }

        string[] blobNames = referencedBy.Keys
            .Order(StringComparer.Ordinal)
            .ToArray();
        foreach (string blobName in blobNames)
        {
            if (!present.Contains(blobName))
            {
                throw Failed(
                    $"The bundle is missing blob '{blobName}' for artifact '{referencedBy[blobName]}'.");
            }
        }

        foreach (string blobName in blobNames)
        {
            string actualSha256 = await FixtureBundle
                .ComputeFileSha256Async(
                    Path.Combine(blobRoot, blobName),
                    cancellationToken)
                .ConfigureAwait(false);
            if (!string.Equals(
                    FixtureBundle.GetBlobFileName(actualSha256),
                    blobName,
                    StringComparison.Ordinal))
            {
                throw Failed(
                    $"Bundle blob '{blobName}' hashes to {actualSha256}.");
            }
        }

        return manifest.Artifacts.ToDictionary(
            artifact => artifact.Id,
            artifact => Path.Combine(
                blobRoot,
                FixtureBundle.GetBlobFileName(artifact.Sha256)),
            StringComparer.Ordinal);
    }

    private static FixtureBundleException Failed(string message) =>
        new(QualificationFailureCodes.BundleVerificationFailed, message);
}
