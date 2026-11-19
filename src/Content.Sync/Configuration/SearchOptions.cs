using Microsoft.Extensions.Options;

namespace Content.Sync.Configuration;

/// <summary>Connection and index settings for the OpenSearch cluster.</summary>
public sealed class SearchOptions
{
    public const string SectionName = "OpenSearch";

    public string Uri { get; set; } = string.Empty;

    public string? Username { get; set; }

    public string? Password { get; set; }

    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>Prefix for index names; the index for a Mongo collection is <c>{prefix}{collection}</c>.</summary>
    public string IndexPrefix { get; set; } = "content-";

    public int Shards { get; set; } = 1;

    /// <summary>Replica count for newly created indexes; 0 suits the single-node demo.</summary>
    public int Replicas { get; set; }
}

internal sealed class SearchOptionsValidator : IValidateOptions<SearchOptions>
{
    public ValidateOptionsResult Validate(string? name, SearchOptions options)
    {
        var failures = new List<string>();

        if (!System.Uri.TryCreate(options.Uri, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
        {
            failures.Add("OpenSearch:Uri must be an absolute http or https URI.");
        }

        if (string.IsNullOrEmpty(options.IndexPrefix)
            || options.IndexPrefix.Any(c => !(char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c is '-' or '_'))
            || options.IndexPrefix[0] is '-' or '_')
        {
            failures.Add("OpenSearch:IndexPrefix must be lowercase letters, digits, '-' or '_' and must not start with '-' or '_'.");
        }

        if (options.Shards < 1)
        {
            failures.Add("OpenSearch:Shards must be at least 1.");
        }

        if (options.Replicas < 0)
        {
            failures.Add("OpenSearch:Replicas must not be negative.");
        }

        if (options.RequestTimeout <= TimeSpan.Zero)
        {
            failures.Add("OpenSearch:RequestTimeout must be positive.");
        }

        if (string.IsNullOrEmpty(options.Username) != string.IsNullOrEmpty(options.Password))
        {
            failures.Add("OpenSearch:Username and OpenSearch:Password must be set together.");
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
