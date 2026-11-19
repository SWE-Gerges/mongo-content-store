using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using Content.Mongo.Serialization;
using Content.Store;
using Content.Store.Models;
using MongoDB.Bson;
using MongoDB.Driver;

// Compares the two ways of asking for N hotels by id:
//   numeric  : { _id: { $in: [ <int64> ... ] } }                       (what the content store does)
//   stringly : { $expr: { $in: [ { $toString: "$_id" }, ["..."] ] } }   (the ids.Contains(h.Id.ToString()) shape)
// on whatever data is in the target collection, and writes the measurements to a markdown file.

var options = BenchOptions.Parse(args);
if (options is null)
{
    Console.WriteLine(BenchOptions.Usage);
    return 2;
}

HotelBsonMappings.EnsureRegistered(new MalformedFieldCounter());
var client = new MongoClient(options.Connection);
var database = client.GetDatabase(options.Database);
var collection = database.GetCollection<HotelDocument>(options.Collection);

var documents = await collection.EstimatedDocumentCountAsync();
if (documents == 0)
{
    Console.Error.WriteLine($"{options.Database}.{options.Collection} is empty; run tools/Content.Seed first.");
    return 1;
}

var maxId = (await collection.Find(FilterDefinition<HotelDocument>.Empty).Sort(Builders<HotelDocument>.Sort.Descending("_id")).Limit(1).Project<BsonDocument>("{ _id: 1 }").FirstAsync())["_id"].ToInt64();
var random = new Random(options.Seed);
var ids = Enumerable.Range(0, options.IdCount).Select(_ => random.NextInt64(1, maxId + 1)).Distinct().ToList();

var projection = Builders<HotelDocument>.Projection.Include(h => h.Id);
FilterDefinition<HotelDocument> numeric = Builders<HotelDocument>.Filter.In(h => h.Id, ids);
FilterDefinition<HotelDocument> stringly = new BsonDocument(
    "$expr",
    new BsonDocument("$in", new BsonArray { new BsonDocument("$toString", "$_id"), new BsonArray(ids.Select(i => i.ToString(CultureInfo.InvariantCulture))) }));

var results = new List<Result>
{
    await MeasureAsync("numeric $in on _id", numeric),
    await MeasureAsync("stringified id via $expr/$toString", stringly),
};

var serverVersion = (await database.RunCommandAsync<BsonDocument>(new BsonDocument("buildInfo", 1)))["version"].AsString;
var markdown = Render(results, serverVersion);
await File.WriteAllTextAsync(options.Output, markdown);
Console.WriteLine(markdown);
Console.WriteLine($"Written to {options.Output}");
return 0;

async Task<Result> MeasureAsync(string label, FilterDefinition<HotelDocument> filter)
{
    async Task<int> RunOnceAsync() =>
        (await collection.Find(filter).Project<HotelDocument>(projection).ToListAsync()).Count;

    var found = 0;
    for (var i = 0; i < options.Warmup; i++)
    {
        found = await RunOnceAsync();
    }

    var timings = new List<double>();
    for (var i = 0; i < options.Iterations; i++)
    {
        var watch = Stopwatch.StartNew();
        found = await RunOnceAsync();
        watch.Stop();
        timings.Add(watch.Elapsed.TotalMilliseconds);
    }

    var rendered = filter.Render(new RenderArgs<HotelDocument>(collection.DocumentSerializer, collection.Settings.SerializerRegistry));
    var explain = await database.RunCommandAsync<BsonDocument>(new BsonDocument
    {
        { "explain", new BsonDocument { { "find", options.Collection }, { "filter", rendered }, { "projection", new BsonDocument("_id", 1) } } },
        { "verbosity", "executionStats" },
    });
    var stats = explain["executionStats"].AsBsonDocument;
    var stages = new List<string>();
    CollectStages(explain["queryPlanner"]["winningPlan"], stages);

    timings.Sort();
    return new Result(
        label,
        found,
        timings[0],
        Percentile(timings, 0.5),
        Percentile(timings, 0.95),
        timings[^1],
        stats["totalDocsExamined"].ToInt64(),
        stats["totalKeysExamined"].ToInt64(),
        string.Join(" > ", stages));
}

static double Percentile(List<double> sorted, double p) => sorted[(int)Math.Min(sorted.Count - 1, Math.Floor(p * sorted.Count))];

static void CollectStages(BsonValue plan, List<string> stages)
{
    if (!plan.IsBsonDocument)
    {
        return;
    }

    var document = plan.AsBsonDocument;
    if (document.TryGetValue("stage", out var stage))
    {
        stages.Add(stage.AsString);
    }

    if (document.TryGetValue("inputStage", out var input))
    {
        CollectStages(input, stages);
    }
}

string Render(List<Result> measured, string version)
{
    var numericMedian = measured[0].MedianMs;
    var text = new StringBuilder();
    text.AppendLine("# Benchmark: numeric `$in` versus stringified ids");
    text.AppendLine();
    text.AppendLine("Produced by `bench/Content.Bench` on the machine and data described below. Nothing here is a claim about other hardware or data sizes.");
    text.AppendLine();
    text.AppendLine($"- Collection: `{options.Database}.{options.Collection}`, about {documents:N0} documents");
    text.AppendLine($"- Ids requested per query: {ids.Count} (random, seed {options.Seed}); projection `{{ _id: 1 }}`");
    text.AppendLine($"- Warm-up runs: {options.Warmup}; measured runs per query: {options.Iterations}");
    text.AppendLine($"- MongoDB server {version}; .NET {Environment.Version}; {RuntimeInformation.OSDescription}; {Environment.ProcessorCount} logical cores");
    text.AppendLine();
    text.AppendLine("| Query | Docs returned | Min ms | Median ms | p95 ms | Max ms | Keys examined | Docs examined | Plan |");
    text.AppendLine("|---|---:|---:|---:|---:|---:|---:|---:|---|");
    foreach (var r in measured)
    {
        text.AppendLine(string.Create(CultureInfo.InvariantCulture, $"| {r.Label} | {r.Found} | {r.MinMs:F2} | {r.MedianMs:F2} | {r.P95Ms:F2} | {r.MaxMs:F2} | {r.KeysExamined:N0} | {r.DocsExamined:N0} | {r.Plan} |"));
    }

    text.AppendLine();
    text.AppendLine(string.Create(CultureInfo.InvariantCulture, $"Median ratio (stringified / numeric): {measured[1].MedianMs / Math.Max(numericMedian, 0.001):F1}x"));
    return text.ToString();
}

internal sealed record Result(string Label, int Found, double MinMs, double MedianMs, double P95Ms, double MaxMs, long DocsExamined, long KeysExamined, string Plan);

internal sealed record BenchOptions(string Connection, string Database, string Collection, int IdCount, int Iterations, int Warmup, int Seed, string Output)
{
    public const string Usage = """
        Content.Bench - numeric $in versus stringified-id lookups on seeded data.

          --connection <uri>    MongoDB connection string  (default: mongodb://localhost:27017)
          --database <name>     database                   (default: content)
          --collection <name>   collection                 (default: hotel_supplier-a)
          --ids <n>             ids per query              (default: 100)
          --iterations <n>      measured runs per query    (default: 20)
          --warmup <n>          warm-up runs               (default: 3)
          --seed <n>            random seed for the ids    (default: 42)
          --out <path>          markdown output file       (default: bench/RESULTS.md)
        """;

    public static BenchOptions? Parse(string[] args)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i < args.Length; i++)
        {
            if (!args[i].StartsWith("--", StringComparison.Ordinal) || i + 1 >= args.Length)
            {
                return null;
            }

            values[args[i]] = args[++i];
        }

        string Get(string name, string fallback) => values.TryGetValue(name, out var value) ? value : fallback;

        return new BenchOptions(
            Get("--connection", "mongodb://localhost:27017"),
            Get("--database", "content"),
            Get("--collection", "hotel_supplier-a"),
            int.Parse(Get("--ids", "100"), CultureInfo.InvariantCulture),
            int.Parse(Get("--iterations", "20"), CultureInfo.InvariantCulture),
            int.Parse(Get("--warmup", "3"), CultureInfo.InvariantCulture),
            int.Parse(Get("--seed", "42"), CultureInfo.InvariantCulture),
            Get("--out", "bench/RESULTS.md"));
    }
}
