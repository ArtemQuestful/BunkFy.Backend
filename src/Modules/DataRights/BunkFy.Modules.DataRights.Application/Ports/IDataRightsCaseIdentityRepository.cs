namespace BunkFy.Modules.DataRights.Application.Ports;

using BunkFy.Modules.DataRights.Domain.Aggregates;

public interface IDataRightsCaseIdentityRepository
{
    Task<DataRightsCase?> GetByIdAsync(
        Guid caseId,
        CancellationToken cancellationToken);
}
