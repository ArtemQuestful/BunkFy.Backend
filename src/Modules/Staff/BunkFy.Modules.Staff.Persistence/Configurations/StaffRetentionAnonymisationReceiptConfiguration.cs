namespace BunkFy.Modules.Staff.Persistence.Configurations;

using BunkFy.Modules.Staff.Domain.Aggregates;
using BunkFy.Modules.Staff.Domain.Retention;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

internal sealed class StaffRetentionAnonymisationReceiptConfiguration
    : IEntityTypeConfiguration<StaffRetentionAnonymisationReceipt>
{
    private const string EmptyGuid =
        "00000000-0000-0000-0000-000000000000";

    public void Configure(
        EntityTypeBuilder<StaffRetentionAnonymisationReceipt> builder)
    {
        builder.ToTable(
            "staff_retention_anonymisation_receipts",
            table =>
            {
                table.HasCheckConstraint(
                    "CK_staff_retention_receipts_coordinates",
                    $"\"Id\" <> '{EmptyGuid}' AND " +
                    $"\"ExecutionId\" <> '{EmptyGuid}' AND " +
                    $"\"StaffMemberId\" <> '{EmptyGuid}' AND " +
                    $"\"EventId\" <> '{EmptyGuid}' AND " +
                    "trim(\"ScopeId\") <> ''");
                table.HasCheckConstraint(
                    "CK_staff_retention_receipts_contract",
                    $"\"ContractVersion\" = {StaffRetentionAnonymisationReceipt.CurrentContractVersion}");
                table.HasCheckConstraint(
                    "CK_staff_retention_receipts_actor",
                    $"\"ActorId\" = '{StaffRetentionAnonymisationReceipt.SystemActorId}'");
                table.HasCheckConstraint(
                    "CK_staff_retention_receipts_versions",
                    "\"SelectedStaffVersion\" >= 1 AND " +
                    "\"ResultingStaffVersion\" = \"SelectedStaffVersion\" + 1 AND " +
                    "\"SelectedOperationLockRevision\" >= 1 AND " +
                    "\"ResultingOperationLockRevision\" = \"SelectedOperationLockRevision\" + 1");
                table.HasCheckConstraint(
                    "CK_staff_retention_receipts_digests",
                    $"char_length(\"PolicyEvidenceSha256\") = {StaffRetentionAnonymisationReceipt.Sha256Length} AND " +
                    "\"PolicyEvidenceSha256\" ~ '^[0-9a-f]+$' AND " +
                    $"char_length(\"CanonicalSha256\") = {StaffRetentionAnonymisationReceipt.Sha256Length} AND " +
                    "\"CanonicalSha256\" ~ '^[0-9a-f]+$'");
                table.HasCheckConstraint(
                    "CK_staff_retention_receipts_timestamps",
                    "\"DepartedAtUtc\" > " +
                    "TIMESTAMPTZ '0001-01-01 00:00:00+00' AND " +
                    "\"RetentionDeadlineUtc\" >= \"DepartedAtUtc\" AND " +
                    "\"CompletedAtUtc\" >= \"RetentionDeadlineUtc\"");
            });
        builder.HasKey(receipt => receipt.Id);
        builder.Property(receipt => receipt.ScopeId)
            .HasMaxLength(128)
            .IsRequired();
        builder.Property(receipt => receipt.PolicyEvidenceSha256)
            .HasMaxLength(
                StaffRetentionAnonymisationReceipt.Sha256Length)
            .IsFixedLength()
            .IsRequired();
        builder.Property(receipt => receipt.CanonicalSha256)
            .HasMaxLength(
                StaffRetentionAnonymisationReceipt.Sha256Length)
            .IsFixedLength()
            .IsRequired();
        builder.Property(receipt => receipt.ActorId)
            .HasMaxLength(StaffMember.ActorIdMaxLength)
            .IsRequired();
        builder.HasIndex(receipt => new
        {
            receipt.ScopeId,
            receipt.StaffMemberId
        })
            .HasDatabaseName(
                "UX_staff_retention_receipts_staff_member")
            .IsUnique();
        builder.HasIndex(receipt => new
        {
            receipt.ScopeId,
            receipt.ExecutionId
        }).HasDatabaseName(
            "IX_staff_retention_receipts_execution");
        builder.HasIndex(receipt => new
        {
            receipt.ScopeId,
            receipt.EventId
        })
            .HasDatabaseName(
                "UX_staff_retention_receipts_event")
            .IsUnique();
        builder.HasOne<StaffRetentionExecution>()
            .WithMany()
            .HasForeignKey(receipt => new
            {
                receipt.ScopeId,
                receipt.ExecutionId
            })
            .HasPrincipalKey(execution => new
            {
                execution.ScopeId,
                execution.Id
            })
            .HasConstraintName(
                "FK_staff_retention_receipts_execution")
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<StaffMember>()
            .WithOne()
            .HasForeignKey<StaffRetentionAnonymisationReceipt>(
                receipt => new
                {
                    receipt.ScopeId,
                    receipt.StaffMemberId
                })
            .HasPrincipalKey<StaffMember>(
                member => new
                {
                    member.ScopeId,
                    member.Id
                })
            .HasConstraintName(
                "FK_staff_retention_receipts_staff_member")
            .OnDelete(DeleteBehavior.Restrict);
        builder.Ignore(receipt => receipt.DomainEvents);
    }
}
