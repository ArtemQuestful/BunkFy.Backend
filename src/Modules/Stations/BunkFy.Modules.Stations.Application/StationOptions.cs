namespace BunkFy.Modules.Stations.Application;

public sealed class StationOptions
{
    public int ActorIdleMinutes { get; set; } = 5;
    public int ActorAbsoluteHours { get; set; } = 12;
    public int PairingDays { get; set; } = 30;
    public int SetupMinutes { get; set; } = 10;
    public int CredentialFailures { get; set; } = 5;
    public int DeviceAttempts { get; set; } = 20;
    public int AttemptWindowMinutes { get; set; } = 15;
    public int CooldownMinutes { get; set; } = 15;
    public int MaximumConcurrentKdf { get; set; } = 2;
    public long ExternalEpoch { get; set; }
    public string PepperVersion { get; set; } = "";
    // Management is separately registered. No default Auth scope or permissive assurance policy.
    public string? ManagementAuthScopeId { get; set; }
    public bool IsValid() => this.ActorIdleMinutes is >= 1 and <= 30 &&
        this.ActorAbsoluteHours is >= 1 and <= 12 && this.PairingDays is >= 1 and <= 30 &&
        this.SetupMinutes is >= 1 and <= 10 && this.CredentialFailures is >= 1 and <= 5 &&
        this.DeviceAttempts is >= 5 and <= 50 && this.AttemptWindowMinutes is >= 1 and <= 60 &&
        this.CooldownMinutes is >= 15 and <= 60 && this.MaximumConcurrentKdf is >= 1 and <= 4 &&
        this.ExternalEpoch > 0 && this.PepperVersion.Length is > 0 and <= 64 &&
        this.PepperVersion.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '.');
}
