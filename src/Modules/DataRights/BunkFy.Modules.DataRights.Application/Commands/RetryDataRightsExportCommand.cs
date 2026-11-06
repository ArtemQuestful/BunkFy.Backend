namespace BunkFy.Modules.DataRights.Application.Commands;

using BunkFy.Modules.DataRights.Application.Models;
using BunkFy.Modules.DataRights.Application.Ports;
using BunkFy.Modules.DataRights.Contracts;
using Gma.Framework.Cqrs;

public sealed record RetryDataRightsExportCommand(
    DataRightsCaseScope Scope,
    Guid CaseId,
    Guid ArtifactId,
    long ExpectedCaseVersion,
    long ExpectedArtifactVersion,
    string ActorId)
    : ITransactionalCommand<DataRightsExportArtifactDto>,
        IDataRightsPersistenceRetryableCommand;
