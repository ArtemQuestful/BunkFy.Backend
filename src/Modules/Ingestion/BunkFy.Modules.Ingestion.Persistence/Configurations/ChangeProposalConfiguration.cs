namespace BunkFy.Modules.Ingestion.Persistence.Configurations;

using BunkFy.Modules.Ingestion.Domain.Connections;
using BunkFy.Modules.Ingestion.Domain.Proposals;
using BunkFy.Modules.Ingestion.Domain.Receipts;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

internal sealed class ChangeProposalConfiguration : IEntityTypeConfiguration<ChangeProposal>
{
    private const string EmptyGuid =
        "00000000-0000-0000-0000-000000000000";

    public void Configure(EntityTypeBuilder<ChangeProposal> builder)
    {
        builder.ToTable("change_proposals", table =>
        {
            table.HasCheckConstraint(
                "CK_change_proposals_coordinates",
                $"\"Id\" <> '{EmptyGuid}' AND \"PropertyId\" <> '{EmptyGuid}' AND " +
                $"\"ConnectionId\" <> '{EmptyGuid}' AND \"ReceiptId\" <> '{EmptyGuid}' AND " +
                $"\"SourcePayloadFileId\" <> '{EmptyGuid}' AND trim(\"ScopeId\") <> '' AND " +
                "\"BaseReservationDetailsRevision\" > 0 AND " +
                $"(\"ReservationId\" <> '{EmptyGuid}' OR \"AnonymisedAtUtc\" IS NOT NULL) AND " +
                $"(\"ProductOperationId\" IS NULL OR \"ProductOperationId\" <> '{EmptyGuid}') AND " +
                "(\"DecisionActor\" IS NULL OR trim(\"DecisionActor\") <> '') AND " +
                "(\"DecisionReason\" IS NULL OR trim(\"DecisionReason\") <> '')");
            table.HasCheckConstraint(
                "CK_change_proposals_reason_code",
                "length(trim(\"ReasonCode\")) > 0");
            table.HasCheckConstraint(
                "CK_change_proposals_lifecycle",
                $"((\"State\" = {(int)ChangeProposalState.Pending} AND \"Version\" = 1 AND " +
                "\"DecisionActor\" IS NULL AND \"DecisionReason\" IS NULL AND " +
                "\"ProductOperationId\" IS NULL AND \"DecidedAtUtc\" IS NULL AND " +
                "\"CompletedAtUtc\" IS NULL AND \"AnonymisedAtUtc\" IS NULL) OR " +
                $"(\"State\" = {(int)ChangeProposalState.Applying} AND \"Version\" = 2 AND " +
                "\"DecisionActor\" IS NOT NULL AND \"DecisionReason\" IS NULL AND " +
                "\"ProductOperationId\" IS NOT NULL AND \"DecidedAtUtc\" IS NOT NULL AND " +
                "\"DecidedAtUtc\" >= \"CreatedAtUtc\" AND \"CompletedAtUtc\" IS NULL AND " +
                "\"AnonymisedAtUtc\" IS NULL) OR " +
                $"(\"State\" = {(int)ChangeProposalState.Applied} AND \"Version\" >= 3 AND " +
                "\"DecisionActor\" IS NOT NULL AND \"DecisionReason\" IS NULL AND " +
                "\"ProductOperationId\" IS NOT NULL AND \"DecidedAtUtc\" IS NOT NULL AND " +
                "\"CompletedAtUtc\" IS NOT NULL AND \"DecidedAtUtc\" >= \"CreatedAtUtc\" AND " +
                "\"CompletedAtUtc\" >= \"DecidedAtUtc\") OR " +
                $"(\"State\" IN ({(int)ChangeProposalState.Rejected}, {(int)ChangeProposalState.Superseded}) AND " +
                "\"Version\" >= 2 AND \"DecisionActor\" IS NOT NULL AND " +
                "(\"DecisionReason\" IS NOT NULL OR \"AnonymisedAtUtc\" IS NOT NULL) AND " +
                "\"ProductOperationId\" IS NULL AND " +
                "\"DecidedAtUtc\" IS NOT NULL AND \"CompletedAtUtc\" IS NOT NULL AND " +
                "\"CompletedAtUtc\" = \"DecidedAtUtc\" AND " +
                "\"DecidedAtUtc\" >= \"CreatedAtUtc\") OR " +
                $"(\"State\" IN ({(int)ChangeProposalState.Stale}, {(int)ChangeProposalState.Failed}) AND " +
                "\"Version\" >= 3 AND \"DecisionActor\" IS NOT NULL AND " +
                "(\"DecisionReason\" IS NOT NULL OR \"AnonymisedAtUtc\" IS NOT NULL) AND " +
                "\"ProductOperationId\" IS NOT NULL AND " +
                "\"DecidedAtUtc\" IS NOT NULL AND \"CompletedAtUtc\" IS NOT NULL AND " +
                "\"DecidedAtUtc\" >= \"CreatedAtUtc\" AND \"CompletedAtUtc\" >= \"DecidedAtUtc\"))");
            table.HasCheckConstraint(
                "CK_change_proposals_sensitive_history_lifecycle",
                $"((\"State\" IN ({(int)ChangeProposalState.Pending}, {(int)ChangeProposalState.Applying}) AND " +
                "\"Diff\" IS NOT NULL AND trim(\"Diff\") <> '' AND " +
                "\"SensitiveDataRetainUntilUtc\" IS NULL AND \"SensitiveDataRedactedAtUtc\" IS NULL AND " +
                "\"AnonymisedAtUtc\" IS NULL) OR " +
                $"(\"State\" IN ({(int)ChangeProposalState.Applied}, {(int)ChangeProposalState.Rejected}, " +
                $"{(int)ChangeProposalState.Superseded}, {(int)ChangeProposalState.Stale}, " +
                $"{(int)ChangeProposalState.Failed}) AND \"CompletedAtUtc\" IS NOT NULL AND " +
                "\"SensitiveDataRetainUntilUtc\" IS NOT NULL AND " +
                "\"SensitiveDataRetainUntilUtc\" > \"CompletedAtUtc\" AND " +
                "((\"Diff\" IS NOT NULL AND trim(\"Diff\") <> '' AND " +
                "\"SensitiveDataRedactedAtUtc\" IS NULL AND \"AnonymisedAtUtc\" IS NULL) OR " +
                "(\"Diff\" IS NULL AND \"SensitiveDataRedactedAtUtc\" IS NOT NULL AND " +
                "((\"AnonymisedAtUtc\" IS NULL AND " +
                "\"SensitiveDataRedactedAtUtc\" >= \"SensitiveDataRetainUntilUtc\") OR " +
                "(\"AnonymisedAtUtc\" IS NOT NULL AND " +
                "\"SensitiveDataRedactedAtUtc\" = \"AnonymisedAtUtc\"))))))");
            table.HasCheckConstraint(
                "CK_change_proposals_anonymised_shape",
                $"\"AnonymisedAtUtc\" IS NULL OR (\"State\" IN ({(int)ChangeProposalState.Applied}, " +
                $"{(int)ChangeProposalState.Rejected}, {(int)ChangeProposalState.Superseded}, " +
                $"{(int)ChangeProposalState.Stale}, {(int)ChangeProposalState.Failed}) AND " +
                $"\"ReservationId\" = '{EmptyGuid}' AND \"Diff\" IS NULL AND \"DecisionReason\" IS NULL AND " +
                "\"CompletedAtUtc\" IS NOT NULL AND \"AnonymisedAtUtc\" >= \"CompletedAtUtc\" AND " +
                "\"SensitiveDataRedactedAtUtc\" IS NOT NULL AND " +
                "\"SensitiveDataRedactedAtUtc\" = \"AnonymisedAtUtc\")");
        });
        builder.HasKey(proposal => proposal.Id);
        builder.HasAlternateKey(proposal => new { proposal.ScopeId, proposal.Id });
        builder.Property(proposal => proposal.ScopeId).HasMaxLength(128).IsRequired();
        builder.Property(proposal => proposal.ReasonCode).HasMaxLength(ChangeProposal.ReasonCodeMaxLength).IsRequired();
        builder.Property(proposal => proposal.Diff).HasMaxLength(ChangeProposal.DiffMaxLength);
        builder.Property(proposal => proposal.State).HasConversion<int>().IsRequired();
        builder.Property(proposal => proposal.DecisionActor).HasMaxLength(ChangeProposal.ActorMaxLength);
        builder.Property(proposal => proposal.DecisionReason).HasMaxLength(ChangeProposal.ReasonMaxLength);
        builder.Property(proposal => proposal.Version).IsConcurrencyToken().IsRequired();
        builder.HasIndex(proposal => new { proposal.ScopeId, proposal.ReceiptId }).IsUnique();
        builder.HasIndex(proposal => new { proposal.ScopeId, proposal.PropertyId, proposal.State, proposal.CreatedAtUtc });
        builder.HasIndex(proposal => new { proposal.ScopeId, proposal.ReservationId, proposal.CreatedAtUtc });
        builder.HasIndex(proposal => new
        {
            proposal.ScopeId,
            proposal.ConnectionId,
            proposal.SensitiveDataRetainUntilUtc,
            proposal.SensitiveDataRedactedAtUtc
        });
        builder.HasOne<AdapterConnection>()
            .WithMany()
            .HasForeignKey(proposal => new { proposal.ScopeId, proposal.ConnectionId })
            .HasPrincipalKey(connection => new { connection.ScopeId, connection.Id })
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<ObservationReceipt>()
            .WithMany()
            .HasForeignKey(proposal => new { proposal.ScopeId, proposal.ReceiptId })
            .HasPrincipalKey(receipt => new { receipt.ScopeId, receipt.Id })
            .OnDelete(DeleteBehavior.Restrict);
        builder.Ignore(proposal => proposal.DomainEvents);
    }
}
