namespace BunkFy.Modules.Staff.Persistence.Repositories;

using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using BunkFy.Modules.Staff.Application.Ports;
using BunkFy.Modules.Staff.Contracts;
using BunkFy.Modules.Staff.Domain.Aggregates;
using BunkFy.Modules.Staff.Domain.DataRights;
using BunkFy.Modules.Staff.Domain.Models;
using Gma.Framework.Naming;
using Microsoft.EntityFrameworkCore;

internal sealed class StaffOnboardingStateVerifier(
    StaffDbContext dbContext,
    TimeProvider? timeProvider = null)
    : IStaffOnboardingStateVerifier
{
    private const int ContractVersion = 1;
    private const string PropertyDigestDomain =
        "bunkfy.staff.onboarding-state.properties.v1";
    private const string StateDigestDomain =
        "bunkfy.staff.onboarding-state.proof.v1";
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;

    public async Task<StaffOnboardingStateVerificationResult> VerifyAsync(
        StaffOnboardingStateVerificationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        DateTimeOffset observedAtUtc = this.clock.GetUtcNow();
        Guid[] expectedPropertyIds = request.PropertyIds?
            .Distinct()
            .Order()
            .ToArray() ?? [];
        if (!TenantIds.TryNormalize(request.TenantId, out string? tenantId) ||
            request.OperationId == Guid.Empty ||
            request.StaffMemberId == Guid.Empty ||
            string.IsNullOrWhiteSpace(request.AuthSubjectId) ||
            request.AuthSubjectId.Length > StaffMember.AuthSubjectIdMaxLength ||
            request.PropertyIds is null ||
            expectedPropertyIds.Length != request.PropertyIds.Count ||
            expectedPropertyIds.Any(propertyId => propertyId == Guid.Empty))
        {
            return Result(
                StaffOnboardingStateVerificationStatus.ReviewRequired,
                StaffOnboardingStateVerificationReason.InvalidRequest,
                observedAtUtc);
        }

        try
        {
            StaffTenantRevision? before = await dbContext.TenantRevisions
                .AsNoTracking()
                .SingleOrDefaultAsync(
                    revision => revision.ScopeId == tenantId,
                    cancellationToken)
                .ConfigureAwait(false);
            if (before is null)
            {
                return Result(
                    StaffOnboardingStateVerificationStatus.RetryLater,
                    StaffOnboardingStateVerificationReason.TenantStateUnavailable,
                    observedAtUtc);
            }

            if (!before.IsOpen)
            {
                return Result(
                    StaffOnboardingStateVerificationStatus.ReviewRequired,
                    StaffOnboardingStateVerificationReason.TenantClosed,
                    observedAtUtc,
                    tenantRevision: before.Revision);
            }

            StaffMemberMutationOperation? operation = await dbContext
                .MemberMutationOperations
                .AsNoTracking()
                .SingleOrDefaultAsync(
                    item => item.ScopeId == tenantId &&
                        item.Id == request.OperationId &&
                        item.Kind == StaffMemberMutationKind.OnboardingProvision,
                    cancellationToken)
                .ConfigureAwait(false);
            if (operation is null)
            {
                return Result(
                    StaffOnboardingStateVerificationStatus.ReviewRequired,
                    StaffOnboardingStateVerificationReason.OperationMissing,
                    observedAtUtc,
                    tenantRevision: before.Revision);
            }

            StaffMemberProofRow? member = await dbContext.StaffMembers
                .AsNoTracking()
                .Where(item => item.ScopeId == tenantId &&
                    item.Id == request.StaffMemberId)
                .Select(item => new StaffMemberProofRow(
                    item.Id,
                    item.ScopeId,
                    item.Status,
                    item.AnonymisedAtUtc,
                    item.AuthSubjectId,
                    item.Version))
                .SingleOrDefaultAsync(cancellationToken)
                .ConfigureAwait(false);
            StaffAssignmentProofRow[] assignments = await dbContext
                .PropertyAssignments
                .AsNoTracking()
                .Where(assignment => assignment.ScopeId == tenantId &&
                    assignment.StaffMemberId == request.StaffMemberId &&
                    assignment.IsCurrent)
                .OrderBy(assignment => assignment.PropertyId)
                .Select(assignment => new StaffAssignmentProofRow(
                    assignment.Id,
                    assignment.PropertyId,
                    assignment.AssignedAtVersion))
                .ToArrayAsync(cancellationToken)
                .ConfigureAwait(false);
            bool processingRestricted = await dbContext
                .ProcessingRestrictions
                .AsNoTracking()
                .AnyAsync(
                    restriction => restriction.ScopeId == tenantId &&
                        restriction.StaffMemberId == request.StaffMemberId &&
                        restriction.Status == StaffProcessingRestrictionState.Active,
                    cancellationToken)
                .ConfigureAwait(false);
            StaffTenantRevision? after = await dbContext.TenantRevisions
                .AsNoTracking()
                .SingleOrDefaultAsync(
                    revision => revision.ScopeId == tenantId,
                    cancellationToken)
                .ConfigureAwait(false);
            if (after is null ||
                after.Revision != before.Revision ||
                after.LifecycleStatus != before.LifecycleStatus)
            {
                return Result(
                    StaffOnboardingStateVerificationStatus.RetryLater,
                    StaffOnboardingStateVerificationReason.RevisionChanged,
                    observedAtUtc,
                    operation.ResultVersion,
                    member?.Version,
                    before.Revision);
            }

            if (operation.StaffMemberId != request.StaffMemberId ||
                operation.ResultStatus != StaffStatus.Active ||
                operation.ResultVersion < operation.ExpectedVersion ||
                (member is not null && operation.ResultVersion > member.Version))
            {
                return Result(
                    StaffOnboardingStateVerificationStatus.ReviewRequired,
                    StaffOnboardingStateVerificationReason.OperationConflict,
                    observedAtUtc,
                    operation.ResultVersion,
                    member?.Version,
                    after.Revision);
            }

            if (member is null)
            {
                return Result(
                    StaffOnboardingStateVerificationStatus.ReviewRequired,
                    StaffOnboardingStateVerificationReason.MemberMissing,
                    observedAtUtc,
                    operation.ResultVersion,
                    tenantRevision: after.Revision);
            }

            if (member.Status != StaffMemberState.Active ||
                member.AnonymisedAtUtc is not null ||
                !string.Equals(
                    member.AuthSubjectId,
                    request.AuthSubjectId,
                    StringComparison.Ordinal))
            {
                return Result(
                    StaffOnboardingStateVerificationStatus.ReviewRequired,
                    StaffOnboardingStateVerificationReason.MemberConflict,
                    observedAtUtc,
                    operation.ResultVersion,
                    member.Version,
                    after.Revision);
            }

            if (processingRestricted)
            {
                return Result(
                    StaffOnboardingStateVerificationStatus.ReviewRequired,
                    StaffOnboardingStateVerificationReason.ProcessingRestricted,
                    observedAtUtc,
                    operation.ResultVersion,
                    member.Version,
                    after.Revision);
            }

            Guid[] currentPropertyIds = assignments
                .Select(assignment => assignment.PropertyId)
                .ToArray();
            string propertyDigest = Hash(
                PropertyDigestDomain,
                currentPropertyIds.Select(id => id.ToString("D")));
            if (!currentPropertyIds.SequenceEqual(expectedPropertyIds))
            {
                return Result(
                    StaffOnboardingStateVerificationStatus.ReviewRequired,
                    StaffOnboardingStateVerificationReason.PropertySetConflict,
                    observedAtUtc,
                    operation.ResultVersion,
                    member.Version,
                    after.Revision,
                    currentPropertyIds.Length,
                    propertyDigest);
            }

            string stateProof = Hash(
                StateDigestDomain,
                [
                    ContractVersion.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    tenantId,
                    operation.Id.ToString("D"),
                    operation.StaffMemberId.ToString("D"),
                    ((int)operation.Kind).ToString(
                        System.Globalization.CultureInfo.InvariantCulture),
                    operation.ExpectedVersion.ToString(
                        System.Globalization.CultureInfo.InvariantCulture),
                    operation.RequestFingerprint,
                    ((int)operation.ResultStatus).ToString(System.Globalization.CultureInfo.InvariantCulture),
                    operation.ResultVersion.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    operation.CompletedAtUtc.ToUniversalTime().ToString(
                        "O",
                        System.Globalization.CultureInfo.InvariantCulture),
                    member.Id.ToString("D"),
                    member.ScopeId,
                    request.AuthSubjectId,
                    ((int)member.Status).ToString(System.Globalization.CultureInfo.InvariantCulture),
                    (member.AnonymisedAtUtc is not null).ToString(
                        System.Globalization.CultureInfo.InvariantCulture),
                    processingRestricted.ToString(
                        System.Globalization.CultureInfo.InvariantCulture),
                    member.Version.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    ((int)after.LifecycleStatus).ToString(
                        System.Globalization.CultureInfo.InvariantCulture),
                    after.IsOpen.ToString(
                        System.Globalization.CultureInfo.InvariantCulture),
                    after.Revision.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    propertyDigest,
                    Hash(
                        "bunkfy.staff.onboarding-state.assignments.v1",
                        assignments.SelectMany(assignment => new[]
                        {
                            assignment.AssignmentId.ToString("D"),
                            assignment.PropertyId.ToString("D"),
                            assignment.AssignedAtVersion.ToString(
                                System.Globalization.CultureInfo.InvariantCulture)
                        }))
                ]);
            return Result(
                StaffOnboardingStateVerificationStatus.Equivalent,
                StaffOnboardingStateVerificationReason.Equivalent,
                observedAtUtc,
                operation.ResultVersion,
                member.Version,
                after.Revision,
                currentPropertyIds.Length,
                propertyDigest,
                stateProof);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return Result(
                StaffOnboardingStateVerificationStatus.RetryLater,
                StaffOnboardingStateVerificationReason.PersistenceUnavailable,
                observedAtUtc);
        }
    }

    private static StaffOnboardingStateVerificationResult Result(
        StaffOnboardingStateVerificationStatus status,
        StaffOnboardingStateVerificationReason reason,
        DateTimeOffset observedAtUtc,
        long? operationResultVersion = null,
        long? currentMemberVersion = null,
        long? tenantRevision = null,
        int propertyCount = 0,
        string? propertySetSha256 = null,
        string? stateProofSha256 = null) => new(
        status,
        reason,
        ContractVersion,
        operationResultVersion,
        currentMemberVersion,
        tenantRevision,
        propertyCount,
        propertySetSha256,
        stateProofSha256,
        observedAtUtc);

    private static string Hash(
        string domain,
        IEnumerable<string> values)
    {
        using MemoryStream canonical = new();
        Append(canonical, domain);
        foreach (string value in values)
        {
            Append(canonical, value);
        }

        return Convert.ToHexStringLower(SHA256.HashData(canonical.ToArray()));
    }

    private static void Append(Stream stream, string value)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(value);
        Span<byte> length = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32BigEndian(length, bytes.Length);
        stream.Write(length);
        stream.Write(bytes);
    }

    private sealed record StaffMemberProofRow(
        Guid Id,
        string ScopeId,
        StaffMemberState Status,
        DateTimeOffset? AnonymisedAtUtc,
        string? AuthSubjectId,
        long Version);

    private sealed record StaffAssignmentProofRow(
        Guid AssignmentId,
        Guid PropertyId,
        long AssignedAtVersion);
}
