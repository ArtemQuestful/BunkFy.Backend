namespace BunkFy.Modules.DataRights.Application.Handlers;

using System.Text.Json;
using BunkFy.Modules.DataRights.Application.Models;
using BunkFy.Modules.DataRights.Application.Ports;
using BunkFy.Modules.DataRights.Contracts;
using Microsoft.Extensions.Logging;

internal sealed class DataRightsExportAssembler(
    IEnumerable<IDataRightsSubjectExportContributor> contributors,
    ILogger<DataRightsExportAssembler> logger)
    : IDataRightsExportAssembler
{
    public const int FormatVersion = 1;
    public const int MaximumRecords = 50_000;

    public async Task<DataRightsExportAssemblyResult> AssembleAsync(
        DataRightsExportGenerationRequest request,
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

        DataRightsSubjectCoordinate[] subjects = ValidateAndOrder(request);
        IReadOnlyDictionary<string, IDataRightsSubjectExportContributor> byOwner =
            this.ResolveContributors(request.CaseType, subjects);

        using Utf8JsonWriter writer = new(destination, new JsonWriterOptions
        {
            Indented = false,
            SkipValidation = false
        });
        writer.WriteStartObject();
        writer.WriteString("format", "bunkfy.data-rights.export");
        writer.WriteNumber("formatVersion", FormatVersion);
        writer.WriteString("caseType", CaseType(request.CaseType));
        writer.WriteString(
            "scopeType",
            request.PropertyId.HasValue ? "property" : "tenant");
        writer.WriteNumber("decisionRevision", request.DecisionRevision);
        writer.WriteString("generatedAtUtc", request.GeneratedAtUtc);
        writer.WriteString("expiresAtUtc", request.ExpiresAtUtc);
        writer.WriteStartArray("subjects");

        long totalRecords = 0;
        foreach (DataRightsSubjectCoordinate subject in subjects)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IDataRightsSubjectExportContributor contributor =
                byOwner[subject.OwnerKey.Trim().ToLowerInvariant()];
            DataRightsExportDescriptor descriptor =
                ValidateDescriptor(contributor, subject.OwnerKey);

            writer.WriteStartObject();
            WriteCoordinate(writer, subject);
            WriteDescriptor(writer, descriptor);
            writer.WriteStartArray("records");

            DataRightsJsonExportSink sink = new(
                writer,
                descriptor,
                () => totalRecords,
                count => totalRecords = count,
                MaximumRecords);
            DataRightsSubjectExportResult result;
            try
            {
                result = await contributor.ExportAsync(
                    new DataRightsSubjectExportRequest(
                        request.TenantId,
                        request.CaseType,
                        request.PropertyId,
                        subject),
                    sink,
                    cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception) when (
                exception is not DataRightsExportGenerationException)
            {
                logger.LogError(
                    "Data Rights export owner {OwnerKey} failed with {ExceptionType}.",
                    subject.OwnerKey.Trim().ToLowerInvariant(),
                    exception.GetType().Name);
                throw new DataRightsExportGenerationException(
                    "owner-export-failed",
                    DataRightsExportFailureDisposition.Retryable);
            }

            if (result is null || result.RecordCount < 0)
            {
                throw new DataRightsExportGenerationException(
                    "owner-result-invalid");
            }

            if (result.Status != DataRightsSubjectExportStatus.Succeeded)
            {
                if (result.RecordCount != 0 || sink.RecordCount != 0)
                {
                    throw new DataRightsExportGenerationException(
                        "owner-result-invalid");
                }

                throw Failure(result.Status);
            }

            if (result.RecordCount != sink.RecordCount)
            {
                throw new DataRightsExportGenerationException(
                    "owner-result-invalid");
            }

            writer.WriteEndArray();
            writer.WriteNumber("recordCount", sink.RecordCount);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WriteStartObject("summary");
        writer.WriteNumber("subjectCount", subjects.Length);
        writer.WriteNumber("recordCount", totalRecords);
        writer.WriteEndObject();
        writer.WriteEndObject();
        writer.Flush();

        return new DataRightsExportAssemblyResult(
            subjects.Length,
            checked((int)totalRecords));
    }

    private IReadOnlyDictionary<string, IDataRightsSubjectExportContributor>
        ResolveContributors(
            DataRightsCaseType caseType,
            IReadOnlyCollection<DataRightsSubjectCoordinate> subjects)
    {
        var result = DataRightsExportContributorSet.Resolve(
            contributors,
            caseType,
            subjects);
        if (result.IsFailure)
        {
            throw new DataRightsExportGenerationException(
                result.Error == DataRightsApplicationErrors.ExportOwnerUnavailable
                    ? "owner-unavailable"
                    : "owner-catalog-invalid");
        }

        return result.Value;
    }

    private static DataRightsSubjectCoordinate[] ValidateAndOrder(
        DataRightsExportGenerationRequest request)
    {
        if (request.ArtifactId == Guid.Empty ||
            string.IsNullOrWhiteSpace(request.TenantId) ||
            request.CaseId == Guid.Empty ||
            request.CaseType is not DataRightsCaseType.GuestRights and
                not DataRightsCaseType.StaffRights ||
            request.DecisionRevision <= 0 ||
            request.GeneratedAtUtc == default ||
            request.ExpiresAtUtc <= request.GeneratedAtUtc ||
            request.SelectedSubjects is null ||
            request.SelectedSubjects.Count is <= 0 or >
                BunkFy.Modules.DataRights.Domain.Aggregates
                    .DataRightsCase.MaxSelectedSubjects ||
            !IsValidScope(request.CaseType, request.PropertyId))
        {
            throw new DataRightsExportGenerationException(
                "generation-coordinate-invalid");
        }

        DataRightsSubjectCoordinate[] ordered = request.SelectedSubjects
            .OrderBy(subject => subject.OwnerKey, StringComparer.Ordinal)
            .ThenBy(subject => subject.RecordType, StringComparer.Ordinal)
            .ThenBy(subject => subject.RecordId)
            .ToArray();
        if (ordered.Any(subject =>
                string.IsNullOrWhiteSpace(subject.OwnerKey) ||
                subject.OwnerKey.Trim().Length >
                    DataRightsExportLimits.OwnerKeyMaxLength ||
                string.IsNullOrWhiteSpace(subject.RecordType) ||
                subject.RecordType.Trim().Length >
                    DataRightsExportLimits.RecordTypeMaxLength ||
                subject.RecordId == Guid.Empty ||
                subject.RecordVersion <= 0) ||
            ordered.GroupBy(subject => (
                    subject.OwnerKey.Trim().ToLowerInvariant(),
                    subject.RecordType.Trim().ToLowerInvariant(),
                    subject.RecordId))
                .Any(group => group.Count() != 1))
        {
            throw new DataRightsExportGenerationException(
                "subject-coordinate-invalid");
        }

        return ordered;
    }

    private static bool IsValidScope(
        DataRightsCaseType caseType,
        Guid? propertyId) =>
        caseType switch
        {
            DataRightsCaseType.GuestRights =>
                propertyId is Guid value && value != Guid.Empty,
            DataRightsCaseType.StaffRights => propertyId is null,
            _ => false
        };

    private static DataRightsExportDescriptor ValidateDescriptor(
        IDataRightsSubjectExportContributor contributor,
        string ownerKey)
    {
        string normalizedOwner = ownerKey.Trim().ToLowerInvariant();
        if (!string.Equals(
                contributor.OwnerKey.Trim(),
                normalizedOwner,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new DataRightsExportGenerationException(
                "owner-descriptor-invalid");
        }

        return DataRightsExportSchemaValidator.Validate(
            contributor.Descriptor,
            normalizedOwner);
    }

    private static void WriteCoordinate(
        Utf8JsonWriter writer,
        DataRightsSubjectCoordinate subject)
    {
        writer.WriteStartObject("coordinate");
        writer.WriteString(
            "owner",
            subject.OwnerKey.Trim().ToLowerInvariant());
        writer.WriteString(
            "recordType",
            subject.RecordType.Trim().ToLowerInvariant());
        writer.WriteString("recordId", subject.RecordId);
        writer.WriteNumber("recordVersion", subject.RecordVersion);
        writer.WriteEndObject();
    }

    private static void WriteDescriptor(
        Utf8JsonWriter writer,
        DataRightsExportDescriptor descriptor)
    {
        writer.WriteStartObject("ownerDescriptor");
        writer.WriteString("catalogId", descriptor.CatalogId);
        writer.WriteNumber("catalogSchemaVersion", descriptor.CatalogSchemaVersion);
        writer.WriteNumber("catalogVersion", descriptor.CatalogVersion);
        writer.WriteString("exportSchemaId", descriptor.ExportSchemaId);
        writer.WriteNumber("exportSchemaVersion", descriptor.ExportSchemaVersion);
        writer.WriteEndObject();
    }

    private static string CaseType(DataRightsCaseType caseType) =>
        caseType == DataRightsCaseType.GuestRights
            ? "guestRights"
            : "staffRights";

    private static DataRightsExportGenerationException Failure(
        DataRightsSubjectExportStatus status) =>
        status switch
        {
            DataRightsSubjectExportStatus.ScopeUnavailable =>
                new("owner-scope-unavailable"),
            DataRightsSubjectExportStatus.NotFound => new("subject-not-found"),
            DataRightsSubjectExportStatus.Stale => new("subject-stale"),
            DataRightsSubjectExportStatus.RetryRequired => new(
                "owner-retry-required",
                DataRightsExportFailureDisposition.Retryable),
            _ => new("owner-result-invalid")
        };

}
