namespace BunkFy.Modules.Workspaces.Persistence;

using System.Globalization;
using BunkFy.Modules.Workspaces.Domain;
using BunkFy.Modules.Workspaces.Domain.DataRights;
using BunkFy.Modules.Workspaces.Domain.Termination;
using BunkFy.Modules.Workspaces.Persistence.TenantTermination;
using Gma.Framework.Messaging.Infrastructure;
using Gma.Framework.Naming;
using Gma.Framework.Persistence.EntityFrameworkCore;
using Gma.Framework.Scoping;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

public sealed class WorkspacesDbContext(
    DbContextOptions<WorkspacesDbContext> options,
    IScopeContext scopeContext)
    : ScopeAwareDbContext<WorkspacesDbContext>(options, scopeContext)
{
    private readonly IScopeContext scopeContext = scopeContext;

    public DbSet<WorkspaceStaffOnboarding> StaffOnboardingApplications =>
        this.Set<WorkspaceStaffOnboarding>();
    public DbSet<WorkspaceStaffOnboardingRetentionExecution>
        StaffOnboardingRetentionExecutions =>
        this.Set<WorkspaceStaffOnboardingRetentionExecution>();
    public DbSet<WorkspaceStaffIdentityAnchorSweepCheckpoint>
        StaffIdentityAnchorSweepCheckpoints =>
        this.Set<WorkspaceStaffIdentityAnchorSweepCheckpoint>();
    public DbSet<WorkspaceStaffHistoricalNoProvisionReceipt>
        StaffHistoricalNoProvisionReceipts =>
        this.Set<WorkspaceStaffHistoricalNoProvisionReceipt>();
    public DbSet<WorkspaceStaffDeferredClaimWithdrawal>
        StaffDeferredClaimWithdrawals =>
        this.Set<WorkspaceStaffDeferredClaimWithdrawal>();
    public DbSet<WorkspaceStaffOnboardingCorrectionReceipt>
        StaffOnboardingCorrectionReceipts =>
        this.Set<WorkspaceStaffOnboardingCorrectionReceipt>();
    public DbSet<WorkspaceStaffOnboardingRecoveryReceipt>
        StaffOnboardingRecoveryReceipts =>
        this.Set<WorkspaceStaffOnboardingRecoveryReceipt>();
    public DbSet<WorkspaceStaffOnboardingProcessingRestriction>
        StaffOnboardingProcessingRestrictions =>
        this.Set<WorkspaceStaffOnboardingProcessingRestriction>();
    public DbSet<WorkspaceStaffOnboardingProcessingRestrictionProjection>
        StaffOnboardingProcessingRestrictionProjections =>
        this.Set<WorkspaceStaffOnboardingProcessingRestrictionProjection>();
    public DbSet<WorkspaceStaffOnboardingProcessingRestrictionReceipt>
        StaffOnboardingProcessingRestrictionReceipts =>
        this.Set<WorkspaceStaffOnboardingProcessingRestrictionReceipt>();
    public DbSet<WorkspaceStaffAccessProcess> StaffAccessProcesses =>
        this.Set<WorkspaceStaffAccessProcess>();
    public DbSet<WorkspaceStaffAccessPlan> StaffAccessPlans =>
        this.Set<WorkspaceStaffAccessPlan>();
    public DbSet<WorkspaceStaffRetentionCorrelationReceipt>
        StaffRetentionCorrelationReceipts =>
        this.Set<WorkspaceStaffRetentionCorrelationReceipt>();
    public DbSet<WorkspaceStaffCorrelationAnonymisationReceipt>
        StaffCorrelationAnonymisationReceipts =>
        this.Set<WorkspaceStaffCorrelationAnonymisationReceipt>();
    public DbSet<WorkspaceStaffCorrelationAnonymisationTombstone>
        StaffCorrelationAnonymisationTombstones =>
        this.Set<WorkspaceStaffCorrelationAnonymisationTombstone>();
    public DbSet<
        WorkspaceStaffCorrelationAnonymisationRestoreReceipt>
        StaffCorrelationAnonymisationRestoreReceipts =>
        this.Set<
            WorkspaceStaffCorrelationAnonymisationRestoreReceipt>();
    public DbSet<WorkspaceTerminationFence> WorkspaceTerminationFences =>
        this.Set<WorkspaceTerminationFence>();
    public DbSet<WorkspaceTerminationFenceReceipt>
        WorkspaceTerminationFenceReceipts =>
        this.Set<WorkspaceTerminationFenceReceipt>();
    public DbSet<WorkspacePropertyProjection> PropertyProjections =>
        this.Set<WorkspacePropertyProjection>();
    public DbSet<WorkspaceProjectionRebuildCheckpoint> ProjectionRebuildCheckpoints =>
        this.Set<WorkspaceProjectionRebuildCheckpoint>();
    public DbSet<InboxMessage> InboxMessages => this.Set<InboxMessage>();
    public DbSet<OutboxMessage> OutboxMessages => this.Set<OutboxMessage>();
    internal DbSet<WorkspaceStaffAccessPlanProperty> StaffAccessPlanProperties =>
        this.Set<WorkspaceStaffAccessPlanProperty>();
    internal DbSet<WorkspaceTenantDestroyOperation> TenantDestroyOperations =>
        this.Set<WorkspaceTenantDestroyOperation>();
    internal DbSet<WorkspaceTenantDestroyReceipt> TenantDestroyReceipts =>
        this.Set<WorkspaceTenantDestroyReceipt>();

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        this.EnsureReceiptsAreAppendOnly();
        return this.SaveChangesWithOperationalFenceAsync(
                acceptAllChangesOnSuccess,
                CancellationToken.None)
            .GetAwaiter()
            .GetResult();
    }

    public override async Task<int> SaveChangesAsync(
        bool acceptAllChangesOnSuccess,
        CancellationToken cancellationToken = default)
    {
        this.EnsureReceiptsAreAppendOnly();
        return await this.SaveChangesWithOperationalFenceAsync(
            acceptAllChangesOnSuccess,
            cancellationToken).ConfigureAwait(false);
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(WorkspacesMigrations.Schema);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(WorkspacesDbContext).Assembly);
        this.ApplyScopeConventions(modelBuilder);
    }

    private void EnsureReceiptsAreAppendOnly()
    {
        bool retentionMutationRequested = this.ChangeTracker
            .Entries<WorkspaceStaffRetentionCorrelationReceipt>()
            .Any(entry =>
                entry.State is
                    EntityState.Modified or EntityState.Deleted);
        bool historicalNoProvisionMutationRequested = this.ChangeTracker
            .Entries<WorkspaceStaffHistoricalNoProvisionReceipt>()
            .Any(entry =>
                entry.State is EntityState.Modified or EntityState.Deleted);
        bool correctionMutationRequested = this.ChangeTracker
            .Entries<WorkspaceStaffOnboardingCorrectionReceipt>()
            .Any(entry =>
                entry.State is
                    EntityState.Modified or EntityState.Deleted);
        bool recoveryMutationRequested = this.ChangeTracker
            .Entries<WorkspaceStaffOnboardingRecoveryReceipt>()
            .Any(entry => entry.State is EntityState.Modified or EntityState.Deleted);
        bool restrictionMutationRequested = this.ChangeTracker
            .Entries<
                WorkspaceStaffOnboardingProcessingRestrictionReceipt>()
            .Any(entry =>
                entry.State is
                    EntityState.Modified or EntityState.Deleted);
        bool anonymisationMutationRequested = this.ChangeTracker
            .Entries<
                WorkspaceStaffCorrelationAnonymisationReceipt>()
            .Any(entry =>
                entry.State is
                    EntityState.Modified or EntityState.Deleted);
        bool restoreMutationRequested = this.ChangeTracker
            .Entries<
                WorkspaceStaffCorrelationAnonymisationRestoreReceipt>()
            .Any(entry =>
                entry.State is
                    EntityState.Modified or EntityState.Deleted);
        bool terminationMutationRequested = this.ChangeTracker
            .Entries<WorkspaceTerminationFenceReceipt>()
            .Any(entry =>
                entry.State is
                    EntityState.Modified or EntityState.Deleted);
        bool terminationFenceDeletionRequested = this.ChangeTracker
            .Entries<WorkspaceTerminationFence>()
            .Any(entry => entry.State == EntityState.Deleted);
        bool tenantDestroyReceiptMutationRequested = this.ChangeTracker
            .Entries<WorkspaceTenantDestroyReceipt>()
            .Any(entry =>
                entry.State is EntityState.Modified or EntityState.Deleted);
        if (retentionMutationRequested ||
            historicalNoProvisionMutationRequested ||
            correctionMutationRequested ||
            recoveryMutationRequested ||
            restrictionMutationRequested ||
            anonymisationMutationRequested ||
            restoreMutationRequested ||
            terminationMutationRequested ||
            terminationFenceDeletionRequested ||
            tenantDestroyReceiptMutationRequested)
        {
            throw new InvalidOperationException(
                "Workspace immutable receipts are append-only.");
        }
    }

    private async Task<int> SaveChangesWithOperationalFenceAsync(
        bool acceptAllChangesOnSuccess,
        CancellationToken cancellationToken)
    {
        if (!this.Database.IsRelational())
        {
            return await base.SaveChangesAsync(
                acceptAllChangesOnSuccess,
                cancellationToken).ConfigureAwait(false);
        }

        MutationKind mutationKind = this.GetMutationKind();
        if (mutationKind == MutationKind.None)
        {
            return await base.SaveChangesAsync(
                acceptAllChangesOnSuccess,
                cancellationToken).ConfigureAwait(false);
        }

        if (!this.scopeContext.IsEnabled ||
            string.IsNullOrWhiteSpace(this.scopeContext.ScopeId))
        {
            throw new WorkspaceOperationalMutationRejectedException();
        }

        IDbContextTransaction? ownedTransaction = null;
        if (this.Database.CurrentTransaction is null)
        {
            ownedTransaction = await this.Database
                .BeginTransactionAsync(cancellationToken)
                .ConfigureAwait(false);
        }

        try
        {
            if (mutationKind == MutationKind.Operational)
            {
                await this.AcquireOperationalMutationAdmissionAsync(
                    cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await WorkspaceTenantMutationLock.AcquireExclusiveAsync(
                    this,
                    this.scopeContext.ScopeId,
                    cancellationToken).ConfigureAwait(false);
            }

            int affected = await base.SaveChangesAsync(
                acceptAllChangesOnSuccess,
                cancellationToken).ConfigureAwait(false);
            if (ownedTransaction is not null)
            {
                await ownedTransaction.CommitAsync(cancellationToken)
                    .ConfigureAwait(false);
            }

            return affected;
        }
        catch
        {
            if (ownedTransaction is not null)
            {
                await ownedTransaction
                    .RollbackAsync(CancellationToken.None)
                    .ConfigureAwait(false);
            }

            throw;
        }
        finally
        {
            if (ownedTransaction is not null)
            {
                await ownedTransaction.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    internal Task AcquireOperationalMutationAdmissionAsync(
        CancellationToken cancellationToken) =>
        this.AcquireOperationalMutationAdmissionCoreAsync(
            exclusive: false,
            cancellationToken);

    internal Task AcquireExclusiveOperationalMutationAdmissionAsync(
        CancellationToken cancellationToken) =>
        this.AcquireOperationalMutationAdmissionCoreAsync(
            exclusive: true,
            cancellationToken);

    private async Task AcquireOperationalMutationAdmissionCoreAsync(
        bool exclusive,
        CancellationToken cancellationToken)
    {
        if (!this.Database.IsRelational())
        {
            return;
        }

        if (!this.scopeContext.IsEnabled ||
            string.IsNullOrWhiteSpace(this.scopeContext.ScopeId) ||
            this.Database.CurrentTransaction is null)
        {
            throw new WorkspaceOperationalMutationRejectedException();
        }

        if (exclusive)
        {
            await WorkspaceTenantMutationLock.AcquireExclusiveAsync(
                    this,
                    this.scopeContext.ScopeId,
                    cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await WorkspaceTenantMutationLock.AcquireAdmissionAsync(
                    this,
                    this.scopeContext.ScopeId,
                    cancellationToken).ConfigureAwait(false);
        }

        bool frozen = await this.WorkspaceTerminationFences
            .AsNoTracking()
            .AnyAsync(
                fence => fence.ScopeId == this.scopeContext.ScopeId &&
                    fence.State != WorkspaceTerminationFenceState.Released,
                cancellationToken)
            .ConfigureAwait(false);
        if (frozen)
        {
            throw new WorkspaceOperationalMutationRejectedException();
        }
    }

    private MutationKind GetMutationKind()
    {
        bool hasTerminationMutation = false;
        bool hasOperationalMutation = false;
        foreach (Microsoft.EntityFrameworkCore.ChangeTracking.EntityEntry
                 entry in this.ChangeTracker.Entries().Where(entry =>
                     entry.State is EntityState.Added or
                         EntityState.Modified or EntityState.Deleted))
        {
            if (entry.Entity is InboxMessage inbox)
            {
                if (entry.State == EntityState.Added &&
                    !string.IsNullOrWhiteSpace(inbox.ScopeId))
                {
                    hasOperationalMutation = true;
                }

                continue;
            }

            if (entry.Entity is OutboxMessage outbox)
            {
                if (entry.State == EntityState.Added &&
                    !string.IsNullOrWhiteSpace(outbox.ScopeId))
                {
                    hasOperationalMutation = true;
                }

                continue;
            }

            if (entry.Entity is WorkspaceTerminationFence or
                WorkspaceTerminationFenceReceipt or
                WorkspaceTenantDestroyOperation or
                WorkspaceTenantDestroyReceipt)
            {
                hasTerminationMutation = true;
                continue;
            }

            hasOperationalMutation = true;
        }

        if (hasTerminationMutation && hasOperationalMutation)
        {
            throw new InvalidOperationException(
                "Workspace termination and operational mutations cannot " +
                "share one transaction.");
        }

        return hasOperationalMutation
            ? MutationKind.Operational
            : hasTerminationMutation
                ? MutationKind.Termination
                : MutationKind.None;
    }

    internal async ValueTask<bool> TryAdmitMessageMutationAsync(
        string? tenantId,
        CancellationToken cancellationToken)
    {
        if (!TenantIds.TryNormalize(tenantId, out string? canonicalTenantId) ||
            !this.scopeContext.IsEnabled ||
            !string.Equals(
                this.scopeContext.ScopeId,
                canonicalTenantId,
                StringComparison.Ordinal) ||
            (this.Database.IsRelational() &&
             this.Database.CurrentTransaction is null))
        {
            return false;
        }

        try
        {
            if (this.Database.IsRelational())
            {
                await WorkspaceTenantMutationLock.AcquireAdmissionAsync(
                    this,
                    canonicalTenantId,
                    cancellationToken).ConfigureAwait(false);
            }

            bool frozen = await this.WorkspaceTerminationFences
                .AsNoTracking()
                .IgnoreQueryFilters()
                .AnyAsync(
                    fence =>
                        fence.ScopeId == canonicalTenantId &&
                        fence.State !=
                            WorkspaceTerminationFenceState.Released,
                    cancellationToken)
                .ConfigureAwait(false);
            return !frozen;
        }
        catch (Exception exception)
            when (exception is not OperationCanceledException)
        {
            return false;
        }
    }

    internal async Task<int> SaveTenantDestructionChangesAsync(
        string tenantId,
        Guid operationId,
        WorkspaceTenantDestroyStage? attemptedStage,
        CancellationToken cancellationToken)
    {
        if (!TenantIds.TryNormalize(tenantId, out string? canonicalTenantId) ||
            operationId == Guid.Empty ||
            attemptedStage is <= WorkspaceTenantDestroyStage.Unknown or
                > WorkspaceTenantDestroyStage.HistoricalNoProvisionReceipts or
                WorkspaceTenantDestroyStage.Completed ||
            !this.scopeContext.IsEnabled ||
            !string.Equals(
                this.scopeContext.ScopeId,
                canonicalTenantId,
                StringComparison.Ordinal) ||
            (this.Database.IsRelational() &&
             this.Database.CurrentTransaction is null))
        {
            throw new InvalidOperationException(
                "Workspaces tenant destruction admission is invalid.");
        }

        WorkspaceTenantDestroyOperation? operation =
            this.TenantDestroyOperations.Local.SingleOrDefault() ??
            await this.TenantDestroyOperations
                .IgnoreQueryFilters()
                .SingleOrDefaultAsync(
                    candidate =>
                        candidate.ScopeId == canonicalTenantId &&
                        candidate.OperationId == operationId,
                    cancellationToken)
                .ConfigureAwait(false);
        if (operation is null)
        {
            throw new InvalidOperationException(
                "Workspaces tenant destruction state is invalid.");
        }

        WorkspaceTerminationFence? fence =
            this.WorkspaceTerminationFences.Local.SingleOrDefault(candidate =>
                candidate.ScopeId == canonicalTenantId) ??
            await this.WorkspaceTerminationFences
                .IgnoreQueryFilters()
                .SingleOrDefaultAsync(
                    candidate =>
                        candidate.ScopeId == canonicalTenantId &&
                        candidate.Id == operation.FenceId,
                    cancellationToken)
                .ConfigureAwait(false);
        if (fence is null ||
            fence.Id != operation.FenceId ||
            fence.State is not (
                WorkspaceTerminationFenceState.DestructionStarted or
                WorkspaceTerminationFenceState.Closed))
        {
            throw new InvalidOperationException(
                "Workspaces tenant destruction state is invalid.");
        }

        if (this.Database.IsNpgsql())
        {
            string attemptedStageValue = attemptedStage.HasValue
                ? ((int)attemptedStage.Value).ToString(
                    CultureInfo.InvariantCulture)
                : string.Empty;
            await this.Database.ExecuteSqlInterpolatedAsync(
                    $"SELECT set_config('bunkfy.workspaces_tenant_destroy_operation_id', {operationId.ToString("D")}, true)",
                    cancellationToken)
                .ConfigureAwait(false);
            await this.Database.ExecuteSqlInterpolatedAsync(
                    $"SELECT set_config('bunkfy.workspaces_tenant_destroy_attempted_stage', {attemptedStageValue}, true)",
                    cancellationToken)
                .ConfigureAwait(false);
        }

        return await base.SaveChangesAsync(
                acceptAllChangesOnSuccess: true,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private enum MutationKind
    {
        None = 0,
        Operational = 1,
        Termination = 2
    }
}
