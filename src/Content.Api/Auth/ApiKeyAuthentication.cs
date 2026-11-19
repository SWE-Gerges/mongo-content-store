using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace Content.Api.Auth;

public static class ApiRoles
{
    public const string Reader = "reader";
    public const string Admin = "admin";
}

/// <summary>
/// Checks a presented key against the configured ones. Keys are compared as SHA-256 digests with a fixed-time
/// comparison and every configured key is always examined, so neither the length of a key nor the position of a
/// match shows up in the response time.
/// </summary>
internal sealed class ApiKeyStore
{
    private readonly (byte[] Digest, string Role)[] _keys;

    public ApiKeyStore(IOptions<ApiAuthOptions> options)
    {
        var value = options.Value;
        _keys =
        [
            .. value.ApiKeys.Select(key => (Digest(key), ApiRoles.Reader)),
            .. value.AdminKeys.Select(key => (Digest(key), ApiRoles.Admin)),
        ];
    }

    /// <returns>The role of the matching key, or null when the key is unknown.</returns>
    public string? Authenticate(string presented)
    {
        var digest = Digest(presented);
        string? role = null;
        foreach (var (candidate, candidateRole) in _keys)
        {
            if (CryptographicOperations.FixedTimeEquals(candidate, digest))
            {
                role = candidateRole;
            }
        }

        return role;
    }

    private static byte[] Digest(string key) => SHA256.HashData(Encoding.UTF8.GetBytes(key));
}

internal sealed class ApiKeyAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    ApiKeyStore keys) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "ApiKey";
    public const string HeaderName = "X-Api-Key";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue(HeaderName, out var values) || values.Count != 1 || string.IsNullOrEmpty(values[0]))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        var role = keys.Authenticate(values[0]!);
        if (role is null)
        {
            return Task.FromResult(AuthenticateResult.Fail("The API key is not valid."));
        }

        var identity = new ClaimsIdentity(
            [new Claim(ClaimTypes.Name, "api-key"), new Claim(ClaimTypes.Role, role)],
            SchemeName);
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName)));
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties) =>
        WriteProblemAsync(StatusCodes.Status401Unauthorized, "Authentication required", $"Send a valid key in the {HeaderName} header.");

    protected override Task HandleForbiddenAsync(AuthenticationProperties properties) =>
        WriteProblemAsync(StatusCodes.Status403Forbidden, "Forbidden", "This route requires an admin API key.");

    private async Task WriteProblemAsync(int status, string title, string detail)
    {
        Response.StatusCode = status;
        var problems = Context.RequestServices.GetRequiredService<IProblemDetailsService>();
        await problems.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = Context,
            ProblemDetails = { Status = status, Title = title, Detail = detail },
        });
    }
}
