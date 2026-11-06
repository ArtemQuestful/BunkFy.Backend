namespace BunkFy.Modules.Ingestion.Persistence.Configurations;

using BunkFy.Modules.Ingestion.Domain.Connections;
using BunkFy.Modules.Ingestion.Domain.Reservations;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

internal sealed class ReservationSourceLinkConfiguration : IEntityTypeConfiguration<ReservationSourceLink>
{
    private const string EmptyGuid =
        "00000000-0000-0000-0000-000000000000";

    public void Configure(EntityTypeBuilder<ReservationSourceLink> builder)
    {
        builder.ToTable("reservation_source_links", table =>
        {
            table.HasCheckConstraint(
                "CK_reservation_source_links_coordinates",
                $"\"Id\" <> '{EmptyGuid}' AND \"PropertyId\" <> '{EmptyGuid}' AND " +
                $"\"ConnectionId\" <> '{EmptyGuid}' AND trim(\"ScopeId\") <> '' AND " +
                "trim(\"SourceSystem\") <> '' AND trim(\"SourceReference\") <> '' AND " +
                $"(\"ReservationId\" IS NULL OR \"ReservationId\" <> '{EmptyGuid}') AND " +
                $"(\"ActiveProductOperationId\" IS NULL OR \"ActiveProductOperationId\" <> '{EmptyGuid}') AND " +
                $"(\"DeferredReceiptId\" IS NULL OR \"DeferredReceiptId\" <> '{EmptyGuid}')");
            table.HasCheckConstraint(
                "CK_reservation_source_links_observation_shape",
                $"((\"LastObservedReceiptId\" = '{EmptyGuid}' AND " +
                "trim(\"LastObservedContentHash\") = '' AND \"LastObservedSourceRevision\" IS NULL AND " +
                "\"LastObservedSourceSequence\" IS NULL AND \"LastObservedSourceUpdatedAtUtc\" IS NULL) OR " +
                $"(\"LastObservedReceiptId\" <> '{EmptyGuid}' AND " +
                "trim(\"LastObservedContentHash\") ~ '^[0-9a-f]{64}$' AND " +
                "(\"LastObservedSourceRevision\" IS NULL OR trim(\"LastObservedSourceRevision\") <> '') AND " +
                "(\"LastObservedSourceSequence\" IS NULL OR \"LastObservedSourceSequence\" >= 0)))");
            table.HasCheckConstraint(
                "CK_reservation_source_links_applied_shape",
                "((\"LastAppliedReceiptId\" IS NULL AND \"LastAppliedSourceRevision\" IS NULL AND " +
                "\"LastAppliedSourceSequence\" IS NULL AND \"LastAppliedReservationDetailsRevision\" IS NULL AND " +
                "\"LastAppliedOperationalBaseline\" IS NULL AND \"LastProductOperationId\" IS NULL) OR " +
                $"(\"LastAppliedReceiptId\" IS NOT NULL AND \"LastAppliedReceiptId\" <> '{EmptyGuid}' AND " +
                $"\"LastProductOperationId\" IS NOT NULL AND \"LastProductOperationId\" <> '{EmptyGuid}' AND " +
                "(\"LastAppliedSourceRevision\" IS NULL OR trim(\"LastAppliedSourceRevision\") <> '') AND " +
                "(\"LastAppliedSourceSequence\" IS NULL OR \"LastAppliedSourceSequence\" >= 0) AND " +
                "\"LastAppliedReservationDetailsRevision\" IS NOT NULL AND " +
                "\"LastAppliedReservationDetailsRevision\" > 0 AND " +
                "(\"LastAppliedOperationalBaseline\" IS NULL OR trim(\"LastAppliedOperationalBaseline\") <> '')))");
            table.HasCheckConstraint(
                "CK_reservation_source_links_lifecycle",
                $"\"State\" IN ({(int)ReservationSourceLinkState.AwaitingCreate}, " +
                $"{(int)ReservationSourceLinkState.Linked}, {(int)ReservationSourceLinkState.CancellationPending}, " +
                $"{(int)ReservationSourceLinkState.Cancelled}, {(int)ReservationSourceLinkState.Anonymised}) AND " +
                "\"Version\" >= 1 AND ((\"Version\" = 1 AND \"UpdatedAtUtc\" IS NULL) OR " +
                "(\"Version\" >= 2 AND \"UpdatedAtUtc\" IS NOT NULL AND " +
                "\"UpdatedAtUtc\" >= \"CreatedAtUtc\")) AND " +
                $"((\"State\" = {(int)ReservationSourceLinkState.AwaitingCreate} AND \"ReservationId\" IS NULL AND " +
                "\"LastAppliedReceiptId\" IS NULL AND " +
                "\"AnonymisedAtUtc\" IS NULL) OR " +
                $"(\"State\" = {(int)ReservationSourceLinkState.Linked} AND \"ReservationId\" IS NOT NULL AND " +
                "\"LastAppliedReceiptId\" IS NOT NULL AND \"LastAppliedOperationalBaseline\" IS NOT NULL AND " +
                "trim(\"LastAppliedOperationalBaseline\") <> '' AND " +
                "\"AnonymisedAtUtc\" IS NULL) OR " +
                $"(\"State\" = {(int)ReservationSourceLinkState.CancellationPending} AND " +
                "\"ReservationId\" IS NOT NULL AND \"ActiveProductOperationId\" IS NOT NULL AND " +
                "\"LastAppliedReceiptId\" IS NOT NULL AND \"LastAppliedOperationalBaseline\" IS NULL AND " +
                "\"AnonymisedAtUtc\" IS NULL) OR " +
                $"(\"State\" = {(int)ReservationSourceLinkState.Cancelled} AND \"ReservationId\" IS NOT NULL AND " +
                "\"ActiveProductOperationId\" IS NULL AND \"LastAppliedReceiptId\" IS NOT NULL AND " +
                "\"LastAppliedOperationalBaseline\" IS NULL AND \"AnonymisedAtUtc\" IS NULL) OR " +
                $"(\"State\" = {(int)ReservationSourceLinkState.Anonymised} AND " +
                "\"LastAppliedReceiptId\" IS NOT NULL AND \"AnonymisedAtUtc\" IS NOT NULL))");
            table.HasCheckConstraint(
                "CK_reservation_source_links_anonymised_shape",
                $"\"AnonymisedAtUtc\" IS NULL OR (\"State\" = {(int)ReservationSourceLinkState.Anonymised} AND " +
                "\"SourceReference\" = 'anonymised:' || replace(lower(CAST(\"Id\" AS text)), '-', '') AND " +
                "\"ReservationId\" IS NULL AND \"LastObservedReceiptId\" <> " +
                $"'{EmptyGuid}' AND trim(\"LastObservedContentHash\") = '{new string('0', ReservationSourceLink.ContentHashLength)}' AND " +
                "\"LastObservedSourceRevision\" IS NULL AND \"LastAppliedSourceRevision\" IS NULL AND " +
                "\"LastAppliedOperationalBaseline\" IS NULL AND \"ActiveProductOperationId\" IS NULL AND " +
                "\"DeferredReceiptId\" IS NULL AND \"UpdatedAtUtc\" IS NOT NULL AND " +
                "\"UpdatedAtUtc\" = \"AnonymisedAtUtc\")");
        });
        builder.HasKey(link => link.Id);
        builder.HasAlternateKey(link => new { link.ScopeId, link.Id });
        builder.Property(link => link.ScopeId).HasMaxLength(128).IsRequired();
        builder.Property(link => link.SourceSystem).HasMaxLength(ReservationSourceLink.SourceSystemMaxLength).IsRequired();
        builder.Property(link => link.SourceReference).HasMaxLength(ReservationSourceLink.SourceReferenceMaxLength).IsRequired();
        builder.Property(link => link.LastObservedSourceRevision).HasMaxLength(ReservationSourceLink.SourceRevisionMaxLength);
        builder.Property(link => link.LastObservedContentHash).HasMaxLength(ReservationSourceLink.ContentHashLength).IsFixedLength().IsRequired();
        builder.Property(link => link.LastAppliedSourceRevision).HasMaxLength(ReservationSourceLink.SourceRevisionMaxLength);
        builder.Property(link => link.LastAppliedOperationalBaseline)
            .HasMaxLength(ReservationSourceLink.OperationalBaselineMaxLength);
        builder.Property(link => link.State).HasConversion<int>().IsRequired();
        builder.Property(link => link.Version).IsConcurrencyToken().IsRequired();
        builder.HasIndex(link => new { link.ScopeId, link.ConnectionId, link.SourceSystem, link.SourceReference }).IsUnique();
        builder.HasIndex(link => new { link.ScopeId, link.ReservationId });
        builder.HasIndex(link => new { link.ScopeId, link.ConnectionId, link.State, link.UpdatedAtUtc });
        builder.HasOne<AdapterConnection>()
            .WithMany()
            .HasForeignKey(link => new { link.ScopeId, link.ConnectionId })
            .HasPrincipalKey(connection => new { connection.ScopeId, connection.Id })
            .OnDelete(DeleteBehavior.Restrict);
        builder.Ignore(link => link.DomainEvents);
    }
}
