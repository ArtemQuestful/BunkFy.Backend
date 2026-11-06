namespace BunkFy.Host.Migrations.Tests;

using Microsoft.Extensions.Configuration;

public sealed class MigrationsProductionAdmissionTests
{
    private const string Commit = "1111111111111111111111111111111111111111";
    private const string DatabaseHash =
        "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string CatalogHash =
        "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

    [Fact]
    public void Development_apply_does_not_require_production_approval()
    {
        MigrationsHostOptions runtime = Runtime(MigrationExecutionMode.Apply);
        MigrationsProductionAdmissionOptions admission = Admission([]);

        Assert.Empty(MigrationsProductionAdmission.ValidateConfiguration(
            admission,
            runtime,
            isProduction: false));
        MigrationsProductionAdmission.ValidateResolvedTargetOrThrow(
            admission,
            runtime,
            isProduction: false,
            DatabaseHash,
            CatalogHash);
    }

    [Fact]
    public void Production_plan_requires_release_and_database_identity_not_apply_approval()
    {
        MigrationsHostOptions runtime = Runtime(MigrationExecutionMode.Plan);
        MigrationsProductionAdmissionOptions admission = Admission(
            BaseProductionValues());

        Assert.Empty(MigrationsProductionAdmission.ValidateConfiguration(
            admission,
            runtime,
            isProduction: true));
    }

    [Fact]
    public void Production_apply_requires_complete_approval_evidence()
    {
        MigrationsHostOptions runtime = Runtime(MigrationExecutionMode.Apply);
        MigrationsProductionAdmissionOptions admission = Admission(
            BaseProductionValues());

        string[] failures = MigrationsProductionAdmission.ValidateConfiguration(
            admission,
            runtime,
            isProduction: true);

        Assert.Contains(failures, failure => failure.Contains(
            "ApprovalState",
            StringComparison.Ordinal));
        Assert.Contains(failures, failure => failure.Contains(
            "ApprovedDatabaseTargetSha256",
            StringComparison.Ordinal));
        Assert.Contains(failures, failure => failure.Contains(
            "BackupEvidenceReference",
            StringComparison.Ordinal));
        Assert.Contains(failures, failure => failure.Contains(
            "ExistingHistoryDisposition",
            StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("ApplyCompatiblePrefix")]
    [InlineData("ApplyInventoryConvergenceV1")]
    public void Production_apply_accepts_exact_release_target_and_recovery_evidence(string disposition)
    {
        MigrationsHostOptions runtime = Runtime(MigrationExecutionMode.Apply);
        Dictionary<string, string?> values = ApprovedProductionValues();
        values["Migrations:ProductionAdmission:ExistingHistoryDisposition"] = disposition;
        MigrationsProductionAdmissionOptions admission = Admission(values);

        Assert.Empty(MigrationsProductionAdmission.ValidateConfiguration(
            admission,
            runtime,
            isProduction: true));
        MigrationsProductionAdmission.ValidateResolvedTargetOrThrow(
            admission,
            runtime,
            isProduction: true,
            DatabaseHash,
            CatalogHash);
    }

    [Fact]
    public void Inventory_transition_does_not_replace_production_approval_or_recovery_evidence()
    {
        Dictionary<string, string?> values = BaseProductionValues();
        values["Migrations:ProductionAdmission:ExistingHistoryDisposition"] = "ApplyInventoryConvergenceV1";
        string[] failures = MigrationsProductionAdmission.ValidateConfiguration(
            Admission(values), Runtime(MigrationExecutionMode.Apply), isProduction: true);

        foreach (string required in new[] { "ApprovalState", "ApprovalReference", "ApprovedDatabaseTargetSha256",
                     "ApprovedTargetCatalogSha256", "BackupEvidenceReference", "RollbackEvidenceReference" })
        {
            Assert.Contains(failures, failure => failure.Contains(required, StringComparison.Ordinal));
        }
    }

    [Theory]
    [InlineData(
        "cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc",
        CatalogHash)]
    [InlineData(
        DatabaseHash,
        "cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc")]
    public void Production_apply_rejects_a_different_resolved_target(
        string databaseHash,
        string catalogHash)
    {
        MigrationsHostOptions runtime = Runtime(MigrationExecutionMode.Apply);
        MigrationsProductionAdmissionOptions admission = Admission(
            ApprovedProductionValues());

        Assert.Throws<InvalidOperationException>(
            () => MigrationsProductionAdmission.ValidateResolvedTargetOrThrow(
                admission,
                runtime,
                isProduction: true,
                databaseHash,
                catalogHash));
    }

    [Fact]
    public void Production_container_requires_an_immutable_image_digest()
    {
        Dictionary<string, string?> values = BaseProductionValues();
        values["Migrations:ProductionAdmission:Runtime"] = "Container";
        MigrationsProductionAdmissionOptions admission = Admission(values);

        string[] failures = MigrationsProductionAdmission.ValidateConfiguration(
            admission,
            Runtime(MigrationExecutionMode.Plan),
            isProduction: true);

        Assert.Contains(failures, failure => failure.Contains(
            "ContainerImageDigest",
            StringComparison.Ordinal));
    }

    [Fact]
    public void Production_plan_rejects_unsafe_optional_log_references()
    {
        Dictionary<string, string?> values = BaseProductionValues();
        values["Migrations:ProductionAdmission:ApprovalReference"] =
            "unsafe reference\nsecond-line";
        MigrationsProductionAdmissionOptions admission = Admission(values);

        string[] failures = MigrationsProductionAdmission.ValidateConfiguration(
            admission,
            Runtime(MigrationExecutionMode.Plan),
            isProduction: true);

        Assert.Contains(failures, failure => failure.Contains(
            "ApprovalReference",
            StringComparison.Ordinal));
    }

    [Fact]
    public void Database_target_fingerprint_is_normalized_and_target_specific()
    {
        string first = PostgreSqlMigrationLock.ComputeDatabaseTargetSha256(
            " POSTGRES.EXAMPLE:5432 ",
            "bunkfy");
        string same = PostgreSqlMigrationLock.ComputeDatabaseTargetSha256(
            "postgres.example:5432",
            "bunkfy");
        string other = PostgreSqlMigrationLock.ComputeDatabaseTargetSha256(
            "postgres.example:5432",
            "bunkfy-shadow");

        Assert.Equal(first, same);
        Assert.NotEqual(first, other);
        Assert.Matches("^[a-f0-9]{64}$", first);
    }

    private static MigrationsHostOptions Runtime(MigrationExecutionMode mode) =>
        MigrationsHostOptions.FromConfiguration(
            MigrationsHostOptionsTests.BuildConfiguration(
                new Dictionary<string, string?>
                {
                    ["Migrations:Mode"] = mode.ToString()
                }));

    private static MigrationsProductionAdmissionOptions Admission(
        IEnumerable<KeyValuePair<string, string?>> values) =>
        MigrationsProductionAdmissionOptions.FromConfiguration(
            MigrationsHostOptionsTests.BuildConfiguration(values));

    private static Dictionary<string, string?> BaseProductionValues() =>
        new()
        {
            ["Migrations:ProductionAdmission:DeploymentProfile"] = "Hosted",
            ["Migrations:ProductionAdmission:Runtime"] = "Process",
            ["Migrations:ProductionAdmission:SourceCommitSha"] = Commit,
            ["Migrations:ProductionAdmission:DatabaseIdentity"] = "prod-primary-eu1",
            ["Migrations:ProductionAdmission:TargetCatalogVersion"] = "1"
        };

    private static Dictionary<string, string?> ApprovedProductionValues()
    {
        Dictionary<string, string?> values = BaseProductionValues();
        values["Migrations:ProductionAdmission:ApprovalState"] = "Approved";
        values["Migrations:ProductionAdmission:ApprovalReference"] = "change/2026-08-05/42";
        values["Migrations:ProductionAdmission:ApprovedDatabaseTargetSha256"] = DatabaseHash;
        values["Migrations:ProductionAdmission:ApprovedTargetCatalogSha256"] = CatalogHash;
        values["Migrations:ProductionAdmission:BackupEvidenceReference"] = "backup/2026-08-05/42";
        values["Migrations:ProductionAdmission:RollbackEvidenceReference"] = "rollback/2026-08-05/42";
        values["Migrations:ProductionAdmission:ExistingHistoryDisposition"] =
            "ApplyCompatiblePrefix";
        return values;
    }
}
