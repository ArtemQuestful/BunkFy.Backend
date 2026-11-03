namespace BunkFy.Host.Migrations;

using System.Security.Cryptography;
using System.Text;

public sealed record BunkFyMigrationHistory(
    string ModuleName,
    IReadOnlyList<string> TargetMigrations,
    IReadOnlyList<string> AppliedMigrations);

public sealed record BunkFyMigrationModulePlan(
    string ModuleName,
    IReadOnlyList<string> TargetMigrations,
    IReadOnlyList<string> AppliedMigrations,
    IReadOnlyList<string> PendingMigrations)
{
    public string? HistoryTransition { get; init; }
}

public sealed record BunkFyMigrationPlan(
    IReadOnlyList<BunkFyMigrationModulePlan> Modules,
    string TargetCatalogSha256,
    string CurrentStateSha256,
    string PendingPlanSha256,
    int TargetMigrationCount,
    int AppliedMigrationCount,
    int PendingMigrationCount)
{
    public static BunkFyMigrationPlan Create(
        IEnumerable<BunkFyMigrationHistory> histories) =>
        Create(histories, MigrationsExistingHistoryDisposition.ApplyCompatiblePrefix);

    public static BunkFyMigrationPlan Create(
        IEnumerable<BunkFyMigrationHistory> histories,
        MigrationsExistingHistoryDisposition disposition)
    {
        ArgumentNullException.ThrowIfNull(histories);
        if (!Enum.IsDefined(disposition))
        {
            throw new InvalidOperationException("The migration history disposition is invalid.");
        }
        BunkFyMigrationHistory[] materialized = [.. histories];
        if (materialized.Length == 0)
        {
            throw new InvalidOperationException(
                "The BunkFy migration catalogue cannot be empty.");
        }

        HashSet<string> moduleNames = new(StringComparer.Ordinal);
        List<BunkFyMigrationModulePlan> modules = [];
        foreach (BunkFyMigrationHistory history in materialized)
        {
            ValidateHistory(history, moduleNames);
            string[] target = [.. history.TargetMigrations];
            string[] applied = [.. history.AppliedMigrations];
            bool compatiblePrefix = applied.Length <= target.Length &&
                target.AsSpan(0, applied.Length).SequenceEqual(applied);
            bool inventoryTransition = !compatiblePrefix &&
                disposition == MigrationsExistingHistoryDisposition.ApplyInventoryConvergenceV1 &&
                InventoryConvergenceHistoryTransition.Matches(history);
            if (!compatiblePrefix && !inventoryTransition)
            {
                throw new InvalidOperationException(
                    $"Applied migration history for module '{history.ModuleName}' is not an exact prefix of the loaded target catalogue.");
            }

            modules.Add(new BunkFyMigrationModulePlan(
                history.ModuleName,
                target,
                applied,
                inventoryTransition
                    ? [InventoryConvergenceHistoryTransition.MigrationId]
                    : target[applied.Length..])
            {
                HistoryTransition = inventoryTransition
                    ? InventoryConvergenceHistoryTransition.Name
                    : null
            });
        }

        return new BunkFyMigrationPlan(
            modules,
            ComputeDigest("bunkfy-target-catalog-v1", modules, DigestPart.Target),
            ComputeDigest("bunkfy-current-state-v1", modules, DigestPart.Applied),
            ComputeDigest("bunkfy-pending-plan-v1", modules, DigestPart.Pending),
            modules.Sum(module => module.TargetMigrations.Count),
            modules.Sum(module => module.AppliedMigrations.Count),
            modules.Sum(module => module.PendingMigrations.Count));
    }

    private static void ValidateHistory(
        BunkFyMigrationHistory history,
        HashSet<string> moduleNames)
    {
        ArgumentNullException.ThrowIfNull(history);
        if (string.IsNullOrWhiteSpace(history.ModuleName) ||
            !string.Equals(history.ModuleName, history.ModuleName.Trim(),
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Migration module names must be non-empty and normalized.");
        }

        if (!moduleNames.Add(history.ModuleName))
        {
            throw new InvalidOperationException(
                $"Migration module '{history.ModuleName}' is registered more than once.");
        }

        if (history.TargetMigrations.Count == 0)
        {
            throw new InvalidOperationException(
                $"Migration module '{history.ModuleName}' has no loaded target migrations.");
        }

        if (history.TargetMigrations.Any(string.IsNullOrWhiteSpace) ||
            history.AppliedMigrations.Any(string.IsNullOrWhiteSpace) ||
            history.TargetMigrations.Distinct(StringComparer.Ordinal).Count() !=
            history.TargetMigrations.Count ||
            history.AppliedMigrations.Distinct(StringComparer.Ordinal).Count() !=
            history.AppliedMigrations.Count)
        {
            throw new InvalidOperationException(
                $"Migration module '{history.ModuleName}' contains empty or duplicate migration identifiers.");
        }
    }

    private static string ComputeDigest(
        string header,
        IReadOnlyList<BunkFyMigrationModulePlan> modules,
        DigestPart part)
    {
        StringBuilder canonical = new();
        canonical.Append(header).Append('\n');
        foreach (BunkFyMigrationModulePlan module in modules)
        {
            canonical.Append("module:").Append(module.ModuleName).Append('\n');
            IReadOnlyList<string> migrations = part switch
            {
                DigestPart.Target => module.TargetMigrations,
                DigestPart.Applied => module.AppliedMigrations,
                DigestPart.Pending => module.PendingMigrations,
                _ => throw new InvalidOperationException("Unknown migration digest part.")
            };

            foreach (string migration in migrations)
            {
                canonical.Append("migration:").Append(migration).Append('\n');
            }
        }

        return Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString())))
            .ToLowerInvariant();
    }

    private enum DigestPart
    {
        Target,
        Applied,
        Pending
    }
}
