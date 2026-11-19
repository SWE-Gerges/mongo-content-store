using Microsoft.Extensions.Options;

namespace Content.Api.Auth;

/// <summary>
/// API keys accepted in the <c>X-Api-Key</c> header. Keys in <see cref="AdminKeys"/> may also call the sync
/// administration routes; keys in <see cref="ApiKeys"/> may only read content. Keys come from configuration
/// (environment variables or user secrets), never from source.
/// </summary>
public sealed class ApiAuthOptions
{
    public const string SectionName = "Auth";
    public const int MinKeyLength = 16;

    public List<string> ApiKeys { get; set; } = [];

    public List<string> AdminKeys { get; set; } = [];
}

internal sealed class ApiAuthOptionsValidator : IValidateOptions<ApiAuthOptions>
{
    public ValidateOptionsResult Validate(string? name, ApiAuthOptions options)
    {
        var failures = new List<string>();
        var all = options.ApiKeys.Concat(options.AdminKeys).ToList();

        if (options.AdminKeys.Count == 0)
        {
            failures.Add("Auth:AdminKeys must contain at least one key, otherwise the sync routes cannot be used.");
        }

        if (all.Any(key => string.IsNullOrWhiteSpace(key) || key.Length < ApiAuthOptions.MinKeyLength))
        {
            failures.Add($"Every key in Auth:ApiKeys and Auth:AdminKeys must be at least {ApiAuthOptions.MinKeyLength} characters.");
        }

        if (all.Count != all.Distinct(StringComparer.Ordinal).Count())
        {
            failures.Add("Auth:ApiKeys and Auth:AdminKeys must not repeat a key (a key has exactly one role).");
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
