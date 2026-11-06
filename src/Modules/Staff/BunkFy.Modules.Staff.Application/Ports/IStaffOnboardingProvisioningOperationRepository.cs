namespace BunkFy.Modules.Staff.Application.Ports;

public interface IStaffOnboardingProvisioningOperationRepository
{
    async Task<IReadOnlyList<StaffMemberMutationOperationRecord>> ListAsync(
        IReadOnlyList<Guid> operationIds,
        CancellationToken cancellationToken)
    {
        List<StaffMemberMutationOperationRecord> records = [];
        foreach (Guid operationId in operationIds)
        {
            StaffMemberMutationOperationRecord? record = await this.GetAsync(
                operationId,
                cancellationToken).ConfigureAwait(false);
            if (record is not null)
            {
                records.Add(record);
            }
        }

        return records;
    }

    Task<StaffMemberMutationOperationRecord?> GetAsync(
        Guid operationId,
        CancellationToken cancellationToken);

    Task AddAsync(
        StaffMemberMutationOperationRecord operation,
        CancellationToken cancellationToken);
}
