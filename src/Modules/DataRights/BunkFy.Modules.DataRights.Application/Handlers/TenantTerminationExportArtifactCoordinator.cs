namespace BunkFy.Modules.DataRights.Application.Handlers;

using BunkFy.Modules.DataRights.Application.Models;
using BunkFy.Modules.DataRights.Domain.Aggregates;
using BunkFy.Modules.DataRights.Domain.Entities;
using BunkFy.Modules.DataRights.Domain.Models;
using Gma.Framework.Results;

internal static class TenantTerminationExportArtifactCoordinator
{
    public static Result<TenantTerminationExportArtifact> Prepare(
        TenantTerminationProcess process,
        IReadOnlyCollection<TenantTerminationOwnerWorkItem> ownerWorkItems,
        IReadOnlyCollection<TenantTerminationExportFragment> fragments,
        Guid artifactId,
        Guid idempotencyKey,
        DateTimeOffset nowUtc,
        DateTimeOffset expiresAtUtc)
    {
        ArgumentNullException.ThrowIfNull(process);
        ArgumentNullException.ThrowIfNull(ownerWorkItems);
        ArgumentNullException.ThrowIfNull(fragments);

        if (!TryValidateAndOrder(
                process,
                ownerWorkItems,
                fragments,
                nowUtc,
                expiresAtUtc,
                out TenantTerminationExportFragment[] exact))
        {
            return Invalid<TenantTerminationExportArtifact>();
        }

        string fragmentSetSha256 = ComputeFragmentSetSha256(
            process,
            exact);
        return TenantTerminationExportArtifact.Request(
            artifactId,
            process.ScopeId,
            process.Id,
            process.CaseId,
            process.ApprovalRevision,
            exact[0].FreezeOperationRevision,
            process.OperationRevision,
            process.TerminationEpoch,
            idempotencyKey,
            exact[0].FrozenRevisionSha256,
            process.PolicyEvidenceSha256,
            exact.Length,
            fragmentSetSha256,
            nowUtc,
            expiresAtUtc);
    }

    public static Result Confirm(
        TenantTerminationProcess process,
        TenantTerminationExportArtifact artifact,
        long expectedProcessVersion,
        string actorId,
        DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(process);
        ArgumentNullException.ThrowIfNull(artifact);
        if (!MatchesAvailableProof(process, artifact, nowUtc) ||
            process.Phase != TenantTerminationProcessPhase.Export ||
            process.Status != TenantTerminationProcessStatus.Running ||
            artifact.ExportOperationRevision != process.OperationRevision)
        {
            return Result.Failure(
                DataRightsApplicationErrors
                    .TenantTerminationExportProofInvalid);
        }

        return process.ConfirmExport(
            artifact.ExportOperationRevision,
            artifact.Id,
            artifact.Version,
            artifact.FrozenRevisionSha256,
            artifact.FragmentSetSha256,
            expectedProcessVersion,
            actorId,
            nowUtc);
    }

    public static bool IsAvailableForDownload(
        TenantTerminationProcess process,
        TenantTerminationExportArtifact artifact,
        DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(process);
        ArgumentNullException.ThrowIfNull(artifact);
        if (!MatchesAvailableProof(process, artifact, nowUtc))
        {
            return false;
        }

        bool currentExport =
            process.Phase == TenantTerminationProcessPhase.Export &&
            artifact.ExportOperationRevision == process.OperationRevision;
        bool confirmedExport = MatchesConfirmationReceipt(
                process,
                artifact) &&
            process.ExportArtifactVersion == artifact.Version;
        return currentExport || confirmedExport;
    }

    internal static bool MatchesConfirmationReceipt(
        TenantTerminationProcess process,
        TenantTerminationExportArtifact artifact)
    {
        ArgumentNullException.ThrowIfNull(process);
        ArgumentNullException.ThrowIfNull(artifact);
        string confirmedBy = process.ExportConfirmedBy?.Trim() ?? string.Empty;
        return MatchesProcessProof(process, artifact) &&
            process.ExportConfirmationRevision > 0 &&
            process.ExportConfirmedOperationRevision ==
                artifact.ExportOperationRevision &&
            process.ExportArtifactId == artifact.Id &&
            process.ExportArtifactVersion is > 0 &&
            process.ExportArtifactVersion <= artifact.Version &&
            string.Equals(
                process.ExportFrozenRevisionSha256,
                artifact.FrozenRevisionSha256,
                StringComparison.Ordinal) &&
            string.Equals(
                process.ExportFragmentSetSha256,
                artifact.FragmentSetSha256,
                StringComparison.Ordinal) &&
            confirmedBy.Length is > 0 and <=
                TenantTerminationProcess.ActorIdMaxLength &&
            string.Equals(
                process.ExportConfirmedBy,
                confirmedBy,
                StringComparison.Ordinal) &&
            artifact.AvailableAtUtc is DateTimeOffset availableAtUtc &&
            process.ExportConfirmedAtUtc is DateTimeOffset confirmedAtUtc &&
            confirmedAtUtc >= availableAtUtc &&
            confirmedAtUtc < artifact.ExpiresAtUtc;
    }

    private static bool MatchesAvailableProof(
        TenantTerminationProcess process,
        TenantTerminationExportArtifact artifact,
        DateTimeOffset nowUtc) =>
        artifact.State == TenantTerminationExportArtifactState.Available &&
        artifact.AvailableAtUtc is DateTimeOffset availableAtUtc &&
        availableAtUtc <= nowUtc &&
        artifact.ExpiresAtUtc > nowUtc &&
        MatchesProcessProof(process, artifact);

    internal static bool MatchesProcessProof(
        TenantTerminationProcess process,
        TenantTerminationExportArtifact artifact) =>
        process.ExportRequested &&
        artifact.ProcessId == process.Id &&
        artifact.CaseId == process.CaseId &&
        artifact.ApprovalRevision == process.ApprovalRevision &&
        artifact.FreezeOperationRevision == process.FreezeOperationRevision &&
        artifact.TerminationEpoch == process.TerminationEpoch &&
        string.Equals(
            artifact.ScopeId,
            process.ScopeId,
            StringComparison.Ordinal) &&
        string.Equals(
            artifact.PolicyEvidenceSha256,
            process.PolicyEvidenceSha256,
            StringComparison.Ordinal) &&
        string.Equals(
            artifact.FrozenRevisionSha256,
            process.FrozenRevisionSha256,
            StringComparison.Ordinal);

    internal static bool MatchesProcessProof(
        TenantTerminationProcess process,
        TenantTerminationExportFragment fragment) =>
        fragment.ExportOperationRevision == process.OperationRevision &&
        MatchesFrozenProcessProof(process, fragment);

    internal static bool MatchesFrozenProcessProof(
        TenantTerminationProcess process,
        TenantTerminationExportFragment fragment) =>
        process.ExportRequested &&
        fragment.ProcessId == process.Id &&
        fragment.CaseId == process.CaseId &&
        fragment.ApprovalRevision == process.ApprovalRevision &&
        fragment.FreezeOperationRevision == process.FreezeOperationRevision &&
        fragment.TerminationEpoch == process.TerminationEpoch &&
        string.Equals(
            fragment.ScopeId,
            process.ScopeId,
            StringComparison.Ordinal) &&
        string.Equals(
            fragment.PolicyEvidenceSha256,
            process.PolicyEvidenceSha256,
            StringComparison.Ordinal) &&
        string.Equals(
            fragment.FrozenRevisionSha256,
            process.FrozenRevisionSha256,
            StringComparison.Ordinal);

    internal static string ComputeFragmentSetSha256(
        TenantTerminationProcess process,
        IReadOnlyCollection<TenantTerminationExportFragment> fragments)
    {
        ArgumentNullException.ThrowIfNull(process);
        ArgumentNullException.ThrowIfNull(fragments);
        List<TenantTerminationExportFragmentManifestEntry> entries = [];
        foreach (TenantTerminationExportFragment fragment in fragments)
        {
            if (!TenantTerminationExportFragmentManifestEntry.TryCreate(
                    fragment,
                    out TenantTerminationExportFragmentManifestEntry? entry))
            {
                throw new InvalidOperationException(
                    "Only available tenant export fragments can be digested.");
            }

            entries.Add(entry);
        }

        return TenantTerminationExportFragmentSet.ComputeSha256(
            new(
                process.ScopeId,
                process.Id,
                process.CaseId,
                process.ApprovalRevision,
                process.OperationRevision,
                process.TerminationEpoch,
                process.PolicyEvidenceSha256),
            entries);
    }

    private static bool TryValidateAndOrder(
        TenantTerminationProcess process,
        IReadOnlyCollection<TenantTerminationOwnerWorkItem> ownerWorkItems,
        IReadOnlyCollection<TenantTerminationExportFragment> fragments,
        DateTimeOffset nowUtc,
        DateTimeOffset expiresAtUtc,
        out TenantTerminationExportFragment[] exact)
    {
        exact = [];
        if (!process.ExportRequested ||
            process.Phase != TenantTerminationProcessPhase.Export ||
            process.Status != TenantTerminationProcessStatus.Running ||
            process.OperationRevision <= 0 ||
            process.FreezeOperationRevision is not > 0 ||
            string.IsNullOrWhiteSpace(process.FrozenRevisionSha256) ||
            process.FrozenExportOwners.Count != ownerWorkItems.Count ||
            nowUtc == default ||
            expiresAtUtc <= nowUtc ||
            fragments.Count != ownerWorkItems.Count ||
            fragments.Count is <= 0 or >
                TenantTerminationExportArtifact.MaximumFragmentCount)
        {
            return false;
        }

        Dictionary<Guid, TenantTerminationExportFragment> byWorkItem =
            fragments
                .Where(fragment => fragment is not null)
                .GroupBy(fragment => fragment.Id)
                .Where(group => group.Count() == 1)
                .ToDictionary(
                    group => group.Key,
                    group => group.Single());
        TenantTerminationOwnerWorkItem[] frozenOwners = [..
            ownerWorkItems
                .Where(workItem => workItem is not null)
                .OrderBy(workItem => workItem.OwnerKey, StringComparer.Ordinal)
                .ThenBy(workItem => workItem.Id)];
        if (byWorkItem.Count != fragments.Count ||
            frozenOwners.Length != ownerWorkItems.Count ||
            frozenOwners.Select(workItem => workItem.Id).Distinct().Count() !=
                frozenOwners.Length ||
            frozenOwners.Select(workItem => workItem.OwnerKey)
                .Distinct(StringComparer.Ordinal).Count() != frozenOwners.Length)
        {
            return false;
        }

        Dictionary<string, TenantTerminationFrozenOwner>
            frozenCatalog = process.FrozenExportOwners.ToDictionary(
                owner => owner.OwnerKey,
                StringComparer.Ordinal);
        if (frozenCatalog.Count != process.FrozenExportOwners.Count)
        {
            return false;
        }

        List<TenantTerminationExportFragment> ordered = [];
        string? frozenRevision = null;
        long? freezeOperationRevision = null;
        foreach (TenantTerminationOwnerWorkItem workItem in frozenOwners)
        {
            if (!byWorkItem.TryGetValue(
                    workItem.Id,
                    out TenantTerminationExportFragment? fragment) ||
                !frozenCatalog.TryGetValue(
                    workItem.OwnerKey,
                    out TenantTerminationFrozenOwner? frozenOwner) ||
                frozenOwner.ContractVersion !=
                    workItem.OwnerContractVersion ||
                frozenOwner.CatalogVersion != workItem.CatalogVersion ||
                !string.Equals(
                    frozenOwner.CatalogSha256,
                    workItem.CatalogSha256,
                    StringComparison.Ordinal) ||
                !FragmentMatches(
                    process,
                    workItem,
                    fragment,
                    nowUtc,
                    expiresAtUtc) ||
                (frozenRevision is not null &&
                 !string.Equals(
                     frozenRevision,
                     fragment.FrozenRevisionSha256,
                     StringComparison.Ordinal)) ||
                (freezeOperationRevision.HasValue &&
                 freezeOperationRevision != fragment.FreezeOperationRevision) ||
                fragment.FreezeOperationRevision !=
                    process.FreezeOperationRevision ||
                !string.Equals(
                    fragment.FrozenRevisionSha256,
                    process.FrozenRevisionSha256,
                    StringComparison.Ordinal))
            {
                return false;
            }

            frozenRevision ??= fragment.FrozenRevisionSha256;
            freezeOperationRevision ??= fragment.FreezeOperationRevision;
            ordered.Add(fragment);
        }

        exact = [.. ordered];
        return true;
    }

    private static bool FragmentMatches(
        TenantTerminationProcess process,
        TenantTerminationOwnerWorkItem workItem,
        TenantTerminationExportFragment fragment,
        DateTimeOffset nowUtc,
        DateTimeOffset artifactExpiresAtUtc) =>
        workItem.State == TenantTerminationOwnerWorkState.Completed &&
        workItem.Phase == TenantTerminationOwnerPhase.Export &&
        string.Equals(workItem.ScopeId, process.ScopeId, StringComparison.Ordinal) &&
        workItem.ProcessId == process.Id &&
        workItem.CaseId == process.CaseId &&
        workItem.ApprovalRevision == process.ApprovalRevision &&
        workItem.OperationRevision == process.OperationRevision &&
        workItem.TerminationEpoch == process.TerminationEpoch &&
        string.Equals(
            workItem.PolicyEvidenceSha256,
            process.PolicyEvidenceSha256,
            StringComparison.Ordinal) &&
        fragment.State == TenantTerminationExportFragmentState.Available &&
        fragment.Id == workItem.Id &&
        string.Equals(fragment.ScopeId, process.ScopeId, StringComparison.Ordinal) &&
        fragment.ProcessId == process.Id &&
        fragment.CaseId == process.CaseId &&
        fragment.ApprovalRevision == process.ApprovalRevision &&
        fragment.ExportOperationRevision == process.OperationRevision &&
        fragment.FreezeOperationRevision > 0 &&
        fragment.FreezeOperationRevision < fragment.ExportOperationRevision &&
        fragment.TerminationEpoch == process.TerminationEpoch &&
        fragment.IdempotencyKey == workItem.IdempotencyKey &&
        string.Equals(
            fragment.OwnerKey,
            workItem.OwnerKey,
            StringComparison.Ordinal) &&
        fragment.OwnerContractVersion == workItem.OwnerContractVersion &&
        fragment.CatalogVersion == workItem.CatalogVersion &&
        string.Equals(
            fragment.CatalogSha256,
            workItem.CatalogSha256,
            StringComparison.Ordinal) &&
        string.Equals(
            fragment.PolicyEvidenceSha256,
            process.PolicyEvidenceSha256,
            StringComparison.Ordinal) &&
        fragment.RecordCount is >= 0 and <=
            TenantTerminationExportFragment.MaximumRecordCount &&
        workItem.AffectedCount == fragment.RecordCount &&
        workItem.RetainedMinimumCount == 0 &&
        workItem.RemainingActiveCount == 0 &&
        !workItem.HoldReviewAtUtc.HasValue &&
        fragment.SelectedProofRevision is >= 0 &&
        fragment.ResultingProofRevision == fragment.SelectedProofRevision &&
        workItem.SelectedProofRevision == fragment.SelectedProofRevision &&
        workItem.ResultingProofRevision == fragment.ResultingProofRevision &&
        !string.IsNullOrWhiteSpace(fragment.ResultCode) &&
        string.Equals(
            workItem.ResultCode,
            fragment.ResultCode,
            StringComparison.Ordinal) &&
        !string.IsNullOrWhiteSpace(fragment.StorageKey) &&
        fragment.EncryptedByteLength is > 0 &&
        !string.IsNullOrWhiteSpace(fragment.PlaintextSha256) &&
        fragment.EncryptionKeyVersion is > 0 &&
        fragment.FormatVersion is > 0 &&
        workItem.ResultRecordedAtUtc is DateTimeOffset resultRecordedAtUtc &&
        fragment.AvailableAtUtc is DateTimeOffset availableAtUtc &&
        resultRecordedAtUtc <= availableAtUtc &&
        availableAtUtc <= nowUtc &&
        fragment.ExpiresAtUtc >= artifactExpiresAtUtc;

    private static Result<T> Invalid<T>() =>
        Result.Failure<T>(
            DataRightsApplicationErrors.TenantTerminationExportProofInvalid);
}
