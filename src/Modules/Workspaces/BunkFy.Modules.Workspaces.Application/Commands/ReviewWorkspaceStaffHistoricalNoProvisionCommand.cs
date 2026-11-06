namespace BunkFy.Modules.Workspaces.Application.Commands;

using BunkFy.Modules.Workspaces.Domain;
using Gma.Framework.Cqrs;

public sealed record ReviewWorkspaceStaffHistoricalNoProvisionCommand(
    Guid OperationId,
    Guid ApplicationId,
    long ExpectedApplicationVersion,
    WorkspaceStaffOnboardingState ExpectedApplicationStatus,
    long ExpectedOrganizationsScopeRevision,
    long ExpectedOrganizationsSourceVersion,
    WorkspaceStaffHistoricalNoProvisionAuthorityStatus
        ExpectedOrganizationsSourceStatus,
    Guid ExternalEvidenceManifestId,
    string ExternalEvidenceSha256,
    string ReviewerId)
    : ITransactionalCommand<
        WorkspaceStaffHistoricalNoProvisionDispositionResult>;
