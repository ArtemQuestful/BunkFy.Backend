namespace BunkFy.Extensions.Operations.Notifications;

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BunkFy.Modules.Staff.Contracts;
using BunkFy.Modules.Workspaces.Contracts;
using Gma.Framework.AccessControl;
using Gma.Framework.Permissions;
using Gma.Modules.Notifications.Contracts;
using Gma.Modules.Organizations.Contracts;
using Microsoft.Extensions.Logging;

internal sealed partial class OperationalNotificationProjector(
    IStaffPropertyAudienceReader audienceReader,
    IStaffNotificationRecipientResolver recipientResolver,
    IWorkspaceOwnerNotificationAudienceReader workspaceOwnerAudienceReader,
    IOrganizationAccessCandidateFilter organizationAccess,
    IAccessAuthorizationService authorization,
    IUserNotificationRequestProjectorV3 notificationProjector,
    ILogger<OperationalNotificationProjector> logger)
{
    private const int AuthorizationCandidateBatchSize = 500;

    public async Task ProjectForPropertyAsync(
        Guid sourceEventId,
        string scopeId,
        DateTimeOffset occurredAtUtc,
        Guid propertyId,
        PermissionCode requiredPermission,
        OperationalNotification notification,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(requiredPermission);
        ArgumentNullException.ThrowIfNull(notification);

        IReadOnlyList<string> propertyStaffRecipients = await audienceReader
            .ListActiveAuthSubjectIdsAsync(scopeId, propertyId, cancellationToken)
            .ConfigureAwait(false);
        IReadOnlyList<string> workspaceOwnerRecipients = await workspaceOwnerAudienceReader
            .ListAuthSubjectIdsAsync(scopeId, cancellationToken)
            .ConfigureAwait(false);

        string[] candidates = propertyStaffRecipients
            .Concat(workspaceOwnerRecipients)
            .Distinct(StringComparer.Ordinal)
            .Where(recipient => !IsInitiatingUser(recipient, notification.ActorId))
            .Order(StringComparer.Ordinal)
            .ToArray();
        IReadOnlyList<string> recipients = await this.FilterActiveMembersAsync(
                scopeId,
                candidates,
                cancellationToken)
            .ConfigureAwait(false);
        recipients = await this.FilterAuthorizedRecipientsAsync(
                scopeId,
                propertyId,
                recipients,
                requiredPermission,
                cancellationToken)
            .ConfigureAwait(false);
        IReadOnlyList<StaffNotificationRecipient> staffRecipients =
            await this.ResolveStaffRecipientsAsync(
                    scopeId,
                    recipients,
                    notification.SourceModule,
                    notification.Name,
                    cancellationToken)
                .ConfigureAwait(false);

        foreach (StaffNotificationRecipient recipient in staffRecipients)
        {
            await this.ProjectAsync(
                    sourceEventId,
                    scopeId,
                    occurredAtUtc,
                    recipient.AuthSubjectId,
                    recipient.StaffMemberId,
                    notification,
                    cancellationToken)
                .ConfigureAwait(false);
        }
    }

    public async Task ProjectForStaffMemberAsync(
        Guid sourceEventId,
        string scopeId,
        DateTimeOffset occurredAtUtc,
        Guid staffMemberId,
        OperationalNotification notification,
        CancellationToken cancellationToken)
    {
        string? recipient = await audienceReader
            .GetAuthSubjectIdAsync(scopeId, staffMemberId, cancellationToken)
            .ConfigureAwait(false);
        if (recipient is null)
        {
            return;
        }

        if (IsInitiatingUser(recipient, notification.ActorId))
        {
            return;
        }

        IReadOnlyList<string> recipients = await this.FilterActiveMembersAsync(
                scopeId,
                [recipient],
                cancellationToken)
            .ConfigureAwait(false);
        if (recipients.Count == 0)
        {
            return;
        }

        IReadOnlyList<StaffNotificationRecipient> staffRecipients =
            await this.ResolveStaffRecipientsAsync(
                    scopeId,
                    recipients,
                    notification.SourceModule,
                    notification.Name,
                    cancellationToken)
                .ConfigureAwait(false);
        if (staffRecipients.Count == 0)
        {
            return;
        }

        StaffNotificationRecipient staffRecipient = staffRecipients[0];
        if (staffRecipient.StaffMemberId != staffMemberId)
        {
            throw new InvalidOperationException(
                "A direct operational notification recipient resolved to a different Staff identity.");
        }

        await this.ProjectAsync(
                sourceEventId,
                scopeId,
                occurredAtUtc,
                staffRecipient.AuthSubjectId,
                staffRecipient.StaffMemberId,
                notification,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<string>> FilterActiveMembersAsync(
        string scopeId,
        string[] candidates,
        CancellationToken cancellationToken)
    {
        if (candidates.Length == 0)
        {
            return [];
        }

        if (!Guid.TryParse(scopeId, out Guid organizationId) || organizationId == Guid.Empty)
        {
            throw new InvalidOperationException(
                "A BunkFy operational notification scope must be an organization id.");
        }

        List<string> allowed = new(candidates.Length);
        foreach (string[] batch in candidates.Chunk(
            OrganizationAccessContract.MaximumCandidateCount))
        {
            IReadOnlyList<string> filtered = await organizationAccess
                .FilterAllowedAsync(organizationId, batch, cancellationToken)
                .ConfigureAwait(false);
            allowed.AddRange(filtered);
        }

        HashSet<string> candidatesSet =
            candidates.ToHashSet(StringComparer.Ordinal);
        if (allowed.Any(subject => !candidatesSet.Contains(subject)))
        {
            throw new InvalidOperationException(
                "The organization access filter returned an unexpected notification recipient.");
        }

        return allowed
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
    }

    private async Task<IReadOnlyList<StaffNotificationRecipient>>
        ResolveStaffRecipientsAsync(
            string scopeId,
            IReadOnlyList<string> recipients,
            string sourceModule,
            string notificationName,
            CancellationToken cancellationToken)
    {
        if (recipients.Count == 0)
        {
            return [];
        }

        List<StaffNotificationRecipient> resolved =
            new(recipients.Count);
        foreach (string[] batch in recipients.Chunk(
            StaffNotificationRecipientContract.MaximumCandidateCount))
        {
            IReadOnlyList<StaffNotificationRecipient> mapped =
                await recipientResolver.ResolveActiveAsync(
                        scopeId,
                        batch,
                        cancellationToken)
                    .ConfigureAwait(false);
            resolved.AddRange(mapped);
        }

        HashSet<string> expected =
            recipients.ToHashSet(StringComparer.Ordinal);
        HashSet<string> actual = [];
        HashSet<Guid> actualStaffIds = [];
        bool invalid = resolved.Any(recipient =>
            recipient is null ||
            recipient.StaffMemberId == Guid.Empty ||
            string.IsNullOrWhiteSpace(recipient.AuthSubjectId) ||
            !expected.Contains(recipient.AuthSubjectId) ||
            !actual.Add(recipient.AuthSubjectId) ||
            !actualStaffIds.Add(recipient.StaffMemberId));
        if (invalid)
        {
            throw new InvalidOperationException(
                "An operational notification recipient resolver returned an invalid Staff correlation.");
        }

        int skippedCount = expected.Count - actual.Count;
        if (skippedCount > 0)
        {
            LogSkippedRecipients(
                logger,
                sourceModule,
                notificationName,
                skippedCount);
        }

        return resolved
            .OrderBy(recipient => recipient.AuthSubjectId, StringComparer.Ordinal)
            .ToArray();
    }

    [LoggerMessage(
        EventId = 7101,
        Level = LogLevel.Warning,
        Message = "Operational notification {SourceModule}/{NotificationName} skipped {SkippedCount} candidate recipient(s) without a current active and unrestricted Staff correlation.")]
    private static partial void LogSkippedRecipients(
        ILogger logger,
        string sourceModule,
        string notificationName,
        int skippedCount);

    private async Task<IReadOnlyList<string>> FilterAuthorizedRecipientsAsync(
        string scopeId,
        Guid propertyId,
        IReadOnlyList<string> recipients,
        PermissionCode requiredPermission,
        CancellationToken cancellationToken)
    {
        if (recipients.Count == 0)
        {
            return recipients;
        }

        AccessScope propertyScope = WorkspaceAccessScopes.CreateProperty(
            scopeId,
            propertyId);
        List<string> allowed = new(recipients.Count);
        foreach (string[] batch in recipients.Chunk(
                     AuthorizationCandidateBatchSize))
        {
            AccessRequirement[] requirements = batch
                .Select(recipient => new AccessRequirement(
                    AccessSubject.User(recipient),
                    requiredPermission,
                    propertyScope))
                .ToArray();
            IReadOnlyList<AccessDecision> decisions = await authorization
                .AuthorizeManyAsync(requirements, cancellationToken)
                .ConfigureAwait(false);
            if (decisions.Count != batch.Length)
            {
                throw new InvalidOperationException(
                    "The access authorization service returned an invalid operational notification decision set.");
            }

            for (int index = 0; index < decisions.Count; index++)
            {
                if (decisions[index].IsAllowed)
                {
                    allowed.Add(batch[index]);
                }
            }
        }

        return allowed;
    }

    private Task ProjectAsync(
        Guid sourceEventId,
        string scopeId,
        DateTimeOffset occurredAtUtc,
        string recipient,
        Guid staffMemberId,
        OperationalNotification notification,
        CancellationToken cancellationToken) =>
        notificationProjector.ProjectAsync(
            new UserNotificationRequestedIntegrationEventV3(
                CreateNotificationId(sourceEventId, recipient, notification.Name),
                scopeId,
                occurredAtUtc,
                recipient,
                notification.SourceModule,
                notification.Name,
                1,
                notification.Title,
                notification.Body,
                notification.Severity,
                JsonSerializer.Serialize(notification.Payload, notification.Payload.GetType()),
                notification.Tags,
                OperationsNotificationsDataRightsCoordinates.FromPayload(
                        scopeId,
                        notification.Payload)
                    .Concat(notification.References)
                    .Append(
                        OperationsNotificationsDataRightsCoordinates
                            .ForTenant(scopeId))
                    .Append(
                        OperationsNotificationsDataRightsCoordinates.ForStaff(
                            scopeId,
                            staffMemberId))
                    .Distinct()
                    .ToArray(),
                notification.DeliveryPolicy),
            cancellationToken);

    internal static Guid CreateNotificationId(Guid sourceEventId, string recipient, string notificationName)
    {
        string identity = $"{sourceEventId:D}|{recipient.Trim()}|{notificationName}";
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(identity));
        return new Guid(hash.AsSpan(0, 16));
    }

    internal static bool IsInitiatingUser(string recipient, string? actorId)
    {
        const char separator = ':';
        string normalizedActor = actorId?.Trim() ?? string.Empty;
        int separatorIndex = normalizedActor.IndexOf(separator);
        return separatorIndex > 0 &&
               string.Equals(
                   normalizedActor[..separatorIndex],
                   AccessSubjectKindNames.User,
                   StringComparison.OrdinalIgnoreCase) &&
               string.Equals(
                   normalizedActor[(separatorIndex + 1)..],
                   recipient.Trim(),
                   StringComparison.Ordinal);
    }
}
