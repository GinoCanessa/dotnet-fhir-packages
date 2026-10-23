// Copyright (c) Gino Canessa. Licensed under the MIT License.

namespace FhirPkg.IO;

/// <summary>
/// Construction-time policy and services. In particular, a populated options
/// snapshot must not be resolved again or mistaken for explicit configuration.
/// </summary>
internal sealed class FileSystemContext
{
    internal FileSystemContext(FileSystemRetryOptions? options)
        : this(options, SystemFileSystemPrimitives.Instance, new FileSystemRetryExecutor())
    {
    }

    internal FileSystemContext(
        FileSystemRetryOptions? options,
        IFileSystemPrimitives primitives,
        FileSystemRetryExecutor executor)
    {
        Policy = FileSystemRetryPolicy.Resolve(options, out bool hasExplicitValues);
        ArgumentNullException.ThrowIfNull(primitives);
        ArgumentNullException.ThrowIfNull(executor);
        HasExplicitValues = hasExplicitValues;
        Primitives = primitives;
        Executor = executor;
        FileSystem = new ResilientFileSystem(this);
    }

    internal FileSystemContext(
        FileSystemRetryPolicy policy,
        bool hasExplicitValues,
        IFileSystemPrimitives primitives,
        FileSystemRetryExecutor executor)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(primitives);
        ArgumentNullException.ThrowIfNull(executor);
        Policy = policy;
        HasExplicitValues = hasExplicitValues;
        Primitives = primitives;
        Executor = executor;
        FileSystem = new ResilientFileSystem(this);
    }

    internal FileSystemRetryPolicy Policy { get; }

    internal bool HasExplicitValues { get; }

    internal IFileSystemPrimitives Primitives { get; }

    internal FileSystemRetryExecutor Executor { get; }

    internal ResilientFileSystem FileSystem { get; }

    internal FileSystemRetryOptions ToOptionsSnapshot() => Policy.ToOptionsSnapshot();

    internal static FileSystemContext CreateDefault() => new(new FileSystemRetryOptions());
}
