using Content.Mongo.Coordination;
using Content.Sync.Indexing;
using Content.Sync.Search;
using Content.TestSupport;
using Content.Tests.Support;

namespace Content.Tests.Sync;

[Collection(ClassMapCollection.Name)]
public class IndexVersionManagerTests
{
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;

    public IndexVersionManagerTests() => _ = TestMappings.Counter;

    [Fact]
    public async Task The_first_rebuild_fills_version_one_verifies_it_and_creates_the_alias()
    {
        var h = new IndexHarness();

        var result = await h.RebuildAsync(cancellationToken: _ct);

        result.Swapped.ShouldBeTrue();
        result.IndexName.ShouldBe(h.Index(1));
        result.PreviousIndex.ShouldBeNull();
        result.Verification.Passed.ShouldBeTrue(result.Verification.Summary);
        h.Cluster.AliasTargets(h.Alias).ShouldBe([h.Index(1)]);
        h.Cluster.Ids(h.Alias).Count.ShouldBe(100);
        h.Record(1).State.ShouldBe(IndexVersionState.Live);
        h.Record(1).Verification.ShouldNotBeNull();
        var swap = h.Cluster.Swaps.ShouldHaveSingleItem();
        swap.AddIndex.ShouldBe(h.Index(1));
        swap.RemoveIndexes.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_later_rebuild_swaps_with_one_request_that_removes_the_old_target_and_adds_the_new()
    {
        var h = new IndexHarness();
        await h.RebuildAsync(cancellationToken: _ct);

        var result = await h.RebuildAsync(cancellationToken: _ct);

        result.IndexName.ShouldBe(h.Index(2));
        result.PreviousIndex.ShouldBe(h.Index(1));
        h.Cluster.Swaps.Count.ShouldBe(2, "exactly one alias request per swap");
        var swap = h.Cluster.Swaps[1];
        swap.AddIndex.ShouldBe(h.Index(2));
        swap.RemoveIndexes.ShouldBe([h.Index(1)]);
        h.Cluster.AliasTargets(h.Alias).ShouldBe([h.Index(2)]);
        h.Record(1).State.ShouldBe(IndexVersionState.Retired);
        h.Record(2).State.ShouldBe(IndexVersionState.Live);
        h.Cluster.IndexNames.ShouldContain(h.Index(1), "the previous version is kept for rollback");
    }

    [Fact]
    public async Task Readers_going_through_the_alias_never_see_it_missing_or_empty_while_it_is_swapped_repeatedly()
    {
        var h = new IndexHarness(documents: 60);
        await h.RebuildAsync(cancellationToken: _ct);
        var ids = SyncHarness.IdStrings(1, 60);
        var stop = false;
        var reads = 0;
        var failures = new List<string>();
        var reader = Task.Run(
            async () =>
            {
                while (!Volatile.Read(ref stop))
                {
                    try
                    {
                        var found = await h.Cluster.GetByIdsAsync(h.Alias, ids, _ct);
                        if (found.Count != 60)
                        {
                            failures.Add($"read returned {found.Count} of 60 documents");
                        }
                    }
                    catch (Exception ex)
                    {
                        failures.Add(ex.Message);
                    }

                    Interlocked.Increment(ref reads);
                }
            },
            _ct);

        for (var i = 0; i < 25; i++)
        {
            (await h.RebuildAsync(cancellationToken: _ct)).Swapped.ShouldBeTrue();
        }

        Volatile.Write(ref stop, true);
        await reader;

        failures.ShouldBeEmpty();
        reads.ShouldBeGreaterThan(0);
        h.Cluster.Swaps.Skip(1).ShouldAllBe(s => s.RemoveIndexes.Count == 1 && s.AddIndex != s.RemoveIndexes[0]);
    }

    [Fact]
    public async Task Pruning_keeps_the_newest_k_retired_versions_and_deletes_older_ones()
    {
        var h = new IndexHarness(documents: 30);
        h.Options.RetainedVersions = 2;

        for (var i = 0; i < 5; i++)
        {
            await h.RebuildAsync(cancellationToken: _ct);
        }

        h.Cluster.IndexNames.Order().ShouldBe([h.Index(3), h.Index(4), h.Index(5)]);
        h.Versions.All.Select(r => (r.Version, r.State)).ShouldBe([(3, IndexVersionState.Retired), (4, IndexVersionState.Retired), (5, IndexVersionState.Live)]);
        h.Cluster.AliasTargets(h.Alias).ShouldBe([h.Index(5)]);
    }

    [Fact]
    public async Task A_build_that_fails_verification_is_kept_for_inspection_and_the_alias_stays_on_the_old_version()
    {
        var h = new IndexHarness();
        await h.RebuildAsync(cancellationToken: _ct);
        h.Sync.Writer.RejectWhen = document => document.Id == "7" ? new BulkItemFailure(document.Id, 400, "mapper_parsing_exception: bad value", false) : null;

        var result = await h.RebuildAsync(cancellationToken: _ct);

        result.Swapped.ShouldBeFalse();
        result.Verification.Passed.ShouldBeFalse();
        result.Verification.Checks.Single(c => c.Name == "dead-letters").Passed.ShouldBeFalse();
        h.Cluster.AliasTargets(h.Alias).ShouldBe([h.Index(1)]);
        h.Cluster.Swaps.Count.ShouldBe(1, "no second alias request was made");
        h.Cluster.IndexNames.ShouldContain(h.Index(2), "the new index stays for inspection");
        h.Record(2).State.ShouldBe(IndexVersionState.Failed);
        h.Record(2).Verification!.DeadLetters.ShouldBe(1);
        h.Record(1).State.ShouldBe(IndexVersionState.Live);
    }

    [Fact]
    public async Task A_failed_build_is_deleted_after_the_next_successful_swap_and_is_never_a_rollback_target()
    {
        var h = new IndexHarness();
        await h.RebuildAsync(cancellationToken: _ct);
        h.Sync.Writer.RejectWhen = document => document.Id == "7" ? new BulkItemFailure(document.Id, 400, "mapper_parsing_exception: bad value", false) : null;
        await h.RebuildAsync(cancellationToken: _ct);
        h.Sync.Writer.RejectWhen = null;
        h.Sync.DeadLetters.Entries.ShouldNotBeEmpty();
        await h.Sync.DeadLetters.RemoveAsync(Collection(), ["7"], _ct);

        var next = await h.RebuildAsync(cancellationToken: _ct);

        next.Swapped.ShouldBeTrue(next.Verification.Summary);
        next.IndexName.ShouldBe(h.Index(3));
        next.Pruned.ShouldBe([h.Index(2)]);
        h.Cluster.IndexNames.ShouldNotContain(h.Index(2));
        h.Versions.All.Select(r => r.Version).ShouldBe([1, 3]);
        (await h.Manager.RollbackAsync(h.Alias, null, _ct)).Version.ShouldBe(1);

        static string Collection() => IndexHarness.Collection;
    }

    [Fact]
    public async Task A_lossy_build_fails_the_count_check_and_is_not_swapped_in()
    {
        var h = new IndexHarness();
        await h.RebuildAsync(cancellationToken: _ct);
        h.Options.SampleSize = 0;
        h.Cluster.SilentlyDropWhen = document => int.Parse(document.Id) % 10 == 0;

        var result = await h.RebuildAsync(cancellationToken: _ct);

        result.Swapped.ShouldBeFalse();
        var count = result.Verification.Checks.Single(c => c.Name == "count");
        count.Passed.ShouldBeFalse();
        count.Detail.ShouldContain("index holds 90, expected 100");
        h.Cluster.AliasTargets(h.Alias).ShouldBe([h.Index(1)]);
    }

    [Fact]
    public async Task A_small_count_deviation_within_the_tolerance_is_accepted()
    {
        var h = new IndexHarness();
        h.Options.SampleSize = 0;
        h.Options.CountTolerance = 0.05;
        h.Cluster.SilentlyDropWhen = document => document.Id is "5" or "6" or "7";

        var result = await h.RebuildAsync(cancellationToken: _ct);

        result.Swapped.ShouldBeTrue(result.Verification.Summary);
        result.Verification.IndexCount.ShouldBe(97);
    }

    [Fact]
    public async Task The_spot_check_catches_documents_that_arrive_with_different_field_values_though_the_count_is_right()
    {
        var h = new IndexHarness();
        h.Cluster.CorruptOnWrite = document =>
        {
            document.Name = "garbled";
            return document;
        };

        var result = await h.RebuildAsync(cancellationToken: _ct);

        result.Swapped.ShouldBeFalse();
        result.Verification.Checks.Single(c => c.Name == "count").Passed.ShouldBeTrue();
        var spot = result.Verification.Checks.Single(c => c.Name == "spot-check");
        spot.Passed.ShouldBeFalse();
        spot.Detail.ShouldContain("with differing fields");
    }

    [Fact]
    public async Task The_spot_check_tolerates_a_missing_sample_only_as_far_as_dead_letters_explain_it()
    {
        var h = new IndexHarness();
        h.Options.MaxDeadLetters = 100;
        h.Options.SampleSize = 100;
        h.Sync.Writer.RejectWhen = document => document.Id == "3" ? new BulkItemFailure(document.Id, 400, "mapper_parsing_exception: bad value", false) : null;

        var result = await h.RebuildAsync(cancellationToken: _ct);

        result.Swapped.ShouldBeTrue(result.Verification.Summary);
        result.Verification.Checks.Single(c => c.Name == "spot-check").Detail.ShouldContain("1 missing (up to 1 tolerated)");

        // The same sample with no dead letters to explain the gap is a failure.
        h.Sync.Writer.RejectWhen = null;
        h.Cluster.SilentlyDropWhen = document => document.Id == "3";
        h.Options.CountTolerance = 0.5;
        var lossy = await h.RebuildAsync(cancellationToken: _ct);
        lossy.Swapped.ShouldBeFalse();
        lossy.Verification.Checks.Single(c => c.Name == "spot-check").Passed.ShouldBeFalse();
    }

    [Fact]
    public async Task Rollback_points_the_alias_back_at_the_previous_version_and_can_roll_forward_again()
    {
        var h = new IndexHarness();
        await h.RebuildAsync(cancellationToken: _ct);
        await h.RebuildAsync(cancellationToken: _ct);

        var back = await h.Manager.RollbackAsync(h.Alias, null, _ct);

        back.Version.ShouldBe(1);
        back.PreviousIndex.ShouldBe(h.Index(2));
        h.Cluster.AliasTargets(h.Alias).ShouldBe([h.Index(1)]);
        h.Cluster.Swaps[^1].RemoveIndexes.ShouldBe([h.Index(2)], "the rollback is one alias request as well");
        h.Record(1).State.ShouldBe(IndexVersionState.Live);
        h.Record(2).State.ShouldBe(IndexVersionState.Retired);

        var forward = await h.Manager.RollbackAsync(h.Alias, null, _ct);

        forward.Version.ShouldBe(2);
        h.Cluster.AliasTargets(h.Alias).ShouldBe([h.Index(2)]);
    }

    [Fact]
    public async Task Rollback_can_target_a_specific_retained_version()
    {
        var h = new IndexHarness(documents: 20);
        for (var i = 0; i < 3; i++)
        {
            await h.RebuildAsync(cancellationToken: _ct);
        }

        var result = await h.Manager.RollbackAsync(h.Alias, 1, _ct);

        result.Version.ShouldBe(1);
        h.Cluster.AliasTargets(h.Alias).ShouldBe([h.Index(1)]);
        (await Should.ThrowAsync<RollbackRefusedException>(() => h.Manager.RollbackAsync(h.Alias, 9, _ct))).Message.ShouldContain("not a retained version");
    }

    [Fact]
    public async Task Rollback_is_refused_when_nothing_was_retained()
    {
        var h = new IndexHarness();

        (await Should.ThrowAsync<RollbackRefusedException>(() => h.Manager.RollbackAsync(h.Alias, null, _ct))).Message.ShouldContain("No retained version");

        await h.RebuildAsync(cancellationToken: _ct);
        await Should.ThrowAsync<RollbackRefusedException>(() => h.Manager.RollbackAsync(h.Alias, null, _ct));
        h.Cluster.AliasTargets(h.Alias).ShouldBe([h.Index(1)]);
    }

    [Fact]
    public async Task Rollback_skips_a_retained_index_that_no_longer_exists_and_refuses_when_none_remain()
    {
        var h = new IndexHarness(documents: 20);
        for (var i = 0; i < 3; i++)
        {
            await h.RebuildAsync(cancellationToken: _ct);
        }

        await h.Cluster.DeleteIndexAsync(h.Index(2), _ct);

        var result = await h.Manager.RollbackAsync(h.Alias, null, _ct);

        result.Version.ShouldBe(1, "version 2 was deleted behind our back");
        h.Versions.All.Select(r => r.Version).ShouldBe([1, 3]);

        await h.Cluster.DeleteIndexAsync(h.Index(3), _ct);
        await Should.ThrowAsync<RollbackRefusedException>(() => h.Manager.RollbackAsync(h.Alias, 3, _ct));
    }

    [Fact]
    public async Task A_rebuild_that_crashed_resumes_into_the_same_index_without_redoing_finished_batches()
    {
        var h = new IndexHarness(documents: 300);
        h.Sync.Writer.ThrowWhen = (call, _) => call == 3 ? new SearchIndexUnavailableException("simulated crash") : null;
        var options = h.Sync.Fast(100);
        options.MaxBulkAttempts = 1;

        await Should.ThrowAsync<SearchIndexUnavailableException>(() =>
            h.Manager.RebuildAsync(h.Alias, IndexHarness.Collection, options, null, "run-1", _ct));

        h.Cluster.AliasTargets(h.Alias).ShouldBeEmpty("nothing was swapped in");
        h.Record(1).State.ShouldBe(IndexVersionState.Building);
        h.Sync.Checkpoints.Current($"rebuild:{h.Index(1)}").ShouldBe(SyncHarness.Id(200));
        h.Cluster.Ids(h.Index(1)).Count.ShouldBe(200);

        h.Sync.Writer.ThrowWhen = null;
        var resumed = await h.Manager.RebuildAsync(h.Alias, IndexHarness.Collection, options, null, "run-2", _ct);

        resumed.Resumed.ShouldBeTrue();
        resumed.IndexName.ShouldBe(h.Index(1), "no new version was started");
        resumed.Sync.ResumedFromId.ShouldBe("200");
        resumed.Swapped.ShouldBeTrue(resumed.Verification.Summary);
        h.Cluster.Ids(h.Alias).Count.ShouldBe(300);
        h.Cluster.IndexOperations.ShouldBe(300, "the finished batches were not indexed again");
        h.Versions.All.Select(r => r.Version).ShouldBe([1]);
    }

    [Fact]
    public async Task A_deposed_owner_never_swaps_the_alias()
    {
        var h = new IndexHarness();
        var hooks = new RebuildHooks { EnsureOwnership = _ => Task.FromException(new LeaseLostException(IndexHarness.Collection)) };

        await Should.ThrowAsync<LeaseLostException>(() => h.RebuildAsync(hooks, cancellationToken: _ct));

        h.Cluster.Swaps.ShouldBeEmpty();
        h.Cluster.AliasTargets(h.Alias).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_plain_index_named_like_the_alias_is_replaced_by_the_alias_in_the_same_request()
    {
        var h = new IndexHarness(documents: 40);
        await h.Sync.Runner.RunAsync(IndexHarness.Collection, h.Sync.Fast(), _ct);
        h.Cluster.IndexNames.ShouldBe([h.Alias], "a plain sync created a concrete index with the alias name");

        var result = await h.RebuildAsync(cancellationToken: _ct);

        result.Swapped.ShouldBeTrue(result.Verification.Summary);
        var swap = h.Cluster.Swaps.ShouldHaveSingleItem();
        swap.ReplaceConcreteIndex.ShouldBeTrue();
        swap.RemoveIndexes.ShouldBe([h.Alias]);
        h.Cluster.IndexNames.ShouldBe([h.Index(1)]);
        h.Cluster.AliasTargets(h.Alias).ShouldBe([h.Index(1)]);
    }

    [Fact]
    public async Task A_plain_sync_after_versioning_writes_through_the_alias_into_the_live_index()
    {
        var h = new IndexHarness(documents: 40);
        await h.RebuildAsync(cancellationToken: _ct);
        h.Sync.Source.Add(IndexHarness.Collection, InMemorySource.Hotels(Enumerable.Range(1, 45).Select(i => (long)i)));

        await h.Sync.Runner.RunAsync(IndexHarness.Collection, h.Sync.Fast(), _ct);

        h.Cluster.Ids(h.Index(1)).Count.ShouldBe(45);
        h.Cluster.IndexNames.ShouldBe([h.Index(1)], "no concrete index appeared under the alias name");
    }

    [Fact]
    public async Task The_overview_lists_versions_the_current_one_the_retained_ones_and_the_last_verification()
    {
        var h = new IndexHarness(documents: 20);
        await h.RebuildAsync(cancellationToken: _ct);
        await h.RebuildAsync(cancellationToken: _ct);
        h.Sync.Writer.RejectWhen = document => document.Id == "2" ? new BulkItemFailure(document.Id, 400, "mapper_parsing_exception: bad value", false) : null;
        await h.RebuildAsync(cancellationToken: _ct);

        var overview = await h.Manager.GetOverviewAsync(h.Alias, _ct);

        overview.Current.ShouldBe(h.Index(2));
        overview.LegacyIndex.ShouldBeFalse();
        overview.Versions.Select(v => (v.Version, v.State, v.IsCurrent, v.ExistsOnCluster)).ShouldBe(
        [
            (3, IndexVersionState.Failed, false, true),
            (2, IndexVersionState.Live, true, true),
            (1, IndexVersionState.Retired, false, true),
        ]);
        overview.LastVerification!.Passed.ShouldBeFalse("the latest verification is the failed build's");
    }

    [Fact]
    public async Task A_version_store_that_lags_behind_the_cluster_is_reconciled_from_the_alias_target()
    {
        var h = new IndexHarness(documents: 20);
        await h.RebuildAsync(cancellationToken: _ct);
        h.Versions.FailSaveWhen = record => record.Version == 2 && record.State == IndexVersionState.Live ? new InvalidOperationException("store down") : null;

        await Should.ThrowAsync<InvalidOperationException>(() => h.RebuildAsync(cancellationToken: _ct));

        h.Cluster.AliasTargets(h.Alias).ShouldBe([h.Index(2)], "the swap itself succeeded");
        h.Versions.FailSaveWhen = null;
        var overview = await h.Manager.GetOverviewAsync(h.Alias, _ct);

        overview.Current.ShouldBe(h.Index(2));
        overview.Versions.Single(v => v.Version == 2).State.ShouldBe(IndexVersionState.Live);
        overview.Versions.Single(v => v.Version == 1).State.ShouldBe(IndexVersionState.Retired);
        (await h.Manager.RollbackAsync(h.Alias, null, _ct)).Version.ShouldBe(1);
    }

    [Fact]
    public void Versioned_index_names_round_trip_and_unrelated_names_are_ignored()
    {
        IndexVersionNaming.For("content-hotels", 12).ShouldBe("content-hotels-v12");
        IndexVersionNaming.TryParse("content-hotels", "content-hotels-v12").ShouldBe(12);
        IndexVersionNaming.TryParse("content-hotels", "content-hotels-v").ShouldBeNull();
        IndexVersionNaming.TryParse("content-hotels", "content-hotels-v1x").ShouldBeNull();
        IndexVersionNaming.TryParse("content-hotels", "content-hotels").ShouldBeNull();
        IndexVersionNaming.TryParse("content-hotels", "content-hotels-v99999999999").ShouldBeNull();
    }
}

public class IndexVersioningOptionsTests
{
    [Theory]
    [InlineData(0, 0.001, 0, 10)]
    [InlineData(21, 0.001, 0, 10)]
    [InlineData(2, -0.1, 0, 10)]
    [InlineData(2, 1.0, 0, 10)]
    [InlineData(2, double.NaN, 0, 10)]
    [InlineData(2, 0.001, -1, 10)]
    [InlineData(2, 0.001, 0, -1)]
    [InlineData(2, 0.001, 0, 1001)]
    public void Invalid_settings_are_rejected(int retained, double tolerance, long maxDeadLetters, int sampleSize)
    {
        var options = new IndexVersioningOptions { RetainedVersions = retained, CountTolerance = tolerance, MaxDeadLetters = maxDeadLetters, SampleSize = sampleSize };

        new IndexVersioningOptionsValidator().Validate(null, options).Failed.ShouldBeTrue();
    }

    [Fact]
    public void The_defaults_are_valid() =>
        new IndexVersioningOptionsValidator().Validate(null, new IndexVersioningOptions()).Succeeded.ShouldBeTrue();
}
