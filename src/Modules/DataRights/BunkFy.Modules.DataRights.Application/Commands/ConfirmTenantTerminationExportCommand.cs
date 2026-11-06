namespace BunkFy.Modules.DataRights.Application.Commands;

using BunkFy.Modules.DataRights.Contracts;
using Gma.Framework.Cqrs;

public sealed record ConfirmTenantTerminationExportCommand(
    Guid CaseId,
    Guid ProcessId,
    Guid ArtifactId,
    long ExportOperationRevision,
    long ExpectedProcessVersion,
    long ExpectedArtifactVersion,
    string FrozenRevisionSha256,
    string FragmentSetSha256,
    string ActorId) : ITransactionalCommand<TenantTerminationProcessDto>;
