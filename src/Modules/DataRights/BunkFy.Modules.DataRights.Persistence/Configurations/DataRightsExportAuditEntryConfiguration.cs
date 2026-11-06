namespace BunkFy.Modules.DataRights.Persistence.Configurations;

using BunkFy.Modules.DataRights.Domain.Aggregates;
using BunkFy.Modules.DataRights.Domain.Entities;
using BunkFy.Modules.DataRights.Domain.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

internal sealed class DataRightsExportAuditEntryConfiguration
    : IEntityTypeConfiguration<DataRightsExportAuditEntry>
{
    public void Configure(EntityTypeBuilder<DataRightsExportAuditEntry> builder)
    {
        builder.ToTable("export_audit_entries", table =>
        {
            table.HasCheckConstraint(
                "CK_data_rights_export_audit_scope",
                $"(\"CaseKind\" = {(int)DataRightsCaseKind.GuestRights} AND " +
                "\"PropertyId\" IS NOT NULL) OR " +
                $"(\"CaseKind\" IN (" +
                $"{(int)DataRightsCaseKind.TenantTermination}, " +
                $"{(int)DataRightsCaseKind.StaffRights}) AND " +
                "\"PropertyId\" IS NULL)");
            table.HasCheckConstraint(
                "CK_data_rights_export_audit_action",
                $"\"Action\" BETWEEN " +
                $"{(int)DataRightsExportAuditAction.GenerationRequested} AND " +
                (int)DataRightsExportAuditAction.Deleted);
        });

        builder.HasKey(entry => entry.Id);
        builder.HasAlternateKey(entry => new { entry.ScopeId, entry.Id });
        builder.Property(entry => entry.Id).ValueGeneratedNever();
        builder.Property(entry => entry.ScopeId)
            .HasMaxLength(128)
            .IsRequired();
        builder.Property(entry => entry.ActorId)
            .HasMaxLength(DataRightsExportArtifact.ActorIdMaxLength)
            .IsRequired();
        builder.Property(entry => entry.OutcomeCode)
            .HasMaxLength(DataRightsExportAuditEntry.OutcomeCodeMaxLength)
            .IsRequired();
        builder.HasIndex(entry => new
        {
            entry.ScopeId,
            entry.ArtifactId,
            entry.OccurredAtUtc,
            entry.Id
        });
        builder.HasIndex(entry => new
        {
            entry.ScopeId,
            entry.CaseId,
            entry.OccurredAtUtc,
            entry.Id
        });
    }
}
