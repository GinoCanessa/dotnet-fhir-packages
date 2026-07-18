// Copyright (c) Gino Canessa. Licensed under the MIT License.

using System.Text;

namespace FhirPkg.Installation;

/// <summary>
/// A normalized archive path whose interpretation is independent of the host
/// operating system.
/// </summary>
internal sealed class PortableArchivePath
{
    internal const int MaximumComponentLength = 255;

    private static readonly UTF8Encoding s_strictUtf8 = new UTF8Encoding(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    private static readonly HashSet<string> s_windowsDeviceNames = new HashSet<string>(
        [
            "CON",
            "PRN",
            "AUX",
            "NUL",
            "CLOCK$",
            "CONIN$",
            "CONOUT$",
            "COM1",
            "COM2",
            "COM3",
            "COM4",
            "COM5",
            "COM6",
            "COM7",
            "COM8",
            "COM9",
            "COM¹",
            "COM²",
            "COM³",
            "LPT1",
            "LPT2",
            "LPT3",
            "LPT4",
            "LPT5",
            "LPT6",
            "LPT7",
            "LPT8",
            "LPT9",
            "LPT¹",
            "LPT²",
            "LPT³"
        ],
        StringComparer.OrdinalIgnoreCase);

    private static readonly HashSet<char> s_portableInvalidCharacters =
    [
        '<',
        '>',
        ':',
        '"',
        '|',
        '?',
        '*'
    ];

    private PortableArchivePath(
        string canonicalPath,
        string exactSpelling,
        string[] segments,
        string[] exactSegments)
    {
        CanonicalPath = canonicalPath;
        ExactSpelling = exactSpelling;
        Segments = segments;
        ExactSegments = exactSegments;
    }

    internal string CanonicalPath { get; }

    internal string ExactSpelling { get; }

    internal IReadOnlyList<string> Segments { get; }

    internal IReadOnlyList<string> ExactSegments { get; }

    internal int Depth => Segments.Count;

    internal static PortableArchivePath Create(
        string entryName,
        bool isDirectory,
        string? directive = null)
    {
        if (string.IsNullOrEmpty(entryName))
            throw InvalidPath(directive, "Archive paths must not be empty.");

        ValidateUtf16(entryName, directive);

        if (entryName[0] is '/' or '\\')
            throw InvalidPath(directive, "Archive paths must not be rooted or UNC paths.");

        if (entryName.Length >= 2
            && char.IsAsciiLetter(entryName[0])
            && entryName[1] == ':')
        {
            throw InvalidPath(directive, "Archive paths must not be drive-qualified.");
        }

        string exactPath = entryName.Replace('\\', '/');
        if (exactPath.EndsWith('/'))
        {
            if (!isDirectory)
                throw InvalidPath(directive, "Regular-file archive paths must not end with a separator.");

            exactPath = exactPath[..^1];
        }

        if (exactPath.Length == 0)
            throw InvalidPath(directive, "Archive paths must not be empty.");

        string[] exactSegments = exactPath.Split('/', StringSplitOptions.None);
        string[] canonicalSegments = new string[exactSegments.Length];
        for (int index = 0; index < exactSegments.Length; index++)
        {
            string exactSegment = exactSegments[index];
            ValidateComponent(
                exactSegment,
                enforcePortableLength: false,
                directive);

            string canonicalSegment;
            try
            {
                canonicalSegment = exactSegment.Normalize(NormalizationForm.FormC);
            }
            catch (ArgumentException exception)
            {
                throw InvalidPath(
                    directive,
                    "Archive paths must contain valid Unicode.",
                    exception);
            }

            ValidateComponent(
                canonicalSegment,
                enforcePortableLength: true,
                directive);
            canonicalSegments[index] = canonicalSegment;
        }

        return new PortableArchivePath(
            string.Join('/', canonicalSegments),
            exactPath,
            canonicalSegments,
            exactSegments);
    }

    internal string GetCanonicalPrefix(int segmentCount) =>
        string.Join('/', Segments.Take(segmentCount));

    internal string GetExactPrefix(int segmentCount) =>
        string.Join('/', ExactSegments.Take(segmentCount));

    private static void ValidateUtf16(string value, string? directive)
    {
        for (int index = 0; index < value.Length; index++)
        {
            char character = value[index];
            if (char.IsHighSurrogate(character))
            {
                if (index + 1 >= value.Length
                    || !char.IsLowSurrogate(value[index + 1]))
                {
                    throw InvalidPath(
                        directive,
                        "Archive paths must contain valid Unicode.");
                }

                index++;
            }
            else if (char.IsLowSurrogate(character))
            {
                throw InvalidPath(
                    directive,
                    "Archive paths must contain valid Unicode.");
            }
        }
    }

    private static void ValidateComponent(
        string component,
        bool enforcePortableLength,
        string? directive)
    {
        if (component.Length == 0)
            throw InvalidPath(directive, "Archive paths must not contain empty components.");

        if (component is "." or "..")
            throw InvalidPath(directive, "Archive paths must not contain traversal components.");

        if (enforcePortableLength)
        {
            if (component.Length > MaximumComponentLength)
                throw ComponentTooLong(directive);

            try
            {
                if (s_strictUtf8.GetByteCount(component) > MaximumComponentLength)
                    throw ComponentTooLong(directive);
            }
            catch (EncoderFallbackException exception)
            {
                throw InvalidPath(
                    directive,
                    "Archive paths must contain valid Unicode.",
                    exception);
            }
        }

        if (component[^1] is '.' or ' ')
        {
            throw InvalidPath(
                directive,
                "Archive path components must not end with a dot or space.");
        }

        foreach (char character in component)
        {
            if (char.IsControl(character))
                throw InvalidPath(directive, "Archive paths must not contain control characters.");

            if (s_portableInvalidCharacters.Contains(character))
            {
                throw InvalidPath(
                    directive,
                    "Archive paths contain a character that is invalid on portable file systems.");
            }
        }

        int extensionIndex = component.IndexOf('.');
        string deviceCandidate = extensionIndex < 0
            ? component
            : component[..extensionIndex];
        deviceCandidate = deviceCandidate.TrimEnd(' ');
        if (s_windowsDeviceNames.Contains(deviceCandidate))
        {
            throw InvalidPath(
                directive,
                "Archive paths must not use Windows reserved device names.");
        }
    }

    private static PackageInstallException InvalidPath(
        string? directive,
        string message,
        Exception? innerException = null) =>
        new PackageInstallException(
            PackageInstallErrorCode.InvalidArchive,
            PackageInstallStage.ArchiveValidation,
            message,
            directive,
            innerException);

    private static PackageInstallException ComponentTooLong(
        string? directive) =>
        new PackageInstallException(
            PackageInstallErrorCode.ArchivePathLengthLimitExceeded,
            PackageInstallStage.ArchiveValidation,
            $"Archive path components must not exceed {MaximumComponentLength} " +
            "UTF-16 code units or UTF-8 bytes.",
            directive);
}
