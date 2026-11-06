namespace BunkFy.Modules.DataRights.Tests.Application;

using System.Text.Json;
using BunkFy.Modules.DataRights.Application.Handlers;
using BunkFy.Modules.DataRights.Application.Models;
using BunkFy.Modules.DataRights.Contracts;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

[Trait("Category", "Unit")]
public sealed class DataRightsExportAssemblerTests
{
    private static readonly DateTimeOffset GeneratedAt =
        new(2026, 7, 27, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Assembly_is_deterministic_and_orders_subjects_and_fields()
    {
        IDataRightsSubjectExportContributor contributor =
            new SuccessfulContributor();
        DataRightsExportAssembler assembler = Assembler(contributor);
        DataRightsExportGenerationRequest request = Request([
            new("staff", "profile", Guid.Parse(
                "22222222-2222-2222-2222-222222222222"), 3),
            new("staff", "profile", Guid.Parse(
                "11111111-1111-1111-1111-111111111111"), 2)
        ]);

        await using MemoryStream first = new();
        await using MemoryStream second = new();
        DataRightsExportAssemblyResult result = await assembler.AssembleAsync(
            request,
            first,
            CancellationToken.None);
        _ = await assembler.AssembleAsync(
            request,
            second,
            CancellationToken.None);

        Assert.Equal(first.ToArray(), second.ToArray());
        Assert.Equal(2, result.SubjectCount);
        Assert.Equal(2, result.RecordCount);
        using JsonDocument document = JsonDocument.Parse(first.ToArray());
        JsonElement root = document.RootElement;
        Assert.Equal("staffRights", root.GetProperty("caseType").GetString());
        Assert.Equal(
            GeneratedAt.AddHours(24),
            root.GetProperty("expiresAtUtc").GetDateTimeOffset());
        JsonElement.ArrayEnumerator subjects =
            root.GetProperty("subjects").EnumerateArray();
        Assert.True(subjects.MoveNext());
        Assert.Equal(
            Guid.Parse("11111111-1111-1111-1111-111111111111"),
            subjects.Current.GetProperty("coordinate")
                .GetProperty("recordId").GetGuid());
    }

    [Fact]
    public async Task Assembly_rejects_partial_non_success_owner_output()
    {
        DataRightsExportAssembler assembler = Assembler(new StaleContributor());
        await using MemoryStream destination = new();

        DataRightsExportGenerationException exception =
            await Assert.ThrowsAsync<DataRightsExportGenerationException>(
                () => assembler.AssembleAsync(
                    Request([
                        new("staff", "profile", Guid.NewGuid(), 2)
                    ]),
                    destination,
                    CancellationToken.None));

        Assert.Equal("owner-result-invalid", exception.Code);
        Assert.False(exception.IsRetryable);
    }

    [Fact]
    public async Task Assembly_rejects_duplicate_owner_contributors()
    {
        DataRightsExportAssembler assembler = Assembler(
            new SuccessfulContributor(),
            new SuccessfulContributor());
        await using MemoryStream destination = new();

        DataRightsExportGenerationException exception =
            await Assert.ThrowsAsync<DataRightsExportGenerationException>(
                () => assembler.AssembleAsync(
                    Request([
                        new("staff", "profile", Guid.NewGuid(), 2)
                    ]),
                    destination,
                    CancellationToken.None));

        Assert.Equal("owner-catalog-invalid", exception.Code);
    }

    [Fact]
    public async Task Assembly_rejects_malformed_unrelated_catalog_entry()
    {
        DataRightsExportAssembler assembler = Assembler(
            new SuccessfulContributor(),
            new MalformedGuestContributor());
        await using MemoryStream destination = new();

        DataRightsExportGenerationException exception =
            await Assert.ThrowsAsync<DataRightsExportGenerationException>(
                () => assembler.AssembleAsync(
                    Request([
                        new("staff", "profile", Guid.NewGuid(), 2)
                    ]),
                    destination,
                    CancellationToken.None));

        Assert.Equal("owner-catalog-invalid", exception.Code);
    }

    [Fact]
    public async Task Assembly_accepts_schema_catalogue_larger_than_one_record()
    {
        DataRightsExportAssembler assembler = Assembler(
            new WideSchemaContributor());
        await using MemoryStream destination = new();

        DataRightsExportAssemblyResult result = await assembler.AssembleAsync(
            Request([
                new("staff", "profile", Guid.NewGuid(), 2)
            ]),
            destination,
            CancellationToken.None);

        Assert.Equal(1, result.RecordCount);
    }

    [Fact]
    public async Task Assembly_rejects_oversized_unrelated_schema_catalogue()
    {
        DataRightsExportAssembler assembler = Assembler(
            new SuccessfulContributor(),
            new OversizedGuestContributor());
        await using MemoryStream destination = new();

        DataRightsExportGenerationException exception =
            await Assert.ThrowsAsync<DataRightsExportGenerationException>(
                () => assembler.AssembleAsync(
                    Request([
                        new("staff", "profile", Guid.NewGuid(), 2)
                    ]),
                    destination,
                    CancellationToken.None));

        Assert.Equal("owner-catalog-invalid", exception.Code);
    }

    [Fact]
    public async Task Assembly_preserves_terminal_stale_owner_result()
    {
        DataRightsExportAssembler assembler = Assembler(
            new PureStaleContributor());
        await using MemoryStream destination = new();

        DataRightsExportGenerationException exception =
            await Assert.ThrowsAsync<DataRightsExportGenerationException>(
                () => assembler.AssembleAsync(
                    Request([
                        new("staff", "profile", Guid.NewGuid(), 2)
                    ]),
                    destination,
                    CancellationToken.None));

        Assert.Equal("subject-stale", exception.Code);
        Assert.False(exception.IsRetryable);
    }

    [Fact]
    public async Task Assembly_preserves_retry_required_owner_result()
    {
        DataRightsExportAssembler assembler = Assembler(
            new RetryRequiredContributor());
        await using MemoryStream destination = new();

        DataRightsExportGenerationException exception =
            await Assert.ThrowsAsync<DataRightsExportGenerationException>(
                () => assembler.AssembleAsync(
                    Request([
                        new("staff", "profile", Guid.NewGuid(), 2)
                    ]),
                    destination,
                    CancellationToken.None));

        Assert.Equal("owner-retry-required", exception.Code);
        Assert.True(exception.IsRetryable);
    }

    [Fact]
    public async Task Unexpected_owner_exception_is_retryable_and_logs_no_payload()
    {
        RecordingLogger logger = new();
        DataRightsExportAssembler assembler = new(
            [new ThrowingContributor()],
            logger);
        Guid recordId = Guid.NewGuid();
        await using MemoryStream destination = new();

        DataRightsExportGenerationException exception =
            await Assert.ThrowsAsync<DataRightsExportGenerationException>(
                () => assembler.AssembleAsync(
                    Request([
                        new("staff", "profile", recordId, 2)
                    ]),
                    destination,
                    CancellationToken.None));

        Assert.Equal("owner-export-failed", exception.Code);
        Assert.True(exception.IsRetryable);
        string message = Assert.Single(logger.Messages);
        Assert.Contains("staff", message, StringComparison.Ordinal);
        Assert.Contains("InvalidOperationException", message, StringComparison.Ordinal);
        Assert.DoesNotContain(recordId.ToString(), message, StringComparison.Ordinal);
        Assert.DoesNotContain("private@example.test", message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Normal_owner_return_after_cancellation_stops_export_assembly()
    {
        using CancellationTokenSource source = new();
        CancelingContributor contributor = new(source);
        DataRightsExportAssembler assembler = Assembler(contributor);
        await using MemoryStream destination = new();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            assembler.AssembleAsync(
                Request([
                    new("staff", "profile", Guid.Parse(
                        "11111111-1111-1111-1111-111111111111"), 2),
                    new("staff", "profile", Guid.Parse(
                        "22222222-2222-2222-2222-222222222222"), 3)
                ]),
                destination,
                source.Token));

        Assert.Equal(1, contributor.InvocationCount);
    }

    private static DataRightsExportAssembler Assembler(
        params IDataRightsSubjectExportContributor[] contributors) =>
        new(contributors, NullLogger<DataRightsExportAssembler>.Instance);

    private static DataRightsExportGenerationRequest Request(
        IReadOnlyCollection<DataRightsSubjectCoordinate> subjects) => new(
            Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
            "tenant-a",
            Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"),
            DataRightsCaseType.StaffRights,
            PropertyId: null,
            DecisionRevision: 7,
            subjects,
            GeneratedAt,
            GeneratedAt.AddHours(24));

    private class SuccessfulContributor : IDataRightsSubjectExportContributor
    {
        public string OwnerKey => "staff";

        public IReadOnlyCollection<DataRightsCaseType> SupportedCaseTypes =>
            [DataRightsCaseType.StaffRights];

        public virtual DataRightsExportDescriptor Descriptor => new(
            "staff",
            "staff.catalog",
            CatalogSchemaVersion: 1,
            CatalogVersion: 2,
            "staff.export",
            ExportSchemaVersion: 1,
            ["name", "email"]);

        public virtual async Task<DataRightsSubjectExportResult> ExportAsync(
            DataRightsSubjectExportRequest request,
            IDataRightsExportSink sink,
            CancellationToken cancellationToken)
        {
            await sink.WriteAsync(
                new DataRightsExportRecord(
                    "profile",
                    request.Coordinate.RecordId,
                    request.Coordinate.RecordVersion,
                    [
                        new("name", JsonSerializer.SerializeToElement("Artem")),
                        new("email", JsonSerializer.SerializeToElement(
                            "user@example.test"))
                    ]),
                cancellationToken);
            return DataRightsSubjectExportResult.Success(1);
        }
    }

    private sealed class StaleContributor : SuccessfulContributor
    {
        public override async Task<DataRightsSubjectExportResult> ExportAsync(
            DataRightsSubjectExportRequest request,
            IDataRightsExportSink sink,
            CancellationToken cancellationToken)
        {
            _ = await base.ExportAsync(request, sink, cancellationToken);
            return DataRightsSubjectExportResult.Stale();
        }
    }

    private sealed class PureStaleContributor : SuccessfulContributor
    {
        public override Task<DataRightsSubjectExportResult> ExportAsync(
            DataRightsSubjectExportRequest request,
            IDataRightsExportSink sink,
            CancellationToken cancellationToken) =>
            Task.FromResult(DataRightsSubjectExportResult.Stale());
    }

    private sealed class RetryRequiredContributor : SuccessfulContributor
    {
        public override Task<DataRightsSubjectExportResult> ExportAsync(
            DataRightsSubjectExportRequest request,
            IDataRightsExportSink sink,
            CancellationToken cancellationToken) =>
            Task.FromResult(DataRightsSubjectExportResult.RetryRequired());
    }

    private sealed class ThrowingContributor : SuccessfulContributor
    {
        public override Task<DataRightsSubjectExportResult> ExportAsync(
            DataRightsSubjectExportRequest request,
            IDataRightsExportSink sink,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("private@example.test");
    }

    private sealed class CancelingContributor(CancellationTokenSource source)
        : SuccessfulContributor
    {
        public int InvocationCount { get; private set; }

        public override async Task<DataRightsSubjectExportResult> ExportAsync(
            DataRightsSubjectExportRequest request,
            IDataRightsExportSink sink,
            CancellationToken cancellationToken)
        {
            this.InvocationCount++;
            DataRightsSubjectExportResult result = await base.ExportAsync(
                request,
                sink,
                cancellationToken);
            source.Cancel();
            return result;
        }
    }

    private sealed class WideSchemaContributor : SuccessfulContributor
    {
        public override DataRightsExportDescriptor Descriptor => new(
            "staff",
            "staff.catalog",
            CatalogSchemaVersion: 1,
            CatalogVersion: 2,
            "staff.export",
            ExportSchemaVersion: 1,
            [
                "name",
                "email",
                .. Enumerable.Range(
                        2,
                        DataRightsExportLimits.MaxFieldsPerRecord - 1)
                    .Select(index => $"field-{index}")
            ]);
    }

    private sealed class MalformedGuestContributor
        : IDataRightsSubjectExportContributor
    {
        public string OwnerKey => "guests";
        public IReadOnlyCollection<DataRightsCaseType> SupportedCaseTypes =>
            [DataRightsCaseType.GuestRights];
        public DataRightsExportDescriptor Descriptor => new(
            "guests",
            new string('x', DataRightsExportLimits.SchemaIdentifierMaxLength + 1),
            1,
            1,
            "guests.export",
            1,
            ["name"]);

        public Task<DataRightsSubjectExportResult> ExportAsync(
            DataRightsSubjectExportRequest request,
            IDataRightsExportSink sink,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class OversizedGuestContributor
        : IDataRightsSubjectExportContributor
    {
        public string OwnerKey => "guests";
        public IReadOnlyCollection<DataRightsCaseType> SupportedCaseTypes =>
            [DataRightsCaseType.GuestRights];
        public DataRightsExportDescriptor Descriptor => new(
            "guests",
            "guests.catalog",
            1,
            1,
            "guests.export",
            1,
            Enumerable.Range(0, DataRightsExportLimits.MaxFieldsPerSchema + 1)
                .Select(index => $"field-{index}")
                .ToArray());

        public Task<DataRightsSubjectExportResult> ExportAsync(
            DataRightsSubjectExportRequest request,
            IDataRightsExportSink sink,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class RecordingLogger : ILogger<DataRightsExportAssembler>
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            this.Messages.Add(formatter(state, exception));
    }
}
