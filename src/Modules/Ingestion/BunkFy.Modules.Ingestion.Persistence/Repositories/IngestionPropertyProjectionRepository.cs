namespace BunkFy.Modules.Ingestion.Persistence.Repositories;

using BunkFy.Modules.Ingestion.Application.Ports;
using BunkFy.Modules.Properties.Contracts;
using Gma.Framework.Persistence.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

internal sealed class IngestionPropertyProjectionRepository(IngestionDbContext dbContext)
    : IIngestionPropertyProjectionRepository, IRetentionFenceRepository
{
    private const string PropertyProjectionLockPrefix =
        "bunkfy:ingestion:property-projection:";

    public async Task ApplyTopologyAsync(
        IngestionPropertyTopologyWriteModel property,
        CancellationToken cancellationToken)
    {
        await this.AcquirePropertyProjectionLockAsync(
            property.ScopeId,
            property.PropertyId,
            cancellationToken).ConfigureAwait(false);
        IngestionPropertyProjection projection = await this.GetOrCreateAsync(
            property.ScopeId,
            property.PropertyId,
            cancellationToken).ConfigureAwait(false);

        projection.ApplyTopology(property.Name, property.Code, property.IsActive, property.SourceVersion);
    }

    public async Task ApplyPolicyAsync(
        IngestionPropertyPolicyWriteModel property,
        CancellationToken cancellationToken)
    {
        await this.AcquirePropertyProjectionLockAsync(
            property.ScopeId,
            property.PropertyId,
            cancellationToken).ConfigureAwait(false);
        IngestionPropertyProjection projection = await this.GetOrCreateAsync(
            property.ScopeId,
            property.PropertyId,
            cancellationToken).ConfigureAwait(false);

        projection.ApplyPolicy(property.ProcessingStatus, property.GovernancePolicy, property.SourceVersion);
    }

    public async Task ApplySnapshotAsync(
        IngestionPropertyProjectionWriteModel property,
        CancellationToken cancellationToken)
    {
        await this.AcquirePropertyProjectionLockAsync(
            property.ScopeId,
            property.PropertyId,
            cancellationToken).ConfigureAwait(false);
        IngestionPropertyProjection projection = await this.GetOrCreateAsync(
            property.ScopeId,
            property.PropertyId,
            cancellationToken).ConfigureAwait(false);

        projection.ApplySnapshot(
            property.Name,
            property.Code,
            property.IsActive,
            property.ProcessingStatus,
            property.GovernancePolicy,
            property.SourceVersion);
    }

    public async Task<IngestionPropertyPolicySnapshot?> GetPolicyAsync(
        Guid propertyId,
        CancellationToken cancellationToken)
    {
        IngestionPropertyProjection? property = await dbContext.PropertyProjections
            .AsNoTracking()
            .Include(item => item.GovernancePolicy)
            .ThenInclude(policy => policy!.Acknowledgements)
            .FirstOrDefaultAsync(item => item.Id == propertyId, cancellationToken)
            .ConfigureAwait(false);
        return property is null
            ? null
            : new IngestionPropertyPolicySnapshot(
                property.IsKnown,
                property.IsActive,
                property.ProcessingStatus,
                MapPolicy(property.GovernancePolicy));
    }

    public async Task<bool> TryAdvanceAsync(
        Guid propertyId,
        CancellationToken cancellationToken)
    {
        IngestionPropertyProjection? property = dbContext.PropertyProjections.Local.FirstOrDefault(
            item => item.Id == propertyId) ?? await dbContext.PropertyProjections.FirstOrDefaultAsync(
            item => item.Id == propertyId && item.IsKnown,
            cancellationToken).ConfigureAwait(false);
        if (property is null || !property.IsKnown)
        {
            return false;
        }

        property.AdvanceRetentionFence();
        return true;
    }

    private async Task<IngestionPropertyProjection> GetOrCreateAsync(
        string scopeId,
        Guid propertyId,
        CancellationToken cancellationToken)
    {
        IngestionPropertyProjection? projection = dbContext.PropertyProjections.Local.FirstOrDefault(
            item => item.Id == propertyId && item.ScopeId == scopeId) ??
            await dbContext.PropertyProjections.FirstOrDefaultAsync(
                item => item.Id == propertyId,
                cancellationToken).ConfigureAwait(false);
        if (projection is null)
        {
            projection = IngestionPropertyProjection.Create(propertyId, scopeId);
            dbContext.PropertyProjections.Add(projection);
        }

        return projection;
    }

    private async Task AcquirePropertyProjectionLockAsync(
        string tenantId,
        Guid propertyId,
        CancellationToken cancellationToken)
    {
        string scopeId = tenantId?.Trim() ?? string.Empty;
        if (scopeId.Length == 0 ||
            !string.Equals(scopeId, dbContext.CurrentScopeId, StringComparison.Ordinal) ||
            propertyId == Guid.Empty)
        {
            throw new InvalidOperationException(
                "An Ingestion property projection lock requires valid scoped coordinates.");
        }

        if (!dbContext.Database.IsRelational())
        {
            return;
        }

        if (dbContext.Database.CurrentTransaction is null)
        {
            throw new InvalidOperationException(
                "An Ingestion property projection lock requires an active database transaction.");
        }

        await EfTransactionKeyLock.AcquireAsync(
            dbContext,
            PropertyProjectionLockPrefix + scopeId + ':' + propertyId.ToString("N"),
            cancellationToken).ConfigureAwait(false);
    }

    private static PropertyGovernancePolicyBinding? MapPolicy(IngestionPropertyPolicyBinding? policy) =>
        policy is null
            ? null
            : new PropertyGovernancePolicyBinding(
                policy.OperatingCountryCode,
                policy.PolicyId,
                policy.PolicyVersion,
                policy.DataRegionId,
                policy.TransferProfileId,
                policy.RetentionPolicyId,
                policy.RetentionPolicyVersion,
                policy.ContentSha256,
                policy.PolicyEffectiveAtUtc,
                policy.PolicyExpiresAtUtc,
                policy.ActivatedAtUtc,
                policy.Acknowledgements.Select(acknowledgement =>
                    new PropertyGovernanceAcknowledgement(
                        acknowledgement.AcknowledgementId,
                        acknowledgement.AcknowledgementVersion)).ToArray());
}
