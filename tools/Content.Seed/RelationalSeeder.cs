using System.Globalization;
using Bogus;
using Microsoft.Data.Sqlite;
using MongoDB.Bson;

namespace Content.Seed;

/// <summary>
/// Writes a relational source for the migration demo: one SQLite table of hotels in the shape a legacy system would hold
/// them, with a share of rows deliberately wrong (an empty name, a rating that is not a number) so the migrator has
/// something to dead-letter. The same seed and count always produce the same rows.
/// </summary>
internal static class RelationalSeeder
{
    public static (long Rows, long Defective) Write(string path, int count, int seed, double malformedRate, bool reset)
    {
        var fullPath = Path.GetFullPath(path);
        if (reset && File.Exists(fullPath))
        {
            File.Delete(fullPath);
        }

        var directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = fullPath }.ToString());
        connection.Open();
        using (var create = connection.CreateCommand())
        {
            create.CommandText =
                "CREATE TABLE IF NOT EXISTS hotels (id INTEGER PRIMARY KEY, code TEXT, name TEXT, city_code TEXT, country_code TEXT, rating TEXT, group_id TEXT)";
            create.ExecuteNonQuery();
        }

        var generator = new HotelGenerator(seed, 0);
        long defective = 0;
        using var transaction = connection.BeginTransaction();
        using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText =
            "INSERT OR REPLACE INTO hotels (id, code, name, city_code, country_code, rating, group_id) VALUES (@id, @code, @name, @city, @country, @rating, @group)";
        var id = insert.Parameters.Add("@id", SqliteType.Integer);
        var code = insert.Parameters.Add("@code", SqliteType.Text);
        var name = insert.Parameters.Add("@name", SqliteType.Text);
        var city = insert.Parameters.Add("@city", SqliteType.Text);
        var country = insert.Parameters.Add("@country", SqliteType.Text);
        var rating = insert.Parameters.Add("@rating", SqliteType.Text);
        var group = insert.Parameters.Add("@group", SqliteType.Text);

        for (long hotelId = 1; hotelId <= count; hotelId++)
        {
            var (document, _) = generator.Supplier("supplier-a", 0, hotelId);
            var random = new Randomizer(unchecked((seed * 31) + (int)hotelId));
            var broken = random.Bool((float)malformedRate);
            id.Value = hotelId;
            code.Value = document["Code"].AsString;
            name.Value = broken && random.Bool(0.5f) ? DBNull.Value : document["Name"].AsString;
            city.Value = document["CityCode"].AsString;
            country.Value = document["CountryCode"].AsString;
            rating.Value = broken ? (name.Value is DBNull ? Text(document["Rating"]) : "n/a") : Text(document["Rating"]);

            // A group id that is a number in some rows and text in others, like the document store sees it.
            group.Value = hotelId % 2 == 0 ? hotelId.ToString(CultureInfo.InvariantCulture) : $"G{hotelId}";
            insert.ExecuteNonQuery();
            if (broken)
            {
                defective++;
            }
        }

        transaction.Commit();
        return (count, defective);
    }

    private static string Text(BsonValue value) => value.IsDouble
        ? value.AsDouble.ToString("R", CultureInfo.InvariantCulture)
        : Convert.ToString(value.ToInt32(), CultureInfo.InvariantCulture)!;
}
