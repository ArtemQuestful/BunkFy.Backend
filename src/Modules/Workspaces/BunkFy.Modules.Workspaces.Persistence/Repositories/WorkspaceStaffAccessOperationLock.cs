namespace BunkFy.Modules.Workspaces.Persistence.Repositories;

using System.Security.Cryptography;
using System.Text;
using BunkFy.Modules.Workspaces.Application.Ports;
using BunkFy.Modules.Workspaces.Domain;
using Gma.Framework.Naming;
using Gma.Framework.Persistence.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

internal sealed class WorkspaceStaffAccessOperationLock(
    WorkspacesDbContext dbContext)
    : IWorkspaceStaffAccessOperationLock
{
    private const string ResourcePrefix =
        "bunkfy:workspaces:staff-access:";

    public async Task AcquireSubjectAsync(
        string subjectId,
        CancellationToken cancellationToken)
    {
        string normalizedSubject = NormalizeSubject(subjectId);
        await dbContext.AcquireOperationalMutationAdmissionAsync(
                cancellationToken).ConfigureAwait(false);
        await this.AcquireSubjectKeyAsync(
                normalizedSubject,
                cancellationToken).ConfigureAwait(false);
    }

    public async Task AcquireStaffAsync(
        Guid staffMemberId,
        CancellationToken cancellationToken)
    {
        ValidateId(
            staffMemberId,
            nameof(staffMemberId),
            "staff member");
        await dbContext.AcquireOperationalMutationAdmissionAsync(
                cancellationToken).ConfigureAwait(false);
        await this.AcquireStaffKeyAsync(staffMemberId, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task AcquireCoordinatesAsync(
        Guid staffMemberId,
        string subjectId,
        CancellationToken cancellationToken)
    {
        ValidateId(
            staffMemberId,
            nameof(staffMemberId),
            "staff member");
        string normalizedSubject = NormalizeSubject(subjectId);
        await dbContext.AcquireOperationalMutationAdmissionAsync(
                cancellationToken).ConfigureAwait(false);
        await this.AcquireNormalizedCoordinateAsync(
                staffMemberId,
                normalizedSubject,
                cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> TryAcquireProcessAsync(
        Guid processId,
        CancellationToken cancellationToken)
    {
        ValidateId(processId, nameof(processId), "access process");
        await dbContext.AcquireOperationalMutationAdmissionAsync(
                cancellationToken).ConfigureAwait(false);

        var coordinate = await dbContext.StaffAccessProcesses
            .AsNoTracking()
            .Where(process => process.Id == processId)
            .Select(process => new
            {
                process.StaffMemberId,
                process.SubjectId
            })
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        if (coordinate is null)
        {
            return false;
        }

        await this.AcquireNormalizedCoordinateAsync(
                coordinate.StaffMemberId,
                NormalizeSubject(coordinate.SubjectId),
                cancellationToken).ConfigureAwait(false);
        return true;
    }

    public async Task<bool> TryAcquireStaffVersionAsync(
        Guid staffMemberId,
        long targetStaffVersion,
        CancellationToken cancellationToken)
    {
        ValidateId(
            staffMemberId,
            nameof(staffMemberId),
            "staff member");
        if (targetStaffVersion <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(targetStaffVersion),
                "A Workspaces staff-access operation lock requires a positive Staff version.");
        }

        await dbContext.AcquireOperationalMutationAdmissionAsync(
                cancellationToken).ConfigureAwait(false);
        var coordinate = await dbContext.StaffAccessProcesses
            .AsNoTracking()
            .Where(process => process.StaffMemberId == staffMemberId &&
                process.TargetStaffVersion == targetStaffVersion)
            .Select(process => new
            {
                process.StaffMemberId,
                process.SubjectId
            })
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        if (coordinate is null)
        {
            return false;
        }

        await this.AcquireNormalizedCoordinateAsync(
                coordinate.StaffMemberId,
                NormalizeSubject(coordinate.SubjectId),
                cancellationToken).ConfigureAwait(false);
        return true;
    }

    private async Task AcquireNormalizedCoordinateAsync(
        Guid staffMemberId,
        string normalizedSubject,
        CancellationToken cancellationToken)
    {
        await this.AcquireSubjectKeyAsync(
                normalizedSubject,
                cancellationToken).ConfigureAwait(false);
        await this.AcquireStaffKeyAsync(
                staffMemberId,
                cancellationToken).ConfigureAwait(false);
    }

    private Task AcquireSubjectKeyAsync(
        string normalizedSubject,
        CancellationToken cancellationToken)
    {
        byte[] subjectSha256 = SHA256.HashData(
            Encoding.UTF8.GetBytes(normalizedSubject));
        return this.AcquireKeyAsync(
            "subject:" + Convert.ToHexString(subjectSha256),
            cancellationToken);
    }

    private Task AcquireStaffKeyAsync(
        Guid staffMemberId,
        CancellationToken cancellationToken) =>
        this.AcquireKeyAsync(
            staffMemberId.ToString("N"),
            cancellationToken);

    private Task AcquireKeyAsync(
        string coordinate,
        CancellationToken cancellationToken)
    {
        if (!dbContext.Database.IsRelational())
        {
            return Task.CompletedTask;
        }

        if (dbContext.Database.CurrentTransaction is null)
        {
            throw new InvalidOperationException(
                "A Workspaces staff-access operation lock requires an active database transaction.");
        }

        if (!TenantIds.TryNormalize(
                dbContext.CurrentScopeId,
                out string? tenantId))
        {
            throw new InvalidOperationException(
                "A Workspaces staff-access operation lock requires the current tenant scope.");
        }

        return EfTransactionKeyLock.AcquireAsync(
            dbContext,
            ResourcePrefix + tenantId + ':' + coordinate,
            EfTransactionKeyLockMode.Exclusive,
            cancellationToken);
    }

    private static string NormalizeSubject(string subjectId)
    {
        string normalized = subjectId?.Trim() ?? string.Empty;
        if (normalized.Length is 0 or >
            WorkspaceStaffAccessProcess.SubjectIdMaxLength)
        {
            throw new ArgumentException(
                "A Workspaces staff-access operation lock requires a valid subject identifier.",
                nameof(subjectId));
        }

        return normalized;
    }

    private static void ValidateId(
        Guid value,
        string parameterName,
        string coordinate)
    {
        if (value == Guid.Empty)
        {
            throw new ArgumentException(
                $"A Workspaces {coordinate} operation lock requires a valid identifier.",
                parameterName);
        }
    }
}
