namespace BunkFy.Modules.Staff.Persistence.Repositories;

using BunkFy.Modules.Staff.Application.Ports;
using BunkFy.Modules.Staff.Contracts;
using Microsoft.EntityFrameworkCore;

internal sealed class StaffOnboardingProvisioningOperationRepository(
    StaffDbContext dbContext)
    : IStaffOnboardingProvisioningOperationRepository
{
    public async Task<IReadOnlyList<StaffMemberMutationOperationRecord>>
        ListAsync(
            IReadOnlyList<Guid> operationIds,
            CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operationIds);
        StaffMemberMutationOperation[] loaded = await dbContext
            .MemberMutationOperations
            .AsNoTracking()
            .Where(operation => operationIds.Contains(operation.Id) &&
                operation.Kind ==
                    StaffMemberMutationKind.OnboardingProvision)
            .OrderBy(operation => operation.Id)
            .ToArrayAsync(cancellationToken).ConfigureAwait(false);
        return loaded.Select(operation => operation.ToRecord()).ToArray();
    }

    public async Task<StaffMemberMutationOperationRecord?> GetAsync(
        Guid operationId,
        CancellationToken cancellationToken)
    {
        StaffMemberMutationOperation? operation = await dbContext
            .MemberMutationOperations
            .AsNoTracking()
            .SingleOrDefaultAsync(
                item => item.Id == operationId &&
                    item.Kind == StaffMemberMutationKind.OnboardingProvision,
                cancellationToken).ConfigureAwait(false);
        return operation?.ToRecord();
    }

    public Task AddAsync(
        StaffMemberMutationOperationRecord operation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operation);
        if (operation.Kind != StaffMemberMutationKind.OnboardingProvision ||
            operation.ResultStatus != StaffStatus.Active)
        {
            throw new ArgumentException(
                "A Staff onboarding repository requires an active onboarding operation.",
                nameof(operation));
        }

        dbContext.MemberMutationOperations.Add(
            new StaffMemberMutationOperation(operation));
        return Task.CompletedTask;
    }
}
