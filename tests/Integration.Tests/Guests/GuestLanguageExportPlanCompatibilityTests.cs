namespace Integration.Tests;

using System.Security.Cryptography;
using System.Text;
using BunkFy.Modules.DataRights.Application.Handlers;
using BunkFy.Modules.DataRights.Application.Models;
using BunkFy.Modules.DataRights.Contracts;
using BunkFy.Modules.Guests.Contracts;
using Xunit;

public sealed class GuestLanguageExportPlanCompatibilityTests
{
    [Fact]
    public async Task Valid_old_guest_export_plan_is_rejected_by_current_owner_catalog_before_output()
    {
        Assert.Equal(6, GuestsTenantTerminationMetadata.CatalogVersion);
        Assert.Equal(17, GuestsTenantTerminationMetadata.PersonalDataCatalogVersion);
        Assert.Equal(5, GuestsTenantTerminationMetadata.ExportSchemaVersion);
        string oldManifest = GuestsTenantTerminationMetadata.CatalogManifest
            .Replace("catalog=6|", "catalog=5|", StringComparison.Ordinal)
            .Replace("personal-data-catalog=17|", "personal-data-catalog=16|", StringComparison.Ordinal)
            .Replace("export-schema=guests.tenant-termination-export:5|",
                "export-schema=guests.tenant-termination-export:4|", StringComparison.Ordinal);
        string oldSha = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(oldManifest)));
        Assert.NotEqual(oldSha, GuestsTenantTerminationMetadata.CatalogSha256);
        Owner reservations = new("reservations", 1, new string('b', 64), []);
        Owner oldGuest = new("guests", 5, oldSha, ["reservations"]);
        Owner currentGuest = new("guests", GuestsTenantTerminationMetadata.CatalogVersion,
            GuestsTenantTerminationMetadata.CatalogSha256, ["reservations"]);
        DateTimeOffset now = new(2026, 9, 13, 0, 0, 0, TimeSpan.Zero);
        TenantTerminationExportFragmentAssemblyRequest request = new(
            "tenant-a", Guid.NewGuid(), Guid.NewGuid(), 1, 1, 2, Guid.NewGuid(), 1,
            new string('0', 64), new string('a', 64), "system:export", now,
            now.AddMinutes(1), now.AddHours(1),
            new("guests", Guid.NewGuid(), Guid.NewGuid(), 1, 5, oldSha),
            [Entry(reservations), Entry(oldGuest)]);
        request = request with
        {
            FrozenRevisionSha256 = TenantTerminationExportFragmentAssembler.ComputeFrozenRevisionSha256(request)
        };
        Assert.Equal(request.FrozenRevisionSha256,
            TenantTerminationExportFragmentAssembler.ComputeFrozenRevisionSha256(request));
        TenantTerminationExportFragmentAssembler assembler = new(
            [reservations, currentGuest], [reservations, currentGuest]);
        await using MemoryStream destination = new();

        DataRightsExportGenerationException error =
            await Assert.ThrowsAsync<DataRightsExportGenerationException>(() =>
                assembler.AssembleAsync(request, destination, CancellationToken.None));

        Assert.Equal("tenant-export-owner-catalog-invalid", error.Code);
        Assert.Equal(0, currentGuest.ExportCalls);
        Assert.Equal(0, reservations.ExportCalls);
        Assert.Equal(0, destination.Length);
        TenantTerminationExportFragmentAssemblyRequest replanned = request with
        {
            FrozenOwners = [Entry(reservations), Entry(currentGuest)],
            OwnerWork = request.OwnerWork with
            {
                CatalogVersion = GuestsTenantTerminationMetadata.CatalogVersion,
                CatalogSha256 = GuestsTenantTerminationMetadata.CatalogSha256
            }
        };
        Assert.NotEqual(request.FrozenRevisionSha256,
            TenantTerminationExportFragmentAssembler.ComputeFrozenRevisionSha256(replanned));
    }

    private static TenantTerminationExportOwnerCatalogEntry Entry(Owner owner) => new(
        owner.Descriptor.OwnerKey, owner.Descriptor.ContractVersion,
        owner.Descriptor.CatalogVersion, owner.Descriptor.CatalogSha256);

    private sealed class Owner(string key, int version, string sha, IReadOnlyCollection<string> dependencies)
        : ITenantTerminationContributor, ITenantTerminationExportContributor
    {
        public int ExportCalls { get; private set; }
        public TenantTerminationContributorDescriptor Descriptor { get; } = new(
            key, TenantTerminationContract.CurrentVersion,
            [new(TenantTerminationContributionPhase.Export, dependencies)], true, version, sha);
        public DataRightsExportDescriptor ExportDescriptor { get; } = new(
            key, $"{key}.tenant-portability", 1, version, $"{key}.tenant-termination-export",
            key == "guests" ? GuestsTenantTerminationMetadata.ExportSchemaVersion : 1,
            key == "guests" ? GuestsTenantTerminationMetadata.ExportFieldIds : ["reservation.id"]);

        public Task<TenantTerminationContributionResult> ExecuteAsync(
            TenantTerminationContributionRequest request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Not part of the export gate test.");

        public Task<TenantTerminationContributionResult> ExportAsync(
            TenantTerminationExportRequest request, IDataRightsExportSink sink, CancellationToken cancellationToken)
        {
            this.ExportCalls++;
            throw new InvalidOperationException("A stale plan must not reach owner output.");
        }
    }
}
