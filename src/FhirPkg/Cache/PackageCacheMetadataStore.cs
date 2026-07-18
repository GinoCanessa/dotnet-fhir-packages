// Copyright (c) Gino Canessa. Licensed under the MIT License.

using System.Globalization;
using FhirPkg.Installation;
using FhirPkg.Utilities;

namespace FhirPkg.Cache;

internal sealed record PackageCacheMetadataMutation(
    string OperationId,
    PackageCacheTransactionState State,
    string CanonicalIdentity);

internal sealed class PackageCacheMetadataStore
{
    internal const string MetadataFileName = "packages.ini";

    private const string MetadataDateFormat = "yyyyMMddHHmmss";
    private const string PackagesSection = "packages";
    private const string PackageSizesSection = "package-sizes";
    private const string SourcePublicationDatesSection =
        "package-source-publication-dates";
    private const string ArchiveSha256Section = "package-archive-sha256";

    private static readonly string[] s_managedSections =
    [
        PackagesSection,
        PackageSizesSection,
        SourcePublicationDatesSection,
        ArchiveSha256Section
    ];

    private readonly string _metadataPath;
    private readonly IPackageCacheFileOperations _fileOperations;
    private readonly IPackageCacheFaultObserver _faultObserver;

    internal PackageCacheMetadataStore(
        string cacheRoot,
        IPackageCacheFileOperations fileOperations,
        IPackageCacheFaultObserver faultObserver)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cacheRoot);
        ArgumentNullException.ThrowIfNull(fileOperations);
        ArgumentNullException.ThrowIfNull(faultObserver);

        _metadataPath = Path.Combine(cacheRoot, MetadataFileName);
        _fileOperations = fileOperations;
        _faultObserver = faultObserver;
    }

    internal async Task<CacheMetadata> ReadAsync(
        CancellationToken cancellationToken)
    {
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> ini =
            await IniParser.ParseFileAsync(_metadataPath, cancellationToken)
                .ConfigureAwait(false);
        return ParseMetadata(ini);
    }

    internal async Task<CacheMetadataEntry?> GetEntryAsync(
        PackageCacheKey cacheKey,
        CancellationToken cancellationToken)
    {
        CacheMetadata metadata = await ReadAsync(cancellationToken)
            .ConfigureAwait(false);
        return metadata.Packages.TryGetValue(
            cacheKey.MetadataKey,
            out CacheMetadataEntry? entry)
            ? entry
            : null;
    }

    internal async Task<bool> EntryMatchesAsync(
        PackageCacheKey cacheKey,
        CacheMetadataEntry? expected,
        CancellationToken cancellationToken)
    {
        CacheMetadataEntry? actual = await GetEntryAsync(
                cacheKey,
                cancellationToken)
            .ConfigureAwait(false);
        if (actual is null || expected is null)
            return actual is null && expected is null;

        return string.Equals(
                actual.DownloadDateTime.ToString(
                    MetadataDateFormat,
                    CultureInfo.InvariantCulture),
                expected.DownloadDateTime.ToString(
                    MetadataDateFormat,
                    CultureInfo.InvariantCulture),
                StringComparison.Ordinal)
            && actual.SizeBytes == expected.SizeBytes
            && actual.SourcePublicationDate == expected.SourcePublicationDate
            && string.Equals(
                actual.ArchiveSha256,
                expected.ArchiveSha256,
                StringComparison.OrdinalIgnoreCase);
    }

    internal async Task SetEntryAsync(
        PackageCacheKey cacheKey,
        CacheMetadataEntry? entry,
        PackageCacheMetadataMutation? mutation,
        CancellationToken cancellationToken)
    {
        if (entry is null && !File.Exists(_metadataPath))
            return;

        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> ini =
            await IniParser.ParseFileAsync(_metadataPath, cancellationToken)
                .ConfigureAwait(false);
        Dictionary<string, Dictionary<string, string>> sections =
            CreateMutableSections(ini);

        EnsureStandardSections(sections);
        string key = cacheKey.MetadataKey;
        foreach (string sectionName in s_managedSections)
            sections[sectionName].Remove(key);

        if (entry is not null)
        {
            sections[PackagesSection][key] =
                entry.DownloadDateTime.ToString(
                    MetadataDateFormat,
                    CultureInfo.InvariantCulture);
            if (entry.SizeBytes.HasValue)
            {
                sections[PackageSizesSection][key] =
                    entry.SizeBytes.Value.ToString(
                        CultureInfo.InvariantCulture);
            }

            if (entry.SourcePublicationDate.HasValue)
            {
                sections[SourcePublicationDatesSection][key] =
                    entry.SourcePublicationDate.Value.ToString(
                        "O",
                        CultureInfo.InvariantCulture);
            }

            if (entry.ArchiveSha256 is not null)
                sections[ArchiveSha256Section][key] = entry.ArchiveSha256;
        }

        await WriteSectionsAsync(
                sections,
                mutation,
                cancellationToken)
            .ConfigureAwait(false);
    }

    internal async Task ClearManagedEntriesAsync(
        CancellationToken cancellationToken)
    {
        if (!File.Exists(_metadataPath))
            return;

        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> ini =
            await IniParser.ParseFileAsync(_metadataPath, cancellationToken)
                .ConfigureAwait(false);
        Dictionary<string, Dictionary<string, string>> sections =
            CreateMutableSections(ini);
        EnsureStandardSections(sections);
        foreach (string sectionName in s_managedSections)
            sections[sectionName].Clear();

        await WriteSectionsAsync(
                sections,
                mutation: null,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task WriteSectionsAsync(
        Dictionary<string, Dictionary<string, string>> sections,
        PackageCacheMetadataMutation? mutation,
        CancellationToken cancellationToken)
    {
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>>
            readonlySections = sections.ToDictionary(
                pair => pair.Key,
                pair => (IReadOnlyDictionary<string, string>)pair.Value,
                StringComparer.OrdinalIgnoreCase);
        await IniParser.WriteFileAtomicallyAsync(
                _metadataPath,
                readonlySections,
                _fileOperations,
                cancellationToken)
            .ConfigureAwait(false);

        if (mutation is not null)
        {
            await _faultObserver.OnEventAsync(
                    new PackageCacheFaultEvent(
                        PackageCacheFaultPoint.MetadataReplaced,
                        mutation.OperationId,
                        mutation.State,
                        mutation.CanonicalIdentity),
                    CancellationToken.None)
                .ConfigureAwait(false);
        }
    }

    private static CacheMetadata ParseMetadata(
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> ini)
    {
        int cacheVersion = 3;
        if (ini.TryGetValue(
                "cache",
                out IReadOnlyDictionary<string, string>? cacheSection)
            && cacheSection.TryGetValue("version", out string? versionText)
            && int.TryParse(
                versionText,
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out int parsedVersion))
        {
            cacheVersion = parsedVersion;
        }

        Dictionary<string, CacheMetadataEntry> packages =
            new Dictionary<string, CacheMetadataEntry>(
                StringComparer.OrdinalIgnoreCase);
        if (!ini.TryGetValue(
                PackagesSection,
                out IReadOnlyDictionary<string, string>? packageSection))
        {
            return new CacheMetadata
            {
                CacheVersion = cacheVersion,
                Packages = packages
            };
        }

        ini.TryGetValue(
            PackageSizesSection,
            out IReadOnlyDictionary<string, string>? sizesSection);
        ini.TryGetValue(
            SourcePublicationDatesSection,
            out IReadOnlyDictionary<string, string>? publicationSection);
        ini.TryGetValue(
            ArchiveSha256Section,
            out IReadOnlyDictionary<string, string>? hashesSection);

        foreach ((string key, string dateText) in packageSection)
        {
            DateTime downloadDate = DateTime.MinValue;
            if (DateTime.TryParseExact(
                dateText,
                MetadataDateFormat,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal
                    | DateTimeStyles.AdjustToUniversal,
                out DateTime parsedDate))
            {
                downloadDate = DateTime.SpecifyKind(
                    parsedDate,
                    DateTimeKind.Utc);
            }

            long? sizeBytes = null;
            if (sizesSection is not null
                && sizesSection.TryGetValue(key, out string? sizeText)
                && long.TryParse(
                    sizeText,
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out long parsedSize))
            {
                sizeBytes = parsedSize;
            }

            DateTimeOffset? publicationDate = null;
            if (publicationSection is not null
                && publicationSection.TryGetValue(
                    key,
                    out string? publicationText)
                && DateTimeOffset.TryParse(
                    publicationText,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind,
                    out DateTimeOffset parsedPublicationDate))
            {
                publicationDate = parsedPublicationDate;
            }

            string? archiveSha256 = null;
            if (hashesSection is not null)
                hashesSection.TryGetValue(key, out archiveSha256);

            packages[key] = new CacheMetadataEntry
            {
                DownloadDateTime = downloadDate,
                SizeBytes = sizeBytes,
                SourcePublicationDate = publicationDate,
                ArchiveSha256 = archiveSha256
            };
        }

        return new CacheMetadata
        {
            CacheVersion = cacheVersion,
            Packages = packages
        };
    }

    private static Dictionary<string, Dictionary<string, string>>
        CreateMutableSections(
            IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>>
                ini)
    {
        Dictionary<string, Dictionary<string, string>> sections =
            new Dictionary<string, Dictionary<string, string>>(
                StringComparer.OrdinalIgnoreCase);
        foreach ((
            string sectionName,
            IReadOnlyDictionary<string, string> values) in ini)
        {
            sections[sectionName] = new Dictionary<string, string>(
                values,
                StringComparer.OrdinalIgnoreCase);
        }

        return sections;
    }

    private static void EnsureStandardSections(
        Dictionary<string, Dictionary<string, string>> sections)
    {
        EnsureSection(
            sections,
            "cache",
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["version"] = "3"
            });
        EnsureSection(sections, "urls");
        EnsureSection(sections, "local");
        foreach (string sectionName in s_managedSections)
            EnsureSection(sections, sectionName);
    }

    private static void EnsureSection(
        Dictionary<string, Dictionary<string, string>> sections,
        string sectionName,
        Dictionary<string, string>? defaultValues = null)
    {
        if (!sections.ContainsKey(sectionName))
        {
            sections[sectionName] = defaultValues
                ?? new Dictionary<string, string>(
                    StringComparer.OrdinalIgnoreCase);
        }
    }
}
