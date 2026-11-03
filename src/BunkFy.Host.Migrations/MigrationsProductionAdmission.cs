namespace BunkFy.Host.Migrations;

using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;

public static class MigrationsProductionAdmission
{
    public static void ValidateConfigurationOrThrow(
        MigrationsProductionAdmissionOptions admission,
        MigrationsHostOptions runtime,
        bool isProduction)
    {
        string[] failures = ValidateConfiguration(admission, runtime, isProduction);
        if (failures.Length > 0)
        {
            throw new InvalidOperationException(
                "Migrations Production admission failed: " +
                string.Join(" ", failures));
        }
    }

    public static string[] ValidateConfiguration(
        MigrationsProductionAdmissionOptions admission,
        MigrationsHostOptions runtime,
        bool isProduction)
    {
        ArgumentNullException.ThrowIfNull(admission);
        ArgumentNullException.ThrowIfNull(runtime);
        if (!isProduction)
        {
            return [];
        }

        List<string> failures = [];
        if (admission.DeploymentProfile is not (
                MigrationsDeploymentProfile.SelfHosted or
                MigrationsDeploymentProfile.Hosted))
        {
            failures.Add(
                $"{MigrationsProductionAdmissionOptions.SectionName}:DeploymentProfile must be SelfHosted or Hosted.");
        }

        if (admission.Runtime is not (
                MigrationsRuntimeKind.Process or
                MigrationsRuntimeKind.Container))
        {
            failures.Add(
                $"{MigrationsProductionAdmissionOptions.SectionName}:Runtime must be Process or Container.");
        }

        if (!IsCommitSha(admission.SourceCommitSha))
        {
            failures.Add(
                $"{MigrationsProductionAdmissionOptions.SectionName}:SourceCommitSha must identify the exact lowercase 40-character Git commit.");
        }

        if (admission.Runtime == MigrationsRuntimeKind.Container &&
            !IsImageDigest(admission.ContainerImageDigest))
        {
            failures.Add(
                $"{MigrationsProductionAdmissionOptions.SectionName}:ContainerImageDigest must be an immutable lowercase sha256 OCI digest for a container.");
        }
        else if (admission.ContainerImageDigest is not null &&
                 !IsImageDigest(admission.ContainerImageDigest))
        {
            failures.Add(
                $"{MigrationsProductionAdmissionOptions.SectionName}:ContainerImageDigest must be an immutable lowercase sha256 OCI digest when supplied.");
        }

        if (!IsReference(admission.DatabaseIdentity))
        {
            failures.Add(
                $"{MigrationsProductionAdmissionOptions.SectionName}:DatabaseIdentity must be a 3-128 character non-secret target identifier.");
        }

        if (admission.TargetCatalogVersion !=
            MigrationsProductionAdmissionOptions.CurrentTargetCatalogVersion)
        {
            failures.Add(
                $"{MigrationsProductionAdmissionOptions.SectionName}:TargetCatalogVersion must be {MigrationsProductionAdmissionOptions.CurrentTargetCatalogVersion}.");
        }

        if (runtime.Mode == MigrationExecutionMode.Apply)
        {
            ValidateApplyApproval(admission, failures);
        }
        else
        {
            ValidateOptionalReference(
                admission.ApprovalReference,
                "ApprovalReference",
                failures);
            ValidateOptionalHash(
                admission.ApprovedDatabaseTargetSha256,
                "ApprovedDatabaseTargetSha256",
                failures);
            ValidateOptionalHash(
                admission.ApprovedTargetCatalogSha256,
                "ApprovedTargetCatalogSha256",
                failures);
            ValidateOptionalReference(
                admission.BackupEvidenceReference,
                "BackupEvidenceReference",
                failures);
            ValidateOptionalReference(
                admission.RollbackEvidenceReference,
                "RollbackEvidenceReference",
                failures);
        }

        return [.. failures];
    }

    public static void ValidateResolvedTargetOrThrow(
        MigrationsProductionAdmissionOptions admission,
        MigrationsHostOptions runtime,
        bool isProduction,
        string databaseTargetSha256,
        string targetCatalogSha256)
    {
        ArgumentNullException.ThrowIfNull(admission);
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentException.ThrowIfNullOrWhiteSpace(databaseTargetSha256);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetCatalogSha256);

        if (!isProduction || runtime.Mode != MigrationExecutionMode.Apply)
        {
            return;
        }

        List<string> failures = [];
        if (!string.Equals(
                admission.ApprovedDatabaseTargetSha256,
                databaseTargetSha256,
                StringComparison.Ordinal))
        {
            failures.Add(
                $"{MigrationsProductionAdmissionOptions.SectionName}:ApprovedDatabaseTargetSha256 does not match the connected PostgreSQL target.");
        }

        if (!string.Equals(
                admission.ApprovedTargetCatalogSha256,
                targetCatalogSha256,
                StringComparison.Ordinal))
        {
            failures.Add(
                $"{MigrationsProductionAdmissionOptions.SectionName}:ApprovedTargetCatalogSha256 does not match the loaded migration catalogue.");
        }

        if (failures.Count > 0)
        {
            throw new InvalidOperationException(
                "Migrations Production target admission failed: " +
                string.Join(" ", failures));
        }
    }

    public static void ReportProductionEvidence(
        ILogger logger,
        MigrationsProductionAdmissionOptions admission,
        MigrationsHostOptions runtime,
        string databaseTargetSha256,
        BunkFyMigrationPlan plan,
        bool isProduction)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(admission);
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(plan);
        if (!isProduction)
        {
            return;
        }

        logger.LogInformation(
            "Migrations Production evidence. Mode {Mode}; approval {ApprovalReference}; profile {DeploymentProfile}; runtime {Runtime}; source {SourceCommitSha}; image {ContainerImageDigest}; database alias {DatabaseAliasSha256}; database target {DatabaseTargetSha256}; catalogue version {CatalogVersion}; target {TargetCatalogSha256}; state {CurrentStateSha256}; pending {PendingPlanSha256}; history disposition {HistoryDisposition}; backup {BackupEvidenceReference}; rollback {RollbackEvidenceReference}.",
            runtime.Mode,
            admission.ApprovalReference ?? "plan-only",
            admission.DeploymentProfile,
            admission.Runtime,
            admission.SourceCommitSha,
            admission.ContainerImageDigest ?? "not-applicable",
            ComputeSha256(admission.DatabaseIdentity!),
            databaseTargetSha256,
            admission.TargetCatalogVersion,
            plan.TargetCatalogSha256,
            plan.CurrentStateSha256,
            plan.PendingPlanSha256,
            admission.ExistingHistoryDisposition,
            admission.BackupEvidenceReference ?? "plan-only",
            admission.RollbackEvidenceReference ?? "plan-only");
    }

    private static string ComputeSha256(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))
            .ToLowerInvariant();

    private static void ValidateApplyApproval(
        MigrationsProductionAdmissionOptions admission,
        List<string> failures)
    {
        if (admission.ApprovalState != MigrationsProductionApprovalState.Approved)
        {
            failures.Add(
                $"{MigrationsProductionAdmissionOptions.SectionName}:ApprovalState must be Approved for Production Apply.");
        }

        ValidateReference(admission.ApprovalReference, "ApprovalReference", failures);
        ValidateHash(
            admission.ApprovedDatabaseTargetSha256,
            "ApprovedDatabaseTargetSha256",
            failures);
        ValidateHash(
            admission.ApprovedTargetCatalogSha256,
            "ApprovedTargetCatalogSha256",
            failures);
        ValidateReference(
            admission.BackupEvidenceReference,
            "BackupEvidenceReference",
            failures);
        ValidateReference(
            admission.RollbackEvidenceReference,
            "RollbackEvidenceReference",
            failures);

        if (admission.ExistingHistoryDisposition is not (
            MigrationsExistingHistoryDisposition.ApplyCompatiblePrefix or
            MigrationsExistingHistoryDisposition.ApplyInventoryConvergenceV1))
        {
            failures.Add(
                $"{MigrationsProductionAdmissionOptions.SectionName}:ExistingHistoryDisposition must be ApplyCompatiblePrefix or ApplyInventoryConvergenceV1 for Production Apply.");
        }
    }

    private static void ValidateReference(
        string? value,
        string name,
        List<string> failures)
    {
        if (!IsReference(value))
        {
            failures.Add(
                $"{MigrationsProductionAdmissionOptions.SectionName}:{name} must be a 3-128 character non-secret evidence reference.");
        }
    }

    private static void ValidateHash(
        string? value,
        string name,
        List<string> failures)
    {
        if (!IsSha256(value))
        {
            failures.Add(
                $"{MigrationsProductionAdmissionOptions.SectionName}:{name} must be a nonzero lowercase SHA-256 digest.");
        }
    }

    private static void ValidateOptionalReference(
        string? value,
        string name,
        List<string> failures)
    {
        if (value is not null && !IsReference(value))
        {
            failures.Add(
                $"{MigrationsProductionAdmissionOptions.SectionName}:{name} must be a 3-128 character non-secret evidence reference when supplied.");
        }
    }

    private static void ValidateOptionalHash(
        string? value,
        string name,
        List<string> failures)
    {
        if (value is not null && !IsSha256(value))
        {
            failures.Add(
                $"{MigrationsProductionAdmissionOptions.SectionName}:{name} must be a nonzero lowercase SHA-256 digest when supplied.");
        }
    }

    private static bool IsReference(string? value)
    {
        if (value is not { Length: >= 3 and <= 128 })
        {
            return false;
        }

        return value.All(character =>
            char.IsAsciiLetterOrDigit(character) ||
            character is '.' or '_' or ':' or '/' or '-');
    }

    private static bool IsCommitSha(string? value) =>
        value is { Length: 40 } && IsLowerHex(value) && !IsAllZeros(value);

    private static bool IsImageDigest(string? value) =>
        value is { Length: 71 } &&
        value.StartsWith("sha256:", StringComparison.Ordinal) &&
        IsLowerHex(value.AsSpan(7)) &&
        !IsAllZeros(value.AsSpan(7));

    private static bool IsSha256(string? value) =>
        value is { Length: 64 } && IsLowerHex(value) && !IsAllZeros(value);

    private static bool IsLowerHex(ReadOnlySpan<char> value)
    {
        foreach (char character in value)
        {
            if (!char.IsAsciiDigit(character) &&
                character is not (>= 'a' and <= 'f'))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsAllZeros(ReadOnlySpan<char> value)
    {
        foreach (char character in value)
        {
            if (character != '0')
            {
                return false;
            }
        }

        return true;
    }
}
