namespace BunkFy.Host.Migrations;

using Microsoft.Extensions.Configuration;

public enum MigrationsProductionApprovalState
{
    Pending = 0,
    Approved = 1
}

public enum MigrationsDeploymentProfile
{
    Unspecified = 0,
    SelfHosted = 1,
    Hosted = 2
}

public enum MigrationsRuntimeKind
{
    Unspecified = 0,
    Process = 1,
    Container = 2
}

public enum MigrationsExistingHistoryDisposition
{
    Unspecified = 0,
    ApplyCompatiblePrefix = 1,
    ApplyInventoryConvergenceV1 = 2
}

public sealed record MigrationsProductionAdmissionOptions
{
    public const string SectionName = "Migrations:ProductionAdmission";
    public const int CurrentTargetCatalogVersion = 1;

    private MigrationsProductionAdmissionOptions() { }

    public MigrationsProductionApprovalState ApprovalState { get; init; }
    public string? ApprovalReference { get; init; }
    public MigrationsDeploymentProfile DeploymentProfile { get; init; }
    public MigrationsRuntimeKind Runtime { get; init; }
    public string? SourceCommitSha { get; init; }
    public string? ContainerImageDigest { get; init; }
    public string? DatabaseIdentity { get; init; }
    public int TargetCatalogVersion { get; init; } = CurrentTargetCatalogVersion;
    public string? ApprovedDatabaseTargetSha256 { get; init; }
    public string? ApprovedTargetCatalogSha256 { get; init; }
    public string? BackupEvidenceReference { get; init; }
    public string? RollbackEvidenceReference { get; init; }
    public MigrationsExistingHistoryDisposition ExistingHistoryDisposition { get; init; }

    public static MigrationsProductionAdmissionOptions FromConfiguration(
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        IConfigurationSection section = configuration.GetSection(SectionName);
        return new MigrationsProductionAdmissionOptions
        {
            ApprovalState = Parse(
                section["ApprovalState"],
                MigrationsProductionApprovalState.Pending,
                $"{SectionName}:ApprovalState"),
            ApprovalReference = Optional(section["ApprovalReference"]),
            DeploymentProfile = Parse(
                section["DeploymentProfile"],
                MigrationsDeploymentProfile.Unspecified,
                $"{SectionName}:DeploymentProfile"),
            Runtime = Parse(
                section["Runtime"],
                MigrationsRuntimeKind.Unspecified,
                $"{SectionName}:Runtime"),
            SourceCommitSha = Optional(section["SourceCommitSha"]),
            ContainerImageDigest = Optional(section["ContainerImageDigest"]),
            DatabaseIdentity = Optional(section["DatabaseIdentity"]),
            TargetCatalogVersion = ParsePositiveInt(
                section["TargetCatalogVersion"],
                CurrentTargetCatalogVersion,
                $"{SectionName}:TargetCatalogVersion"),
            ApprovedDatabaseTargetSha256 = Optional(
                section["ApprovedDatabaseTargetSha256"]),
            ApprovedTargetCatalogSha256 = Optional(
                section["ApprovedTargetCatalogSha256"]),
            BackupEvidenceReference = Optional(
                section["BackupEvidenceReference"]),
            RollbackEvidenceReference = Optional(
                section["RollbackEvidenceReference"]),
            ExistingHistoryDisposition = Parse(
                section["ExistingHistoryDisposition"],
                MigrationsExistingHistoryDisposition.Unspecified,
                $"{SectionName}:ExistingHistoryDisposition")
        };
    }

    private static T Parse<T>(string? value, T fallback, string name)
        where T : struct, Enum
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return fallback;
        }

        if (!Enum.TryParse(value.Trim(), ignoreCase: true, out T parsed) ||
            !Enum.IsDefined(parsed))
        {
            throw new InvalidOperationException($"{name} is invalid.");
        }

        return parsed;
    }

    private static int ParsePositiveInt(string? value, int fallback, string name)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return fallback;
        }

        if (!int.TryParse(
                value.Trim(),
                System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture,
                out int parsed) ||
            parsed <= 0)
        {
            throw new InvalidOperationException($"{name} must be a positive integer.");
        }

        return parsed;
    }

    private static string? Optional(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
