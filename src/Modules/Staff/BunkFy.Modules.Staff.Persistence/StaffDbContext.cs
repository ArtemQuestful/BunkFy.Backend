namespace BunkFy.Modules.Staff.Persistence;

using Gma.Framework.Messaging.Infrastructure;
using Gma.Framework.Persistence.EntityFrameworkCore;
using Gma.Framework.Scoping;
using Microsoft.EntityFrameworkCore;
using BunkFy.Modules.Staff.Domain.Aggregates;
using BunkFy.Modules.Staff.Domain.DataRights;
using BunkFy.Modules.Staff.Domain.Entities;
using BunkFy.Modules.Staff.Domain.Governance;
using BunkFy.Modules.Staff.Domain.Retention;
using BunkFy.Modules.Staff.Persistence.Models;
using BunkFy.Modules.Staff.Persistence.TenantTermination;
using BunkFy.Modules.Workspaces.Contracts;
using Microsoft.EntityFrameworkCore.Storage;
using Gma.Framework.Naming;

public sealed class StaffDbContext(
    DbContextOptions<StaffDbContext> options,
    IScopeContext scopeContext,
    IWorkspaceTerminationFenceReader? terminationFences = null)
    : ScopeAwareDbContext<StaffDbContext>(options, scopeContext)
{
    private readonly IScopeContext scopeContext = scopeContext;

    public DbSet<StaffMember> StaffMembers => this.Set<StaffMember>();
    internal DbSet<StaffIdentityProvisioningAnchor>
        IdentityProvisioningAnchors =>
        this.Set<StaffIdentityProvisioningAnchor>();
    internal DbSet<StaffIdentityProvisioningAnchorResolution>
        IdentityProvisioningAnchorResolutions =>
        this.Set<StaffIdentityProvisioningAnchorResolution>();
    internal DbSet<StaffMemberMutationOperation> MemberMutationOperations =>
        this.Set<StaffMemberMutationOperation>();
    public DbSet<StaffDataRightsCorrectionReceipt> DataRightsCorrectionReceipts =>
        this.Set<StaffDataRightsCorrectionReceipt>();
    public DbSet<StaffProcessingRestriction> ProcessingRestrictions =>
        this.Set<StaffProcessingRestriction>();
    public DbSet<StaffProcessingRestrictionProjection>
        ProcessingRestrictionProjections =>
        this.Set<StaffProcessingRestrictionProjection>();
    public DbSet<StaffProcessingRestrictionReceipt>
        ProcessingRestrictionReceipts =>
        this.Set<StaffProcessingRestrictionReceipt>();
    public DbSet<StaffEmploymentGovernance> EmploymentGovernance =>
        this.Set<StaffEmploymentGovernance>();
    public DbSet<StaffEmploymentGovernanceChangeReceipt>
        EmploymentGovernanceChangeReceipts =>
        this.Set<StaffEmploymentGovernanceChangeReceipt>();
    public DbSet<StaffDataHold> DataHolds =>
        this.Set<StaffDataHold>();
    public DbSet<StaffDataHoldReceipt> DataHoldReceipts =>
        this.Set<StaffDataHoldReceipt>();
    public DbSet<StaffAnonymisationReceipt> AnonymisationReceipts =>
        this.Set<StaffAnonymisationReceipt>();
    public DbSet<StaffAnonymisationRestoreReceipt>
        AnonymisationRestoreReceipts =>
        this.Set<StaffAnonymisationRestoreReceipt>();
    public DbSet<StaffAnonymisationTombstone> AnonymisationTombstones =>
        this.Set<StaffAnonymisationTombstone>();
    public DbSet<StaffRetentionExecution> RetentionExecutions =>
        this.Set<StaffRetentionExecution>();
    public DbSet<StaffRetentionSweepCheckpoint>
        RetentionSweepCheckpoints =>
        this.Set<StaffRetentionSweepCheckpoint>();
    public DbSet<StaffRetentionAnonymisationReceipt>
        RetentionAnonymisationReceipts =>
        this.Set<StaffRetentionAnonymisationReceipt>();
    internal DbSet<StaffOperationLock> OperationLocks =>
        this.Set<StaffOperationLock>();
    public DbSet<StaffPropertyAssignment> PropertyAssignments => this.Set<StaffPropertyAssignment>();
    public DbSet<StaffPropertyProjection> PropertyProjections => this.Set<StaffPropertyProjection>();
    public DbSet<OutboxMessage> OutboxMessages => this.Set<OutboxMessage>();
    public DbSet<InboxMessage> InboxMessages => this.Set<InboxMessage>();
    public DbSet<StaffProjectionRebuildCheckpoint> ProjectionRebuildCheckpoints =>
        this.Set<StaffProjectionRebuildCheckpoint>();
    internal DbSet<StaffTenantRevision> TenantRevisions =>
        this.Set<StaffTenantRevision>();
    internal DbSet<StaffTenantDestroyOperation> TenantDestroyOperations =>
        this.Set<StaffTenantDestroyOperation>();
    internal DbSet<StaffTenantDestroyReceipt> TenantDestroyReceipts =>
        this.Set<StaffTenantDestroyReceipt>();

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        this.EnsureDataRightsReceiptsAreAppendOnly();
        return this.SaveChangesWithAdmissionAsync(
                acceptAllChangesOnSuccess,
                CancellationToken.None)
            .GetAwaiter()
            .GetResult();
    }

    public override Task<int> SaveChangesAsync(
        bool acceptAllChangesOnSuccess,
        CancellationToken cancellationToken = default)
    {
        this.EnsureDataRightsReceiptsAreAppendOnly();
        return this.SaveChangesWithAdmissionAsync(
            acceptAllChangesOnSuccess,
            cancellationToken);
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(StaffMigrations.Schema);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(StaffDbContext).Assembly);
        this.ApplyScopeConventions(modelBuilder);
    }

    private void EnsureDataRightsReceiptsAreAppendOnly()
    {
        bool correctionMutationRequested = this.ChangeTracker
            .Entries<StaffDataRightsCorrectionReceipt>()
            .Any(entry =>
                entry.State is EntityState.Modified or EntityState.Deleted);
        bool restrictionMutationRequested = this.ChangeTracker
            .Entries<StaffProcessingRestrictionReceipt>()
            .Any(entry =>
                entry.State is EntityState.Modified or EntityState.Deleted);
        bool governanceMutationRequested = this.ChangeTracker
            .Entries<StaffEmploymentGovernanceChangeReceipt>()
            .Any(entry =>
                entry.State is EntityState.Modified or EntityState.Deleted);
        bool holdReceiptMutationRequested = this.ChangeTracker
            .Entries<StaffDataHoldReceipt>()
            .Any(entry =>
                entry.State is EntityState.Modified or EntityState.Deleted);
        bool anonymisationReceiptMutationRequested = this.ChangeTracker
            .Entries<StaffAnonymisationReceipt>()
            .Any(entry =>
                entry.State is EntityState.Modified or EntityState.Deleted);
        bool anonymisationRestoreReceiptMutationRequested =
            this.ChangeTracker
                .Entries<StaffAnonymisationRestoreReceipt>()
                .Any(entry =>
                    entry.State is
                        EntityState.Modified or EntityState.Deleted);
        bool retentionReceiptMutationRequested =
            this.ChangeTracker
                .Entries<StaffRetentionAnonymisationReceipt>()
                .Any(entry =>
                    entry.State is
                        EntityState.Modified or EntityState.Deleted);
        bool tombstoneDeletionRequested = this.ChangeTracker
            .Entries<StaffAnonymisationTombstone>()
            .Any(entry => entry.State == EntityState.Deleted);
        bool tenantDestroyReceiptMutationRequested = this.ChangeTracker
            .Entries<StaffTenantDestroyReceipt>()
            .Any(entry =>
                entry.State is EntityState.Modified or EntityState.Deleted);
        bool memberMutationOperationMutationRequested = this.ChangeTracker
            .Entries<StaffMemberMutationOperation>()
            .Any(entry => entry.State == EntityState.Modified);
        bool identityProvisioningAnchorMutationRequested = this.ChangeTracker
            .Entries<StaffIdentityProvisioningAnchor>()
            .Any(entry =>
                entry.State is EntityState.Modified or EntityState.Deleted);
        bool identityProvisioningAnchorResolutionMutationRequested =
            this.ChangeTracker
                .Entries<StaffIdentityProvisioningAnchorResolution>()
                .Any(entry =>
                    entry.State is
                        EntityState.Modified or EntityState.Deleted);
        if (correctionMutationRequested ||
            restrictionMutationRequested ||
            governanceMutationRequested ||
            holdReceiptMutationRequested ||
            anonymisationReceiptMutationRequested ||
            anonymisationRestoreReceiptMutationRequested ||
            retentionReceiptMutationRequested ||
            tombstoneDeletionRequested ||
            tenantDestroyReceiptMutationRequested ||
            memberMutationOperationMutationRequested ||
            identityProvisioningAnchorMutationRequested ||
            identityProvisioningAnchorResolutionMutationRequested)
        {
            throw new InvalidOperationException(
                "Staff immutable receipts are append-only.");
        }
    }

    private async Task<int> SaveChangesWithAdmissionAsync(
        bool acceptAllChangesOnSuccess,
        CancellationToken cancellationToken)
    {
        bool hasOperationalMutation = this.HasOperationalMutation();
        if (!hasOperationalMutation && !this.HasTenantOwnedMutation())
        {
            return await base.SaveChangesAsync(
                acceptAllChangesOnSuccess,
                cancellationToken).ConfigureAwait(false);
        }

        if (!this.scopeContext.IsEnabled ||
            string.IsNullOrWhiteSpace(this.scopeContext.ScopeId))
        {
            throw new StaffOperationalAdmissionException(
                StaffOperationalAdmissionFailure.Unavailable);
        }

        string tenantId = this.scopeContext.ScopeId;
        IDbContextTransaction? ownedTransaction = null;
        if (this.Database.IsRelational() &&
            this.Database.CurrentTransaction is null)
        {
            ownedTransaction = await this.Database
                .BeginTransactionAsync(cancellationToken)
                .ConfigureAwait(false);
        }

        try
        {
            await this.AcquireOperationalMutationAdmissionAsync(
                    cancellationToken)
                .ConfigureAwait(false);
            if (this.Database.IsRelational() && hasOperationalMutation)
            {
                await StaffTenantMutationLock.AcquireRevisionAdvanceAsync(
                        this,
                        tenantId,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            if (hasOperationalMutation)
            {
                await this.AdvanceTenantRevisionAsync(
                    tenantId,
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
                await ownedTransaction.RollbackAsync(CancellationToken.None)
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

    internal async Task AcquireOperationalMutationAdmissionAsync(
        CancellationToken cancellationToken)
    {
        if (!this.scopeContext.IsEnabled ||
            string.IsNullOrWhiteSpace(this.scopeContext.ScopeId) ||
            (this.Database.IsRelational() &&
             this.Database.CurrentTransaction is null))
        {
            throw new StaffOperationalAdmissionException(
                StaffOperationalAdmissionFailure.Unavailable);
        }

        if (this.Database.IsRelational())
        {
            await StaffTenantMutationLock.AcquireAdmissionAsync(
                    this,
                    this.scopeContext.ScopeId,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        await this.EnsureOperationalAdmissionAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task EnsureOperationalAdmissionAsync(
        CancellationToken cancellationToken)
    {
        StaffTenantRevision? localState =
            this.TenantRevisions.Local.SingleOrDefault() ??
            await this.TenantRevisions
                .AsNoTracking()
                .SingleOrDefaultAsync(cancellationToken)
                .ConfigureAwait(false);
        if (localState is not null && !localState.IsOpen)
        {
            throw new StaffOperationalAdmissionException(
                StaffOperationalAdmissionFailure.Restricted);
        }

        if (terminationFences is null)
        {
            if (this.Database.IsRelational())
            {
                throw new StaffOperationalAdmissionException(
                    StaffOperationalAdmissionFailure.Unavailable);
            }

            return;
        }

        WorkspaceTerminationFenceSnapshot? fence;
        try
        {
            fence = await terminationFences.GetCurrentAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception)
            when (exception is not OperationCanceledException)
        {
            throw new StaffOperationalAdmissionException(
                StaffOperationalAdmissionFailure.Unavailable);
        }

        if (fence is null)
        {
            return;
        }

        bool valid = fence.ProcessId != Guid.Empty &&
            fence.TerminationEpoch != Guid.Empty &&
            fence.Version > 0 &&
            fence.State is
                WorkspaceTerminationFenceState.Frozen or
                WorkspaceTerminationFenceState.DestructionStarted or
                WorkspaceTerminationFenceState.Closed;
        throw new StaffOperationalAdmissionException(
            valid
                ? StaffOperationalAdmissionFailure.Restricted
                : StaffOperationalAdmissionFailure.Unavailable);
    }

    private async Task AdvanceTenantRevisionAsync(
        string tenantId,
        CancellationToken cancellationToken)
    {
        StaffTenantRevision? revision =
            this.TenantRevisions.Local.SingleOrDefault() ??
            await this.TenantRevisions
                .SingleOrDefaultAsync(cancellationToken)
                .ConfigureAwait(false);
        if (revision is null)
        {
            this.TenantRevisions.Add(StaffTenantRevision.Create(tenantId));
        }
        else
        {
            revision.Advance();
        }
    }

    private bool HasOperationalMutation() =>
        this.ChangeTracker.Entries().Any(entry =>
            (entry.State is EntityState.Added or EntityState.Modified or
                EntityState.Deleted) &&
            entry.Entity is not (
                InboxMessage or
                OutboxMessage or
                StaffProcessingRestrictionProjection or
                StaffPropertyProjection or
                StaffOperationLock or
                StaffProjectionRebuildCheckpoint or
                StaffRetentionSweepCheckpoint or
                StaffTenantRevision or
                StaffTenantDestroyOperation or
                StaffTenantDestroyReceipt));

    private bool HasTenantOwnedMutation() =>
        this.ChangeTracker.Entries().Any(entry =>
            (entry.State is EntityState.Added or
                EntityState.Modified or EntityState.Deleted) &&
            entry.Entity switch
            {
                InboxMessage message =>
                    entry.State == EntityState.Added &&
                    !string.IsNullOrWhiteSpace(message.ScopeId),
                OutboxMessage message =>
                    entry.State == EntityState.Added &&
                    !string.IsNullOrWhiteSpace(message.ScopeId),
                StaffTenantRevision or
                StaffTenantDestroyOperation or
                StaffTenantDestroyReceipt => false,
                _ => true
            });

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
                await StaffTenantMutationLock.AcquireAdmissionAsync(
                    this,
                    canonicalTenantId,
                    cancellationToken).ConfigureAwait(false);
            }

            await this.EnsureOperationalAdmissionAsync(cancellationToken)
                .ConfigureAwait(false);
            return true;
        }
        catch (StaffOperationalAdmissionException)
        {
            return false;
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
        CancellationToken cancellationToken,
        StaffTenantDestroyStage? attemptedStage = null)
    {
        if (!TenantIds.TryNormalize(tenantId, out string? canonicalTenantId) ||
            operationId == Guid.Empty ||
            !this.scopeContext.IsEnabled ||
            !string.Equals(
                this.scopeContext.ScopeId,
                canonicalTenantId,
                StringComparison.Ordinal) ||
            (this.Database.IsRelational() &&
             this.Database.CurrentTransaction is null))
        {
            throw new InvalidOperationException(
                "Staff tenant destruction admission is invalid.");
        }

        StaffTenantRevision? state =
            this.TenantRevisions.Local.SingleOrDefault() ??
            await this.TenantRevisions
                .SingleOrDefaultAsync(cancellationToken)
                .ConfigureAwait(false);
        if (state is null || state.DestroyOperationId != operationId ||
            state.LifecycleStatus is not (
                StaffTenantLifecycleStatus.Closing or
                StaffTenantLifecycleStatus.Closed))
        {
            throw new InvalidOperationException(
                "Staff tenant destruction state is invalid.");
        }

        StaffTenantDestroyOperation? operation =
            this.TenantDestroyOperations.Local.SingleOrDefault(item =>
                item.OperationId == operationId) ??
            await this.TenantDestroyOperations.SingleOrDefaultAsync(
                item => item.OperationId == operationId,
                cancellationToken).ConfigureAwait(false);
        if (operation is null)
        {
            throw new InvalidOperationException(
                "Staff tenant destruction operation is invalid.");
        }

        StaffTenantDestroyStage authorizedStage =
            attemptedStage ?? operation.Stage;
        bool anchorDeletionRequested = this.ChangeTracker
            .Entries<StaffIdentityProvisioningAnchor>()
            .Any(entry => entry.State == EntityState.Deleted);
        bool resolutionDeletionRequested = this.ChangeTracker
            .Entries<StaffIdentityProvisioningAnchorResolution>()
            .Any(entry => entry.State == EntityState.Deleted);
        if (anchorDeletionRequested || resolutionDeletionRequested)
        {
            if ((anchorDeletionRequested && authorizedStage !=
                    StaffTenantDestroyStage.IdentityProvisioningAnchors) ||
                (resolutionDeletionRequested && authorizedStage !=
                    StaffTenantDestroyStage
                        .IdentityProvisioningAnchorResolutions))
            {
                throw new InvalidOperationException(
                    "Staff identity-anchor destruction stage is invalid.");
            }
        }

        if (this.Database.IsNpgsql())
        {
            await this.Database.ExecuteSqlInterpolatedAsync(
                    $"SELECT set_config('bunkfy.staff_tenant_destroy_operation_id', {operationId.ToString("D")}, true)",
                    cancellationToken)
                .ConfigureAwait(false);
            await this.Database.ExecuteSqlInterpolatedAsync(
                    $"SELECT set_config('bunkfy.staff_tenant_destroy_stage', {((int)authorizedStage).ToString(System.Globalization.CultureInfo.InvariantCulture)}, true)",
                    cancellationToken)
                .ConfigureAwait(false);
        }

        return await base.SaveChangesAsync(
                acceptAllChangesOnSuccess: true,
                cancellationToken)
            .ConfigureAwait(false);
    }
}
