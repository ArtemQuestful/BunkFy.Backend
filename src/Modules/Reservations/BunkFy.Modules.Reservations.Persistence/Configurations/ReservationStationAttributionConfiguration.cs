namespace BunkFy.Modules.Reservations.Persistence.Configurations;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

internal sealed class ReservationStationAttributionConfiguration : IEntityTypeConfiguration<ReservationStationAttribution>
{
    private static readonly string IdentityConstraint = string.Join(" AND ",
        new[] { "ReservationId", "OperationId", "StationId", "BrowserSessionId", "StaffMemberId", "ActorSessionId" }
            .Select(column => $"\"{column}\" <> '00000000-0000-0000-0000-000000000000'"));

    public void Configure(EntityTypeBuilder<ReservationStationAttribution> builder)
    {
        builder.ToTable("station_attributions", table =>
        {
            table.HasCheckConstraint("CK_station_attribution_versions", "\"Generation\" > 0 AND \"ResultingVersion\" > 0");
            table.HasCheckConstraint("CK_station_attribution_authority", "\"Authority\" IN (1, 2)");
            table.HasCheckConstraint("CK_station_attribution_identity", IdentityConstraint);
            table.HasCheckConstraint("CK_station_attribution_job_grant", "\"JobGrantRevision\" IS NULL OR (\"Authority\" = 2 AND \"JobGrantRevision\" > 0)");
        });
        builder.HasKey(x => new { x.ScopeId, x.ReservationId, x.OperationId });
        builder.Property(x => x.ScopeId).HasMaxLength(128).IsRequired();
        builder.Property(x => x.Authority).HasConversion<int>().IsRequired();
        builder.HasOne<ReservationManagementOperation>().WithMany()
            .HasForeignKey(x => new { x.ScopeId, x.ReservationId, x.OperationId })
            .HasPrincipalKey(x => new { x.ScopeId, x.ReservationId, x.Id }).OnDelete(DeleteBehavior.Cascade);
    }
}
