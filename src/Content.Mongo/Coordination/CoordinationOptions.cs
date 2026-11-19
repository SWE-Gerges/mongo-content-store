using Microsoft.Extensions.Options;

namespace Content.Mongo.Coordination;

/// <summary>Timing of the per-target run lease.</summary>
public sealed class CoordinationOptions
{
    public const string SectionName = "Coordination";

    /// <summary>How long a lease stays valid without a heartbeat. A crashed owner is replaced after this long.</summary>
    public TimeSpan LeaseDuration { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>How often the owner renews its lease and looks for a cancel request.</summary>
    public TimeSpan HeartbeatInterval { get; set; } = TimeSpan.FromSeconds(10);
}

internal sealed class CoordinationOptionsValidator : IValidateOptions<CoordinationOptions>
{
    public ValidateOptionsResult Validate(string? name, CoordinationOptions options)
    {
        var failures = new List<string>();

        if (options.LeaseDuration <= TimeSpan.Zero)
        {
            failures.Add("Coordination:LeaseDuration must be positive.");
        }

        if (options.HeartbeatInterval <= TimeSpan.Zero)
        {
            failures.Add("Coordination:HeartbeatInterval must be positive.");
        }

        if (options.HeartbeatInterval * 2 > options.LeaseDuration)
        {
            failures.Add("Coordination:HeartbeatInterval must be at most half of Coordination:LeaseDuration so one missed beat does not lose the lease.");
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}

/// <summary>Identifies this process in lease records. Random per process; carries no host information.</summary>
public sealed class InstanceIdentity
{
    public string Id { get; } = Guid.NewGuid().ToString("N")[..12];
}
