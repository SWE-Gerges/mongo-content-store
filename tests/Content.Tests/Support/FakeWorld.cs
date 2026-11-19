using Content.TestSupport;

namespace Content.Tests.Support;

/// <summary>
/// The shared state of one fake deployment: the stores two or more API instances would share in production. Hosts
/// built from the same world behave like several instances over one database and one search cluster.
/// </summary>
internal sealed class FakeWorld
{
    public FakeWorld()
    {
        Leases = new InMemoryLeaseStore(Clock);
        Checkpoints = new InMemoryCheckpoints();
        DeadLetters = new InMemoryDeadLetters();
    }

    public TimeProvider Clock { get; } = TimeProvider.System;

    public InMemorySource Source { get; } = new();

    public InMemorySearchCluster Index { get; } = new();

    public InMemoryIndexVersionStore Versions { get; } = new();

    public RecordingMissSink Misses { get; } = new();

    public InMemoryMigrationTarget MigrationTarget { get; } = new();

    public InMemoryLeaseStore Leases { get; }

    public InMemoryRunStore Runs { get; } = new();

    public InMemoryCheckpoints Checkpoints { get; }

    public InMemoryDeadLetters DeadLetters { get; }
}
