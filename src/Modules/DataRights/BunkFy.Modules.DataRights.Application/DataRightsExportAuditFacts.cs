namespace BunkFy.Modules.DataRights.Application;

using BunkFy.Modules.DataRights.Application.Models;
using BunkFy.Modules.DataRights.Contracts;
using BunkFy.Modules.DataRights.Domain.Aggregates;
using BunkFy.Modules.DataRights.Domain.Models;

internal static class DataRightsExportAuditFacts
{
    public static DataRightsExportAuditFact Create(
        DataRightsExportArtifact artifact,
        DataRightsExportAuditAction action,
        string actorId,
        string outcomeCode,
        DateTimeOffset occurredAtUtc) =>
        new(
            artifact.ScopeId,
            artifact.Id,
            artifact.CaseId,
            (DataRightsCaseType)artifact.CaseKind,
            artifact.PropertyId,
            action,
            actorId,
            outcomeCode,
            occurredAtUtc);

    public static DataRightsExportAuditFact Create(
        TenantTerminationExportArtifact artifact,
        DataRightsExportAuditAction action,
        string actorId,
        string outcomeCode,
        DateTimeOffset occurredAtUtc) =>
        new(
            artifact.ScopeId,
            artifact.Id,
            artifact.CaseId,
            DataRightsCaseType.TenantTermination,
            PropertyId: null,
            action,
            actorId,
            outcomeCode,
            occurredAtUtc);

    public static DataRightsExportAuditFact Create(
        TenantTerminationExportFragment fragment,
        DataRightsExportAuditAction action,
        string actorId,
        string outcomeCode,
        DateTimeOffset occurredAtUtc) =>
        new(
            fragment.ScopeId,
            fragment.Id,
            fragment.CaseId,
            DataRightsCaseType.TenantTermination,
            PropertyId: null,
            action,
            actorId,
            outcomeCode,
            occurredAtUtc);

    public static DataRightsExportAuditFact Create(
        string tenantId,
        DataRightsCaseScope scope,
        Guid artifactId,
        Guid caseId,
        DataRightsExportAuditAction action,
        string actorId,
        string outcomeCode,
        DateTimeOffset occurredAtUtc) =>
        new(
            tenantId,
            artifactId,
            caseId,
            scope.CaseType,
            scope.PropertyId,
            action,
            actorId,
            outcomeCode,
            occurredAtUtc);
}
