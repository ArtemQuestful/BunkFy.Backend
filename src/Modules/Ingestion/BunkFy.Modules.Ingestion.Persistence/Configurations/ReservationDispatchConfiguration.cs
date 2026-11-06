namespace BunkFy.Modules.Ingestion.Persistence.Configurations;

using BunkFy.Modules.Ingestion.Domain.Receipts;
using BunkFy.Modules.Ingestion.Domain.Reservations;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

internal sealed class ReservationDispatchConfiguration : IEntityTypeConfiguration<ReservationDispatch>
{
    private const string EmptyGuid =
        "00000000-0000-0000-0000-000000000000";

    public void Configure(EntityTypeBuilder<ReservationDispatch> builder)
    {
        builder.ToTable("reservation_dispatches", table =>
        {
            table.HasCheckConstraint(
                "CK_reservation_dispatches_coordinates",
                $"\"Id\" <> '{EmptyGuid}' AND \"SourceLinkId\" <> '{EmptyGuid}' AND " +
                $"\"TriggerId\" <> '{EmptyGuid}' AND \"ReceiptId\" <> '{EmptyGuid}' AND " +
                $"\"ConnectionId\" <> '{EmptyGuid}' AND \"PropertyId\" <> '{EmptyGuid}' AND " +
                "trim(\"ScopeId\") <> '' AND " +
                $"(\"ReservationId\" IS NULL OR \"ReservationId\" <> '{EmptyGuid}') AND " +
                "(\"SourceRevision\" IS NULL OR trim(\"SourceRevision\") <> '') AND " +
                "(\"SourceSequence\" IS NULL OR \"SourceSequence\" >= 0) AND " +
                "(\"ExpectedDetailsRevision\" IS NULL OR \"ExpectedDetailsRevision\" > 0) AND " +
                "(\"ResultDetailsRevision\" IS NULL OR \"ResultDetailsRevision\" > 0) AND " +
                "(\"ResultReservationVersion\" IS NULL OR \"ResultReservationVersion\" > 0) AND " +
                "(\"ErrorCode\" IS NULL OR trim(\"ErrorCode\") <> '')");
            table.HasCheckConstraint(
                "CK_reservation_dispatches_kind_shape",
                $"\"TriggerKind\" IN ({(int)ReservationDispatchTriggerKind.Observation}, " +
                $"{(int)ReservationDispatchTriggerKind.Proposal}) AND " +
                $"\"Kind\" IN ({(int)ReservationDispatchKind.Create}, " +
                $"{(int)ReservationDispatchKind.ChangeGuestDetails}, {(int)ReservationDispatchKind.Cancel}, " +
                $"{(int)ReservationDispatchKind.Amend}) AND " +
                $"((\"Kind\" = {(int)ReservationDispatchKind.Create} AND " +
                "\"ExpectedDetailsRevision\" IS NULL) OR " +
                $"(\"Kind\" IN ({(int)ReservationDispatchKind.ChangeGuestDetails}, " +
                $"{(int)ReservationDispatchKind.Cancel}, {(int)ReservationDispatchKind.Amend}) AND " +
                "\"ExpectedDetailsRevision\" IS NOT NULL AND \"ExpectedDetailsRevision\" > 0)) AND " +
                $"(\"Kind\" = {(int)ReservationDispatchKind.Create} OR \"ReservationId\" IS NOT NULL OR " +
                "\"AnonymisedAtUtc\" IS NOT NULL) AND " +
                $"NOT (\"State\" = {(int)ReservationDispatchState.Pending} AND " +
                $"\"Kind\" = {(int)ReservationDispatchKind.Create} AND \"ReservationId\" IS NOT NULL)");
            table.HasCheckConstraint(
                "CK_reservation_dispatches_lifecycle",
                $"((\"State\" = {(int)ReservationDispatchState.Pending} AND \"Version\" = 1 AND " +
                "\"CompletedAtUtc\" IS NULL AND \"ResultDetailsRevision\" IS NULL AND " +
                "\"ResultReservationVersion\" IS NULL AND \"ErrorCode\" IS NULL AND " +
                "\"AnonymisedAtUtc\" IS NULL) OR " +
                $"(\"State\" = {(int)ReservationDispatchState.Accepted} AND " +
                $"\"Kind\" = {(int)ReservationDispatchKind.Cancel} AND \"Version\" >= 2 AND " +
                "\"CompletedAtUtc\" IS NOT NULL AND \"CompletedAtUtc\" >= \"CreatedAtUtc\" AND " +
                "\"ReservationId\" IS NOT NULL AND \"ResultDetailsRevision\" IS NOT NULL AND " +
                "\"ResultReservationVersion\" IS NOT NULL AND \"ErrorCode\" IS NULL AND " +
                "\"AnonymisedAtUtc\" IS NULL) OR " +
                $"(\"State\" IN ({(int)ReservationDispatchState.Applied}, " +
                $"{(int)ReservationDispatchState.Unchanged}) AND " +
                "\"Version\" >= 2 AND \"CompletedAtUtc\" IS NOT NULL AND " +
                "\"CompletedAtUtc\" >= \"CreatedAtUtc\" AND " +
                "(\"ReservationId\" IS NOT NULL OR \"AnonymisedAtUtc\" IS NOT NULL) AND " +
                "\"ResultDetailsRevision\" IS NOT NULL AND \"ResultReservationVersion\" IS NOT NULL AND " +
                "\"ErrorCode\" IS NULL) OR " +
                $"(\"State\" IN ({(int)ReservationDispatchState.ProposalRequired}, " +
                $"{(int)ReservationDispatchState.Rejected}, {(int)ReservationDispatchState.Conflict}) AND " +
                "\"Version\" >= 2 AND \"CompletedAtUtc\" IS NOT NULL AND " +
                "\"CompletedAtUtc\" >= \"CreatedAtUtc\"))");
            table.HasCheckConstraint(
                "CK_reservation_dispatches_sensitive_history_lifecycle",
                $"((\"State\" IN ({(int)ReservationDispatchState.Pending}, " +
                $"{(int)ReservationDispatchState.Accepted}) AND \"NormalizedSnapshot\" IS NOT NULL AND " +
                "trim(\"NormalizedSnapshot\") <> '' AND \"SensitiveDataRetainUntilUtc\" IS NULL AND " +
                "\"SensitiveDataRedactedAtUtc\" IS NULL AND \"AnonymisedAtUtc\" IS NULL) OR " +
                $"(\"State\" IN ({(int)ReservationDispatchState.Applied}, " +
                $"{(int)ReservationDispatchState.Unchanged}, {(int)ReservationDispatchState.ProposalRequired}, " +
                $"{(int)ReservationDispatchState.Rejected}, {(int)ReservationDispatchState.Conflict}) AND " +
                "\"CompletedAtUtc\" IS NOT NULL AND \"SensitiveDataRetainUntilUtc\" IS NOT NULL AND " +
                "\"SensitiveDataRetainUntilUtc\" > \"CompletedAtUtc\" AND " +
                "((\"NormalizedSnapshot\" IS NOT NULL AND trim(\"NormalizedSnapshot\") <> '' AND " +
                "\"SensitiveDataRedactedAtUtc\" IS NULL AND \"AnonymisedAtUtc\" IS NULL) OR " +
                "(\"NormalizedSnapshot\" IS NULL AND \"SensitiveDataRedactedAtUtc\" IS NOT NULL AND " +
                "((\"AnonymisedAtUtc\" IS NULL AND " +
                "\"SensitiveDataRedactedAtUtc\" >= \"SensitiveDataRetainUntilUtc\") OR " +
                "(\"AnonymisedAtUtc\" IS NOT NULL AND " +
                "\"SensitiveDataRedactedAtUtc\" = \"AnonymisedAtUtc\"))))))");
            table.HasCheckConstraint(
                "CK_reservation_dispatches_anonymised_shape",
                $"\"AnonymisedAtUtc\" IS NULL OR (\"State\" IN ({(int)ReservationDispatchState.Applied}, " +
                $"{(int)ReservationDispatchState.Unchanged}, {(int)ReservationDispatchState.ProposalRequired}, " +
                $"{(int)ReservationDispatchState.Rejected}, {(int)ReservationDispatchState.Conflict}) AND " +
                "\"CompletedAtUtc\" IS NOT NULL AND \"AnonymisedAtUtc\" >= \"CompletedAtUtc\" AND " +
                "\"ReservationId\" IS NULL AND \"SourceRevision\" IS NULL AND " +
                "\"NormalizedSnapshot\" IS NULL AND \"SensitiveDataRedactedAtUtc\" IS NOT NULL AND " +
                "\"SensitiveDataRedactedAtUtc\" = \"AnonymisedAtUtc\")");
        });
        builder.HasKey(dispatch => dispatch.Id);
        builder.HasAlternateKey(dispatch => new { dispatch.ScopeId, dispatch.Id });
        builder.Property(dispatch => dispatch.ScopeId).HasMaxLength(128).IsRequired();
        builder.Property(dispatch => dispatch.TriggerKind).HasConversion<int>().IsRequired();
        builder.Property(dispatch => dispatch.Kind).HasConversion<int>().IsRequired();
        builder.Property(dispatch => dispatch.SourceRevision).HasMaxLength(ReservationDispatch.SourceRevisionMaxLength);
        builder.Property(dispatch => dispatch.NormalizedSnapshot).HasMaxLength(ReservationDispatch.NormalizedSnapshotMaxLength);
        builder.Property(dispatch => dispatch.State).HasConversion<int>().IsRequired();
        builder.Property(dispatch => dispatch.ErrorCode).HasMaxLength(ReservationDispatch.ErrorCodeMaxLength);
        builder.Property(dispatch => dispatch.Version).IsConcurrencyToken().IsRequired();
        builder.HasIndex(dispatch => new { dispatch.ScopeId, dispatch.TriggerKind, dispatch.TriggerId }).IsUnique();
        builder.HasIndex(dispatch => new { dispatch.ScopeId, dispatch.ReceiptId, dispatch.CreatedAtUtc });
        builder.HasIndex(dispatch => new { dispatch.ScopeId, dispatch.SourceLinkId, dispatch.CreatedAtUtc });
        builder.HasIndex(dispatch => new { dispatch.ScopeId, dispatch.ReservationId, dispatch.Kind, dispatch.State });
        builder.HasIndex(dispatch => new
        {
            dispatch.ScopeId,
            dispatch.ConnectionId,
            dispatch.SensitiveDataRetainUntilUtc,
            dispatch.SensitiveDataRedactedAtUtc
        });
        builder.HasOne<ReservationSourceLink>()
            .WithMany()
            .HasForeignKey(dispatch => new { dispatch.ScopeId, dispatch.SourceLinkId })
            .HasPrincipalKey(link => new { link.ScopeId, link.Id })
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<ObservationReceipt>()
            .WithMany()
            .HasForeignKey(dispatch => new { dispatch.ScopeId, dispatch.ReceiptId })
            .HasPrincipalKey(receipt => new { receipt.ScopeId, receipt.Id })
            .OnDelete(DeleteBehavior.Restrict);
        builder.Ignore(dispatch => dispatch.DomainEvents);
    }
}
