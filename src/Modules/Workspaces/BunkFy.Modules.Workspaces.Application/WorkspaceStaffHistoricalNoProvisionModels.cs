namespace BunkFy.Modules.Workspaces.Application;

using BunkFy.Modules.Workspaces.Domain;

public sealed record WorkspaceStaffHistoricalNoProvisionDispositionResult(
    Guid ReceiptId,
    Guid OperationId,
    Guid ApplicationId,
    long ResultApplicationVersion,
    WorkspaceStaffOnboardingState ResultApplicationStatus,
    string StaffEvidenceSha256,
    string CanonicalSha256,
    DateTimeOffset ReviewedAtUtc,
    bool AlreadyReviewed);
