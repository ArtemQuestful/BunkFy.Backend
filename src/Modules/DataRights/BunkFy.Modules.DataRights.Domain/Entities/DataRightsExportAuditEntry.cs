namespace BunkFy.Modules.DataRights.Domain.Entities;

using BunkFy.Modules.DataRights.Domain.Aggregates;
using BunkFy.Modules.DataRights.Domain.Errors;
using BunkFy.Modules.DataRights.Domain.Models;
using Gma.Framework.Domain.Models;
using Gma.Framework.Naming;
using Gma.Framework.Results;

public sealed class DataRightsExportAuditEntry : ScopedEntity<Guid>
{
    public const int OutcomeCodeMaxLength = 100;

    private DataRightsExportAuditEntry() { }

    private DataRightsExportAuditEntry(Guid id, string scopeId)
        : base(id, scopeId)
    {
    }

    public Guid ArtifactId { get; private set; }
    public Guid CaseId { get; private set; }
    public Guid? PropertyId { get; private set; }
    public DataRightsCaseKind CaseKind { get; private set; }
    public DataRightsExportAuditAction Action { get; private set; }
    public string ActorId { get; private set; } = string.Empty;
    public string OutcomeCode { get; private set; } = string.Empty;
    public DateTimeOffset OccurredAtUtc { get; private set; }

    public static Result<DataRightsExportAuditEntry> Create(
        Guid id,
        string tenantId,
        Guid artifactId,
        Guid caseId,
        Guid? propertyId,
        DataRightsCaseKind caseKind,
        DataRightsExportAuditAction action,
        string actorId,
        string outcomeCode,
        DateTimeOffset occurredAtUtc)
    {
        string actor = actorId?.Trim() ?? string.Empty;
        string outcome = outcomeCode?.Trim() ?? string.Empty;
        if (id == Guid.Empty ||
            artifactId == Guid.Empty ||
            caseId == Guid.Empty ||
            action == DataRightsExportAuditAction.Unknown ||
            !Enum.IsDefined(action) ||
            actor.Length is 0 or > DataRightsExportArtifact.ActorIdMaxLength ||
            outcome.Length is 0 or > OutcomeCodeMaxLength ||
            occurredAtUtc == default ||
            !IsSupportedScope(caseKind, propertyId) ||
            !TenantIds.TryNormalize(tenantId, out string? scopeId))
        {
            return Result.Failure<DataRightsExportAuditEntry>(
                DataRightsDomainErrors.ExportAuditEntryInvalid);
        }

        return Result.Success(new DataRightsExportAuditEntry(id, scopeId)
        {
            ArtifactId = artifactId,
            CaseId = caseId,
            PropertyId = propertyId,
            CaseKind = caseKind,
            Action = action,
            ActorId = actor,
            OutcomeCode = outcome,
            OccurredAtUtc = occurredAtUtc.ToUniversalTime()
        });
    }

    private static bool IsSupportedScope(
        DataRightsCaseKind caseKind,
        Guid? propertyId) =>
        caseKind switch
        {
            DataRightsCaseKind.GuestRights => propertyId is not null &&
                propertyId != Guid.Empty,
            DataRightsCaseKind.StaffRights => propertyId is null,
            DataRightsCaseKind.TenantTermination => propertyId is null,
            _ => false
        };
}
