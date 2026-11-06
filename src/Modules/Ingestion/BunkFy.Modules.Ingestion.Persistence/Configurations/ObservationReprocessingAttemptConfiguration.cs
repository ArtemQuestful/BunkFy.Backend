namespace BunkFy.Modules.Ingestion.Persistence.Configurations;

using BunkFy.Modules.Ingestion.Domain.Connections;
using BunkFy.Modules.Ingestion.Domain.Receipts;
using BunkFy.Modules.Ingestion.Domain.Reprocessing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

internal sealed class ObservationReprocessingAttemptConfiguration
    : IEntityTypeConfiguration<ObservationReprocessingAttempt>
{
    private const string EmptyGuid =
        "00000000-0000-0000-0000-000000000000";

    public void Configure(EntityTypeBuilder<ObservationReprocessingAttempt> builder)
    {
        builder.ToTable("observation_reprocessing_attempts", table =>
        {
            table.HasCheckConstraint(
                "CK_observation_reprocessing_attempts_coordinates",
                $"\"Id\" <> '{EmptyGuid}' AND \"TaskRunId\" = \"Id\" AND " +
                $"\"PropertyId\" <> '{EmptyGuid}' AND \"ConnectionId\" <> '{EmptyGuid}' AND " +
                $"\"SourceReceiptId\" <> '{EmptyGuid}' AND trim(\"ScopeId\") <> '' AND " +
                "trim(\"ParserType\") <> '' AND \"ParserVersion\" > 0 AND " +
                "trim(\"RequestedBy\") <> '' AND \"Version\" >= 1 AND " +
                "\"LastTaskAttempt\" >= 0 AND " +
                "\"ReservationExpiresAtUtc\" > \"RequestedAtUtc\" AND " +
                "(\"LastErrorCode\" IS NULL OR trim(\"LastErrorCode\") <> '')");
            table.HasCheckConstraint(
                "CK_observation_reprocessing_attempts_counters",
                "\"ParsedCount\" >= 0 AND \"AcceptedCount\" >= 0 AND " +
                "\"DuplicateCount\" >= 0 AND \"RejectedCount\" >= 0 AND " +
                "CAST(\"AcceptedCount\" AS bigint) + \"DuplicateCount\" + " +
                "\"RejectedCount\" = \"ParsedCount\"");
            table.HasCheckConstraint(
                "CK_observation_reprocessing_attempts_lifecycle",
                "(\"StartedAtUtc\" IS NULL OR \"StartedAtUtc\" >= \"RequestedAtUtc\") AND " +
                "(\"CompletedAtUtc\" IS NULL OR (\"CompletedAtUtc\" >= \"RequestedAtUtc\" AND " +
                "(\"StartedAtUtc\" IS NULL OR \"CompletedAtUtc\" >= \"StartedAtUtc\"))) AND " +
                $"((\"State\" = {(int)ObservationReprocessingState.Queued} AND " +
                "\"CompletedAtUtc\" IS NULL AND \"ParsedCount\" = 0 AND " +
                "((\"LastTaskAttempt\" = 0 AND \"StartedAtUtc\" IS NULL AND " +
                "\"LastErrorCode\" IS NULL AND \"Version\" = 1) OR " +
                "(\"LastTaskAttempt\" > 0 AND \"StartedAtUtc\" IS NOT NULL AND " +
                "\"LastErrorCode\" IS NOT NULL AND \"Version\" >= 3))) OR " +
                $"(\"State\" = {(int)ObservationReprocessingState.Running} AND " +
                "\"LastTaskAttempt\" > 0 AND \"StartedAtUtc\" IS NOT NULL AND " +
                "\"CompletedAtUtc\" IS NULL AND \"LastErrorCode\" IS NULL AND " +
                "\"ParsedCount\" = 0 AND \"Version\" >= 2) OR " +
                $"(\"State\" = {(int)ObservationReprocessingState.Succeeded} AND " +
                "\"LastTaskAttempt\" > 0 AND \"StartedAtUtc\" IS NOT NULL AND " +
                "\"CompletedAtUtc\" IS NOT NULL AND \"RejectedCount\" = 0 AND " +
                "\"LastErrorCode\" IS NULL AND \"Version\" >= 3) OR " +
                $"(\"State\" = {(int)ObservationReprocessingState.NoMatch} AND " +
                "\"LastTaskAttempt\" > 0 AND \"StartedAtUtc\" IS NOT NULL AND " +
                "\"CompletedAtUtc\" IS NOT NULL AND \"ParsedCount\" = 0 AND " +
                "\"LastErrorCode\" IS NOT NULL AND \"Version\" >= 3) OR " +
                $"(\"State\" = {(int)ObservationReprocessingState.Failed} AND " +
                "\"CompletedAtUtc\" IS NOT NULL AND \"LastErrorCode\" IS NOT NULL AND \"Version\" >= 2) OR " +
                $"(\"State\" IN ({(int)ObservationReprocessingState.Canceled}, " +
                $"{(int)ObservationReprocessingState.Expired}) AND \"CompletedAtUtc\" IS NOT NULL AND " +
                "\"LastErrorCode\" IS NOT NULL AND \"ParsedCount\" = 0 AND \"Version\" >= 2))");
        });
        builder.HasKey(attempt => attempt.Id);
        builder.HasAlternateKey(attempt => new { attempt.ScopeId, attempt.Id });
        builder.Property(attempt => attempt.ScopeId).HasMaxLength(128).IsRequired();
        builder.Property(attempt => attempt.ParserType)
            .HasMaxLength(ObservationReprocessingAttempt.ParserTypeMaxLength).IsRequired();
        builder.Property(attempt => attempt.RequestedBy)
            .HasMaxLength(ObservationReprocessingAttempt.RequestedByMaxLength).IsRequired();
        builder.Property(attempt => attempt.LastErrorCode)
            .HasMaxLength(ObservationReprocessingAttempt.ErrorCodeMaxLength);
        builder.Property(attempt => attempt.State).HasConversion<int>().IsRequired();
        builder.Property(attempt => attempt.Version).IsConcurrencyToken().IsRequired();
        builder.HasIndex(attempt => new { attempt.ScopeId, attempt.TaskRunId }).IsUnique();
        builder.HasIndex(attempt => new
        {
            attempt.ScopeId,
            attempt.SourceReceiptId,
            attempt.State,
            attempt.RequestedAtUtc
        });
        builder.HasOne<AdapterConnection>()
            .WithMany()
            .HasForeignKey(attempt => new { attempt.ScopeId, attempt.ConnectionId })
            .HasPrincipalKey(connection => new { connection.ScopeId, connection.Id })
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<ObservationReceipt>()
            .WithMany()
            .HasForeignKey(attempt => new { attempt.ScopeId, attempt.SourceReceiptId })
            .HasPrincipalKey(receipt => new { receipt.ScopeId, receipt.Id })
            .OnDelete(DeleteBehavior.Restrict);
        builder.Ignore(attempt => attempt.DomainEvents);
    }
}
