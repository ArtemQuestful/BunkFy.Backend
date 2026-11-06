namespace BunkFy.Modules.Workspaces.Persistence.Repositories;

using System.Security.Cryptography;
using System.Text;
using BunkFy.Modules.Workspaces.Application.Ports;
using BunkFy.Modules.Workspaces.Domain;
using Gma.Framework.Naming;
using Gma.Framework.Persistence.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

internal sealed class WorkspaceStaffOnboardingOperationLock(
    WorkspacesDbContext dbContext)
    : IWorkspaceStaffOnboardingOperationLock
{
    private const string ResourcePrefix =
        "bunkfy:workspaces:staff-onboarding:";

    public Task AcquireSourceReadAsync(
        Guid sourceId,
        CancellationToken cancellationToken) =>
        this.AcquireSourceAsync(
            sourceId,
            EfTransactionKeyLockMode.Shared,
            cancellationToken);

    public Task AcquireSourceWriteAsync(
        Guid sourceId,
        CancellationToken cancellationToken) =>
        this.AcquireSourceAsync(
            sourceId,
            EfTransactionKeyLockMode.Exclusive,
            cancellationToken);

    public async Task AcquireApplicantAsync(
        Guid sourceId,
        string subjectId,
        CancellationToken cancellationToken)
    {
        ValidateId(sourceId, nameof(sourceId), "source");
        string normalizedSubject = subjectId?.Trim() ?? string.Empty;
        if (normalizedSubject.Length is 0 or >
            WorkspaceStaffOnboardingRules.SubjectIdMaxLength)
        {
            throw new ArgumentException(
                "A Staff-onboarding applicant lock requires a valid subject identifier.",
                nameof(subjectId));
        }

        await dbContext.AcquireOperationalMutationAdmissionAsync(
                cancellationToken).ConfigureAwait(false);
        byte[] subjectSha256 = SHA256.HashData(
            Encoding.UTF8.GetBytes(normalizedSubject));
        await this.AcquireKeyAsync(
                "applicant:" + sourceId.ToString("N") + ':' +
                    Convert.ToHexString(subjectSha256),
                EfTransactionKeyLockMode.Exclusive,
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<bool> TryAcquireAsync(
        Guid applicationId,
        CancellationToken cancellationToken)
    {
        if (applicationId == Guid.Empty)
        {
            throw new ArgumentException(
                "An onboarding operation lock requires an application identifier.",
                nameof(applicationId));
        }

        if (dbContext.Database.IsRelational() &&
            dbContext.Database.CurrentTransaction is null)
        {
            throw new InvalidOperationException(
                "An onboarding operation lock requires an active database transaction.");
        }

        if (!dbContext.Database.IsRelational())
        {
            return await dbContext.StaffOnboardingApplications
                .AsNoTracking()
                .AnyAsync(
                    application => application.Id == applicationId,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        await dbContext.AcquireOperationalMutationAdmissionAsync(
            cancellationToken).ConfigureAwait(false);
        if (dbContext.Database.IsNpgsql())
        {
            // A no-op UPDATE creates a new PostgreSQL tuple and changes xmin.
            // Preserve the committed resolution tuple while acquiring the same
            // physical row lock, so acknowledgement can prove a prior commit.
            Guid lockedId = await dbContext.Database.SqlQuery<Guid>($"""
                    SELECT "Id" AS "Value"
                    FROM "workspaces"."staff_onboarding_applications"
                    WHERE "Id" = {applicationId}
                      AND "ScopeId" = {dbContext.CurrentScopeId}
                    FOR UPDATE
                    """)
                .SingleOrDefaultAsync(cancellationToken)
                .ConfigureAwait(false);
            return lockedId != Guid.Empty;
        }

        int affected = await dbContext.StaffOnboardingApplications
            .Where(application => application.Id == applicationId)
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(
                    application => application.Version,
                    application => application.Version),
                cancellationToken)
            .ConfigureAwait(false);
        return affected == 1;
    }

    private async Task AcquireSourceAsync(
        Guid sourceId,
        EfTransactionKeyLockMode mode,
        CancellationToken cancellationToken)
    {
        ValidateId(sourceId, nameof(sourceId), "source");
        await dbContext.AcquireOperationalMutationAdmissionAsync(
                cancellationToken).ConfigureAwait(false);
        await this.AcquireKeyAsync(
                "source:" + sourceId.ToString("N"),
                mode,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private Task AcquireKeyAsync(
        string coordinate,
        EfTransactionKeyLockMode mode,
        CancellationToken cancellationToken)
    {
        if (!dbContext.Database.IsRelational())
        {
            return Task.CompletedTask;
        }

        if (dbContext.Database.CurrentTransaction is null)
        {
            throw new InvalidOperationException(
                "A Workspaces Staff-onboarding operation lock requires an active database transaction.");
        }

        if (!TenantIds.TryNormalize(
                dbContext.CurrentScopeId,
                out string? tenantId))
        {
            throw new InvalidOperationException(
                "A Workspaces Staff-onboarding operation lock requires the current tenant scope.");
        }

        return EfTransactionKeyLock.AcquireAsync(
            dbContext,
            ResourcePrefix + tenantId + ':' + coordinate,
            mode,
            cancellationToken);
    }

    private static void ValidateId(
        Guid value,
        string parameterName,
        string coordinate)
    {
        if (value == Guid.Empty)
        {
            throw new ArgumentException(
                $"A Staff-onboarding {coordinate} lock requires a valid identifier.",
                parameterName);
        }
    }
}
