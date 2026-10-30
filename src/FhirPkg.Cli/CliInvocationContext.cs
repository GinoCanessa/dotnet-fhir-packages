// Copyright (c) Gino Canessa. Licensed under the MIT License.

using FhirPkg.Installation;
using FhirPkg.IO;

namespace FhirPkg.Cli;

/// <summary>
/// One invocation's resolved filesystem services and immutable-by-ownership
/// manager configuration. Populated option copies do not determine provenance.
/// </summary>
internal sealed class CliInvocationContext
{
    private readonly FhirPackageManagerConfiguration _configuration;

    private CliInvocationContext(
        FileSystemContext context,
        FhirPackageManagerOptions options)
    {
        FileSystemContext = context;
        _configuration = FhirPackageManagerConfiguration.CreateResolved(options, context);
        // Retain the existing CLI merge key on the SDK-produced options copies.
        _configuration.Options.GitHubToken = options.GitHubToken;
    }

    /// <summary>Isolated test hook applied only after SDK policy validation.</summary>
    internal static Func<FileSystemContext, FileSystemContext>? FileSystemContextOverride { get; set; }

    /// <summary>Isolated test hook for raw environment reads during SDK policy validation.</summary>
    internal static Func<string, string?>? EnvironmentReaderOverride { get; set; }

    internal FileSystemContext FileSystemContext { get; }

    internal FhirPackageManagerConfiguration Configuration => _configuration;

    internal FhirPackageManagerOptions CreateManagerOptions()
    {
        FhirPackageManagerOptions options = _configuration.CreateOptionsSnapshot();
        options.GitHubToken = _configuration.Options.GitHubToken;
        return options;
    }

    internal static CliInvocationContext Create(GlobalOptions options)
    {
        FileSystemContext context = ResolveFileSystemContext(options);
        ConfigFile? config = CliConfigurationLoader.Load(context);
        return new CliInvocationContext(context, options.MergeManagerOptions(config));
    }

    internal static async Task<CliInvocationContext> CreateAsync(
        GlobalOptions options,
        CancellationToken cancellationToken,
        Action<FhirPackageManagerOptions>? configureManagerOptions = null)
    {
        FileSystemContext context = ResolveFileSystemContext(options);
        ConfigFile? config = await CliConfigurationLoader.LoadAsync(context, cancellationToken)
            .ConfigureAwait(false);
        FhirPackageManagerOptions managerOptions = options.MergeManagerOptions(config);
        configureManagerOptions?.Invoke(managerOptions);
        return new CliInvocationContext(context, managerOptions);
    }

    private static FileSystemContext ResolveFileSystemContext(GlobalOptions options)
    {
        FileSystemRetryOptions retry = new FileSystemRetryOptions();
        if (options.FileSystemRetryCount is int count)
            retry.RetryCount = count;
        if (options.FileSystemMinimumDelayMilliseconds is int minimum)
            retry.MinimumDelayMilliseconds = minimum;
        if (options.FileSystemMaximumJitterMilliseconds is int jitter)
            retry.MaximumJitterMilliseconds = jitter;

        Func<string, string?>? reader = EnvironmentReaderOverride;
        FileSystemContext context;
        if (reader is null)
        {
            context = new FileSystemContext(retry);
        }
        else
        {
            FileSystemRetryPolicy policy = FileSystemRetryPolicy.Resolve(retry, reader, out bool hasExplicitValues);
            context = new FileSystemContext(
                policy, hasExplicitValues, SystemFileSystemPrimitives.Instance, new FileSystemRetryExecutor());
        }
        return FileSystemContextOverride?.Invoke(context) ?? context;
    }

    internal ValueTask<bool> FileExistsAsync(string path, CancellationToken cancellationToken) =>
        FileExistsAsync(FileSystemContext, path, cancellationToken);

    internal static bool FileExists(FileSystemContext context, string path)
    {
        string fullPath;
        try
        {
            fullPath = context.FileSystem.GetFullPath(path);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
        {
            // Preserve File.Exists's invalid-path outcome, not its I/O-error swallowing.
            return false;
        }

        FileSystemProbeResult probe = context.FileSystem.ProbeFollowing(fullPath);
        return IsFile(probe);
    }

    internal static async ValueTask<bool> FileExistsAsync(
        FileSystemContext context,
        string path,
        CancellationToken cancellationToken)
    {
        string fullPath;
        try
        {
            fullPath = await context.FileSystem.GetFullPathAsync(path, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
        {
            return false;
        }

        FileSystemProbeResult probe = await context.FileSystem.ExecuteAsync(
                "ProbeFollowing",
                () => context.Primitives.Probe(fullPath, followLinks: true),
                cancellationToken)
            .ConfigureAwait(false);
        return IsFile(probe);
    }

    private static bool IsFile(FileSystemProbeResult probe) =>
        probe.Exists
        && probe.Kind is not (FileSystemEntryKind.Directory or FileSystemEntryKind.DirectorySymbolicLink);
}
