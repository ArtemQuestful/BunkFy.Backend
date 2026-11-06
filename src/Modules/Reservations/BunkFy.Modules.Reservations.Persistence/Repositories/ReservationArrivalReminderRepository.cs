namespace BunkFy.Modules.Reservations.Persistence.Repositories;

using System.Runtime.CompilerServices;
using BunkFy.Modules.Properties.Contracts;
using BunkFy.Modules.Reservations.Application.Ports;
using BunkFy.Modules.Reservations.Contracts;
using BunkFy.Modules.Reservations.Domain.Aggregates;
using Gma.Framework.Persistence.EntityFrameworkCore;
using Gma.Framework.Runtime.Identity;
using Microsoft.EntityFrameworkCore;

internal sealed class ReservationArrivalReminderRepository(
    ReservationsDbContext dbContext,
    IIdGenerator idGenerator,
    IReservationOperationLock operationLock)
    : IReservationArrivalReminderRepository, IReservationPropertyPolicyRepository
{
    private const string PropertyProjectionLockPrefix =
        "bunkfy:reservations:property-projection:";

    public async Task ApplyPropertyAsync(
        ReservationReminderPropertyWriteModel property,
        CancellationToken cancellationToken)
    {
        await this.AcquirePropertyProjectionLockAsync(
            property.ScopeId,
            property.PropertyId,
            cancellationToken).ConfigureAwait(false);
        ReservationPropertyProjection? projection = dbContext.PropertyProjections.Local.FirstOrDefault(
            item => item.Id == property.PropertyId && item.ScopeId == property.ScopeId) ??
            await dbContext.PropertyProjections.FirstOrDefaultAsync(
                item => item.Id == property.PropertyId,
                cancellationToken).ConfigureAwait(false);
        if (projection is null)
        {
            projection = ReservationPropertyProjection.Create(property.PropertyId, property.ScopeId);
            dbContext.PropertyProjections.Add(projection);
        }

        if (!projection.ApplyTopology(property.TimeZoneId, property.IsActive, property.SourceVersion))
        {
            return;
        }

        ReservationArrivalReminder[] existing = await dbContext.ArrivalReminders
            .Where(reminder => reminder.PropertyId == property.PropertyId)
            .ToArrayAsync(cancellationToken).ConfigureAwait(false);

        if (!projection.IsActive || string.IsNullOrWhiteSpace(projection.TimeZoneId))
        {
            Supersede(existing);
            return;
        }

        DateOnly earliestRelevantArrival = DateOnly.FromDateTime(property.OccurredAtUtc.UtcDateTime.AddDays(-1));
        ReservationReminderSource[] reservations = await dbContext.Reservations
            .AsNoTracking()
            .Where(reservation =>
                reservation.PropertyId == property.PropertyId &&
                !reservation.IsAnonymised &&
                reservation.ExpectedArrivalTime != null &&
                reservation.Arrival >= earliestRelevantArrival &&
                (reservation.Status == ReservationState.PendingAllocation ||
                 reservation.Status == ReservationState.Confirmed) &&
                dbContext.ProcessingRestrictionProjections.Any(projection =>
                    projection.PropertyId == reservation.PropertyId &&
                    projection.ReservationId == reservation.Id &&
                    projection.ContractVersion ==
                        ReservationProcessingRestrictionContract.CurrentVersion &&
                    !projection.IsRestricted))
            .Select(reservation => new ReservationReminderSource(
                reservation.ScopeId,
                reservation.Id,
                reservation.PropertyId,
                reservation.Arrival,
                reservation.ExpectedArrivalTime,
                reservation.DetailsRevision))
            .ToArrayAsync(cancellationToken).ConfigureAwait(false);

        HashSet<Guid> relevantReservationIds = reservations.Select(reservation => reservation.ReservationId).ToHashSet();
        Supersede(existing.Where(reminder => !relevantReservationIds.Contains(reminder.ReservationId)));
        foreach (ReservationReminderSource reservation in reservations)
        {
            this.RefreshCore(
                reservation,
                projection,
                existing.Where(reminder => reminder.ReservationId == reservation.ReservationId).ToArray());
        }
    }

    public async Task ApplyPolicyAsync(
        ReservationPropertyPolicyWriteModel property,
        CancellationToken cancellationToken)
    {
        await this.AcquirePropertyProjectionLockAsync(
            property.ScopeId,
            property.PropertyId,
            cancellationToken).ConfigureAwait(false);
        ReservationPropertyProjection projection = await this.GetOrCreatePropertyAsync(
            property.ScopeId,
            property.PropertyId,
            cancellationToken).ConfigureAwait(false);
        projection.ApplyPolicy(property.ProcessingStatus, property.GovernancePolicy, property.SourceVersion);
    }

    public async Task<ReservationPropertyPolicySnapshot?> GetPolicyAsync(
        Guid propertyId,
        CancellationToken cancellationToken)
    {
        ReservationPropertyProjection? property = await dbContext.PropertyProjections
            .AsNoTracking()
            .Include(item => item.GovernancePolicy)
            .ThenInclude(policy => policy!.Acknowledgements)
            .FirstOrDefaultAsync(item => item.Id == propertyId, cancellationToken)
            .ConfigureAwait(false);
        return property is null
            ? null
            : new ReservationPropertyPolicySnapshot(
                property.IsKnown,
                property.IsActive,
                property.ProcessingStatus,
                MapPolicy(property.GovernancePolicy));
    }

    public async Task RefreshReservationAsync(
        ReservationReminderSource reservation,
        CancellationToken cancellationToken)
    {
        ReservationArrivalReminder[] existing = await dbContext.ArrivalReminders
            .Where(reminder => reminder.ReservationId == reservation.ReservationId)
            .ToArrayAsync(cancellationToken).ConfigureAwait(false);
        bool processingAllowed =
            await dbContext.ProcessingRestrictionProjections.AnyAsync(
                projection =>
                    projection.PropertyId == reservation.PropertyId &&
                    projection.ReservationId == reservation.ReservationId &&
                    projection.ContractVersion ==
                        ReservationProcessingRestrictionContract.CurrentVersion &&
                    !projection.IsRestricted,
                cancellationToken).ConfigureAwait(false);
        if (!processingAllowed)
        {
            Supersede(existing);
            return;
        }

        ReservationPropertyProjection? property = dbContext.PropertyProjections.Local.FirstOrDefault(
            item => item.Id == reservation.PropertyId && item.ScopeId == reservation.ScopeId) ??
            await dbContext.PropertyProjections.AsNoTracking().FirstOrDefaultAsync(
                item => item.Id == reservation.PropertyId,
                cancellationToken).ConfigureAwait(false);

        this.RefreshCore(reservation, property, existing);
    }

    public async Task SuppressForProcessingRestrictionAsync(
        Guid reservationId,
        CancellationToken cancellationToken)
    {
        ReservationArrivalReminder[] existing = await dbContext.ArrivalReminders
            .Where(reminder => reminder.ReservationId == reservationId)
            .ToArrayAsync(cancellationToken).ConfigureAwait(false);
        Supersede(existing);
    }

    public async Task<ReservationArrivalReminderClaimResult> ClaimDueAsync(
        DateTimeOffset nowUtc,
        int batchSize,
        CancellationToken cancellationToken)
    {
        ReservationArrivalReminder[] scannedCandidates =
            await dbContext.ArrivalReminders
            .AsNoTracking()
            .Where(reminder =>
                reminder.State == ReservationArrivalReminderState.Pending &&
                reminder.DueAtUtc <= nowUtc)
            .OrderBy(reminder => reminder.DueAtUtc)
            .ThenBy(reminder => reminder.Id)
            .Take(batchSize)
            .ToArrayAsync(cancellationToken).ConfigureAwait(false);
        if (scannedCandidates.Length == 0)
        {
            return new(0, []);
        }

        Guid[] reservationIds = scannedCandidates
            .Select(reminder => reminder.ReservationId)
            .Distinct()
            .OrderBy(reservationId => reservationId)
            .ToArray();
        Guid[] candidateIds = scannedCandidates
            .Select(reminder => reminder.Id)
            .ToArray();
        Guid[] propertyIds = scannedCandidates
            .Select(reminder => reminder.PropertyId)
            .Distinct()
            .ToArray();
        List<Guid> acquiredReservationIds = new(reservationIds.Length);
        foreach (Guid reservationId in reservationIds)
        {
            if (await operationLock.TryAcquireExistingAsync(
                    dbContext.CurrentScopeId,
                    reservationId,
                    cancellationToken).ConfigureAwait(false))
            {
                acquiredReservationIds.Add(reservationId);
            }
        }

        HashSet<Guid> acquiredReservationIdSet =
            acquiredReservationIds.ToHashSet();
        ReservationArrivalReminder[] candidates =
            await dbContext.ArrivalReminders
                .Where(reminder => candidateIds.Contains(reminder.Id))
                .OrderBy(reminder => reminder.DueAtUtc)
                .ThenBy(reminder => reminder.Id)
                .ToArrayAsync(cancellationToken).ConfigureAwait(false);
        Dictionary<Guid, ReservationReminderCandidate> reservations = await dbContext.Reservations
            .AsNoTracking()
            .Where(reservation =>
                acquiredReservationIds.Contains(reservation.Id) &&
                !reservation.IsAnonymised &&
                dbContext.ProcessingRestrictionProjections.Any(projection =>
                    projection.PropertyId == reservation.PropertyId &&
                    projection.ReservationId == reservation.Id &&
                    projection.ContractVersion ==
                        ReservationProcessingRestrictionContract.CurrentVersion &&
                    !projection.IsRestricted))
            .Select(reservation => new ReservationReminderCandidate(
                reservation.Id,
                reservation.PropertyId,
                reservation.Status,
                reservation.DetailsRevision,
                reservation.Arrival,
                reservation.ExpectedArrivalTime))
            .ToDictionaryAsync(reservation => reservation.ReservationId, cancellationToken).ConfigureAwait(false);
        Dictionary<Guid, ReservationPropertyProjection> properties = await dbContext.PropertyProjections
            .AsNoTracking()
            .Where(property => propertyIds.Contains(property.Id))
            .ToDictionaryAsync(property => property.Id, cancellationToken).ConfigureAwait(false);
        List<ReservationArrivalReminderDispatch> dispatches =
            new(candidates.Length);

        foreach (ReservationArrivalReminder candidate in candidates)
        {
            if (candidate.State != ReservationArrivalReminderState.Pending ||
                candidate.DueAtUtc > nowUtc)
            {
                continue;
            }

            if (!acquiredReservationIdSet.Contains(candidate.ReservationId) ||
                !reservations.TryGetValue(candidate.ReservationId, out ReservationReminderCandidate? reservation) ||
                reservation.Status != ReservationState.Confirmed ||
                reservation.PropertyId != candidate.PropertyId ||
                reservation.DetailsRevision != candidate.DetailsRevision ||
                reservation.Arrival != candidate.Arrival ||
                reservation.ExpectedArrivalTime != candidate.ExpectedArrivalTime ||
                !properties.TryGetValue(candidate.PropertyId, out ReservationPropertyProjection? property) ||
                !property.IsKnown ||
                !property.IsActive ||
                !string.Equals(property.TimeZoneId, candidate.TimeZoneId, StringComparison.Ordinal) ||
                candidate.ExpectedArrivalAtUtc <= nowUtc)
            {
                candidate.Supersede();
                continue;
            }

            candidate.Dispatch(nowUtc);
            dispatches.Add(new(
                candidate.Id,
                candidate.ScopeId,
                candidate.ReservationId,
                candidate.PropertyId,
                candidate.Arrival,
                candidate.ExpectedArrivalTime,
                candidate.TimeZoneId,
                candidate.DetailsRevision));
        }

        return new(scannedCandidates.Length, dispatches);
    }

    public async Task<IReadOnlyList<string>> ListScheduleScopeIdsAsync(
        CancellationToken cancellationToken) => await this.ScheduleScopeIds()
            .ToArrayAsync(cancellationToken)
            .ConfigureAwait(false);

    public async IAsyncEnumerable<string> StreamScheduleScopeIdsAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (string scopeId in this.ScheduleScopeIds()
            .AsAsyncEnumerable()
            .WithCancellation(cancellationToken)
            .ConfigureAwait(false))
        {
            yield return scopeId;
        }
    }

    private void RefreshCore(
        ReservationReminderSource reservation,
        ReservationPropertyProjection? property,
        IReadOnlyCollection<ReservationArrivalReminder> existing)
    {
        if (reservation.ExpectedArrivalTime is not { } expectedArrivalTime ||
            property is null ||
            !property.IsKnown ||
            !property.IsActive ||
            string.IsNullOrWhiteSpace(property.TimeZoneId) ||
            !TryResolveExpectedArrivalUtc(
                reservation.Arrival,
                expectedArrivalTime,
                property.TimeZoneId,
                out DateTimeOffset expectedArrivalAtUtc))
        {
            Supersede(existing);
            return;
        }

        ReservationArrivalReminder? matching = existing.SingleOrDefault(reminder =>
            reminder.DetailsRevision == reservation.DetailsRevision &&
            reminder.LeadTimeMinutes == ReservationsModuleMetadata.ArrivalReminderLeadTimeMinutes &&
            string.Equals(reminder.TimeZoneId, property.TimeZoneId, StringComparison.Ordinal));
        Supersede(existing.Where(reminder => reminder != matching));
        if (matching is not null)
        {
            matching.Reactivate();
            return;
        }

        dbContext.ArrivalReminders.Add(ReservationArrivalReminder.Create(
            idGenerator.NewId(),
            reservation.ScopeId,
            reservation.ReservationId,
            reservation.PropertyId,
            reservation.DetailsRevision,
            property.TimeZoneId,
            reservation.Arrival,
            expectedArrivalTime,
            expectedArrivalAtUtc,
            expectedArrivalAtUtc.AddMinutes(-ReservationsModuleMetadata.ArrivalReminderLeadTimeMinutes),
            ReservationsModuleMetadata.ArrivalReminderLeadTimeMinutes));
    }

    private IQueryable<string> ScheduleScopeIds() => dbContext.Reservations
        .IgnoreQueryFilters()
        .AsNoTracking()
        .Select(reservation => reservation.ScopeId)
        .Distinct()
        .Order();

    private static void Supersede(IEnumerable<ReservationArrivalReminder> reminders)
    {
        foreach (ReservationArrivalReminder reminder in reminders)
        {
            reminder.Supersede();
        }
    }

    private async Task<ReservationPropertyProjection> GetOrCreatePropertyAsync(
        string scopeId,
        Guid propertyId,
        CancellationToken cancellationToken)
    {
        ReservationPropertyProjection? projection = dbContext.PropertyProjections.Local.FirstOrDefault(
            item => item.Id == propertyId && item.ScopeId == scopeId) ??
            await dbContext.PropertyProjections.FirstOrDefaultAsync(
                item => item.Id == propertyId,
                cancellationToken).ConfigureAwait(false);
        if (projection is null)
        {
            projection = ReservationPropertyProjection.Create(propertyId, scopeId);
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
                "A Reservations property projection lock requires valid scoped coordinates.");
        }

        if (!dbContext.Database.IsRelational())
        {
            return;
        }

        if (dbContext.Database.CurrentTransaction is null)
        {
            throw new InvalidOperationException(
                "A Reservations property projection lock requires an active database transaction.");
        }

        await EfTransactionKeyLock.AcquireAsync(
            dbContext,
            PropertyProjectionLockPrefix + scopeId + ':' + propertyId.ToString("N"),
            cancellationToken).ConfigureAwait(false);
    }

    private static PropertyGovernancePolicyBinding? MapPolicy(ReservationPropertyPolicyBinding? policy) =>
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

    private static bool TryResolveExpectedArrivalUtc(
        DateOnly arrival,
        TimeOnly expectedArrivalTime,
        string timeZoneId,
        out DateTimeOffset expectedArrivalAtUtc)
    {
        try
        {
            TimeZoneInfo timeZone = TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
            DateTime local = DateTime.SpecifyKind(
                arrival.ToDateTime(expectedArrivalTime),
                DateTimeKind.Unspecified);
            if (timeZone.IsInvalidTime(local))
            {
                expectedArrivalAtUtc = default;
                return false;
            }

            expectedArrivalAtUtc = new(
                TimeZoneInfo.ConvertTimeToUtc(local, timeZone),
                TimeSpan.Zero);
            return true;
        }
        catch (TimeZoneNotFoundException)
        {
            expectedArrivalAtUtc = default;
            return false;
        }
        catch (InvalidTimeZoneException)
        {
            expectedArrivalAtUtc = default;
            return false;
        }
    }

    private sealed record ReservationReminderCandidate(
        Guid ReservationId,
        Guid PropertyId,
        ReservationState Status,
        long DetailsRevision,
        DateOnly Arrival,
        TimeOnly? ExpectedArrivalTime);
}
