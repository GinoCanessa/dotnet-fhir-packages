// Copyright (c) Gino Canessa. Licensed under the MIT License.

namespace FhirPkg.IO;

internal enum DurableFileOperationKind
{
    CreateDirectory,
    Move,
    Replace,
    Delete,
    Synchronize,
}

/// <summary>
/// One budget for namespace changes and their required barriers. A successful
/// mutation is recorded before yielding; retries can only visit unfinished work.
/// </summary>
internal sealed class DurableFileReplayOperation : FileSystemReplayOperation<bool>
{
    private readonly IFileSystemPrimitives _primitives;
    private readonly DurableFileOperationKind _kind;
    private readonly string _path;
    private readonly string? _destinationPath;
    private readonly FileSystemEntryKind _expectedKind;
    private readonly bool _recursive;
    private readonly bool _missingAllowed;
    private readonly List<DirectoryCreation> _creations = [];
    private readonly Stack<Deletion> _deletions = new Stack<Deletion>();
    private string? _fullPath;
    private string? _fullDestinationPath;
    private bool _initialized;
    private string? _candidate;
    private bool _creationDiscoveryComplete;
    private int _creationIndex;
    private bool _destinationChecked;
    private bool _mutationCompleted;
    private string[] _barriers = [];
    private int _barrierIndex;
    private DirectorySynchronizationState? _synchronization;

    internal DurableFileReplayOperation(
        FileSystemContext context,
        DurableFileOperationKind kind,
        string path,
        string? destinationPath = null,
        FileSystemEntryKind expectedKind = FileSystemEntryKind.Missing,
        bool recursive = false,
        bool missingAllowed = true) : base(context.Policy, "ResolvePath")
    {
        _primitives = context.Primitives;
        _kind = kind;
        _path = path;
        _destinationPath = destinationPath;
        _expectedKind = expectedKind;
        _recursive = recursive;
        _missingAllowed = missingAllowed;
    }

    internal override FileSystemReplayOutcome<bool> ExecuteStep(CancellationToken cancellationToken)
    {
        if (_fullPath is null)
        {
            FileSystemReplayOutcome<string> resolved = ResilientFileSystem.ResolvePathAttempt(_primitives, _path);
            if (resolved.Kind != FileSystemReplayOutcomeKind.Completed)
                return ResilientFileSystem.Failure<string, bool>(resolved);
            _fullPath = Path.TrimEndingDirectorySeparator(resolved.Value);
            return FileSystemReplayOutcome<bool>.Continue();
        }

        if (_destinationPath is not null && _fullDestinationPath is null)
        {
            SetPhase("ResolveDestinationPath");
            FileSystemReplayOutcome<string> resolved = ResilientFileSystem.ResolvePathAttempt(_primitives, _destinationPath);
            if (resolved.Kind != FileSystemReplayOutcomeKind.Completed)
                return ResilientFileSystem.Failure<string, bool>(resolved);
            _fullDestinationPath = Path.TrimEndingDirectorySeparator(resolved.Value);
            return FileSystemReplayOutcome<bool>.Continue();
        }

        if (!_initialized)
        {
            _candidate = _fullPath;
            if (_kind is DurableFileOperationKind.Move or DurableFileOperationKind.Replace)
            {
                ArgumentNullException.ThrowIfNull(_fullDestinationPath);
                string sourceParent = RequireParent(_fullPath);
                string destinationParent = RequireParent(_fullDestinationPath);
                _barriers = string.Equals(sourceParent, destinationParent, StringComparison.Ordinal)
                    ? [sourceParent]
                    : [sourceParent, destinationParent];
            }
            else if (_kind == DurableFileOperationKind.Synchronize)
            {
                _barriers = [_fullPath];
            }
            else if (_kind == DurableFileOperationKind.Delete)
            {
                _deletions.Push(new Deletion(_fullPath, _expectedKind, _missingAllowed));
            }
            _initialized = true;
        }

        return _kind switch
        {
            DurableFileOperationKind.CreateDirectory => CreateDirectoryStep(),
            DurableFileOperationKind.Move or DurableFileOperationKind.Replace => MoveStep(),
            DurableFileOperationKind.Delete => DeleteStep(cancellationToken),
            DurableFileOperationKind.Synchronize => SynchronizeStep(),
            _ => throw new InvalidOperationException("Unknown durable filesystem operation."),
        };
    }

    private FileSystemReplayOutcome<bool> CreateDirectoryStep()
    {
        if (!_creationDiscoveryComplete)
        {
            SetPhase("InspectDirectoryAncestors");
            FileSystemReplayOutcome<FileSystemProbeResult> probe = _primitives.Probe(_candidate!, followLinks: true);
            if (probe.Kind is not (FileSystemReplayOutcomeKind.Completed or FileSystemReplayOutcomeKind.Missing))
                return ResilientFileSystem.Failure<FileSystemProbeResult, bool>(probe);
            if (!probe.Value.Exists)
            {
                string parent = RequireParent(_candidate!);
                // Record both barriers before mkdir, not by rediscovering missing
                // directories after a later failure.
                _creations.Add(new DirectoryCreation(_candidate!, parent));
                _candidate = parent;
                return FileSystemReplayOutcome<bool>.Continue();
            }

            RequireDirectory(probe.Value, _candidate!);
            _creationDiscoveryComplete = true;
            _creationIndex = _creations.Count - 1;
            return FileSystemReplayOutcome<bool>.Continue();
        }

        if (_creationIndex < 0)
            return FileSystemReplayOutcome<bool>.Complete(true);

        DirectoryCreation creation = _creations[_creationIndex];
        if (creation.Collision)
        {
            SetPhase("InspectDirectoryCollision");
            FileSystemReplayOutcome<FileSystemProbeResult> probe = _primitives.Probe(creation.Path, followLinks: true);
            if (probe.Kind is not (FileSystemReplayOutcomeKind.Completed or FileSystemReplayOutcomeKind.Missing))
                return ResilientFileSystem.Failure<FileSystemProbeResult, bool>(probe);
            RequireDirectory(probe.Value, creation.Path);
            // It belongs to another creator. Do not claim ownership or delete it.
            _creationIndex--;
            return FileSystemReplayOutcome<bool>.Continue();
        }

        if (!creation.Created)
        {
            SetPhase("CreateDirectory");
            FileSystemReplayOutcome<bool> created = _primitives.CreateDirectory(creation.Path);
            if (created.Kind == FileSystemReplayOutcomeKind.KindRejected)
            {
                creation.Collision = true;
                return FileSystemReplayOutcome<bool>.Continue();
            }
            if (created.Kind != FileSystemReplayOutcomeKind.Completed)
                return created;
            creation.Created = true;
            _barriers = creation.Barriers;
            _barrierIndex = 0;
            return FileSystemReplayOutcome<bool>.Continue();
        }

        FileSystemReplayOutcome<bool> synchronized = SynchronizeStep();
        if (synchronized.Kind != FileSystemReplayOutcomeKind.Completed)
            return synchronized;
        _creationIndex--;
        return FileSystemReplayOutcome<bool>.Continue();
    }

    private FileSystemReplayOutcome<bool> MoveStep()
    {
        if (!_mutationCompleted)
        {
            if (_kind == DurableFileOperationKind.Move && !_destinationChecked)
            {
                SetPhase("InspectRenameDestination");
                FileSystemReplayOutcome<FileSystemProbeResult> probe = _primitives.Probe(_fullDestinationPath!);
                if (probe.Kind is not (FileSystemReplayOutcomeKind.Completed or FileSystemReplayOutcomeKind.Missing))
                    return ResilientFileSystem.Failure<FileSystemProbeResult, bool>(probe);
                if (probe.Value.Exists)
                    throw new IOException($"The destination path '{_fullDestinationPath}' already exists.");
                _destinationChecked = true;
                return FileSystemReplayOutcome<bool>.Continue();
            }

            SetPhase(_kind == DurableFileOperationKind.Replace ? "Replace" : "Rename");
            FileSystemReplayOutcome<bool> moved = _primitives.MovePath(
                _fullPath!, _fullDestinationPath!, _kind == DurableFileOperationKind.Replace);
            if (moved.Kind != FileSystemReplayOutcomeKind.Completed)
            {
                _destinationChecked = false;
                return moved;
            }
            _mutationCompleted = true;
            return FileSystemReplayOutcome<bool>.Continue();
        }

        return SynchronizeStep();
    }

    private FileSystemReplayOutcome<bool> DeleteStep(CancellationToken cancellationToken)
    {
        if (_deletions.Count == 0)
            return FileSystemReplayOutcome<bool>.Complete(true);

        Deletion deletion = _deletions.Peek();
        switch (deletion.Stage)
        {
            case DeletionStage.Probe:
                SetPhase("InspectDeleteEntry");
                FileSystemReplayOutcome<FileSystemProbeResult> probe = _primitives.Probe(deletion.Path);
                if (probe.Kind is not (FileSystemReplayOutcomeKind.Completed or FileSystemReplayOutcomeKind.Missing))
                    return ResilientFileSystem.Failure<FileSystemProbeResult, bool>(probe);
                if (!probe.Value.Exists)
                    return MissingDeletion(deletion);
                deletion.Kind = probe.Value.Kind;
                RequireDeletionKind(deletion);
                deletion.Stage = deletion.Kind == FileSystemEntryKind.Directory && _recursive
                    ? DeletionStage.Snapshot
                    : DeletionStage.Mutate;
                return FileSystemReplayOutcome<bool>.Continue();

            case DeletionStage.Snapshot:
                SetPhase("SnapshotDeleteChildren");
                FileSystemReplayOutcome<string[]> snapshot = ResilientFileSystem.SnapshotAttempt(
                    _primitives, deletion.Path, cancellationToken);
                if (snapshot.Kind != FileSystemReplayOutcomeKind.Completed)
                {
                    deletion.Stage = DeletionStage.Probe;
                    return ResilientFileSystem.Failure<string[], bool>(snapshot);
                }
                deletion.Children = snapshot.Value;
                deletion.Stage = DeletionStage.Children;
                return FileSystemReplayOutcome<bool>.Continue();

            case DeletionStage.Children:
                if (deletion.NextChild < deletion.Children.Length)
                {
                    string child = deletion.Children[deletion.NextChild++];
                    _deletions.Push(new Deletion(child, FileSystemEntryKind.Missing, missingAllowed: true));
                }
                else
                {
                    deletion.Stage = DeletionStage.Mutate;
                }
                return FileSystemReplayOutcome<bool>.Continue();

            case DeletionStage.Mutate:
                SetPhase("DeleteEntry");
                FileSystemReplayOutcome<bool> removed = _primitives.DeleteEntry(deletion.Path, deletion.Kind);
                if (removed.Kind == FileSystemReplayOutcomeKind.Missing)
                    return MissingDeletion(deletion);
                if (removed.Kind != FileSystemReplayOutcomeKind.Completed)
                    return removed;
                // No later failure can revisit this entry or its completed children.
                deletion.Stage = DeletionStage.Synchronize;
                deletion.Synchronization = new DirectorySynchronizationState(_primitives, RequireParent(deletion.Path));
                return FileSystemReplayOutcome<bool>.Continue();

            case DeletionStage.Synchronize:
                SetPhase(deletion.Synchronization!.Phase);
                FileSystemReplayOutcome<bool> synchronized = deletion.Synchronization.Step();
                if (synchronized.Kind != FileSystemReplayOutcomeKind.Completed)
                    return synchronized;
                _deletions.Pop();
                return FileSystemReplayOutcome<bool>.Continue();

            default:
                throw new InvalidOperationException("Unknown deletion phase.");
        }
    }

    private FileSystemReplayOutcome<bool> MissingDeletion(Deletion deletion)
    {
        if (!deletion.MissingAllowed)
            throw new DirectoryNotFoundException($"The directory '{deletion.Path}' does not exist.");
        _deletions.Pop();
        return FileSystemReplayOutcome<bool>.Continue();
    }

    private FileSystemReplayOutcome<bool> SynchronizeStep()
    {
        if (_barrierIndex == _barriers.Length)
            return FileSystemReplayOutcome<bool>.Complete(true);
        _synchronization ??= new DirectorySynchronizationState(_primitives, _barriers[_barrierIndex]);
        SetPhase(_synchronization.Phase);
        FileSystemReplayOutcome<bool> synchronized = _synchronization.Step();
        if (synchronized.Kind != FileSystemReplayOutcomeKind.Completed)
            return synchronized;
        _synchronization = null;
        _barrierIndex++;
        return FileSystemReplayOutcome<bool>.Continue();
    }

    internal void ReleaseAfterFailure()
    {
        _synchronization?.ReleaseAfterFailure();
        foreach (Deletion deletion in _deletions)
            deletion.Synchronization?.ReleaseAfterFailure();
    }

    private static string RequireParent(string path) =>
        Path.GetDirectoryName(path)
        ?? throw new IOException($"The filesystem path '{path}' has no parent directory.");

    private static void RequireDirectory(FileSystemProbeResult probe, string path)
    {
        if (probe.Kind is not (FileSystemEntryKind.Directory or FileSystemEntryKind.DirectorySymbolicLink))
            throw new IOException($"The path '{path}' is not a regular directory.");
    }

    private static void RequireDeletionKind(Deletion deletion)
    {
        bool accepted = deletion.ExpectedKind switch
        {
            FileSystemEntryKind.Missing => true,
            FileSystemEntryKind.Directory => deletion.Kind is FileSystemEntryKind.Directory
                or FileSystemEntryKind.DirectorySymbolicLink or FileSystemEntryKind.SymbolicLink,
            FileSystemEntryKind.File => deletion.Kind is FileSystemEntryKind.File or FileSystemEntryKind.Other or FileSystemEntryKind.SymbolicLink,
            _ => deletion.Kind == deletion.ExpectedKind,
        };
        if (!accepted)
            throw new IOException($"The filesystem entry '{deletion.Path}' changed kind before deletion.");
    }

    private sealed class DirectoryCreation
    {
        internal DirectoryCreation(string path, string parent)
        {
            Path = path;
            Barriers = [path, parent];
        }

        internal string Path { get; }
        internal string[] Barriers { get; }
        internal bool Created { get; set; }
        internal bool Collision { get; set; }
    }

    private enum DeletionStage
    {
        Probe,
        Snapshot,
        Children,
        Mutate,
        Synchronize,
    }

    private sealed class Deletion
    {
        internal Deletion(string path, FileSystemEntryKind expectedKind, bool missingAllowed)
        {
            Path = path;
            ExpectedKind = expectedKind;
            MissingAllowed = missingAllowed;
        }

        internal string Path { get; }
        internal FileSystemEntryKind ExpectedKind { get; }
        internal bool MissingAllowed { get; }
        internal FileSystemEntryKind Kind { get; set; }
        internal DeletionStage Stage { get; set; }
        internal string[] Children { get; set; } = [];
        internal int NextChild { get; set; }
        internal DirectorySynchronizationState? Synchronization { get; set; }
    }
}

/// <summary>
/// A retained directory descriptor and capability phase, shared by compound
/// operations rather than executed with a new fault budget.
/// </summary>
internal sealed class DirectorySynchronizationState
{
    private readonly IFileSystemPrimitives _primitives;
    private readonly string _path;
    private int? _descriptor;
    private bool _synchronized;
    private bool _standardSync;
    private FileSystemReplayOutcome<bool>? _closeResult;

    internal DirectorySynchronizationState(IFileSystemPrimitives primitives, string path)
    {
        _primitives = primitives;
        _path = path;
        _standardSync = primitives.Platform != FileSystemPlatform.MacOS;
    }

    internal string Phase => _descriptor is null && !_synchronized
        ? "OpenDirectoryForSync"
        : _synchronized
            ? "CloseDirectoryAfterSync"
            : _standardSync ? "StandardDirectorySync" : "FullDirectorySync";

    internal FileSystemReplayOutcome<bool> Step()
    {
        if (_primitives.Platform == FileSystemPlatform.Windows)
        {
            // MOVEFILE_WRITE_THROUGH is the existing Windows rename barrier.
            return FileSystemReplayOutcome<bool>.Complete(true);
        }
        if (_primitives.Platform == FileSystemPlatform.Unsupported)
            throw new PlatformNotSupportedException("Directory synchronization requires Windows, Linux, or macOS.");
        if (_closeResult.HasValue)
            return _closeResult.Value;

        if (_descriptor is null)
        {
            FileSystemReplayOutcome<int> opened = _primitives.OpenDescriptor(_path, 0, 0);
            if (opened.Kind != FileSystemReplayOutcomeKind.Completed)
                return ResilientFileSystem.Failure<int, bool>(opened);
            _descriptor = opened.Value;
            return FileSystemReplayOutcome<bool>.Continue();
        }

        if (!_synchronized)
        {
            FileSystemReplayOutcome<FileSystemDirectorySyncOutcome> synchronized =
                _primitives.SynchronizeDescriptor(_descriptor.Value, fullSync: !_standardSync, _path);
            if (synchronized.Kind != FileSystemReplayOutcomeKind.Completed)
                return ResilientFileSystem.Failure<FileSystemDirectorySyncOutcome, bool>(synchronized);
            if (!_standardSync && synchronized.Value == FileSystemDirectorySyncOutcome.Unsupported)
            {
                _standardSync = true;
                return FileSystemReplayOutcome<bool>.Continue();
            }
            _synchronized = true;
            return FileSystemReplayOutcome<bool>.Continue();
        }

        int descriptor = _descriptor.Value;
        _descriptor = null;
        // Consume the token before close: even EINTR may have released it.
        _closeResult = _primitives.CloseDescriptor(descriptor, _path);
        return _closeResult.Value;
    }

    internal void ReleaseAfterFailure()
    {
        int? descriptor = _descriptor;
        _descriptor = null;
        if (descriptor.HasValue)
        {
            try
            {
                _primitives.CloseDescriptor(descriptor.Value, _path);
            }
            catch
            {
                // Cleanup cannot replace a primary failure or retry a close.
            }
        }
    }
}
