using System.Globalization;
using Content.Store.Configuration;

namespace Content.Seed;

/// <summary>Command-line settings of the seed tool.</summary>
internal sealed record SeedOptions(
    string Connection,
    string Database,
    IReadOnlyList<string> Suppliers,
    int Count,
    int Seed,
    double MalformedRate,
    int BatchSize,
    bool Reset,
    string? RelationalPath)
{
    public const string Usage = """
        Content.Seed - generates synthetic hotel content (with deliberately malformed fields) into MongoDB.

          --connection <uri>        MongoDB connection string   (default: mongodb://localhost:27017)
          --database <name>         database name               (default: content)
          --suppliers <a,b,...>     supplier keys               (default: supplier-a,supplier-b,supplier-c)
          --count <n>               hotels per supplier         (default: 50000)
          --seed <n>                random seed                 (default: 12345)
          --malformed-rate <0..1>   share of documents that get at least one defect (default: 0.02)
          --batch-size <n>          documents per bulk write    (default: 5000)
          --relational <path>       write a SQLite source for the migration demo (table hotels) instead of filling MongoDB
          --reset                   drop the target collections first
          --help                    show this text

        The same --seed and --count always produce the same documents, so ids, codes and room ids are stable.
        """;

    public static SeedOptions Parse(string[] args)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        var reset = false;
        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (arg == "--reset")
            {
                reset = true;
            }
            else if (arg.StartsWith("--", StringComparison.Ordinal) && i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
            {
                values[arg] = args[++i];
            }
            else
            {
                throw new ArgumentException($"Unknown or incomplete argument '{arg}'.");
            }
        }

        string Get(string name, string fallback) => values.TryGetValue(name, out var value) ? value : fallback;

        var suppliers = Get("--suppliers", "supplier-a,supplier-b,supplier-c")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var invalid = suppliers.FirstOrDefault(key => !SupplierKey.IsValid(key));
        if (invalid is not null || suppliers.Length == 0)
        {
            throw new ArgumentException($"'{invalid}' is not a valid supplier key.");
        }

        var options = new SeedOptions(
            Get("--connection", Environment.GetEnvironmentVariable("SEED_MONGO_CONNECTION") ?? "mongodb://localhost:27017"),
            Get("--database", "content"),
            suppliers,
            int.Parse(Get("--count", "50000"), CultureInfo.InvariantCulture),
            int.Parse(Get("--seed", "12345"), CultureInfo.InvariantCulture),
            double.Parse(Get("--malformed-rate", "0.02"), CultureInfo.InvariantCulture),
            int.Parse(Get("--batch-size", "5000"), CultureInfo.InvariantCulture),
            reset,
            values.GetValueOrDefault("--relational"));

        if (options.Count < 1 || options.BatchSize < 1 || options.MalformedRate is < 0 or > 1)
        {
            throw new ArgumentException("--count and --batch-size must be positive and --malformed-rate must be between 0 and 1.");
        }

        return options;
    }
}
