using Content.Sync.Indexing;
using Content.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;

namespace Content.Tests.Support;

/// <summary>A sync harness plus the version store, verifier and manager of the versioned index, over one in-memory cluster.</summary>
internal sealed class IndexHarness
{
    public const string Collection = SyncHarness.Collection;

    public IndexHarness(int documents = 100)
    {
        Sync = new SyncHarness().WithHotels(documents);
        Options = new IndexVersioningOptions { RetainedVersions = 2, CountTolerance = 0, MaxDeadLetters = 0, SampleSize = 10 };
        Verifier = new IndexVerifier(
            Sync.Index,
            Sync.Index,
            Sync.Source,
            Sync.DeadLetters,
            Microsoft.Extensions.Options.Options.Create(Options),
            Sync.Clock,
            NullLogger<IndexVerifier>.Instance);
        Manager = new IndexVersionManager(
            Sync.Index,
            Sync.Runner,
            Versions,
            Verifier,
            Microsoft.Extensions.Options.Options.Create(Options),
            Sync.Clock,
            NullLogger<IndexVersionManager>.Instance);
    }

    public SyncHarness Sync { get; }

    public InMemoryIndexVersionStore Versions { get; } = new();

    public IndexVersioningOptions Options { get; }

    public IndexVerifier Verifier { get; }

    public IndexVersionManager Manager { get; }

    public InMemorySearchCluster Cluster => Sync.Index;

    public string Alias => Sync.IndexName();

    public string Index(int version) => IndexVersionNaming.For(Alias, version);

    public Task<RebuildResult> RebuildAsync(RebuildHooks? hooks = null, int batchSize = 50, CancellationToken cancellationToken = default) =>
        Manager.RebuildAsync(Alias, Collection, Sync.Fast(batchSize), hooks, Guid.NewGuid().ToString("N"), cancellationToken);

    public IndexVersionRecord Record(int version) => Versions.All.Single(r => r.Version == version);
}
