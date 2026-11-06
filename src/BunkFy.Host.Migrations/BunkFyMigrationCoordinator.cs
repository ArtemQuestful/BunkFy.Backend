namespace BunkFy.Host.Migrations;

using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

public sealed class BunkFyMigrationCoordinator(
    ILogger logger,
    MigrationsHostOptions options,
    MigrationsProductionAdmissionOptions admission,
    bool isProduction)
{
    public async Task<BunkFyMigrationPlan> RunAsync(
        IReadOnlyList<BunkFyMigrationModule> modules,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(modules);
        if (modules.Count == 0)
        {
            throw new InvalidOperationException(
                "The BunkFy migration module catalogue cannot be empty.");
        }

        MigrationsProductionAdmission.ValidateConfigurationOrThrow(
            admission,
            options,
            isProduction);

        foreach (BunkFyMigrationModule module in modules)
        {
            module.Context.Database.SetCommandTimeout(options.CommandTimeoutSeconds);
        }

        await using PostgreSqlMigrationLock migrationLock =
            await PostgreSqlMigrationLock.AcquireAsync(
                    modules[0].Context,
                    TimeSpan.FromSeconds(options.LockAcquireTimeoutSeconds),
                    TimeSpan.FromMilliseconds(options.LockRetryDelayMilliseconds),
                    cancellationToken)
                .ConfigureAwait(false);

        BunkFyMigrationPlan plan = await this.ReadPlanAsync(modules, cancellationToken)
            .ConfigureAwait(false);
        MigrationsProductionAdmission.ValidateResolvedTargetOrThrow(
            admission,
            options,
            isProduction,
            migrationLock.DatabaseTargetSha256,
            plan.TargetCatalogSha256);
        MigrationsProductionAdmission.ReportProductionEvidence(
            logger,
            admission,
            options,
            migrationLock.DatabaseTargetSha256,
            plan,
            isProduction);
        this.ReportPlan(plan);

        if (options.Mode == MigrationExecutionMode.Plan)
        {
            logger.LogInformation(
                "Migration plan completed without mutation. Modules {ModuleCount}; target {TargetCount}; applied {AppliedCount}; pending {PendingCount}; target digest {TargetCatalogSha256}; state digest {CurrentStateSha256}; pending digest {PendingPlanSha256}.",
                plan.Modules.Count,
                plan.TargetMigrationCount,
                plan.AppliedMigrationCount,
                plan.PendingMigrationCount,
                plan.TargetCatalogSha256,
                plan.CurrentStateSha256,
                plan.PendingPlanSha256);
            return plan;
        }

        Stopwatch elapsed = Stopwatch.StartNew();
        for (int index = 0; index < modules.Count; index++)
        {
            BunkFyMigrationModule module = modules[index];
            BunkFyMigrationModulePlan modulePlan = plan.Modules[index];
            if (modulePlan.PendingMigrations.Count == 0)
            {
                logger.LogInformation(
                    "Module {ModuleName} is current at {AppliedCount} migrations.",
                    module.Name,
                    modulePlan.AppliedMigrations.Count);
                continue;
            }

            logger.LogInformation(
                "Applying {PendingCount} migrations for module {ModuleName}; first {FirstMigration}; target {LastMigration}.",
                modulePlan.PendingMigrations.Count,
                module.Name,
                modulePlan.PendingMigrations[0],
                modulePlan.PendingMigrations[^1]);
            Stopwatch moduleElapsed = Stopwatch.StartNew();
            await module.Context.Database.MigrateAsync(cancellationToken)
                .ConfigureAwait(false);
            string[] applied =
            [
                .. await module.Context.Database
                    .GetAppliedMigrationsAsync(cancellationToken)
                    .ConfigureAwait(false)
            ];
            if (!modulePlan.TargetMigrations.SequenceEqual(
                    applied,
                    StringComparer.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Module '{module.Name}' did not reach its loaded target migration after apply.");
            }

            logger.LogInformation(
                "Module {ModuleName} reached {AppliedCount} migrations in {ElapsedMilliseconds} ms.",
                module.Name,
                applied.Length,
                moduleElapsed.ElapsedMilliseconds);
        }

        BunkFyMigrationPlan completed = await this.ReadPlanAsync(modules, cancellationToken)
            .ConfigureAwait(false);
        if (completed.PendingMigrationCount != 0 ||
            !string.Equals(
                completed.TargetCatalogSha256,
                plan.TargetCatalogSha256,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The BunkFy migration catalogue did not reach the inspected target.");
        }

        logger.LogInformation(
            "All BunkFy PostgreSQL migrations are current. Modules {ModuleCount}; applied {AppliedCount}; target digest {TargetCatalogSha256}; state digest {CurrentStateSha256}; elapsed {ElapsedMilliseconds} ms.",
            completed.Modules.Count,
            completed.AppliedMigrationCount,
            completed.TargetCatalogSha256,
            completed.CurrentStateSha256,
            elapsed.ElapsedMilliseconds);
        return completed;
    }

    private async Task<BunkFyMigrationPlan> ReadPlanAsync(
        IReadOnlyList<BunkFyMigrationModule> modules,
        CancellationToken cancellationToken)
    {
        List<BunkFyMigrationHistory> histories = new(modules.Count);
        foreach (BunkFyMigrationModule module in modules)
        {
            string[] target = [.. module.Context.Database.GetMigrations()];
            string[] applied =
            [
                .. await module.Context.Database
                    .GetAppliedMigrationsAsync(cancellationToken)
                    .ConfigureAwait(false)
            ];
            histories.Add(new BunkFyMigrationHistory(module.Name, target, applied));
        }

        return BunkFyMigrationPlan.Create(
            histories,
            admission.ExistingHistoryDisposition);
    }

    private void ReportPlan(BunkFyMigrationPlan plan)
    {
        foreach (BunkFyMigrationModulePlan module in plan.Modules)
        {
            logger.LogInformation(
                "Migration plan for {ModuleName}: target {TargetCount}; applied {AppliedCount}; pending {PendingCount}; history transition {HistoryTransition}.",
                module.ModuleName,
                module.TargetMigrations.Count,
                module.AppliedMigrations.Count,
                module.PendingMigrations.Count,
                module.HistoryTransition ?? "exact-prefix");
        }
    }
}
