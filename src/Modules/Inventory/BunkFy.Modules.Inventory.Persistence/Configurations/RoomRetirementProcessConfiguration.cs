namespace BunkFy.Modules.Inventory.Persistence.Configurations;

using BunkFy.Modules.Inventory.Domain.Aggregates;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

internal sealed class RoomRetirementProcessConfiguration : IEntityTypeConfiguration<RoomRetirementProcess>
{
    private const string EmptyGuid =
        "00000000-0000-0000-0000-000000000000";

    public void Configure(EntityTypeBuilder<RoomRetirementProcess> builder)
    {
        builder.ToTable("room_retirements", table =>
        {
            table.HasCheckConstraint(
                "CK_room_retirements_coordinates",
                $"\"Id\" <> '{EmptyGuid}' AND \"PropertyId\" <> '{EmptyGuid}' AND " +
                $"\"RoomId\" <> '{EmptyGuid}' AND length(trim(\"ScopeId\")) > 0");
            table.HasCheckConstraint(
                "CK_room_retirements_request",
                "length(trim(\"Reason\")) > 0 AND length(trim(\"RequestedBy\")) > 0");
            table.HasCheckConstraint(
                "CK_room_retirements_lifecycle",
                "(\"State\" = 1 AND \"Version\" = 1 AND " +
                "\"RejectionReasonCode\" IS NULL AND \"UpdatedAtUtc\" IS NULL AND " +
                "\"CompletedAtUtc\" IS NULL AND \"CancellationReason\" IS NULL AND " +
                "\"CanceledBy\" IS NULL AND \"CanceledAtUtc\" IS NULL) OR " +
                "(\"State\" = 2 AND \"Version\" >= 2 AND " +
                "\"RejectionReasonCode\" IS NULL AND \"UpdatedAtUtc\" IS NOT NULL AND " +
                "\"UpdatedAtUtc\" >= \"CreatedAtUtc\" AND " +
                "\"CompletedAtUtc\" IS NULL AND \"CancellationReason\" IS NULL AND " +
                "\"CanceledBy\" IS NULL AND \"CanceledAtUtc\" IS NULL) OR " +
                "(\"State\" = 3 AND \"Version\" >= 3 AND " +
                "\"RejectionReasonCode\" IS NULL AND \"UpdatedAtUtc\" IS NOT NULL AND " +
                "\"UpdatedAtUtc\" >= \"CreatedAtUtc\" AND " +
                "\"CompletedAtUtc\" IS NULL AND \"CancellationReason\" IS NULL AND " +
                "\"CanceledBy\" IS NULL AND \"CanceledAtUtc\" IS NULL) OR " +
                "(\"State\" = 4 AND \"Version\" >= 3 AND " +
                "\"RejectionReasonCode\" IS NULL AND \"UpdatedAtUtc\" IS NOT NULL AND " +
                "\"UpdatedAtUtc\" >= \"CreatedAtUtc\" AND \"CompletedAtUtc\" IS NOT NULL AND " +
                "\"CompletedAtUtc\" = \"UpdatedAtUtc\" AND \"CancellationReason\" IS NULL AND " +
                "\"CanceledBy\" IS NULL AND \"CanceledAtUtc\" IS NULL) OR " +
                "(\"State\" = 5 AND \"Version\" >= 3 AND " +
                "\"RejectionReasonCode\" IS NOT NULL AND \"RejectionReasonCode\" > 0 AND " +
                "\"UpdatedAtUtc\" IS NOT NULL AND \"UpdatedAtUtc\" >= \"CreatedAtUtc\" AND " +
                "\"CompletedAtUtc\" IS NULL AND \"CancellationReason\" IS NULL AND " +
                "\"CanceledBy\" IS NULL AND \"CanceledAtUtc\" IS NULL) OR " +
                "(\"State\" = 6 AND \"Version\" = 2 AND " +
                "\"RejectionReasonCode\" IS NULL AND \"CompletedAtUtc\" IS NULL AND " +
                "\"CancellationReason\" IS NOT NULL AND \"CanceledBy\" IS NOT NULL AND " +
                "\"UpdatedAtUtc\" IS NOT NULL AND \"CanceledAtUtc\" IS NOT NULL AND " +
                "length(trim(\"CancellationReason\")) > 0 AND length(trim(\"CanceledBy\")) > 0 AND " +
                "\"CanceledAtUtc\" = \"UpdatedAtUtc\" AND \"CanceledAtUtc\" >= \"CreatedAtUtc\")");
        });
        builder.HasKey(process => process.Id);
        builder.Property(process => process.ScopeId).HasMaxLength(128).IsRequired();
        builder.Property(process => process.Reason).HasMaxLength(RoomRetirementProcess.ReasonMaxLength).IsRequired();
        builder.Property(process => process.RequestedBy).HasMaxLength(RoomRetirementProcess.ActorIdMaxLength).IsRequired();
        builder.Property(process => process.CancellationReason).HasMaxLength(RoomRetirementProcess.ReasonMaxLength);
        builder.Property(process => process.CanceledBy).HasMaxLength(RoomRetirementProcess.ActorIdMaxLength);
        builder.Property(process => process.State).HasConversion<int>().IsRequired();
        builder.Property(process => process.Version).IsConcurrencyToken().IsRequired();
        builder.HasIndex(process => new { process.ScopeId, process.RoomId, process.State });
        builder.HasIndex(process => new { process.ScopeId, process.PropertyId, process.State });
        builder.HasIndex(process => new
        {
            process.ScopeId,
            process.RoomId
        })
            .IsUnique()
            .HasFilter("\"State\" IN (1, 2, 3, 5)")
            .HasDatabaseName(
                "UX_room_retirements_ScopeId_RoomId_active");
        builder.Ignore(process => process.DomainEvents);
    }
}
