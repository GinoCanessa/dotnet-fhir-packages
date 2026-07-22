// Copyright (c) Gino Canessa. Licensed under the MIT License.

using System.Formats.Tar;
using System.IO.Compression;
using System.Text;

namespace FhirPkg.Tests.Support;

internal sealed record ArbitraryTarEntry(
    string Name,
    TarEntryType EntryType,
    byte[]? Content = null,
    string? LinkName = null);

internal static class ArbitraryTarBuilder
{
    internal static ArbitraryTarEntry File(string name, string content) =>
        new ArbitraryTarEntry(
            name,
            TarEntryType.RegularFile,
            Encoding.UTF8.GetBytes(content));

    internal static ArbitraryTarEntry File(string name, byte[] content) =>
        new ArbitraryTarEntry(
            name,
            TarEntryType.RegularFile,
            content);

    internal static ArbitraryTarEntry Directory(string name) =>
        new ArbitraryTarEntry(name, TarEntryType.Directory);

    internal static ArbitraryTarEntry SymbolicLink(
        string name,
        string target) =>
        new ArbitraryTarEntry(
            name,
            TarEntryType.SymbolicLink,
            LinkName: target);

    internal static ArbitraryTarEntry HardLink(
        string name,
        string target) =>
        new ArbitraryTarEntry(
            name,
            TarEntryType.HardLink,
            LinkName: target);

    internal static MemoryStream Create(
        params ArbitraryTarEntry[] entries)
    {
        MemoryStream archive = new MemoryStream();
        using (GZipStream gzip = new GZipStream(
            archive,
            CompressionMode.Compress,
            leaveOpen: true))
        using (TarWriter writer = new TarWriter(gzip, leaveOpen: true))
        {
            foreach (ArbitraryTarEntry definition in entries)
            {
                PaxTarEntry entry = new PaxTarEntry(
                    definition.EntryType,
                    definition.Name);
                if (definition.Content is not null)
                    entry.DataStream = new MemoryStream(definition.Content);

                if (definition.LinkName is not null)
                    entry.LinkName = definition.LinkName;

                writer.WriteEntry(entry);
            }
        }

        archive.Position = 0;
        return archive;
    }
}
