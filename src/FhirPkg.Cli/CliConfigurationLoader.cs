// Copyright (c) Gino Canessa. Licensed under the MIT License.

using System.Text.Json;
using FhirPkg.IO;

namespace FhirPkg.Cli;

/// <summary>
/// Optional configuration discovery and physical reads under the invocation
/// policy. Deserialization and warning/fallback behavior are not replay units.
/// </summary>
internal static class CliConfigurationLoader
{
    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    internal static ConfigFile? Load(FileSystemContext context)
    {
        if (GlobalOptions.ConfigLoaderOverride is Func<ConfigFile?> loader)
            return loader();

        string currentPath = Path.Combine(context.FileSystem.GetCurrentDirectory(), ".fhir-pkg.json");
        if (TryLoad(context, currentPath, out ConfigFile? config))
            return config;

        string homePath = Path.Combine(context.FileSystem.GetHomeDirectory(), ".fhir-pkg.json");
        return TryLoad(context, homePath, out config) ? config : null;
    }

    internal static async Task<ConfigFile?> LoadAsync(
        FileSystemContext context,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (GlobalOptions.ConfigLoaderOverride is Func<ConfigFile?> loader)
            return loader();

        string currentDirectory = await context.FileSystem.GetCurrentDirectoryAsync(cancellationToken)
            .ConfigureAwait(false);
        (bool loaded, ConfigFile? config) = await TryLoadAsync(
                context, Path.Combine(currentDirectory, ".fhir-pkg.json"), cancellationToken)
            .ConfigureAwait(false);
        if (loaded)
            return config;

        string homeDirectory = await context.FileSystem.GetHomeDirectoryAsync(cancellationToken)
            .ConfigureAwait(false);
        (_, config) = await TryLoadAsync(
                context, Path.Combine(homeDirectory, ".fhir-pkg.json"), cancellationToken)
            .ConfigureAwait(false);
        return config;
    }

    private static bool TryLoad(FileSystemContext context, string path, out ConfigFile? config)
    {
        config = null;
        try
        {
            if (!CliInvocationContext.FileExists(context, path))
                return false;

            string json;
            using (Stream stream = context.FileSystem.OpenPositionedFile(
                path, FileMode.Open, FileAccess.Read, FileShare.Read, FileOptions.SequentialScan))
            {
                using StreamReader reader = new(stream, leaveOpen: true);
                json = reader.ReadToEnd();
            }

            config = JsonSerializer.Deserialize<ConfigFile>(json, s_jsonOptions);
            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            WriteWarning(path, exception);
            return false;
        }
    }

    private static async Task<(bool Loaded, ConfigFile? Config)> TryLoadAsync(
        FileSystemContext context,
        string path,
        CancellationToken cancellationToken)
    {
        try
        {
            if (!await CliInvocationContext.FileExistsAsync(context, path, cancellationToken).ConfigureAwait(false))
                return (false, null);

            string json;
            await using (Stream stream = await context.FileSystem.OpenPositionedFileAsync(
                    path, FileMode.Open, FileAccess.Read, FileShare.Read,
                    FileOptions.Asynchronous | FileOptions.SequentialScan, cancellationToken)
                .ConfigureAwait(false))
            {
                using StreamReader reader = new(stream, leaveOpen: true);
                json = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
            }

            return (true, JsonSerializer.Deserialize<ConfigFile>(json, s_jsonOptions));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            WriteWarning(path, exception);
            return (false, null);
        }
    }

    private static void WriteWarning(string path, Exception exception) =>
        Console.Error.WriteLine(
            $"Warning: Failed to parse config file '{path}': {FileSystemFailureContext.FormatMessage(exception)}");
}
