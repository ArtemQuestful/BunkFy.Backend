namespace BunkFy.Modules.DataRights.Tests.Application;

using System.Text;
using System.Text.Json;
using BunkFy.Modules.DataRights.Application.Handlers;
using BunkFy.Modules.DataRights.Application.Models;
using BunkFy.Modules.DataRights.Contracts;
using Xunit;

[Trait("Category", "Unit")]
public sealed class TenantTerminationExportFragmentAssemblerTests
{
    private const string PolicySha =
        "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string WorkspacesCatalogSha =
        "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private const string ReservationsCatalogSha =
        "cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc";
    private static readonly DateTimeOffset FrozenAt =
        new(2026, 7, 31, 10, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset GeneratedAt = FrozenAt.AddMinutes(5);

    [Fact]
    public async Task Fragment_is_deterministic_and_exports_only_selected_owner()
    {
        StubContributor workspaces = new(
            "workspaces",
            WorkspacesCatalogSha,
            [],
            proofRevision: 11,
            GeneratedAt.AddMinutes(1));
        StubContributor reservations = new(
            "reservations",
            ReservationsCatalogSha,
            ["workspaces"],
            proofRevision: 22,
            GeneratedAt.AddMinutes(2));
        TenantTerminationExportFragmentAssembler assembler = new(
            [reservations, workspaces],
            [reservations, workspaces]);
        TenantTerminationExportFragmentAssemblyRequest request = Request(
            OwnerWork(reservations),
            [CatalogEntry(reservations), CatalogEntry(workspaces)]);

        await using MemoryStream first = new();
        await using MemoryStream second = new();
        TenantTerminationExportFragmentAssemblyResult result =
            await assembler.AssembleAsync(
                request,
                first,
                CancellationToken.None);
        _ = await assembler.AssembleAsync(
            request,
            second,
            CancellationToken.None);

        Assert.Equal(first.ToArray(), second.ToArray());
        Assert.Equal(request.FrozenRevisionSha256, result.FrozenRevisionSha256);
        Assert.Equal("reservations", result.Owner.OwnerKey);
        Assert.Equal(1, result.Owner.RecordCount);
        Assert.Equal(22, result.Owner.SelectedProofRevision);
        Assert.Equal(0, workspaces.ExportCalls);
        Assert.Equal(2, reservations.ExportCalls);

        using JsonDocument document = JsonDocument.Parse(first.ToArray());
        JsonElement root = document.RootElement;
        Assert.Equal(
            "bunkfy.tenant-termination.export-fragment",
            root.GetProperty("format").GetString());
        Assert.Equal(
            request.FrozenRevisionSha256,
            root.GetProperty("frozenRevisionSha256").GetString());
        Assert.Equal(
            "reservations",
            root.GetProperty("owner").GetProperty("ownerKey").GetString());
        Assert.DoesNotContain(
            "tenant-a",
            Encoding.UTF8.GetString(first.ToArray()),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Empty_owner_fragment_preserves_revision_zero()
    {
        StubContributor organizations = new(
            "organizations",
            WorkspacesCatalogSha,
            [],
            proofRevision: 0,
            GeneratedAt.AddMinutes(1),
            writeRecord: false);
        TenantTerminationExportFragmentAssembler assembler = new(
            [organizations],
            [organizations]);
        TenantTerminationExportFragmentAssemblyRequest request = Request(
            OwnerWork(organizations),
            [CatalogEntry(organizations)]);

        await using MemoryStream output = new();
        TenantTerminationExportFragmentAssemblyResult result =
            await assembler.AssembleAsync(
                request,
                output,
                CancellationToken.None);

        Assert.Equal(0, result.Owner.RecordCount);
        Assert.Equal(0, result.Owner.SelectedProofRevision);
        Assert.Equal(0, result.Owner.ResultingProofRevision);
    }

    [Fact]
    public void Frozen_revision_is_order_independent_but_catalog_sensitive()
    {
        StubContributor workspaces = new(
            "workspaces",
            WorkspacesCatalogSha,
            [],
            11,
            GeneratedAt);
        StubContributor reservations = new(
            "reservations",
            ReservationsCatalogSha,
            ["workspaces"],
            22,
            GeneratedAt);
        TenantTerminationExportFragmentAssemblyRequest first = Request(
            OwnerWork(workspaces),
            [CatalogEntry(workspaces), CatalogEntry(reservations)]);
        TenantTerminationExportFragmentAssemblyRequest reversed = Request(
            OwnerWork(workspaces),
            [CatalogEntry(reservations), CatalogEntry(workspaces)]);
        TenantTerminationExportOwnerCatalogEntry changedOwner =
            CatalogEntry(reservations) with
            {
                CatalogVersion = 2,
                CatalogSha256 = WorkspacesCatalogSha
            };
        TenantTerminationExportFragmentAssemblyRequest changed = Request(
            OwnerWork(workspaces),
            [CatalogEntry(workspaces), changedOwner]);

        Assert.Equal(first.FrozenRevisionSha256, reversed.FrozenRevisionSha256);
        Assert.NotEqual(first.FrozenRevisionSha256, changed.FrozenRevisionSha256);
    }

    [Fact]
    public async Task Fragment_rejects_catalogue_drift_before_owner_output()
    {
        StubContributor workspaces = new(
            "workspaces",
            WorkspacesCatalogSha,
            [],
            11,
            GeneratedAt);
        TenantTerminationExportFragmentAssemblyRequest request = Request(
            OwnerWork(workspaces),
            [CatalogEntry(workspaces)]);
        TenantTerminationExportFragmentAssemblyRequest drifted = request with
        {
            FrozenOwners =
            [
                CatalogEntry(workspaces) with
                {
                    CatalogVersion = 2
                }
            ]
        };
        TenantTerminationExportFragmentAssembler assembler = new(
            [workspaces],
            [workspaces]);
        await using MemoryStream destination = new();

        DataRightsExportGenerationException exception =
            await Assert.ThrowsAsync<DataRightsExportGenerationException>(
                () => assembler.AssembleAsync(
                    drifted,
                    destination,
                    CancellationToken.None));

        Assert.Equal(
            "tenant-export-frozen-revision-invalid",
            exception.Code);
        Assert.Equal(0, workspaces.ExportCalls);
    }

    [Fact]
    public async Task Fragment_rejects_owner_revision_changed_during_export()
    {
        StubContributor workspaces = new(
            "workspaces",
            WorkspacesCatalogSha,
            [],
            proofRevision: 11,
            GeneratedAt,
            resultingProofRevision: 12);
        TenantTerminationExportFragmentAssembler assembler = new(
            [workspaces],
            [workspaces]);
        await using MemoryStream destination = new();

        DataRightsExportGenerationException exception =
            await Assert.ThrowsAsync<DataRightsExportGenerationException>(
                () => assembler.AssembleAsync(
                    Request(
                        OwnerWork(workspaces),
                        [CatalogEntry(workspaces)]),
                    destination,
                    CancellationToken.None));

        Assert.Equal("tenant-export-owner-result-invalid", exception.Code);
    }

    [Fact]
    public async Task Fragment_rejects_owner_result_at_exact_deadline()
    {
        StubContributor workspaces = new(
            "workspaces",
            WorkspacesCatalogSha,
            [],
            proofRevision: 11,
            GeneratedAt.AddHours(1));
        TenantTerminationExportFragmentAssembler assembler = new(
            [workspaces],
            [workspaces]);
        await using MemoryStream destination = new();

        DataRightsExportGenerationException exception =
            await Assert.ThrowsAsync<DataRightsExportGenerationException>(
                () => assembler.AssembleAsync(
                    Request(
                        OwnerWork(workspaces),
                        [CatalogEntry(workspaces)]),
                    destination,
                    CancellationToken.None));

        Assert.Equal("tenant-export-owner-result-invalid", exception.Code);
    }

    [Fact]
    public async Task Fragment_requires_exporter_for_every_frozen_owner()
    {
        StubContributor workspaces = new(
            "workspaces",
            WorkspacesCatalogSha,
            [],
            11,
            GeneratedAt);
        StubContributor reservations = new(
            "reservations",
            ReservationsCatalogSha,
            ["workspaces"],
            22,
            GeneratedAt);
        TenantTerminationExportFragmentAssembler assembler = new(
            [workspaces, reservations],
            [workspaces]);
        await using MemoryStream destination = new();

        DataRightsExportGenerationException exception =
            await Assert.ThrowsAsync<DataRightsExportGenerationException>(
                () => assembler.AssembleAsync(
                    Request(
                        OwnerWork(workspaces),
                        [CatalogEntry(workspaces), CatalogEntry(reservations)]),
                    destination,
                    CancellationToken.None));

        Assert.Equal("tenant-export-owner-unavailable", exception.Code);
    }

    private static TenantTerminationExportFragmentAssemblyRequest Request(
        TenantTerminationExportOwnerWork ownerWork,
        IReadOnlyCollection<TenantTerminationExportOwnerCatalogEntry>
            frozenOwners)
    {
        TenantTerminationExportFragmentAssemblyRequest request = new(
            "tenant-a",
            Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
            Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"),
            ApprovalRevision: 7,
            FreezeOperationRevision: 1,
            ExportOperationRevision: 2,
            Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc"),
            WorkspaceFenceRevision: 3,
            FrozenRevisionSha256: new string('0', 64),
            PolicySha,
            ExecutingActorId: "system:tenant-termination-export",
            FrozenAt,
            GeneratedAt,
            GeneratedAt.AddHours(1),
            ownerWork,
            frozenOwners);
        return request with
        {
            FrozenRevisionSha256 =
                TenantTerminationExportFragmentAssembler
                    .ComputeFrozenRevisionSha256(request)
        };
    }

    private static TenantTerminationExportOwnerCatalogEntry CatalogEntry(
        StubContributor contributor) =>
        new(
            contributor.Descriptor.OwnerKey,
            contributor.Descriptor.ContractVersion,
            contributor.Descriptor.CatalogVersion,
            contributor.Descriptor.CatalogSha256);

    private static TenantTerminationExportOwnerWork OwnerWork(
        StubContributor contributor)
    {
        byte marker = contributor.Descriptor.OwnerKey == "workspaces"
            ? (byte)1
            : (byte)2;
        return new TenantTerminationExportOwnerWork(
            contributor.Descriptor.OwnerKey,
            GuidFrom(marker, 1),
            GuidFrom(marker, 2),
            contributor.Descriptor.ContractVersion,
            contributor.Descriptor.CatalogVersion,
            contributor.Descriptor.CatalogSha256);
    }

    private static Guid GuidFrom(byte marker, byte suffix)
    {
        byte[] bytes = new byte[16];
        bytes[0] = marker;
        bytes[^1] = suffix;
        return new Guid(bytes);
    }

    private sealed class StubContributor(
        string ownerKey,
        string catalogSha256,
        IReadOnlyCollection<string> dependencies,
        long proofRevision,
        DateTimeOffset recordedAtUtc,
        long? resultingProofRevision = null,
        bool writeRecord = true)
        : ITenantTerminationContributor,
            ITenantTerminationExportContributor
    {
        public int ExportCalls { get; private set; }

        public TenantTerminationContributorDescriptor Descriptor { get; } =
            new(
                ownerKey,
                TenantTerminationContract.CurrentVersion,
                [
                    new(
                        TenantTerminationContributionPhase.Export,
                        dependencies)
                ],
                MandatoryForProduction: true,
                CatalogVersion: 1,
                catalogSha256);

        public DataRightsExportDescriptor ExportDescriptor { get; } = new(
            ownerKey,
            $"{ownerKey}.tenant-export-catalog",
            CatalogSchemaVersion: 1,
            CatalogVersion: 1,
            $"{ownerKey}.tenant-export",
            ExportSchemaVersion: 1,
            ["email", "name"]);

        public Task<TenantTerminationContributionResult> ExecuteAsync(
            TenantTerminationContributionRequest request,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException(
                "Export phases must use the export contributor contract.");

        public async Task<TenantTerminationContributionResult> ExportAsync(
            TenantTerminationExportRequest request,
            IDataRightsExportSink sink,
            CancellationToken cancellationToken)
        {
            this.ExportCalls++;
            Assert.Equal(
                TenantTerminationContributionPhase.Export,
                request.Contribution.Phase);
            Assert.Equal(
                request.Contribution.OperationRevision - 1,
                request.FreezeOperationRevision);
            if (writeRecord)
            {
                await sink.WriteAsync(
                    new DataRightsExportRecord(
                        "tenant-record",
                        GuidFrom(
                            ownerKey == "workspaces" ? (byte)1 : (byte)2,
                            3),
                        Math.Max(1, proofRevision),
                        [
                            new(
                                "name",
                                JsonSerializer.SerializeToElement("Example")),
                            new(
                                "email",
                                JsonSerializer.SerializeToElement(
                                    "user@example.test"))
                        ]),
                    cancellationToken);
            }
            return new TenantTerminationContributionResult(
                TenantTerminationContributionStatus.Completed,
                $"{ownerKey}.tenant-export.completed",
                AffectedCount: writeRecord ? 1 : 0,
                RetainedMinimumCount: 0,
                RemainingActiveCount: 0,
                HoldReviewAtUtc: null,
                SelectedProofRevision: proofRevision,
                ResultingProofRevision:
                    resultingProofRevision ?? proofRevision,
                CatalogVersion: 1,
                catalogSha256,
                recordedAtUtc);
        }
    }
}
