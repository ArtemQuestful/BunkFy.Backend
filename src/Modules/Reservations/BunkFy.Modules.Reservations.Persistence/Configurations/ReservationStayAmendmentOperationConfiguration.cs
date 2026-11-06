namespace BunkFy.Modules.Reservations.Persistence.Configurations;

using BunkFy.Modules.Reservations.Domain.Aggregates;
using BunkFy.Modules.Reservations.Domain.StayAmendments;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

internal sealed class ReservationStayAmendmentOperationConfiguration
    : IEntityTypeConfiguration<ReservationStayAmendmentOperation>
{
    public void Configure(
        EntityTypeBuilder<ReservationStayAmendmentOperation> builder)
    {
        builder.ToTable("stay_amendment_operations", table =>
        {
            table.HasCheckConstraint(
                "CK_stay_amendment_operations_request",
                $"\"RequestSchemaVersion\" IN (" +
                $"{ReservationStayAmendmentOperation.LegacyRequestSchemaVersion}, " +
                $"{ReservationStayAmendmentOperation.CurrentRequestSchemaVersion}) AND " +
                $"char_length(\"RequestFingerprint\") = {ReservationStayAmendmentOperation.RequestFingerprintLength} AND " +
                "\"RequestFingerprint\" ~ '^[0-9a-f]{64}$' AND " +
                "\"ExpectedDetailsRevision\" >= 1 AND " +
                $"((\"Outcome\" = {(int)ReservationStayAmendmentOperationOutcome.OutcomeUnknown} AND " +
                $"\"RequestSchemaVersion\" = {ReservationStayAmendmentOperation.LegacyRequestSchemaVersion} AND " +
                "\"RequestedBy\" IS NULL AND \"InventoryRequestId\" IS NULL) OR " +
                $"(\"Outcome\" = {(int)ReservationStayAmendmentOperationOutcome.Pending} AND " +
                "\"InventoryRequestId\" IS NOT NULL AND " +
                "\"RequestedBy\" IS NOT NULL AND " +
                "\"RequestedBy\" = trim(\"RequestedBy\") AND " +
                "char_length(\"RequestedBy\") > 0 AND " +
                "\"RequestedBy\" !~ '[[:cntrl:]]') OR " +
                $"(\"Outcome\" IN ({(int)ReservationStayAmendmentOperationOutcome.Applied}, " +
                $"{(int)ReservationStayAmendmentOperationOutcome.Rejected}) AND " +
                "((\"OperationVersion\" = 1 AND \"InventoryRequestId\" IS NULL) OR " +
                "(\"OperationVersion\" > 1 AND \"InventoryRequestId\" IS NOT NULL)) AND " +
                "\"RequestedBy\" IS NOT NULL AND " +
                "\"RequestedBy\" = trim(\"RequestedBy\") AND " +
                "char_length(\"RequestedBy\") > 0 AND " +
                "\"RequestedBy\" !~ '[[:cntrl:]]'))");
            table.HasCheckConstraint(
                "CK_stay_amendment_operations_target",
                $"(\"Outcome\" = {(int)ReservationStayAmendmentOperationOutcome.OutcomeUnknown} AND " +
                "\"TargetArrival\" IS NULL AND \"TargetDeparture\" IS NULL AND " +
                "\"TargetExpectedArrivalTime\" IS NULL AND " +
                "\"TargetExpectedDepartureTime\" IS NULL AND " +
                "\"TargetInventoryUnitIds\" IS NULL) OR " +
                $"(\"Outcome\" IN ({(int)ReservationStayAmendmentOperationOutcome.Pending}, " +
                $"{(int)ReservationStayAmendmentOperationOutcome.Applied}, " +
                $"{(int)ReservationStayAmendmentOperationOutcome.Rejected}) AND " +
                "\"TargetArrival\" IS NOT NULL AND \"TargetDeparture\" IS NOT NULL AND " +
                "\"TargetArrival\" < \"TargetDeparture\" AND " +
                "(\"TargetExpectedArrivalTime\" IS NULL OR " +
                "date_part('second', \"TargetExpectedArrivalTime\") = 0) AND " +
                "(\"TargetExpectedDepartureTime\" IS NULL OR " +
                "date_part('second', \"TargetExpectedDepartureTime\") = 0) AND " +
                "\"TargetInventoryUnitIds\" IS NOT NULL AND " +
                "\"TargetInventoryUnitIds\" ~ " +
                "'^[0-9a-f]{32}(,[0-9a-f]{32}){0,99}$')");
            table.HasCheckConstraint(
                "CK_stay_amendment_operations_outcome",
                $"(\"Outcome\" = {(int)ReservationStayAmendmentOperationOutcome.Pending} AND " +
                "\"CompletedAtUtc\" IS NULL AND \"ResultingDetailsRevision\" IS NULL AND " +
                "\"ResultingReservationVersion\" IS NULL AND " +
                "\"ResultingAllocationVersion\" IS NULL AND \"RejectionCode\" IS NULL) OR " +
                $"(\"Outcome\" = {(int)ReservationStayAmendmentOperationOutcome.Applied} AND " +
                "\"CompletedAtUtc\" IS NOT NULL AND \"ResultingDetailsRevision\" >= 1 AND " +
                "\"ResultingReservationVersion\" >= 1 AND " +
                "\"ResultingAllocationVersion\" >= 1 AND \"RejectionCode\" IS NULL) OR " +
                $"(\"Outcome\" = {(int)ReservationStayAmendmentOperationOutcome.Rejected} AND " +
                "\"CompletedAtUtc\" IS NOT NULL AND \"ResultingDetailsRevision\" >= 1 AND " +
                "\"ResultingReservationVersion\" >= 1 AND " +
                "\"ResultingAllocationVersion\" IS NULL AND \"RejectionCode\" >= 1) OR " +
                $"(\"Outcome\" = {(int)ReservationStayAmendmentOperationOutcome.OutcomeUnknown} AND " +
                "\"CompletedAtUtc\" IS NULL AND \"ResultingDetailsRevision\" IS NULL AND " +
                "\"ResultingReservationVersion\" IS NULL AND " +
                "\"ResultingAllocationVersion\" IS NULL AND \"RejectionCode\" IS NULL AND " +
                "\"ReconciliationCount\" = 0)");
            table.HasCheckConstraint(
                "CK_stay_amendment_operations_timestamps",
                "\"UpdatedAtUtc\" >= \"RequestedAtUtc\" AND " +
                "(\"CompletedAtUtc\" IS NULL OR " +
                "(\"CompletedAtUtc\" >= \"RequestedAtUtc\" AND " +
                "\"CompletedAtUtc\" <= \"UpdatedAtUtc\")) AND " +
                "(\"LastReconciledAtUtc\" IS NULL OR " +
                "(\"LastReconciledAtUtc\" >= \"RequestedAtUtc\" AND " +
                "\"LastReconciledAtUtc\" <= \"UpdatedAtUtc\"))");
            table.HasCheckConstraint(
                "CK_stay_amendment_operations_reconciliation",
                "(\"ReconciliationCount\" = 0 AND " +
                "\"LastReconciledAtUtc\" IS NULL AND \"LastReconciledBy\" IS NULL) OR " +
                "(\"ReconciliationCount\" >= 1 AND " +
                "\"LastReconciledAtUtc\" IS NOT NULL AND \"LastReconciledBy\" IS NOT NULL AND " +
                "\"LastReconciledBy\" = trim(\"LastReconciledBy\") AND " +
                "char_length(\"LastReconciledBy\") > 0 AND " +
                "\"LastReconciledBy\" !~ '[[:cntrl:]]')");
            table.HasCheckConstraint(
                "CK_stay_amendment_operations_version",
                $"(\"Outcome\" = {(int)ReservationStayAmendmentOperationOutcome.Pending} AND " +
                "\"OperationVersion\" = \"ReconciliationCount\" + 1) OR " +
                $"(\"Outcome\" = {(int)ReservationStayAmendmentOperationOutcome.Applied} AND " +
                "((\"OperationVersion\" = 1 AND \"ReconciliationCount\" = 0) OR " +
                "\"OperationVersion\" = \"ReconciliationCount\" + 2)) OR " +
                $"(\"Outcome\" = {(int)ReservationStayAmendmentOperationOutcome.Rejected} AND " +
                "\"OperationVersion\" = \"ReconciliationCount\" + 2) OR " +
                $"(\"Outcome\" = {(int)ReservationStayAmendmentOperationOutcome.OutcomeUnknown} AND " +
                "\"OperationVersion\" = 1 AND \"ReconciliationCount\" = 0)");
        });
        builder.HasKey(operation => new
        {
            operation.ScopeId,
            operation.ReservationId,
            operation.Id
        });
        builder.Property(operation => operation.ScopeId)
            .HasMaxLength(128)
            .IsRequired();
        builder.Property(operation => operation.PropertyId)
            .IsRequired();
        builder.Property(operation => operation.ReservationId)
            .IsRequired();
        builder.Property(operation => operation.InventoryRequestId);
        builder.Property(operation => operation.RequestSchemaVersion)
            .IsRequired();
        builder.Property(operation => operation.RequestFingerprint)
            .HasMaxLength(
                ReservationStayAmendmentOperation.RequestFingerprintLength)
            .IsFixedLength()
            .IsRequired();
        builder.Property(operation => operation.TargetExpectedArrivalTime)
            .HasPrecision(0);
        builder.Property(operation => operation.TargetExpectedDepartureTime)
            .HasPrecision(0);
        builder.Property(operation => operation.TargetInventoryUnitIds)
            .HasMaxLength(
                ReservationStayAmendmentOperation.TargetInventoryUnitIdsMaxLength);
        builder.Property(operation => operation.ExpectedDetailsRevision)
            .IsRequired();
        builder.Property(operation => operation.RequestedBy)
            .HasMaxLength(Reservation.ActorIdMaxLength);
        builder.Property(operation => operation.LastReconciledBy)
            .HasMaxLength(Reservation.ActorIdMaxLength);
        builder.Property(operation => operation.Outcome)
            .HasConversion<int>()
            .IsRequired();
        builder.Property(operation => operation.OperationVersion)
            .IsConcurrencyToken()
            .IsRequired();
        builder.Property(operation => operation.RequestedAtUtc)
            .IsRequired();
        builder.Property(operation => operation.UpdatedAtUtc)
            .IsRequired();
        builder.Property(operation => operation.ReconciliationCount)
            .IsRequired();
        builder.HasOne<ReservationManagementOperation>()
            .WithOne()
            .HasForeignKey<ReservationStayAmendmentOperation>(operation => new
            {
                operation.ScopeId,
                operation.ReservationId,
                operation.Id
            })
            .HasPrincipalKey<ReservationManagementOperation>(operation => new
            {
                operation.ScopeId,
                operation.ReservationId,
                operation.Id
            })
            .OnDelete(DeleteBehavior.Cascade);
        builder.HasIndex(operation => operation.InventoryRequestId)
            .IsUnique();
        builder.HasIndex(operation => new
        {
            operation.ScopeId,
            operation.PropertyId,
            operation.Outcome,
            operation.UpdatedAtUtc,
            operation.Id,
            operation.ReservationId
        });
        builder.Ignore(operation => operation.DomainEvents);
    }
}
