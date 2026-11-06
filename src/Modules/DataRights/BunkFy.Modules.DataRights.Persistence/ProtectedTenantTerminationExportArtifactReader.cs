namespace BunkFy.Modules.DataRights.Persistence;

using BunkFy.Modules.DataRights.Application.Models;
using BunkFy.Modules.DataRights.Application.Ports;
using BunkFy.Modules.DataRights.Domain.Aggregates;

internal sealed class ProtectedTenantTerminationExportArtifactReader(
    ProtectedDataRightsExportObjectReader reader)
    : ITenantTerminationExportArtifactReader
{
    public async Task<DataRightsExportDownload> OpenVerifiedAsync(
        TenantTerminationExportArtifact artifact,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        FileStream plaintext = await reader.OpenVerifiedAsync(
            new DataRightsProtectedExportReadRequest(
                artifact.StorageKey ?? string.Empty,
                artifact.EncryptedByteLength ?? 0,
                artifact.PlaintextSha256 ?? string.Empty,
                artifact.EncryptionKeyVersion ?? 0,
                artifact.FormatVersion ?? 0,
                Context(artifact)),
            cancellationToken).ConfigureAwait(false);
        return new DataRightsExportDownload(
            plaintext,
            plaintext.Length,
            $"bunkfy-tenant-termination-export-{artifact.Id:N}.zip");
    }

    private static TenantTerminationExportArtifactProtectionContext Context(
        TenantTerminationExportArtifact artifact) => new(
        artifact.Id,
        artifact.ScopeId,
        artifact.ProcessId,
        artifact.CaseId,
        artifact.ApprovalRevision,
        artifact.FreezeOperationRevision,
        artifact.ExportOperationRevision,
        artifact.TerminationEpoch,
        artifact.FrozenRevisionSha256,
        artifact.PolicyEvidenceSha256,
        artifact.ExpectedFragmentCount,
        artifact.FragmentSetSha256,
        artifact.GenerationRunId.GetValueOrDefault(),
        artifact.GenerationAttempt.GetValueOrDefault(),
        artifact.ExpiresAtUtc);
}
