namespace BunkFy.Modules.Guests.Persistence.Repositories;

using BunkFy.Modules.Guests.Application.Ports;
using BunkFy.Modules.Guests.Contracts;
using BunkFy.Modules.Guests.Domain.Aggregates;
using BunkFy.Modules.Guests.Domain.Models;
using BunkFy.Modules.Properties.Contracts;
using Microsoft.EntityFrameworkCore;

internal sealed class GuestRetentionCandidateRepository(
    GuestsDbContext dbContext)
    : IGuestRetentionCandidateRepository
{
    private const int MaximumCandidateHeadsPerPage = 1000;

    public async Task<GuestRetentionScanPage> ScanAsync(
        long afterProjectionOrdinal,
        int limit,
        CancellationToken cancellationToken)
    {
        if (afterProjectionOrdinal < 0 ||
            limit is < 1 or > MaximumCandidateHeadsPerPage)
        {
            throw new ArgumentOutOfRangeException(
                afterProjectionOrdinal < 0
                    ? nameof(afterProjectionOrdinal)
                    : nameof(limit));
        }

        string scopeId = dbContext.CurrentScopeId;
        GuestRetentionProfileHead[] rows = await dbContext.GuestProfiles
            .AsNoTracking()
            .Where(profile =>
                profile.ScopeId == scopeId &&
                profile.ProjectionOrdinal > afterProjectionOrdinal &&
                (profile.Status == GuestProfileState.Active ||
                 profile.Status == GuestProfileState.Archived))
            .OrderBy(profile => profile.ProjectionOrdinal)
            .Select(profile => new GuestRetentionProfileHead(
                profile.Id,
                profile.Version,
                profile.ProjectionOrdinal,
                profile.Status,
                profile.OriginPropertyId))
            .Take(checked(limit + 1))
            .ToArrayAsync(cancellationToken)
            .ConfigureAwait(false);
        bool sourceReachedEnd = rows.Length <= limit;
        GuestRetentionProfileHead[] requestedPage = rows
            .Take(limit)
            .ToArray();
        GuestRetentionAssociationCount[] associationCounts =
            await this.LoadAssociationCountsAsync(
                requestedPage,
                cancellationToken).ConfigureAwait(false);
        Dictionary<Guid, int> countsByGuest = associationCounts
            .ToDictionary(
                count => count.GuestId,
                count => count.PropertyCount);
        List<GuestRetentionProfileHead> selectedPage = [];
        int associationBudget = 0;
        foreach (GuestRetentionProfileHead profile in requestedPage)
        {
            int propertyCount = countsByGuest[profile.GuestId];
            if (propertyCount <= GuestAnonymisationEligibilityContract
                    .MaximumAffectedProperties)
            {
                if (associationBudget >
                    GuestAnonymisationEligibilityContract
                        .MaximumAffectedProperties - propertyCount)
                {
                    break;
                }

                associationBudget += propertyCount;
            }

            selectedPage.Add(profile);
        }

        bool reachedEnd = sourceReachedEnd &&
            selectedPage.Count == requestedPage.Length;
        IReadOnlyList<GuestRetentionCandidateSnapshot> candidates =
            await this.LoadSnapshotsAsync(
                selectedPage,
                associationCounts,
                cancellationToken).ConfigureAwait(false);
        return new(candidates, reachedEnd);
    }

    public async Task<GuestRetentionCandidateSnapshot?> LoadAsync(
        Guid guestId,
        CancellationToken cancellationToken)
    {
        string scopeId = dbContext.CurrentScopeId;
        GuestRetentionProfileHead? profile = await dbContext.GuestProfiles
            .AsNoTracking()
            .Where(item =>
                item.ScopeId == scopeId &&
                item.Id == guestId &&
                (item.Status == GuestProfileState.Active ||
                 item.Status == GuestProfileState.Archived))
            .Select(item => new GuestRetentionProfileHead(
                item.Id,
                item.Version,
                item.ProjectionOrdinal,
                item.Status,
                item.OriginPropertyId))
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        if (profile is null)
        {
            return null;
        }

        GuestRetentionAssociationCount[] associationCounts =
            await this.LoadAssociationCountsAsync(
                [profile],
                cancellationToken).ConfigureAwait(false);
        return (await this.LoadSnapshotsAsync(
                [profile],
                associationCounts,
                cancellationToken).ConfigureAwait(false))
            .Single();
    }

    private async Task<GuestRetentionAssociationCount[]>
        LoadAssociationCountsAsync(
            IReadOnlyList<GuestRetentionProfileHead> profiles,
            CancellationToken cancellationToken)
    {
        if (profiles.Count == 0)
        {
            return [];
        }

        Guid[] guestIds = profiles
            .Select(profile => profile.GuestId)
            .ToArray();
        string scopeId = dbContext.CurrentScopeId;
        GuestRetentionAssociationCount[] counts = await dbContext.GuestProfiles
            .AsNoTracking()
            .Where(profile =>
                profile.ScopeId == scopeId &&
                guestIds.Contains(profile.Id))
            .Select(profile => new
            {
                GuestId = profile.Id,
                PropertyId = profile.OriginPropertyId
            })
            .Concat(
                dbContext.StayHistory
                    .AsNoTracking()
                    .Where(stay =>
                        stay.ScopeId == scopeId &&
                        guestIds.Contains(stay.GuestId))
                    .Select(stay => new
                    {
                        stay.GuestId,
                        stay.PropertyId
                    }))
            .Concat(
                dbContext.DataHolds
                    .AsNoTracking()
                    .Where(hold =>
                        hold.ScopeId == scopeId &&
                        guestIds.Contains(hold.GuestId) &&
                        hold.State == GuestDataHoldState.Active)
                    .Select(hold => new
                    {
                        hold.GuestId,
                        hold.PropertyId
                    }))
            .Distinct()
            .GroupBy(association => association.GuestId)
            .Select(group => new
            {
                GuestId = group.Key,
                PropertyCount = group.Count()
            })
            .Join(
                dbContext.GuestProfiles
                    .AsNoTracking()
                    .Where(profile =>
                        profile.ScopeId == scopeId &&
                        guestIds.Contains(profile.Id)),
                count => count.GuestId,
                profile => profile.Id,
                (count, _) => new GuestRetentionAssociationCount(
                    count.GuestId,
                    count.PropertyCount))
            .ToArrayAsync(cancellationToken)
            .ConfigureAwait(false);
        if (counts.Length == profiles.Count)
        {
            return counts;
        }

        HashSet<Guid> countedGuestIds = counts
            .Select(count => count.GuestId)
            .ToHashSet();
        return counts.Concat(profiles
                .Where(profile =>
                    !countedGuestIds.Contains(profile.GuestId))
                .Select(profile => new GuestRetentionAssociationCount(
                    profile.GuestId,
                    GuestAnonymisationEligibilityContract
                        .MaximumAffectedProperties + 1)))
            .ToArray();
    }

    private async Task<IReadOnlyList<GuestRetentionCandidateSnapshot>>
        LoadSnapshotsAsync(
            IReadOnlyList<GuestRetentionProfileHead> profiles,
            IReadOnlyList<GuestRetentionAssociationCount> associationCounts,
            CancellationToken cancellationToken)
    {
        if (profiles.Count == 0)
        {
            return [];
        }

        Guid[] guestIds = profiles.Select(profile => profile.GuestId).ToArray();
        string scopeId = dbContext.CurrentScopeId;
        HashSet<Guid> overflowedGuestIds = associationCounts
            .Where(count => count.PropertyCount >
                GuestAnonymisationEligibilityContract
                    .MaximumAffectedProperties)
            .Select(count => count.GuestId)
            .ToHashSet();
        Guid[] boundedGuestIds = guestIds
            .Where(guestId => !overflowedGuestIds.Contains(guestId))
            .ToArray();
        GuestRetentionStayAggregateRow[] stayRows =
            await dbContext.StayHistory
                .AsNoTracking()
                .Where(stay =>
                    stay.ScopeId == scopeId &&
                    boundedGuestIds.Contains(stay.GuestId))
                .GroupBy(stay => new
                {
                    stay.GuestId,
                    stay.PropertyId
                })
                .Select(group => new GuestRetentionStayAggregateRow(
                    group.Key.GuestId,
                    group.Key.PropertyId,
                    !group.Any(stay =>
                        stay.PropertyId == Guid.Empty ||
                        stay.ReservationId == Guid.Empty ||
                        stay.Role != GuestStayRole.Primary ||
                        (int)stay.Status <
                            (int)GuestStayStatus.PendingAllocation ||
                        (int)stay.Status >
                            (int)GuestStayStatus.CheckedOut ||
                        stay.ReservationVersion < 1 ||
                        stay.ProjectionContractVersion !=
                            GuestsModuleMetadata
                                .StayHistoryProjectionVersion),
                    group.Any(stay =>
                        stay.IsCurrentParticipant &&
                        (stay.Status ==
                            GuestStayStatus.PendingAllocation ||
                         stay.Status == GuestStayStatus.Confirmed ||
                         stay.Status ==
                            GuestStayStatus.CancellationPending ||
                         stay.Status == GuestStayStatus.CheckedIn ||
                         stay.Status == GuestStayStatus.NoShowPending ||
                         stay.Status ==
                            GuestStayStatus.CheckoutPending)),
                    group.Max(stay =>
                        stay.Status ==
                            GuestStayStatus.AllocationRejected ||
                        stay.Status == GuestStayStatus.Cancelled ||
                        stay.Status == GuestStayStatus.NoShow ||
                        stay.Status == GuestStayStatus.CheckedOut
                            ? (DateOnly?)stay.Departure
                            : null),
                    group.Max(stay =>
                        stay.Status ==
                            GuestStayStatus.AllocationRejected ||
                        stay.Status == GuestStayStatus.Cancelled ||
                        stay.Status == GuestStayStatus.NoShow ||
                        stay.Status == GuestStayStatus.CheckedOut
                            ? stay.NoShowBusinessDate
                            : null),
                    group.Max(stay =>
                        stay.Status ==
                            GuestStayStatus.AllocationRejected ||
                        stay.Status == GuestStayStatus.Cancelled ||
                        stay.Status == GuestStayStatus.NoShow ||
                        stay.Status == GuestStayStatus.CheckedOut
                            ? stay.CheckedOutBusinessDate
                            : null)))
                .Take(GuestAnonymisationEligibilityContract
                    .MaximumAffectedProperties + 1)
                .ToArrayAsync(cancellationToken)
                .ConfigureAwait(false);
        GuestRetentionHoldRow[] holdRows = await dbContext.DataHolds
            .AsNoTracking()
            .Where(hold =>
                hold.ScopeId == scopeId &&
                boundedGuestIds.Contains(hold.GuestId) &&
                hold.State == GuestDataHoldState.Active)
            .GroupBy(hold => new
            {
                hold.GuestId,
                hold.PropertyId
            })
            .Select(group => new GuestRetentionHoldRow(
                group.Key.GuestId,
                group.Key.PropertyId,
                group.Min(hold => hold.PlacedAtUtc)))
            .Take(GuestAnonymisationEligibilityContract
                .MaximumAffectedProperties + 1)
            .ToArrayAsync(cancellationToken)
            .ConfigureAwait(false);

        int maximumAffectedProperties =
            GuestAnonymisationEligibilityContract
                .MaximumAffectedProperties;
        bool associationBudgetChanged =
            stayRows.Length > maximumAffectedProperties ||
            holdRows.Length > maximumAffectedProperties;
        if (!associationBudgetChanged)
        {
            int materializedAssociationCount = profiles
                .Where(profile =>
                    !overflowedGuestIds.Contains(profile.GuestId))
                .Select(profile => (
                    profile.GuestId,
                    PropertyId: profile.OriginPropertyId))
                .Concat(stayRows.Select(stay => (
                    stay.GuestId,
                    stay.PropertyId)))
                .Concat(holdRows.Select(hold => (
                    hold.GuestId,
                    hold.PropertyId)))
                .Distinct()
                .Count();
            associationBudgetChanged =
                materializedAssociationCount > maximumAffectedProperties;
        }

        if (associationBudgetChanged)
        {
            overflowedGuestIds.UnionWith(boundedGuestIds);
            boundedGuestIds = [];
            stayRows = [];
            holdRows = [];
        }

        Guid[] propertyIds = profiles
            .Where(profile =>
                !overflowedGuestIds.Contains(profile.GuestId))
            .Select(profile => profile.OriginPropertyId)
            .Concat(stayRows.Select(stay => stay.PropertyId))
            .Concat(holdRows.Select(hold => hold.PropertyId))
            .Distinct()
            .ToArray();
        GuestRetentionPropertyRow[] properties =
            await dbContext.PropertyProjections
                .AsNoTracking()
                .Where(property =>
                    property.ScopeId == scopeId &&
                    propertyIds.Contains(property.Id))
                .Select(property => new GuestRetentionPropertyRow(
                    property.Id,
                    property.IsKnown,
                    property.Status,
                    property.ProcessingStatus,
                    property.TimeZoneId,
                    property.CanonicalTimeZoneId,
                    property.TimeZoneStatus,
                    property.TimeZoneCatalogVersion,
                    property.TimeZoneEvidenceSource,
                    property.TimeZoneEvidenceSourceVersion,
                    property.TopologySourceVersion,
                    property.PolicySourceVersion,
                    property.GovernancePolicy != null,
                    property.GovernancePolicy == null
                        ? null
                        : property.GovernancePolicy.OperatingCountryCode,
                    property.GovernancePolicy == null
                        ? null
                        : property.GovernancePolicy.PolicyId,
                    property.GovernancePolicy == null
                        ? null
                        : property.GovernancePolicy.PolicyVersion,
                    property.GovernancePolicy == null
                        ? null
                        : property.GovernancePolicy.DataRegionId,
                    property.GovernancePolicy == null
                        ? null
                        : property.GovernancePolicy.TransferProfileId,
                    property.GovernancePolicy == null
                        ? null
                        : property.GovernancePolicy.RetentionPolicyId,
                    property.GovernancePolicy == null
                        ? null
                        : property.GovernancePolicy.RetentionPolicyVersion,
                    property.GovernancePolicy == null
                        ? null
                        : property.GovernancePolicy.ContentSha256,
                    property.GovernancePolicy == null
                        ? null
                        : property.GovernancePolicy.PolicyEffectiveAtUtc,
                    property.GovernancePolicy == null
                        ? null
                        : property.GovernancePolicy.PolicyExpiresAtUtc,
                    property.GovernancePolicy == null
                        ? null
                        : property.GovernancePolicy.ActivatedAtUtc))
                .ToArrayAsync(cancellationToken)
                .ConfigureAwait(false);
        GuestRetentionAcknowledgementCount[] acknowledgementCounts =
            await dbContext.PropertyProjections
                .AsNoTracking()
                .Where(property =>
                    property.ScopeId == scopeId &&
                    propertyIds.Contains(property.Id) &&
                    property.GovernancePolicy != null)
                .SelectMany(
                    property => property.GovernancePolicy!
                        .Acknowledgements,
                    (property, acknowledgement) => new
                    {
                        PropertyId = property.Id,
                        Acknowledgement = acknowledgement
                    })
                .GroupBy(row => row.PropertyId)
                .Select(group => new GuestRetentionAcknowledgementCount(
                    group.Key,
                    group.Count()))
                .ToArrayAsync(cancellationToken)
                .ConfigureAwait(false);
        HashSet<Guid> unavailablePropertyIds = acknowledgementCounts
            .Where(count => count.AcknowledgementCount >
                PropertiesContractLimits.MaximumPolicyAcknowledgements)
            .Select(count => count.PropertyId)
            .ToHashSet();
        Guid[] acknowledgementPropertyIds = propertyIds
            .Where(propertyId =>
                !unavailablePropertyIds.Contains(propertyId))
            .ToArray();
        int maximumAcknowledgementRows = checked(
            acknowledgementPropertyIds.Length *
            PropertiesContractLimits.MaximumPolicyAcknowledgements);
        GuestRetentionAcknowledgementRow[] acknowledgementRows =
            acknowledgementPropertyIds.Length == 0
                ? []
                : await dbContext.PropertyProjections
                    .AsNoTracking()
                    .Where(property =>
                        property.ScopeId == scopeId &&
                        acknowledgementPropertyIds.Contains(property.Id) &&
                        property.GovernancePolicy != null)
                    .SelectMany(
                        property => property.GovernancePolicy!
                            .Acknowledgements
                            .OrderBy(acknowledgement =>
                                acknowledgement.AcknowledgementId)
                            .ThenBy(acknowledgement =>
                                acknowledgement.AcknowledgementVersion)
                            .Take(PropertiesContractLimits
                                .MaximumPolicyAcknowledgements + 1),
                        (property, acknowledgement) => new
                        {
                            PropertyId = property.Id,
                            acknowledgement.AcknowledgementId,
                            acknowledgement.AcknowledgementVersion
                        })
                    .OrderBy(row => row.PropertyId)
                    .ThenBy(row => row.AcknowledgementId)
                    .ThenBy(row => row.AcknowledgementVersion)
                    .Take(checked(maximumAcknowledgementRows + 1))
                    .Select(row => new GuestRetentionAcknowledgementRow(
                        row.PropertyId,
                        row.AcknowledgementId,
                        row.AcknowledgementVersion))
                    .ToArrayAsync(cancellationToken)
                    .ConfigureAwait(false);
        if (acknowledgementRows.Length > maximumAcknowledgementRows)
        {
            unavailablePropertyIds.UnionWith(
                acknowledgementPropertyIds);
            acknowledgementRows = [];
        }
        else
        {
            unavailablePropertyIds.UnionWith(
                acknowledgementRows
                    .GroupBy(row => row.PropertyId)
                    .Where(group => group.Count() >
                        PropertiesContractLimits
                            .MaximumPolicyAcknowledgements)
                    .Select(group => group.Key));
        }

        ILookup<Guid, GuestRetentionAcknowledgementRow>
            acknowledgementsByProperty = acknowledgementRows
                .ToLookup(row => row.PropertyId);
        Dictionary<Guid, GuestRetentionPropertySnapshot> propertyIndex =
            properties.ToDictionary(
                property => property.Id,
                property => new GuestRetentionPropertySnapshot(
                    property.Id,
                    property.IsKnown &&
                        !unavailablePropertyIds.Contains(property.Id),
                    property.Status,
                    property.ProcessingStatus,
                    property.TimeZoneId,
                    property.CanonicalTimeZoneId,
                    property.TimeZoneStatus,
                    property.TimeZoneCatalogVersion,
                    property.TimeZoneEvidenceSource,
                    property.TimeZoneEvidenceSourceVersion,
                    property.TopologySourceVersion,
                    property.PolicySourceVersion,
                    unavailablePropertyIds.Contains(property.Id)
                        ? null
                        : CreateGovernancePolicy(
                            property,
                            acknowledgementsByProperty[property.Id])));

        ILookup<Guid, GuestRetentionStayAggregateRow> staysByGuest =
            stayRows.ToLookup(stay => stay.GuestId);
        ILookup<Guid, GuestRetentionHoldRow> holdsByGuest =
            holdRows.ToLookup(hold => hold.GuestId);
        return profiles.Select(profile =>
        {
            bool associationOverflowed = overflowedGuestIds.Contains(
                profile.GuestId);
            GuestRetentionStaySnapshot[] stays = staysByGuest[profile.GuestId]
                .Select(stay => new GuestRetentionStaySnapshot(
                    stay.PropertyId,
                    stay.ProjectionSupported,
                    stay.HasOperationalStay,
                    Latest(
                        stay.LatestDeparture,
                        stay.LatestNoShow,
                        stay.LatestCheckout)))
                .OrderBy(stay => stay.PropertyId)
                .ToArray();
            GuestRetentionHoldSnapshot[] holds =
                holdsByGuest[profile.GuestId]
                    .Select(hold => new GuestRetentionHoldSnapshot(
                        hold.PropertyId,
                        hold.PlacedAtUtc))
                    .OrderBy(hold => hold.PropertyId)
                    .ThenBy(hold => hold.PlacedAtUtc)
                    .ToArray();
            Guid[] affectedPropertyIds = stays
                .Select(stay => stay.PropertyId)
                .Append(profile.OriginPropertyId)
                .Concat(holds.Select(hold => hold.PropertyId))
                .Distinct()
                .OrderBy(propertyId => propertyId)
                .ToArray();
            GuestRetentionPropertySnapshot[] snapshots =
                affectedPropertyIds
                    .Where(propertyIndex.ContainsKey)
                    .Select(propertyId => propertyIndex[propertyId])
                    .ToArray();
            return new GuestRetentionCandidateSnapshot(
                profile.GuestId,
                profile.GuestVersion,
                profile.ProjectionOrdinal,
                profile.GuestState,
                profile.OriginPropertyId,
                stays,
                holds,
                snapshots,
                associationOverflowed);
        }).ToArray();
    }

    private static DateOnly? Latest(
        DateOnly? first,
        DateOnly? second,
        DateOnly? third)
    {
        DateOnly? result = first;
        if (second.HasValue &&
            (!result.HasValue || second.Value > result.Value))
        {
            result = second;
        }

        if (third.HasValue &&
            (!result.HasValue || third.Value > result.Value))
        {
            result = third;
        }

        return result;
    }

    private static PropertyGovernancePolicyBinding? CreateGovernancePolicy(
        GuestRetentionPropertyRow property,
        IEnumerable<GuestRetentionAcknowledgementRow> acknowledgements)
    {
        if (!property.HasGovernancePolicy)
        {
            return null;
        }

        return new PropertyGovernancePolicyBinding(
            property.OperatingCountryCode!,
            property.PolicyId!,
            property.PolicyVersion!.Value,
            property.DataRegionId!,
            property.TransferProfileId!,
            property.RetentionPolicyId!,
            property.RetentionPolicyVersion!.Value,
            property.PolicyContentSha256!,
            property.PolicyEffectiveAtUtc!.Value,
            property.PolicyExpiresAtUtc!.Value,
            property.PolicyActivatedAtUtc!.Value,
            acknowledgements.Select(acknowledgement =>
                new PropertyGovernanceAcknowledgement(
                    acknowledgement.AcknowledgementId,
                    acknowledgement.AcknowledgementVersion)).ToArray());
    }

    private sealed record GuestRetentionProfileHead(
        Guid GuestId,
        long GuestVersion,
        long ProjectionOrdinal,
        GuestProfileState GuestState,
        Guid OriginPropertyId);

    private sealed record GuestRetentionStayAggregateRow(
        Guid GuestId,
        Guid PropertyId,
        bool ProjectionSupported,
        bool HasOperationalStay,
        DateOnly? LatestDeparture,
        DateOnly? LatestNoShow,
        DateOnly? LatestCheckout);

    private sealed record GuestRetentionHoldRow(
        Guid GuestId,
        Guid PropertyId,
        DateTimeOffset PlacedAtUtc);

    private sealed record GuestRetentionPropertyRow(
        Guid Id,
        bool IsKnown,
        PropertyStatus Status,
        PropertyProcessingStatus ProcessingStatus,
        string? TimeZoneId,
        string? CanonicalTimeZoneId,
        PropertyTimeZoneStatus TimeZoneStatus,
        string? TimeZoneCatalogVersion,
        GuestPropertyTimeZoneEvidenceSource TimeZoneEvidenceSource,
        long TimeZoneEvidenceSourceVersion,
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
        string? PolicyContentSha256,
        DateTimeOffset? PolicyEffectiveAtUtc,
        DateTimeOffset? PolicyExpiresAtUtc,
        DateTimeOffset? PolicyActivatedAtUtc);

    private sealed record GuestRetentionAcknowledgementCount(
        Guid PropertyId,
        int AcknowledgementCount);

    private sealed record GuestRetentionAcknowledgementRow(
        Guid PropertyId,
        string AcknowledgementId,
        int AcknowledgementVersion);

    private sealed record GuestRetentionAssociationCount(
        Guid GuestId,
        int PropertyCount);
}
