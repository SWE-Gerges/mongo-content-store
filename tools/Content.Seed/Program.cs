using System.Diagnostics;
using Content.Mongo;
using Content.Mongo.Repository;
using Content.Store;
using Content.Seed;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Driver;

SeedOptions options;
try
{
    if (args.Contains("--help") || args.Contains("-h"))
    {
        Console.WriteLine(SeedOptions.Usage);
        return 0;
    }

    options = SeedOptions.Parse(args);
}
catch (Exception ex) when (ex is ArgumentException or FormatException or OverflowException)
{
    Console.Error.WriteLine(ex.Message);
    Console.Error.WriteLine();
    Console.Error.WriteLine(SeedOptions.Usage);
    return 2;
}

if (options.RelationalPath is not null)
{
    var (rows, defective) = RelationalSeeder.Write(options.RelationalPath, options.Count, options.Seed, options.MalformedRate, options.Reset);
    Console.WriteLine($"{options.RelationalPath}: {rows:N0} hotels in table 'hotels', {defective:N0} deliberately malformed (seed {options.Seed}).");
    return 0;
}

var configuration = new ConfigurationBuilder()
    .AddInMemoryCollection(new Dictionary<string, string?>
    {
        ["Mongo:ConnectionString"] = options.Connection,
        ["Mongo:DefaultDatabase"] = options.Database,
        ["Mongo:ServerSelectionTimeout"] = "00:00:30",
        ["Mongo:BootstrapIndexes"] = "false",
    })
    .Build();

var services = new ServiceCollection();
services.AddContentMongo(configuration);
await using var provider = services.BuildServiceProvider();

using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cancellation.Cancel();
};

try
{
    await SeedAsync(provider, options, cancellation.Token);
    return 0;
}
catch (OperationCanceledException)
{
    Console.Error.WriteLine("Cancelled.");
    return 130;
}

static async Task SeedAsync(IServiceProvider provider, SeedOptions options, CancellationToken cancellationToken)
{
    var repository = provider.GetRequiredService<IMongoRepository<BsonDocument>>();
    var database = provider.GetRequiredService<IMongoDatabaseFactory>().Get();
    var naming = new HotelCollectionNaming();
    var generator = new HotelGenerator(options.Seed, options.MalformedRate);
    var totals = Defect.All.ToDictionary(kind => kind, _ => 0, StringComparer.Ordinal);
    var watch = Stopwatch.StartNew();

    var collections = options.Suppliers.Select(naming.ForSupplier).Append(naming.Master).ToList();
    if (options.Reset)
    {
        foreach (var collection in collections)
        {
            await database.DropCollectionAsync(collection, cancellationToken);
        }
    }

    for (var supplierIndex = 0; supplierIndex < options.Suppliers.Count; supplierIndex++)
    {
        var key = options.Suppliers[supplierIndex];
        var collection = naming.ForSupplier(key);
        var defective = 0;
        foreach (var range in Ranges(options.Count, options.BatchSize))
        {
            var batch = new List<BsonDocument>(range.Count);
            for (var id = range.First; id < range.First + range.Count; id++)
            {
                var (document, defects) = generator.Supplier(key, supplierIndex, id);
                batch.Add(document);
                if (defects.Count > 0)
                {
                    defective++;
                    foreach (var defect in defects)
                    {
                        totals[defect]++;
                    }
                }
            }

            await repository.UpsertManyAsync(collection, batch, d => d["_id"], cancellationToken: cancellationToken);
        }

        Console.WriteLine($"{collection}: {options.Count:N0} hotels, {defective:N0} with at least one malformed field");
    }

    foreach (var range in Ranges(options.Count, options.BatchSize))
    {
        var batch = Enumerable.Range(0, range.Count).Select(offset => generator.Master(options.Suppliers, range.First + offset)).ToList();
        await repository.UpsertManyAsync(naming.Master, batch, d => d["_id"], cancellationToken: cancellationToken);
    }

    Console.WriteLine($"{naming.Master}: {options.Count:N0} merged hotels");
    Console.WriteLine("Injected defects: " + string.Join(", ", totals.Where(t => t.Value > 0).Select(t => $"{t.Key}={t.Value:N0}")));
    Console.WriteLine($"Done in {watch.Elapsed.TotalSeconds:N1}s (seed {options.Seed}, malformed rate {options.MalformedRate:P1}).");
}

static IEnumerable<(long First, int Count)> Ranges(int total, int batchSize)
{
    for (long first = 1; first <= total; first += batchSize)
    {
        yield return (first, (int)Math.Min(batchSize, total - first + 1));
    }
}
