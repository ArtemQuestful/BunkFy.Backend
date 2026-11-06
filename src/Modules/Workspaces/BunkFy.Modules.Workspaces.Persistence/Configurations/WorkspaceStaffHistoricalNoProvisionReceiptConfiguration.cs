namespace BunkFy.Modules.Workspaces.Persistence.Configurations;

using BunkFy.Modules.Workspaces.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

internal sealed class WorkspaceStaffHistoricalNoProvisionReceiptConfiguration
    : IEntityTypeConfiguration<WorkspaceStaffHistoricalNoProvisionReceipt>
{
    public void Configure(
        EntityTypeBuilder<WorkspaceStaffHistoricalNoProvisionReceipt> builder)
    {
        builder.ToTable(
            "staff_historical_no_provision_receipts",
            table =>
            {
                table.HasCheckConstraint(
                    "CK_ws_hist_no_prov_contract",
                    $"\"ContractVersion\" = " +
                    $"{WorkspaceStaffHistoricalNoProvisionReceipt.CurrentContractVersion}");
                table.HasCheckConstraint(
                    "CK_ws_hist_no_prov_versions",
                    "\"ExpectedApplicationVersion\" >= 1 AND " +
                    "\"ResultApplicationVersion\" >= " +
                    "\"ExpectedApplicationVersion\" AND " +
                    "\"ResultApplicationVersion\" <= " +
                    "\"ExpectedApplicationVersion\" + 1 AND " +
                    "\"OrganizationsScopeRevision\" >= 0 AND " +
                    "\"OrganizationsSourceVersion\" >= 1");
                table.HasCheckConstraint(
                    "CK_ws_hist_no_prov_transition",
                    "((\"ExpectedApplicationStatus\" IN (5, 7, 8, 9, 10) " +
                    "AND \"ResultApplicationStatus\" = " +
                    "\"ExpectedApplicationStatus\") OR " +
                    "(\"ExpectedApplicationStatus\" IN (1, 2, 3, 4, 6) " +
                    "AND \"ResultApplicationStatus\" = 8 AND " +
                    "\"ResultApplicationVersion\" = " +
                    "\"ExpectedApplicationVersion\" + 1))");
                table.HasCheckConstraint(
                    "CK_ws_hist_no_prov_authority",
                    "((\"SourceKind\" = 1 AND " +
                    "\"OrganizationsSourceStatus\" IN (3, 4, 5)) OR " +
                    "(\"SourceKind\" = 2 AND " +
                    "\"OrganizationsSourceStatus\" IN (7, 8, 9)))");
                table.HasCheckConstraint(
                    "CK_ws_hist_no_prov_hashes",
                    "\"StaffEvidenceSha256\" ~ '^[0-9a-f]{64}$' AND " +
                    "\"ExternalEvidenceSha256\" ~ '^[0-9a-f]{64}$' AND " +
                    "\"CanonicalSha256\" ~ '^[0-9a-f]{64}$'");
                table.HasCheckConstraint(
                    "CK_ws_hist_no_prov_identifiers",
                    "\"Id\" <> '00000000-0000-0000-0000-000000000000'::uuid AND " +
                    "\"OperationId\" <> " +
                    "'00000000-0000-0000-0000-000000000000'::uuid AND " +
                    "\"ApplicationId\" <> " +
                    "'00000000-0000-0000-0000-000000000000'::uuid AND " +
                    "\"SourceId\" <> " +
                    "'00000000-0000-0000-0000-000000000000'::uuid AND " +
                    "\"ExternalEvidenceManifestId\" <> " +
                    "'00000000-0000-0000-0000-000000000000'::uuid");
                table.HasCheckConstraint(
                    "CK_ws_hist_no_prov_reviewer",
                    "char_length(\"ReviewerId\") BETWEEN 1 AND " +
                    $"{WorkspaceStaffHistoricalNoProvisionReceipt.ReviewerIdMaxLength} AND " +
                    "\"ReviewerId\" = btrim(\"ReviewerId\") AND " +
                    "\"ReviewerId\" !~ '[[:cntrl:]]'");
            });
        builder.HasKey(receipt => receipt.Id);
        builder.HasAlternateKey(receipt => new
        {
            receipt.ScopeId,
            receipt.Id
        });
        builder.Property(receipt => receipt.ScopeId)
            .HasMaxLength(128)
            .IsRequired();
        builder.Property(receipt => receipt.ContractVersion).IsRequired();
        builder.Property(receipt => receipt.SourceKind)
            .HasConversion<int>()
            .IsRequired();
        builder.Property(receipt => receipt.ExpectedApplicationStatus)
            .HasConversion<int>()
            .IsRequired();
        builder.Property(receipt => receipt.ResultApplicationStatus)
            .HasConversion<int>()
            .IsRequired();
        builder.Property(receipt => receipt.OrganizationsSourceStatus)
            .HasConversion<int>()
            .IsRequired();
        builder.Property(receipt => receipt.StaffEvidenceSha256)
            .HasMaxLength(
                WorkspaceStaffHistoricalNoProvisionReceipt.Sha256Length)
            .IsFixedLength()
            .IsRequired();
        builder.Property(receipt => receipt.ExternalEvidenceSha256)
            .HasMaxLength(
                WorkspaceStaffHistoricalNoProvisionReceipt.Sha256Length)
            .IsFixedLength()
            .IsRequired();
        builder.Property(receipt => receipt.CanonicalSha256)
            .HasMaxLength(
                WorkspaceStaffHistoricalNoProvisionReceipt.Sha256Length)
            .IsFixedLength()
            .IsRequired();
        builder.Property(receipt => receipt.ReviewerId)
            .HasMaxLength(
                WorkspaceStaffHistoricalNoProvisionReceipt
                    .ReviewerIdMaxLength)
            .IsRequired();
        builder.HasIndex(receipt => new
        {
            receipt.ScopeId,
            receipt.OperationId
        }).IsUnique();
        builder.HasIndex(receipt => new
        {
            receipt.ScopeId,
            receipt.ApplicationId
        }).IsUnique();
        builder.HasIndex(receipt => new
        {
            receipt.ScopeId,
            receipt.SourceKind,
            receipt.SourceId
        });
        builder.HasIndex(receipt => new
        {
            receipt.ScopeId,
            receipt.ExternalEvidenceManifestId
        });
        builder.Ignore(receipt => receipt.DomainEvents);
    }
}
