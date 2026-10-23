// Copyright (c) Gino Canessa. Licensed under the MIT License.

namespace FhirPkg.IO;

internal enum FileSystemPlatform
{
    Windows,
    Linux,
    MacOS,
    Unsupported,
}

internal enum FileSystemEntryKind
{
    Missing,
    File,
    Directory,
    SymbolicLink,
    DirectorySymbolicLink,
    Other,
}

internal enum FileSystemDirectorySyncOutcome
{
    Full,
    Standard,
    Unsupported,
}

internal enum FileSystemLinuxStatxOutcome
{
    Success,
    Missing,
    Unsupported,
    Failure,
}

/// <summary>
/// A successful inspection. No-follow is the default; legacy existence probes may
/// follow links. Absence is distinct from a failed inspection;
/// the latter is carried by the primitive outcome, never disguised as this value.
/// </summary>
internal readonly record struct FileSystemProbeResult(
    FileSystemEntryKind Kind,
    FileAttributes Attributes = 0,
    int Mode = 0)
{
    internal bool Exists => Kind != FileSystemEntryKind.Missing;

    internal bool IsLink => Kind is FileSystemEntryKind.SymbolicLink
        or FileSystemEntryKind.DirectorySymbolicLink;

    internal static FileSystemProbeResult FromAttributes(FileAttributes attributes)
    {
        FileSystemEntryKind kind = (attributes & FileAttributes.ReparsePoint) != 0
            ? (attributes & FileAttributes.Directory) != 0
                ? FileSystemEntryKind.DirectorySymbolicLink
                : FileSystemEntryKind.SymbolicLink
            : (attributes & FileAttributes.Directory) != 0
                ? FileSystemEntryKind.Directory
                : (attributes & FileAttributes.Device) != 0
                    ? FileSystemEntryKind.Other
                    : FileSystemEntryKind.File;
        return new FileSystemProbeResult(kind, attributes);
    }

    internal static FileSystemProbeResult FromMode(int mode) =>
        new FileSystemProbeResult(
            (mode & 0xF000) switch
            {
                0x8000 => FileSystemEntryKind.File,
                0x4000 => FileSystemEntryKind.Directory,
                0xA000 => FileSystemEntryKind.SymbolicLink,
                _ => FileSystemEntryKind.Other,
            },
            Mode: mode);
}
