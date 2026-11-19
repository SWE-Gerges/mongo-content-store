using Microsoft.Extensions.Options;
using MongoDB.Driver;

namespace Content.Mongo.Configuration;

internal sealed class MongoOptionsValidator : IValidateOptions<MongoOptions>
{
    private static readonly char[] InvalidDatabaseNameCharacters = ['/', '\\', '.', ' ', '"', '$', '*', '<', '>', ':', '|', '?', '\0'];

    public ValidateOptionsResult Validate(string? name, MongoOptions options)
    {
        var failures = new List<string>();

        if (string.IsNullOrWhiteSpace(options.ConnectionString))
        {
            failures.Add("Mongo:ConnectionString is required.");
        }
        else
        {
            try
            {
                var url = MongoUrl.Create(options.ConnectionString);
                if (url.SocketTimeout != TimeSpan.Zero)
                {
                    failures.Add("Mongo:ConnectionString must not set socketTimeoutMS; long socket timeouts hide outages.");
                }
            }
            catch (Exception ex) when (ex is MongoConfigurationException or FormatException or ArgumentException)
            {
                failures.Add($"Mongo:ConnectionString is not a valid MongoDB connection string: {ex.Message}");
            }
        }

        if (string.IsNullOrWhiteSpace(options.DefaultDatabase)
            || options.DefaultDatabase.IndexOfAny(InvalidDatabaseNameCharacters) >= 0
            || options.DefaultDatabase.Length > 63)
        {
            failures.Add("Mongo:DefaultDatabase must be a valid database name (1-63 characters, no spaces or / \\ . \" $ * < > : | ?).");
        }

        if (options.ConnectTimeout <= TimeSpan.Zero)
        {
            failures.Add("Mongo:ConnectTimeout must be positive.");
        }

        if (options.ServerSelectionTimeout <= TimeSpan.Zero)
        {
            failures.Add("Mongo:ServerSelectionTimeout must be positive.");
        }

        if (options.MaxConnectionPoolSize is < 1 or > 10_000)
        {
            failures.Add("Mongo:MaxConnectionPoolSize must be between 1 and 10000.");
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
