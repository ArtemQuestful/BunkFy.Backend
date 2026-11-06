namespace BunkFy.Modules.Reservations.Persistence.Repositories;

using BunkFy.Modules.Properties.Contracts;
using BunkFy.Modules.Reservations.Application;
using BunkFy.Modules.Reservations.Application.Ports;
using BunkFy.Modules.Reservations.Domain.Aggregates;
using BunkFy.Modules.Reservations.Domain.Models;
using Microsoft.EntityFrameworkCore;

internal sealed class ReservationRetentionCandidateRepository(
    ReservationsDbContext dbContext)
    : IReservationRetentionCandidateRepository
{
    public async Task<ReservationRetentionScanPage> ScanAsync(
        long afterProjectionOrdinal,
        int limit,
        CancellationToken cancellationToken)
    {
        if (afterProjectionOrdinal < 0 ||
            limit is < 1 or >
                ReservationRetentionOptions.MaximumScanSize)
        {
            throw new ArgumentOutOfRangeException(
                afterProjectionOrdinal < 0
                    ? nameof(afterProjectionOrdinal)
                    : nameof(limit));
        }

        IQueryable<Reservation> candidates = this.QueryCandidates()
            .Where(reservation =>
                reservation.ProjectionOrdinal >
                    afterProjectionOrdinal)
            .OrderBy(reservation =>
                reservation.ProjectionOrdinal)
            .Take(checked(limit + 1));
        ReservationRetentionHead[] rows = await this.ProjectHeads(
                candidates)
            .ToArrayAsync(cancellationToken)
            .ConfigureAwait(false);
        bool reachedEnd = rows.Length <= limit;
        ReservationRetentionHead[] page =
            rows.Take(limit).ToArray();
        return new(
            await this.LoadSnapshotsAsync(
                page,
                cancellationToken).ConfigureAwait(false),
            reachedEnd);
    }

    public async Task<ReservationRetentionCandidateSnapshot?> LoadAsync(
        Guid propertyId,
        Guid reservationId,
        CancellationToken cancellationToken)
    {
        IQueryable<Reservation> candidates = this.QueryCandidates()
            .Where(reservation =>
                reservation.PropertyId == propertyId &&
                reservation.Id == reservationId);
        ReservationRetentionHead? head =
            await this.ProjectHeads(candidates)
                .SingleOrDefaultAsync(
                    cancellationToken)
                .ConfigureAwait(false);
        if (head is null)
        {
            return null;
        }

        return (await this.LoadSnapshotsAsync(
                [head],
                cancellationToken).ConfigureAwait(false))
            .Single();
    }

    private IQueryable<Reservation> QueryCandidates()
    {
        string scopeId = dbContext.CurrentScopeId;
        return dbContext.Reservations
            .AsNoTracking()
            .Where(reservation =>
                reservation.ScopeId == scopeId &&
                !reservation.IsAnonymised &&
                reservation.TerminalAtUtc != null &&
                (reservation.Status ==
                    ReservationState.AllocationRejected ||
                 reservation.Status == ReservationState.Cancelled ||
                 reservation.Status == ReservationState.NoShow ||
                 reservation.Status == ReservationState.CheckedOut));
    }

    private IQueryable<ReservationRetentionHead> ProjectHeads(
        IQueryable<Reservation> candidates)
    {
        string scopeId = dbContext.CurrentScopeId;
        return candidates.Select(reservation => new ReservationRetentionHead(
                reservation.Id,
                reservation.PropertyId,
                reservation.Version,
                reservation.DetailsRevision,
                reservation.ProjectionOrdinal,
                reservation.Status,
                reservation.PendingAllocationAmendmentId != null,
                reservation.IsAnonymised,
                reservation.TerminalAtUtc,
                dbContext.DataHolds.Count(hold =>
                    hold.ScopeId == scopeId &&
                    hold.PropertyId == reservation.PropertyId &&
                    hold.ReservationId == reservation.Id &&
                    hold.State == ReservationDataHoldState.Active),
                dbContext.DataHolds
                    .Where(hold =>
                        hold.ScopeId == scopeId &&
                        hold.PropertyId == reservation.PropertyId &&
                        hold.ReservationId == reservation.Id &&
                        hold.State ==
                            ReservationDataHoldState.Active)
                    .Select(hold =>
                        (DateTimeOffset?)hold.PlacedAtUtc)
                    .Min(),
                dbContext.ProcessingRestrictionProjections
                    .Where(projection =>
                        projection.ScopeId == scopeId &&
                        projection.PropertyId ==
                            reservation.PropertyId &&
                        projection.ReservationId == reservation.Id)
                    .Select(projection =>
                        (int?)projection.ContractVersion)
                    .SingleOrDefault()));
    }

    private async Task<
        IReadOnlyList<ReservationRetentionCandidateSnapshot>>
        LoadSnapshotsAsync(
            IReadOnlyList<ReservationRetentionHead> heads,
            CancellationToken cancellationToken)
    {
        if (heads.Count == 0)
        {
            return [];
        }

        Guid[] propertyIds = heads
            .Select(head => head.PropertyId)
            .Distinct()
            .ToArray();
        string scopeId = dbContext.CurrentScopeId;
        var propertySqlRows =
            await dbContext.PropertyProjections
                .AsNoTracking()
                .Where(property =>
                    property.ScopeId == scopeId &&
                    propertyIds.Contains(property.Id))
                .SelectMany(
                    property => property.GovernancePolicy!
                        .Acknowledgements
                        .OrderBy(acknowledgement =>
                            acknowledgement.AcknowledgementId)
                        .ThenBy(acknowledgement =>
                            acknowledgement.AcknowledgementVersion)
                        .Select(acknowledgement => new
                        {
                            // Retain the outer correlation so Npgsql emits a
                            // per-property LATERAL LIMIT instead of a global
                            // acknowledgement ROW_NUMBER window.
                            CorrelatedPropertyId =
                                (Guid?)property.Id,
                            acknowledgement.AcknowledgementId,
                            AcknowledgementVersion =
                                (int?)acknowledgement
                                    .AcknowledgementVersion
                        })
                        .Take(PropertiesContractLimits
                            .MaximumPolicyAcknowledgements + 1)
                        .DefaultIfEmpty(),
                    (property, acknowledgement) =>
                        new
                        {
                            PropertyId = property.Id,
                            property.IsKnown,
                            property.IsActive,
                            property.ProcessingStatus,
                            property.TopologySourceVersion,
                            property.PolicySourceVersion,
                            HasGovernancePolicy =
                                property.GovernancePolicy != null,
                            OperatingCountryCode =
                                property.GovernancePolicy == null
                                ? null
                                : property.GovernancePolicy
                                    .OperatingCountryCode,
                            PolicyId = property.GovernancePolicy == null
                                ? null
                                : property.GovernancePolicy.PolicyId,
                            PolicyVersion = property.GovernancePolicy == null
                                ? null
                                : (int?)property.GovernancePolicy
                                    .PolicyVersion,
                            DataRegionId = property.GovernancePolicy == null
                                ? null
                                : property.GovernancePolicy.DataRegionId,
                            TransferProfileId =
                                property.GovernancePolicy == null
                                ? null
                                : property.GovernancePolicy
                                    .TransferProfileId,
                            RetentionPolicyId =
                                property.GovernancePolicy == null
                                ? null
                                : property.GovernancePolicy
                                    .RetentionPolicyId,
                            RetentionPolicyVersion =
                                property.GovernancePolicy == null
                                ? null
                                : (int?)property.GovernancePolicy
                                    .RetentionPolicyVersion,
                            ContentSha256 =
                                property.GovernancePolicy == null
                                ? null
                                : property.GovernancePolicy.ContentSha256,
                            PolicyEffectiveAtUtc =
                                property.GovernancePolicy == null
                                ? null
                                : (DateTimeOffset?)property.GovernancePolicy
                                    .PolicyEffectiveAtUtc,
                            PolicyExpiresAtUtc =
                                property.GovernancePolicy == null
                                ? null
                                : (DateTimeOffset?)property.GovernancePolicy
                                    .PolicyExpiresAtUtc,
                            ActivatedAtUtc =
                                property.GovernancePolicy == null
                                ? null
                                : (DateTimeOffset?)property.GovernancePolicy
                                    .ActivatedAtUtc,
                            AcknowledgementPropertyId =
                                acknowledgement == null
                                ? null
                                : acknowledgement.CorrelatedPropertyId,
                            AcknowledgementId = acknowledgement == null
                                ? null
                                : acknowledgement.AcknowledgementId,
                            AcknowledgementVersion =
                                acknowledgement == null
                                ? null
                                : acknowledgement
                                    .AcknowledgementVersion
                        })
                .OrderBy(row => row.PropertyId)
                .ThenBy(row => row.AcknowledgementId)
                .ThenBy(row => row.AcknowledgementVersion)
                .ToArrayAsync(cancellationToken)
                .ConfigureAwait(false);
        ReservationRetentionPropertyPolicyRow[] propertyRows =
            propertySqlRows
                .Select(row =>
                    new ReservationRetentionPropertyPolicyRow(
                        row.PropertyId,
                        row.IsKnown,
                        row.IsActive,
                        row.ProcessingStatus,
                        row.TopologySourceVersion,
                        row.PolicySourceVersion,
                        row.HasGovernancePolicy,
                        row.OperatingCountryCode,
                        row.PolicyId,
                        row.PolicyVersion,
                        row.DataRegionId,
                        row.TransferProfileId,
                        row.RetentionPolicyId,
                        row.RetentionPolicyVersion,
                        row.ContentSha256,
                        row.PolicyEffectiveAtUtc,
                        row.PolicyExpiresAtUtc,
                        row.ActivatedAtUtc,
                        row.AcknowledgementPropertyId,
                        row.AcknowledgementId,
                        row.AcknowledgementVersion))
                .ToArray();
        Dictionary<Guid, ReservationRetentionPropertySnapshot>
            propertyById = propertyRows
                .GroupBy(row => row.PropertyId)
                .ToDictionary(
                    group => group.Key,
                    group => CreateProperty(group.ToArray()));

        return heads
            .Select(head =>
                new ReservationRetentionCandidateSnapshot(
                    head.ReservationId,
                    head.PropertyId,
                    head.ReservationVersion,
                    head.DetailsRevision,
                    head.ProjectionOrdinal,
                    head.Status,
                    head.HasPendingAllocationAmendment,
                    head.IsAnonymised,
                    head.TerminalAtUtc,
                    head.ActiveHoldCount,
                    head.EarliestHoldPlacedAtUtc,
                    head.ProcessingRestrictionContractVersion,
                    propertyById.GetValueOrDefault(
                        head.PropertyId)))
            .ToArray();
    }

    private static ReservationRetentionPropertySnapshot CreateProperty(
        IReadOnlyList<ReservationRetentionPropertyPolicyRow> rows)
    {
        ReservationRetentionPropertyPolicyRow property = rows[0];
        ReservationRetentionPropertyPolicyRow[] acknowledgements = rows
            .Where(row => row.AcknowledgementPropertyId.HasValue)
            .ToArray();
        return new(
            property.IsKnown,
            property.IsActive,
            property.ProcessingStatus,
            property.TopologySourceVersion,
            property.PolicySourceVersion,
            CreatePolicy(property, acknowledgements));
    }

    private static PropertyGovernancePolicyBinding? CreatePolicy(
        ReservationRetentionPropertyPolicyRow policy,
        IReadOnlyList<ReservationRetentionPropertyPolicyRow>
            acknowledgements)
    {
        if (!policy.HasGovernancePolicy ||
            acknowledgements.Count >
                PropertiesContractLimits.MaximumPolicyAcknowledgements)
        {
            return null;
        }

        try
        {
            return new(
                policy.OperatingCountryCode!,
                policy.PolicyId!,
                policy.PolicyVersion!.Value,
                policy.DataRegionId!,
                policy.TransferProfileId!,
                policy.RetentionPolicyId!,
                policy.RetentionPolicyVersion!.Value,
                policy.ContentSha256!,
                policy.PolicyEffectiveAtUtc!.Value,
                policy.PolicyExpiresAtUtc!.Value,
                policy.ActivatedAtUtc!.Value,
                acknowledgements.Select(acknowledgement =>
                    new PropertyGovernanceAcknowledgement(
                        acknowledgement.AcknowledgementId!,
                        acknowledgement.AcknowledgementVersion!.Value))
                    .ToArray());
        }
        catch (Exception exception) when (
            exception is ArgumentException or InvalidOperationException)
        {
            return null;
        }
    }

    private sealed record ReservationRetentionHead(
        Guid ReservationId,
        Guid PropertyId,
        long ReservationVersion,
        long DetailsRevision,
        long ProjectionOrdinal,
        ReservationState Status,
        bool HasPendingAllocationAmendment,
        bool IsAnonymised,
        DateTimeOffset? TerminalAtUtc,
        int ActiveHoldCount,
        DateTimeOffset? EarliestHoldPlacedAtUtc,
        int? ProcessingRestrictionContractVersion);

    private sealed record ReservationRetentionPropertyPolicyRow(
        Guid PropertyId,
        bool IsKnown,
        bool IsActive,
        PropertyProcessingStatus ProcessingStatus,
        long TopologySourceVersion,
        long PolicySourceVersion,
        bool HasGovernancePolicy,
        string? OperatingCountryCode,
        string? PolicyId,
        int? PolicyVersion,
        string? DataRegionId,
        string? TransferProfileId,
        string? RetentionPolicyId,
        int? RetentionPolicyVersion,
        string? ContentSha256,
        DateTimeOffset? PolicyEffectiveAtUtc,
        DateTimeOffset? PolicyExpiresAtUtc,
        DateTimeOffset? ActivatedAtUtc,
        Guid? AcknowledgementPropertyId,
        string? AcknowledgementId,
        int? AcknowledgementVersion);
}
