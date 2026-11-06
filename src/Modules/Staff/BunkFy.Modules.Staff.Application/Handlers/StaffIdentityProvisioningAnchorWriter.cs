namespace BunkFy.Modules.Staff.Application.Handlers;

using BunkFy.Modules.Staff.Application.Ports;
using BunkFy.Modules.Staff.Contracts;
using Gma.Framework.Messaging;
using Gma.Framework.Runtime.Identity;

internal sealed class StaffIdentityProvisioningAnchorWriter(
    IStaffIdentityProvisioningAnchorRepository anchors,
    IOutboxWriterRegistry outboxWriters,
    IIdGenerator ids)
    : IStaffIdentityProvisioningAnchorWriter
{
    public async Task<StaffIdentityProvisioningAnchorRecord> AddAsync(
        StaffIdentityProvisioningAnchorRecord anchor,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(anchor);
        if (anchor.ResolutionEventId.HasValue)
        {
            throw new InvalidOperationException(
                "Callers cannot supply an identity-anchor resolution event coordinate.");
        }

        StaffIdentityProvisioningAnchorRecord? existing =
            await anchors.GetAsync(
                anchor.SourceKind,
                anchor.SourceId,
                cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            return Matches(existing, anchor)
                ? existing
                : throw new InvalidOperationException(
                    "The identity-provisioning anchor coordinate is already owned by another target.");
        }

        StaffIdentityProvisioningAnchorRecord persisted =
            this.NormalizeForInsert(anchor);
        await anchors.AddAsync(persisted, cancellationToken)
            .ConfigureAwait(false);
        if (persisted.SourceKind !=
            StaffIdentityProvisioningSourceKind.WorkspaceOnboarding)
        {
            return persisted;
        }

        await outboxWriters.GetRequired(StaffModuleMetadata.Name)
            .EnqueueAsync(
                new StaffIdentityProvisioningAnchorCreatedIntegrationEvent(
                    persisted.SourceId,
                    persisted.ScopeId,
                    persisted.AnchoredAtUtc,
                    persisted.SourceId,
                    persisted.StaffMemberId,
                    persisted.ResolutionEventId!.Value),
                cancellationToken).ConfigureAwait(false);
        return persisted;
    }

    private StaffIdentityProvisioningAnchorRecord NormalizeForInsert(
        StaffIdentityProvisioningAnchorRecord anchor)
    {
        if (anchor.SourceKind ==
            StaffIdentityProvisioningSourceKind.OrganizationMembership)
        {
            return anchor.ResolutionEventId is null
                ? anchor
                : throw new InvalidOperationException(
                    "Organization-membership anchors cannot own a Workspace resolution event.");
        }

        if (anchor.SourceKind !=
            StaffIdentityProvisioningSourceKind.WorkspaceOnboarding)
        {
            throw new InvalidOperationException(
                "The identity-provisioning anchor source kind is invalid.");
        }

        Guid resolutionEventId = this.GenerateResolutionEventId(anchor.SourceId);
        if (resolutionEventId == Guid.Empty ||
            resolutionEventId == anchor.SourceId)
        {
            throw new InvalidOperationException(
                "The Workspace resolution event coordinate is invalid.");
        }

        return anchor with { ResolutionEventId = resolutionEventId };
    }

    private Guid GenerateResolutionEventId(Guid sourceId)
    {
        for (int attempt = 0; attempt < 8; attempt++)
        {
            Guid candidate = ids.NewId();
            if (candidate != Guid.Empty && candidate != sourceId)
            {
                return candidate;
            }
        }

        throw new InvalidOperationException(
            "A distinct Workspace resolution event coordinate could not be generated.");
    }

    private static bool Matches(
        StaffIdentityProvisioningAnchorRecord existing,
        StaffIdentityProvisioningAnchorRecord requested) =>
        string.Equals(
            existing.ScopeId,
            requested.ScopeId,
            StringComparison.Ordinal) &&
        existing.SourceKind == requested.SourceKind &&
        existing.SourceId == requested.SourceId &&
        existing.StaffMemberId == requested.StaffMemberId &&
        existing.ResolutionEventId.HasValue ==
            (existing.SourceKind ==
                StaffIdentityProvisioningSourceKind.WorkspaceOnboarding);
}
