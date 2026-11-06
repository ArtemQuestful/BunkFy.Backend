namespace BunkFy.Modules.DataRights.Application.Handlers;

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BunkFy.Modules.DataRights.Application.Models;
using BunkFy.Modules.DataRights.Application.Ports;
using BunkFy.Modules.DataRights.Contracts;
using Gma.Framework.Naming;
using Gma.Framework.Results;

internal sealed class TenantTerminationExportFragmentAssembler(
    IEnumerable<ITenantTerminationContributor> contributors,
    IEnumerable<ITenantTerminationExportContributor> exportContributors)
    : ITenantTerminationExportFragmentAssembler
{
    private const string FrozenRevisionDomain =
        "bunkfy.tenant-termination.frozen-revision.v1";

    public async Task<TenantTerminationExportFragmentAssemblyResult>
        AssembleAsync(
            TenantTerminationExportFragmentAssemblyRequest request,
            Stream destination,
            CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(destination);
        if (!destination.CanWrite)
        {
            throw new ArgumentException(
                "The export destination must be writable.",
                nameof(destination));
        }

        TenantTerminationExportOwnerCatalogEntry[] frozenOwners =
            ValidateAndOrderFrozenOwners(request);
        IReadOnlyList<ITenantTerminationContributor> orderedContributors =
            this.ResolveContributors(frozenOwners);
        Dictionary<string, ITenantTerminationExportContributor> exporters =
            ResolveExporters(
                orderedContributors,
                exportContributors);
        TenantTerminationContributorDescriptor ownerDescriptor =
            orderedContributors
                .Single(contributor => string.Equals(
                    contributor.Descriptor.OwnerKey,
                    request.OwnerWork.OwnerKey,
                    StringComparison.Ordinal))
                .Descriptor;
        ITenantTerminationExportContributor exporter =
            exporters[ownerDescriptor.OwnerKey];
        DataRightsExportDescriptor exportDescriptor =
            DataRightsExportSchemaValidator.Validate(
                exporter.ExportDescriptor,
                ownerDescriptor.OwnerKey);

        using Utf8JsonWriter writer = new(destination, new JsonWriterOptions
        {
            Indented = false,
            SkipValidation = false
        });
        WriteEnvelopeHeader(writer, request);
        writer.WriteStartObject("owner");
        WriteOwnerDescriptor(writer, ownerDescriptor, exportDescriptor);
        writer.WriteStartArray("records");

        long totalRecords = 0;
        DataRightsJsonExportSink sink = new(
            writer,
            exportDescriptor,
            () => totalRecords,
            count => totalRecords = count,
            TenantTerminationExportContract.MaximumRecordsPerFragment);
        TenantTerminationContributionResult result;
        try
        {
            result = await exporter.ExportAsync(
                CreateOwnerRequest(request),
                sink,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is not DataRightsExportGenerationException)
        {
            throw Failure("owner-export-failed");
        }

        TenantTerminationExportOwnerResult ownerResult =
            ValidateResult(request, sink.RecordCount, result);
        writer.WriteEndArray();
        writer.WriteNumber("recordCount", ownerResult.RecordCount);
        writer.WriteStartObject("proof");
        writer.WriteNumber(
            "selectedRevision",
            ownerResult.SelectedProofRevision);
        writer.WriteNumber(
            "resultingRevision",
            ownerResult.ResultingProofRevision);
        writer.WriteString("resultCode", ownerResult.ResultCode);
        writer.WriteString("recordedAtUtc", ownerResult.RecordedAtUtc);
        writer.WriteEndObject();
        writer.WriteEndObject();
        writer.WriteEndObject();
        writer.Flush();

        return new TenantTerminationExportFragmentAssemblyResult(
            request.FrozenRevisionSha256,
            ownerResult);
    }

    internal static string ComputeFrozenRevisionSha256(
        TenantTerminationExportFragmentAssemblyRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return ComputeFrozenRevisionSha256(new TenantTerminationFrozenRevision(
            request.TenantId,
            request.ProcessId,
            request.CaseId,
            request.ApprovalRevision,
            request.FreezeOperationRevision,
            request.TerminationEpoch,
            request.WorkspaceFenceRevision,
            request.PolicyEvidenceSha256,
            request.FrozenAtUtc,
            request.FrozenOwners));
    }

    internal static string ComputeFrozenRevisionSha256(
        TenantTerminationFrozenRevision snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (!TenantIds.TryNormalize(snapshot.TenantId, out string? tenantId) ||
            snapshot.ProcessId == Guid.Empty ||
            snapshot.CaseId == Guid.Empty ||
            snapshot.ApprovalRevision <= 0 ||
            snapshot.FreezeOperationRevision <= 0 ||
            snapshot.TerminationEpoch == Guid.Empty ||
            snapshot.WorkspaceFenceRevision <= 0 ||
            !IsSha256(snapshot.PolicyEvidenceSha256) ||
            snapshot.FrozenAtUtc == default ||
            snapshot.FrozenOwners is null ||
            snapshot.FrozenOwners.Count is <= 0 or >
                TenantTerminationContract.MaximumContributors)
        {
            throw Failure("tenant-export-coordinate-invalid");
        }

        TenantTerminationExportOwnerCatalogEntry[] owners =
            snapshot.FrozenOwners
                .OrderBy(item => item.OwnerKey, StringComparer.Ordinal)
                .ToArray();
        if (!HasValidFrozenOwners(owners))
        {
            throw Failure("tenant-export-owner-catalog-invalid");
        }

        StringBuilder canonical = new();
        Append(canonical, FrozenRevisionDomain);
        Append(
            canonical,
            TenantTerminationExportContract.FormatVersion.ToString(
                CultureInfo.InvariantCulture));
        Append(canonical, tenantId);
        Append(canonical, snapshot.ProcessId.ToString("N"));
        Append(canonical, snapshot.CaseId.ToString("N"));
        Append(
            canonical,
            snapshot.ApprovalRevision.ToString(CultureInfo.InvariantCulture));
        Append(
            canonical,
            snapshot.FreezeOperationRevision.ToString(
                CultureInfo.InvariantCulture));
        Append(canonical, snapshot.TerminationEpoch.ToString("N"));
        Append(
            canonical,
            snapshot.WorkspaceFenceRevision.ToString(
                CultureInfo.InvariantCulture));
        Append(
            canonical,
            snapshot.FrozenAtUtc.ToUniversalTime().ToString(
                "O",
                CultureInfo.InvariantCulture));
        Append(canonical, snapshot.PolicyEvidenceSha256);
        foreach (TenantTerminationExportOwnerCatalogEntry owner in owners)
        {
            Append(canonical, owner.OwnerKey);
            Append(
                canonical,
                owner.ContractVersion.ToString(CultureInfo.InvariantCulture));
            Append(
                canonical,
                owner.CatalogVersion.ToString(CultureInfo.InvariantCulture));
            Append(canonical, owner.CatalogSha256);
        }

        byte[] bytes = Encoding.UTF8.GetBytes(canonical.ToString());
        try
        {
            return Convert.ToHexStringLower(SHA256.HashData(bytes));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    private IReadOnlyList<ITenantTerminationContributor> ResolveContributors(
        TenantTerminationExportOwnerCatalogEntry[] frozenOwners)
    {
        Result<IReadOnlyList<ITenantTerminationContributor>> ordered =
            TenantTerminationContributorSet.OrderForPhase(
                contributors,
                TenantTerminationContributionPhase.Export);
        if (ordered.IsFailure || ordered.Value.Count != frozenOwners.Length)
        {
            throw Failure("tenant-export-owner-catalog-invalid");
        }

        Dictionary<string, TenantTerminationExportOwnerCatalogEntry>
            frozenByOwner = frozenOwners.ToDictionary(
                item => item.OwnerKey,
                StringComparer.Ordinal);
        bool exact = ordered.Value.All(contributor =>
        {
            TenantTerminationContributorDescriptor descriptor =
                contributor.Descriptor;
            return frozenByOwner.TryGetValue(
                    descriptor.OwnerKey,
                    out TenantTerminationExportOwnerCatalogEntry? frozen) &&
                descriptor.ContractVersion == frozen.ContractVersion &&
                descriptor.CatalogVersion == frozen.CatalogVersion &&
                string.Equals(
                    descriptor.CatalogSha256,
                    frozen.CatalogSha256,
                    StringComparison.Ordinal);
        });
        return exact
            ? ordered.Value
            : throw Failure("tenant-export-owner-catalog-invalid");
    }

    private static Dictionary<string, ITenantTerminationExportContributor>
        ResolveExporters(
            IReadOnlyCollection<ITenantTerminationContributor>
                phaseContributors,
            IEnumerable<ITenantTerminationExportContributor> supplied)
    {
        if (supplied is null)
        {
            throw Failure("tenant-export-owner-unavailable");
        }

        ITenantTerminationExportContributor[] candidates = supplied.ToArray();
        Dictionary<string, ITenantTerminationExportContributor> byOwner =
            new(StringComparer.Ordinal);
        foreach (ITenantTerminationExportContributor? candidate in candidates)
        {
            DataRightsExportDescriptor? descriptor =
                candidate?.ExportDescriptor;
            string ownerKey = descriptor?.OwnerKey?.Trim().ToLowerInvariant() ??
                string.Empty;
            if (candidate is null ||
                !IsStableKey(ownerKey) ||
                !byOwner.TryAdd(ownerKey, candidate))
            {
                throw Failure("tenant-export-owner-unavailable");
            }
        }

        if (byOwner.Count != phaseContributors.Count ||
            phaseContributors.Any(contributor =>
                !byOwner.TryGetValue(
                    contributor.Descriptor.OwnerKey,
                    out ITenantTerminationExportContributor? exporter) ||
                exporter.ExportDescriptor.CatalogVersion !=
                    contributor.Descriptor.CatalogVersion))
        {
            throw Failure("tenant-export-owner-unavailable");
        }

        return byOwner;
    }

    private static TenantTerminationExportOwnerCatalogEntry[]
        ValidateAndOrderFrozenOwners(
            TenantTerminationExportFragmentAssemblyRequest request)
    {
        if (!TenantIds.TryNormalize(request.TenantId, out _) ||
            request.ProcessId == Guid.Empty ||
            request.CaseId == Guid.Empty ||
            request.ApprovalRevision <= 0 ||
            request.FreezeOperationRevision <= 0 ||
            request.ExportOperationRevision <=
                request.FreezeOperationRevision ||
            request.TerminationEpoch == Guid.Empty ||
            request.WorkspaceFenceRevision <= 0 ||
            !IsSha256(request.FrozenRevisionSha256) ||
            !IsSha256(request.PolicyEvidenceSha256) ||
            !IsActor(request.ExecutingActorId) ||
            request.FrozenAtUtc == default ||
            request.GeneratedAtUtc < request.FrozenAtUtc ||
            request.DeadlineUtc <= request.GeneratedAtUtc ||
            request.OwnerWork is null ||
            request.FrozenOwners is null ||
            request.FrozenOwners.Count is <= 0 or >
                TenantTerminationContract.MaximumContributors)
        {
            throw Failure("tenant-export-coordinate-invalid");
        }

        TenantTerminationExportOwnerCatalogEntry[] owners =
            request.FrozenOwners
                .OrderBy(item => item.OwnerKey, StringComparer.Ordinal)
                .ToArray();
        if (!HasValidFrozenOwners(owners) ||
            !HasValidOwnerWork(request.OwnerWork) ||
            !owners.Any(owner => Matches(owner, request.OwnerWork)) ||
            !string.Equals(
                request.FrozenRevisionSha256,
                ComputeFrozenRevisionSha256(request),
                StringComparison.Ordinal))
        {
            throw Failure("tenant-export-frozen-revision-invalid");
        }

        return owners;
    }

    private static bool HasValidFrozenOwners(
        TenantTerminationExportOwnerCatalogEntry[] owners) =>
        owners.All(owner =>
            owner is not null &&
            IsStableKey(owner.OwnerKey) &&
            owner.ContractVersion == TenantTerminationContract.CurrentVersion &&
            owner.CatalogVersion > 0 &&
            IsSha256(owner.CatalogSha256)) &&
        owners.Select(owner => owner.OwnerKey)
            .Distinct(StringComparer.Ordinal).Count() == owners.Length;

    private static bool HasValidOwnerWork(
        TenantTerminationExportOwnerWork work) =>
        IsStableKey(work.OwnerKey) &&
        work.WorkItemId != Guid.Empty &&
        work.IdempotencyKey != Guid.Empty &&
        work.ContractVersion == TenantTerminationContract.CurrentVersion &&
        work.CatalogVersion > 0 &&
        IsSha256(work.CatalogSha256);

    private static bool Matches(
        TenantTerminationExportOwnerCatalogEntry owner,
        TenantTerminationExportOwnerWork work) =>
        string.Equals(owner.OwnerKey, work.OwnerKey, StringComparison.Ordinal) &&
        owner.ContractVersion == work.ContractVersion &&
        owner.CatalogVersion == work.CatalogVersion &&
        string.Equals(
            owner.CatalogSha256,
            work.CatalogSha256,
            StringComparison.Ordinal);

    private static TenantTerminationExportRequest CreateOwnerRequest(
        TenantTerminationExportFragmentAssemblyRequest request) =>
        new(
            new TenantTerminationContributionRequest(
                request.OwnerWork.ContractVersion,
                request.TenantId,
                request.ProcessId,
                request.CaseId,
                request.ApprovalRevision,
                request.ExportOperationRevision,
                request.TerminationEpoch,
                TenantTerminationContributionPhase.Export,
                request.OwnerWork.WorkItemId,
                request.OwnerWork.IdempotencyKey,
                request.PolicyEvidenceSha256,
                request.ExecutingActorId,
                request.DeadlineUtc),
            request.FreezeOperationRevision,
            request.WorkspaceFenceRevision,
            request.FrozenRevisionSha256,
            request.FrozenAtUtc);

    private static TenantTerminationExportOwnerResult ValidateResult(
        TenantTerminationExportFragmentAssemblyRequest request,
        long recordCount,
        TenantTerminationContributionResult? result)
    {
        if (result is null ||
            result.Status != TenantTerminationContributionStatus.Completed ||
            !IsStableResultCode(result.ResultCode) ||
            result.AffectedCount != recordCount ||
            result.RetainedMinimumCount != 0 ||
            result.RemainingActiveCount != 0 ||
            result.HoldReviewAtUtc.HasValue ||
            result.SelectedProofRevision is not long selectedRevision ||
            result.ResultingProofRevision is not long resultingRevision ||
            selectedRevision < 0 ||
            resultingRevision != selectedRevision ||
            result.CatalogVersion != request.OwnerWork.CatalogVersion ||
            !string.Equals(
                result.CatalogSha256,
                request.OwnerWork.CatalogSha256,
                StringComparison.Ordinal) ||
            result.RecordedAtUtc < request.GeneratedAtUtc ||
            result.RecordedAtUtc >= request.DeadlineUtc)
        {
            throw Failure("tenant-export-owner-result-invalid");
        }

        return new TenantTerminationExportOwnerResult(
            request.OwnerWork.OwnerKey,
            recordCount,
            selectedRevision,
            resultingRevision,
            result.ResultCode,
            result.RecordedAtUtc);
    }

    private static void WriteEnvelopeHeader(
        Utf8JsonWriter writer,
        TenantTerminationExportFragmentAssemblyRequest request)
    {
        writer.WriteStartObject();
        writer.WriteString(
            "format",
            "bunkfy.tenant-termination.export-fragment");
        writer.WriteNumber(
            "formatVersion",
            TenantTerminationExportContract.FormatVersion);
        writer.WriteString("processId", request.ProcessId);
        writer.WriteString("caseId", request.CaseId);
        writer.WriteNumber("approvalRevision", request.ApprovalRevision);
        writer.WriteNumber(
            "freezeOperationRevision",
            request.FreezeOperationRevision);
        writer.WriteNumber(
            "exportOperationRevision",
            request.ExportOperationRevision);
        writer.WriteString("terminationEpoch", request.TerminationEpoch);
        writer.WriteNumber(
            "workspaceFenceRevision",
            request.WorkspaceFenceRevision);
        writer.WriteString(
            "frozenRevisionSha256",
            request.FrozenRevisionSha256);
        writer.WriteString(
            "policyEvidenceSha256",
            request.PolicyEvidenceSha256);
        writer.WriteString("frozenAtUtc", request.FrozenAtUtc);
        writer.WriteString("generatedAtUtc", request.GeneratedAtUtc);
    }

    private static void WriteOwnerDescriptor(
        Utf8JsonWriter writer,
        TenantTerminationContributorDescriptor owner,
        DataRightsExportDescriptor export)
    {
        writer.WriteString("ownerKey", owner.OwnerKey);
        writer.WriteNumber("ownerContractVersion", owner.ContractVersion);
        writer.WriteNumber("catalogVersion", owner.CatalogVersion);
        writer.WriteString("catalogSha256", owner.CatalogSha256);
        writer.WriteStartObject("exportSchema");
        writer.WriteString("catalogId", export.CatalogId);
        writer.WriteNumber("catalogSchemaVersion", export.CatalogSchemaVersion);
        writer.WriteString("schemaId", export.ExportSchemaId);
        writer.WriteNumber("schemaVersion", export.ExportSchemaVersion);
        writer.WriteEndObject();
    }

    private static bool IsActor(string? value)
    {
        string normalized = value?.Trim() ?? string.Empty;
        return normalized.Length is > 0 and <=
            TenantTerminationContract.ActorIdMaxLength;
    }

    private static bool IsStableKey(string? value)
    {
        string normalized = value?.Trim() ?? string.Empty;
        return normalized.Length is > 0 and <=
                TenantTerminationContract.OwnerKeyMaxLength &&
            normalized[0] is >= 'a' and <= 'z' &&
            normalized.All(character =>
                character is (>= 'a' and <= 'z') or
                    (>= '0' and <= '9') or '.' or '-' or '_');
    }

    private static bool IsStableResultCode(string? value)
    {
        string normalized = value?.Trim() ?? string.Empty;
        return normalized.Length is > 0 and <=
                TenantTerminationContract.ResultCodeMaxLength &&
            normalized[0] is >= 'a' and <= 'z' &&
            normalized.All(character =>
                character is (>= 'a' and <= 'z') or
                    (>= '0' and <= '9') or '.' or '-' or '_');
    }

    private static bool IsSha256(string? value) =>
        value is { Length: TenantTerminationContract.Sha256Length } &&
        value.All(character =>
            character is (>= '0' and <= '9') or (>= 'a' and <= 'f'));

    private static void Append(StringBuilder target, string value)
    {
        target.Append(value.Length.ToString(CultureInfo.InvariantCulture));
        target.Append(':');
        target.Append(value);
    }

    private static DataRightsExportGenerationException Failure(string code) =>
        new(code);
}
