namespace BunkFy.Modules.DataRights.Application.Models;

public sealed record TenantTerminationExportObjectDeletionStart(
    bool DeletionRequired,
    Guid ObjectId);
