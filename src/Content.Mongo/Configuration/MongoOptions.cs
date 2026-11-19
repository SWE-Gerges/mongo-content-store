namespace Content.Mongo.Configuration;

/// <summary>
/// Connection settings for the shared MongoDB client.
/// There is deliberately no socket timeout setting: a long socket timeout hides outages
/// behind slow failures, so the driver default (no socket timeout) is kept and only the
/// connect and server-selection timeouts bound how long a caller waits.
/// </summary>
public sealed class MongoOptions
{
    public const string SectionName = "Mongo";

    public string ConnectionString { get; set; } = string.Empty;

    public string DefaultDatabase { get; set; } = string.Empty;

    public TimeSpan ConnectTimeout { get; set; } = TimeSpan.FromSeconds(5);

    public TimeSpan ServerSelectionTimeout { get; set; } = TimeSpan.FromSeconds(5);

    public int MaxConnectionPoolSize { get; set; } = 100;

    /// <summary>When true the index bootstrapper hosted service creates declared indexes at startup.</summary>
    public bool BootstrapIndexes { get; set; } = true;
}
