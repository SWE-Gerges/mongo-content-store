using Content.Migration;
using Content.Tests.Support;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MongoDB.Bson;
using Content.Sync.Checkpoints;

namespace Content.Tests.Migration;

/// <summary>The migration the Development settings define, against a table shaped like the one the seed tool writes.</summary>
public sealed class DemoDefinitionTests : IDisposable
{
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;
    private readonly SqliteSource _source = SqliteSource.Create();

    public void Dispose() => _source.Dispose();

    private static string SettingsPath()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "MongoContentStore.slnx")))
        {
            directory = directory.Parent;
        }

        directory.ShouldNotBeNull();
        return Path.Combine(directory.FullName, "src", "Content.Api", "appsettings.Development.json");
    }

    private MigrationOptions Load()
    {
        var configuration = new ConfigurationBuilder().AddJsonFile(SettingsPath()).Build();
        var options = configuration.GetSection(MigrationOptions.SectionName).Get<MigrationOptions>();
        options.ShouldNotBeNull();
        return options;
    }

    [Fact]
    public void The_development_settings_define_a_valid_migration() =>
        new MigrationOptionsValidator().Validate(null, Load()).Succeeded.ShouldBeTrue();

    [Fact]
    public async Task The_demo_migration_moves_rows_shaped_like_the_seed_tools_output()
    {
        var definition = Load().Find("hotels-from-sqlite")!;
        definition.ConnectionString = _source.ConnectionString;

        // The same table the seed tool's --relational option writes.
        _source.Execute("CREATE TABLE hotels (id INTEGER PRIMARY KEY, code TEXT, name TEXT, city_code TEXT, country_code TEXT, rating TEXT, group_id TEXT)");
        for (var id = 1; id <= 40; id++)
        {
            var name = id == 5 ? "NULL" : $"'Grand Hotel {id}'";
            var rating = id == 6 ? "'n/a'" : "'4.5'";
            _source.Execute($"INSERT INTO hotels VALUES ({id}, 'AH{id:D6}', {name}, 'C0{id % 10}', 'X{id % 10}', {rating}, 'G{id}')");
        }

        var target = new InMemoryMigrationTarget();
        var runner = new MigrationRunner(
            new DbRowSource([new SqliteProvider()]),
            target,
            new InMemoryCheckpoints(),
            new InMemoryDeadLetters(),
            NullLogger<MigrationRunner>.Instance);

        var summary = await runner.RunAsync(definition, 15, _ct);

        summary.Migrated.ShouldBe(38);
        summary.Invalid.ShouldBe(2);
        summary.Verification.Matches.ShouldBeTrue(summary.Verification.Describe());
        target.Get(1)!["CountryCode"].AsString.ShouldBe("X1");
        target.Get(1)!["Rating"].ShouldBe(new BsonDouble(4.5));
        target.Get(1)!["Name"].AsString.ShouldBe("Grand Hotel 1");
    }
}
