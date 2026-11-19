using System.Globalization;
using Content.Sync.DeadLetters;
using Content.Sync.Search;
using Content.Sync.Source;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Content.Sync.Indexing;

/// <summary>
/// Judges a freshly built index against its source before it may replace the live one: the document count must be
/// within tolerance of what the source minus the dead letters should have produced, the build must not have
/// dead-lettered more than a threshold, and a random sample of source documents must be present in the new index
/// with the same field values.
/// </summary>
public sealed class IndexVerifier(
    ISearchIndexAdmin admin,
    ISearchDocumentReader reader,
    ISourceStatistics source,
    IDeadLetterStore deadLetters,
    IOptions<IndexVersioningOptions> options,
    TimeProvider clock,
    ILogger<IndexVerifier> logger)
{
    public async Task<IndexVerification> VerifyAsync(string collection, string indexName, CancellationToken cancellationToken)
    {
        var settings = options.Value;
        await admin.RefreshAsync(indexName, cancellationToken).ConfigureAwait(false);
        var sourceCount = await source.CountAsync(collection, cancellationToken).ConfigureAwait(false);
        var indexCount = await admin.CountAsync(indexName, cancellationToken).ConfigureAwait(false);
        var dead = await deadLetters.CountAsync(collection, indexName, cancellationToken).ConfigureAwait(false);

        var checks = new List<VerificationCheck>
        {
            CountCheck(settings, sourceCount, indexCount, dead),
            new(
                "dead-letters",
                dead <= settings.MaxDeadLetters,
                $"{dead} document(s) dead-lettered by this build, at most {settings.MaxDeadLetters} allowed"),
        };

        if (settings.SampleSize > 0)
        {
            checks.Add(await SpotCheckAsync(collection, indexName, settings.SampleSize, dead, cancellationToken).ConfigureAwait(false));
        }

        var verification = new IndexVerification(checks.All(c => c.Passed), clock.GetUtcNow(), sourceCount, indexCount, dead, checks);
        logger.LogInformation(
            "Verification of {Index} {Outcome}: source {Source}, index {Indexed}, dead letters {Dead}.",
            indexName,
            verification.Passed ? "passed" : "failed: " + verification.Summary,
            sourceCount,
            indexCount,
            dead);
        return verification;
    }

    private static VerificationCheck CountCheck(IndexVersioningOptions settings, long sourceCount, long indexCount, long dead)
    {
        var expected = Math.Max(0, sourceCount - dead);
        var allowed = (long)Math.Ceiling(sourceCount * settings.CountTolerance);
        var deviation = Math.Abs(indexCount - expected);
        return new VerificationCheck(
            "count",
            deviation <= allowed,
            string.Create(
                CultureInfo.InvariantCulture,
                $"index holds {indexCount}, expected {expected} (source {sourceCount} minus {dead} dead-lettered), allowed deviation {allowed}"));
    }

    private async Task<VerificationCheck> SpotCheckAsync(string collection, string indexName, int sampleSize, long deadLetters, CancellationToken cancellationToken)
    {
        var sample = await source.SampleAsync(collection, sampleSize, cancellationToken).ConfigureAwait(false);
        var expected = new Dictionary<string, SearchDocument>(StringComparer.Ordinal);
        var undecodable = 0;
        foreach (var raw in sample)
        {
            if (HotelSearchDocumentMapper.TryMap(raw, out var document))
            {
                expected[document.Id] = document;
            }
            else
            {
                undecodable++;
            }
        }

        var found = expected.Count == 0
            ? new Dictionary<string, SearchDocument>()
            : await reader.GetByIdsAsync(indexName, [.. expected.Keys], cancellationToken).ConfigureAwait(false);

        var missing = expected.Keys.Where(id => !found.ContainsKey(id)).ToList();
        var differing = expected
            .Where(e => found.TryGetValue(e.Key, out var actual) && !Same(e.Value, actual))
            .Select(e => e.Key)
            .ToList();

        // A sampled document the build dead-lettered is legitimately absent, so as many may be missing as there are dead letters.
        var tolerated = Math.Min(deadLetters, expected.Count);
        var passed = differing.Count == 0 && missing.Count <= tolerated;
        var detail = string.Create(
            CultureInfo.InvariantCulture,
            $"{expected.Count} sampled document(s) compared ({undecodable} not decodable and skipped): {missing.Count} missing (up to {tolerated} tolerated), {differing.Count} with differing fields");
        if (!passed)
        {
            detail += $"; first offenders: {string.Join(", ", missing.Concat(differing).Take(5))}";
        }

        return new VerificationCheck("spot-check", passed, detail);
    }

    private static bool Same(SearchDocument expected, SearchDocument actual) =>
        expected.Name == actual.Name
        && expected.Code == actual.Code
        && expected.CityCode == actual.CityCode
        && expected.CountryCode == actual.CountryCode
        && expected.Rating.Equals(actual.Rating)
        && expected.ProviderKeys.SequenceEqual(actual.ProviderKeys, StringComparer.Ordinal);
}
