namespace BunkFy.Modules.Reservations.Tests;

using Gma.Framework.Domain;
using Gma.Framework.Scoping;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using BunkFy.Modules.Reservations.Domain.Aggregates;
using BunkFy.Modules.Reservations.Domain.DataRights;
using BunkFy.Modules.Reservations.Domain.Entities;
using BunkFy.Modules.Reservations.Domain.Models;
using BunkFy.Modules.Reservations.Persistence;
using BunkFy.Modules.Reservations.Persistence.TenantTermination;
using Xunit;

[Trait("Category", "Unit")]
public sealed class ReservationsModelTests
{
    [Fact]
    public void Every_reservations_scope_shaped_entity_is_scoped_and_filtered()
    {
        using ReservationsDbContext dbContext = CreateDbContext();

        IEntityType[] scopeShapedEntities = dbContext.Model.GetEntityTypes()
            .Where(entity =>
                !entity.IsOwned() &&
                entity.ClrType.Namespace?.StartsWith(
                    "BunkFy.Modules.Reservations.",
                    StringComparison.Ordinal) == true &&
                entity.FindProperty(nameof(IScopedEntity.ScopeId)) is not null)
            .ToArray();
        Assert.NotEmpty(scopeShapedEntities);

        string[] invalidEntities = scopeShapedEntities
            .Where(entity =>
                !typeof(IScopedEntity).IsAssignableFrom(entity.ClrType) ||
                entity.GetDeclaredQueryFilters().Count == 0)
            .Select(entity => entity.ClrType.FullName ?? entity.Name)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Empty(invalidEntities);
    }

    [Fact]
    public void Every_reservations_tenant_relationship_is_scope_qualified()
    {
        using ReservationsDbContext dbContext = CreateDbContext();

        IEntityType[] scopedEntities = dbContext.Model.GetEntityTypes()
            .Where(entity =>
                !entity.IsOwned() &&
                entity.ClrType.Namespace?.StartsWith(
                    "BunkFy.Modules.Reservations.",
                    StringComparison.Ordinal) == true &&
                typeof(IScopedEntity).IsAssignableFrom(entity.ClrType))
            .ToArray();
        Assert.NotEmpty(scopedEntities);

        string[] unqualifiedRelationships = scopedEntities
            .SelectMany(entity => entity.GetForeignKeys())
            .Where(foreignKey =>
                typeof(IScopedEntity).IsAssignableFrom(
                    foreignKey.PrincipalEntityType.ClrType))
            .Where(foreignKey =>
                !foreignKey.Properties.Any(property =>
                    property.Name == nameof(IScopedEntity.ScopeId)) ||
                !foreignKey.PrincipalKey.Properties.Any(property =>
                    property.Name == nameof(IScopedEntity.ScopeId)))
            .Select(foreignKey =>
                $"{foreignKey.DeclaringEntityType.ClrType.FullName} -> " +
                foreignKey.PrincipalEntityType.ClrType.FullName)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Empty(unqualifiedRelationships);
    }

    [Fact]
    public void Tenant_owned_operation_history_and_guest_projection_are_query_scoped()
    {
        using ReservationsDbContext dbContext = CreateDbContext();

        Type[] scopedTypes =
        [
            typeof(ReservationManagementOperation),
            typeof(ReservationExternalOperation),
            typeof(ReservationDetailsHistoryEntry),
            typeof(ReservationGuestProfileProjection)
        ];

        Assert.All(
            scopedTypes,
            type => Assert.NotEmpty(
                dbContext.Model.FindEntityType(type)!.GetDeclaredQueryFilters()));
    }

    [Fact]
    public void Tenant_revision_is_scope_keyed_and_concurrency_guarded()
    {
        using ReservationsDbContext dbContext = CreateDbContext();

        IEntityType revisionEntity = dbContext.Model.FindEntityType(
            typeof(ReservationsTenantRevision))!;
        IEntityType designRevisionEntity = dbContext
            .GetService<IDesignTimeModel>()
            .Model
            .FindEntityType(typeof(ReservationsTenantRevision))!;

        Assert.Equal(
            [nameof(ReservationsTenantRevision.ScopeId)],
            revisionEntity.FindPrimaryKey()!.Properties.Select(
                property => property.Name));
        Assert.True(
            revisionEntity.FindProperty(
                nameof(ReservationsTenantRevision.Revision))!
                .IsConcurrencyToken);
        Assert.NotEmpty(revisionEntity.GetDeclaredQueryFilters());
        Assert.Contains(
            designRevisionEntity.GetCheckConstraints(),
            constraint => string.Equals(
                constraint.Name,
                "CK_reservations_tenant_revision_positive",
                StringComparison.Ordinal));
        Assert.Contains(
            designRevisionEntity.GetCheckConstraints(),
            constraint => string.Equals(
                constraint.Name,
                "CK_reservations_tenant_revision_lifecycle",
                StringComparison.Ordinal));
    }

    [Fact]
    public void Tenant_destruction_progress_and_receipt_are_scope_unique_and_constrained()
    {
        using ReservationsDbContext dbContext = CreateDbContext();
        IModel designModel = dbContext.GetService<IDesignTimeModel>().Model;
        IEntityType operation = designModel.FindEntityType(
            typeof(ReservationsTenantDestroyOperation))!;
        IEntityType receipt = designModel.FindEntityType(
            typeof(ReservationsTenantDestroyReceipt))!;

        Assert.Equal(
            [nameof(ReservationsTenantDestroyOperation.OperationId)],
            operation.FindPrimaryKey()!.Properties.Select(
                property => property.Name));
        Assert.True(operation.FindProperty(
            nameof(ReservationsTenantDestroyOperation.ConcurrencyVersion))!
            .IsConcurrencyToken);
        Assert.Contains(
            operation.GetIndexes(),
            index => index.IsUnique &&
                index.Properties.Select(property => property.Name)
                    .SequenceEqual([
                        nameof(ReservationsTenantDestroyOperation.ScopeId)
                    ]));
        Assert.Contains(
            operation.GetCheckConstraints(),
            constraint => constraint.Name ==
                "CK_reservations_tenant_destroy_operation_batch");
        Assert.Contains(
            operation.GetCheckConstraints(),
            constraint => constraint.Name ==
                "CK_reservations_tenant_destroy_operation_progress");

        Assert.Equal(
            [nameof(ReservationsTenantDestroyReceipt.OperationId)],
            receipt.FindPrimaryKey()!.Properties.Select(
                property => property.Name));
        Assert.Contains(
            receipt.GetIndexes(),
            index => index.IsUnique &&
                index.Properties.Select(property => property.Name)
                    .SequenceEqual([
                        nameof(ReservationsTenantDestroyReceipt.ScopeId)
                    ]));
        Assert.Contains(
            receipt.GetCheckConstraints(),
            constraint => constraint.Name ==
                "CK_reservations_tenant_destroy_receipt_progress");
    }

    [Fact]
    public void Model_has_scoped_requested_units_idempotency_and_version_concurrency()
    {
        using ReservationsDbContext dbContext = CreateDbContext();
        IEntityType reservationEntity = dbContext.Model.FindEntityType(typeof(Reservation))!;
        IEntityType reservationDesignEntity = dbContext.GetService<IDesignTimeModel>()
            .Model.FindEntityType(typeof(Reservation))!;
        IEntityType unitEntity = dbContext.Model.FindEntityType(typeof(RequestedInventoryUnit))!;
        IForeignKey parent = Assert.Single(unitEntity.GetForeignKeys(), candidate => candidate.PrincipalEntityType == reservationEntity);

        Assert.True(reservationEntity.FindProperty(nameof(Reservation.Version))!.IsConcurrencyToken);
        Assert.NotNull(reservationEntity.FindProperty(nameof(Reservation.DetailsRevision)));
        Assert.NotNull(reservationEntity.FindProperty(nameof(Reservation.LastDetailsChangeOrigin)));
        Assert.Equal("time(0) without time zone", ColumnType(reservationDesignEntity, nameof(Reservation.ExpectedArrivalTime)));
        Assert.Equal("time(0) without time zone", ColumnType(reservationDesignEntity, nameof(Reservation.ExpectedDepartureTime)));
        Assert.Equal("time(0) without time zone", ColumnType(reservationDesignEntity, nameof(Reservation.PendingExpectedArrivalTime)));
        Assert.Equal("time(0) without time zone", ColumnType(reservationDesignEntity, nameof(Reservation.PendingExpectedDepartureTime)));
        Assert.Equal(
            Reservation.ActorIdMaxLength,
            reservationEntity.FindProperty(nameof(Reservation.PendingStayActorId))!.GetMaxLength());
        Assert.Equal(
            Reservation.ActorIdMaxLength,
            reservationEntity.FindProperty(nameof(Reservation.CheckedInBy))!.GetMaxLength());
        Assert.NotNull(reservationEntity.FindProperty(nameof(Reservation.NoShowBusinessDate)));
        Assert.NotNull(reservationEntity.FindProperty(nameof(Reservation.CheckedOutAtUtc)));
        Assert.NotNull(reservationEntity.FindProperty(nameof(Reservation.IsAnonymised)));
        Assert.NotNull(reservationEntity.FindProperty(nameof(Reservation.AnonymisedAtUtc)));
        string[] lifecycleConstraints =
        [
            "CK_reservations_pending_stay_complete",
            "CK_reservations_checked_in_complete",
            "CK_reservations_no_show_complete",
            "CK_reservations_checked_out_complete",
            "CK_reservations_anonymisation_state"
        ];
        Assert.All(
            lifecycleConstraints,
            constraint => Assert.Contains(
                reservationDesignEntity.GetCheckConstraints(),
                candidate => candidate.Name == constraint));
        Assert.Contains(
            reservationEntity.GetIndexes(),
            index => index.IsUnique && index.Properties.Select(property => property.Name)
                .SequenceEqual([nameof(Reservation.ScopeId), nameof(Reservation.AllocationRequestId)]));
        string[][] subjectLookupIndexes =
        [
            [nameof(Reservation.ScopeId), nameof(Reservation.PropertyId), nameof(Reservation.EmailSearch), nameof(Reservation.Id)],
            [nameof(Reservation.ScopeId), nameof(Reservation.PropertyId), nameof(Reservation.PhoneSearch), nameof(Reservation.Id)],
            [nameof(Reservation.ScopeId), nameof(Reservation.PropertyId), nameof(Reservation.PendingEmailSearch), nameof(Reservation.Id)],
            [nameof(Reservation.ScopeId), nameof(Reservation.PropertyId), nameof(Reservation.PendingPhoneSearch), nameof(Reservation.Id)]
        ];
        Assert.All(
            subjectLookupIndexes,
            expected => Assert.Contains(
                reservationEntity.GetIndexes(),
                index => index.Properties.Select(property => property.Name).SequenceEqual(expected)));
        Assert.Equal(["ScopeId", "ReservationId"], parent.Properties.Select(property => property.Name));
        Assert.Equal(["ScopeId", "Id"], parent.PrincipalKey.Properties.Select(property => property.Name));
        Assert.Equal(DeleteBehavior.Cascade, parent.DeleteBehavior);
    }

    [Fact]
    public void Model_has_scoped_revision_history_and_provider_agnostic_operation_deduplication()
    {
        using ReservationsDbContext dbContext = CreateDbContext();
        IEntityType history = dbContext.Model.FindEntityType(typeof(ReservationDetailsHistoryEntry))!;
        IEntityType externalOperation = dbContext.Model.FindEntityType(typeof(ReservationExternalOperation))!;

        Assert.Contains(
            history.GetIndexes(),
            index => index.IsUnique && index.Properties.Select(property => property.Name)
                .SequenceEqual([
                    nameof(ReservationDetailsHistoryEntry.ScopeId),
                    nameof(ReservationDetailsHistoryEntry.ReservationId),
                    nameof(ReservationDetailsHistoryEntry.ToRevision)
                ]));
        Assert.Contains(
            history.GetIndexes(),
            index => index.IsUnique && index.Properties.Select(property => property.Name)
                .SequenceEqual([
                    nameof(ReservationDetailsHistoryEntry.ScopeId),
                    nameof(ReservationDetailsHistoryEntry.OperationDeduplicationKey)
                ]));
        Assert.Null(history.GetIndexes().Single(index => index.Properties.Any(property =>
            property.Name == nameof(ReservationDetailsHistoryEntry.OperationDeduplicationKey))).GetFilter());
        Assert.Equal(
            [nameof(ReservationExternalOperation.ScopeId), nameof(ReservationExternalOperation.Id)],
            externalOperation.FindPrimaryKey()!.Properties.Select(property => property.Name));
        Assert.Equal(64, externalOperation.FindProperty(nameof(ReservationExternalOperation.RequestFingerprint))!.GetMaxLength());
        Assert.Contains(
            externalOperation.GetIndexes(),
            index => index.Properties.Select(property => property.Name)
                .SequenceEqual([
                    nameof(ReservationExternalOperation.ScopeId),
                    nameof(ReservationExternalOperation.ConnectionId),
                    nameof(ReservationExternalOperation.CompletedAtUtc)
                ]));
    }

    [Fact]
    public void Management_operation_journal_is_scoped_constrained_and_reservation_owned()
    {
        using ReservationsDbContext dbContext = CreateDbContext();
        IModel designModel = dbContext.GetService<IDesignTimeModel>().Model;
        IEntityType operation = designModel.FindEntityType(
            typeof(ReservationManagementOperation))!;
        IEntityType reservation = designModel.FindEntityType(
            typeof(Reservation))!;

        Assert.Equal(
            [
                nameof(ReservationManagementOperation.ScopeId),
                nameof(ReservationManagementOperation.ReservationId),
                nameof(ReservationManagementOperation.Id)
            ],
            operation.FindPrimaryKey()!.Properties.Select(
                property => property.Name));
        IForeignKey owner = Assert.Single(
            operation.GetForeignKeys(),
            candidate => candidate.PrincipalEntityType == reservation);
        Assert.Equal(
            [
                nameof(ReservationManagementOperation.ScopeId),
                nameof(ReservationManagementOperation.ReservationId)
            ],
            owner.Properties.Select(property => property.Name));
        Assert.Equal(DeleteBehavior.Cascade, owner.DeleteBehavior);
        Assert.Contains(
            operation.GetIndexes(),
            index => index.Properties.Select(property => property.Name)
                .SequenceEqual([
                    nameof(ReservationManagementOperation.ScopeId),
                    nameof(ReservationManagementOperation.PropertyId),
                    nameof(ReservationManagementOperation.CreatedAtUtc),
                    nameof(ReservationManagementOperation.Id)
                ]));
        Assert.True(operation.FindProperty(
            nameof(ReservationManagementOperation.ExpectedVersion))!.IsNullable);
        Assert.True(operation.FindProperty(
            nameof(ReservationManagementOperation.ExpectedDetailsRevision))!.IsNullable);
        IProperty requestFingerprint = operation.FindProperty(
            nameof(ReservationManagementOperation.RequestFingerprint))!;
        Assert.True(requestFingerprint.IsNullable);
        Assert.Equal(Reservation.RequestFingerprintLength, requestFingerprint.GetMaxLength());
        string[] constraints =
        [
            "CK_management_operations_business_date",
            "CK_management_operations_expected_revision",
            "CK_management_operations_kind",
            "CK_management_operations_request_fingerprint"
        ];
        Assert.All(
            constraints,
            constraint => Assert.Contains(
                operation.GetCheckConstraints(),
                candidate => candidate.Name == constraint));
        Assert.Contains(
            "1, 2, 3, 4, 5, 6, 7",
            operation.GetCheckConstraints().Single(constraint =>
                constraint.Name == "CK_management_operations_kind").Sql,
            StringComparison.Ordinal);
        Assert.Contains(
            "IN (6, 7)",
            operation.GetCheckConstraints().Single(constraint =>
                constraint.Name ==
                    "CK_management_operations_request_fingerprint").Sql,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Model_has_scoped_immutable_anonymisation_owner_proof()
    {
        using ReservationsDbContext dbContext = CreateDbContext();
        IEntityType receipt = dbContext.Model.FindEntityType(
            typeof(ReservationAnonymisationReceipt))!;
        IEntityType designReceipt = dbContext.GetService<IDesignTimeModel>()
            .Model.FindEntityType(typeof(ReservationAnonymisationReceipt))!;

        Assert.Contains(
            receipt.GetIndexes(),
            index => index.IsUnique &&
                index.Properties.Select(property => property.Name)
                    .SequenceEqual([
                        nameof(ReservationAnonymisationReceipt.ScopeId),
                        nameof(ReservationAnonymisationReceipt.IdempotencyKey)
                    ]));
        Assert.Contains(
            receipt.GetIndexes(),
            index => index.IsUnique &&
                index.Properties.Select(property => property.Name)
                    .SequenceEqual([
                        nameof(ReservationAnonymisationReceipt.ScopeId),
                        nameof(ReservationAnonymisationReceipt.PropertyId),
                        nameof(ReservationAnonymisationReceipt.ReservationId),
                        nameof(ReservationAnonymisationReceipt.ResultingReservationVersion)
                    ]));
        string[] constraints =
        [
            "CK_reservation_anonymisation_receipts_contract",
            "CK_reservation_anonymisation_receipts_revisions",
            "CK_reservation_anonymisation_receipts_versions",
            "CK_reservation_anonymisation_receipts_outcome",
            "CK_reservation_anonymisation_receipts_counts",
            "CK_reservation_anonymisation_receipts_digests",
            "CK_reservation_anonymisation_receipts_actor"
        ];
        Assert.All(
            constraints,
            constraint => Assert.Contains(
                designReceipt.GetCheckConstraints(),
                candidate => candidate.Name == constraint));
        IForeignKey parent = Assert.Single(receipt.GetForeignKeys());
        Assert.Equal(DeleteBehavior.Restrict, parent.DeleteBehavior);
        Assert.Equal(
            [
                nameof(ReservationAnonymisationReceipt.ScopeId),
                nameof(ReservationAnonymisationReceipt.ReservationId)
            ],
            parent.Properties.Select(property => property.Name));
    }

    [Fact]
    public void Model_keeps_restore_proof_independent_from_operational_row()
    {
        using ReservationsDbContext dbContext = CreateDbContext();
        IEntityType tombstone = dbContext.Model.FindEntityType(
            typeof(ReservationAnonymisationTombstone))!;
        IEntityType restoreReceipt = dbContext.Model.FindEntityType(
            typeof(ReservationAnonymisationRestoreReceipt))!;
        IEntityType designTombstone =
            dbContext.GetService<IDesignTimeModel>().Model.FindEntityType(
                typeof(ReservationAnonymisationTombstone))!;
        IEntityType designRestoreReceipt =
            dbContext.GetService<IDesignTimeModel>().Model.FindEntityType(
                typeof(ReservationAnonymisationRestoreReceipt))!;

        Assert.Empty(tombstone.GetForeignKeys());
        Assert.True(tombstone.FindProperty(
            nameof(ReservationAnonymisationTombstone.Revision))!
            .IsConcurrencyToken);
        Assert.Contains(
            tombstone.GetIndexes(),
            index => index.IsUnique &&
                index.Properties.Select(property => property.Name)
                    .SequenceEqual([
                        nameof(ReservationAnonymisationTombstone.ScopeId),
                        nameof(ReservationAnonymisationTombstone.LedgerEntryId)
                    ]));
        Assert.Contains(
            designTombstone.GetCheckConstraints(),
            constraint => constraint.Name ==
                "CK_reservation_anonymisation_tombstones_replay");

        IForeignKey parent = Assert.Single(
            restoreReceipt.GetForeignKeys());
        Assert.Equal(
            typeof(ReservationAnonymisationTombstone),
            parent.PrincipalEntityType.ClrType);
        Assert.Equal(DeleteBehavior.Restrict, parent.DeleteBehavior);
        Assert.Contains(
            designRestoreReceipt.GetCheckConstraints(),
            constraint => constraint.Name ==
                "CK_reservation_anonymisation_restore_receipts_digests");
    }

    [Fact]
    public async Task Restore_receipts_are_append_only_and_tombstones_survive()
    {
        await using ReservationsDbContext dbContext = CreateDbContext();
        Guid reservationId = Guid.NewGuid();
        Guid propertyId = Guid.NewGuid();
        Guid ownerReceiptId = Guid.NewGuid();
        Guid ledgerEntryId = Guid.NewGuid();
        DateTimeOffset completedAtUtc =
            new(2026, 7, 26, 2, 0, 0, TimeSpan.Zero);
        DateTimeOffset replayedAtUtc = completedAtUtc.AddHours(1);
        ReservationAnonymisationTombstone tombstone =
            ReservationAnonymisationTombstone.Restore(
                "tenant-a",
                reservationId,
                propertyId,
                ownerReceiptContractVersion: 1,
                ownerReceiptId,
                new string('a', 64),
                resultingReservationVersion: 2,
                resultingDetailsRevision: 2,
                completedAtUtc,
                ledgerEntryId,
                replayedAtUtc).Value;
        ReservationAnonymisationRestoreReceipt restoreReceipt =
            ReservationAnonymisationRestoreReceipt.Create(
                "tenant-a",
                ledgerEntryId,
                tenantSequence: 1,
                new string('b', 64),
                propertyId,
                reservationId,
                ownerReceiptContractVersion: 1,
                ownerReceiptId,
                new string('a', 64),
                resultingReservationVersion: 2,
                resultingDetailsRevision: 2,
                completedAtUtc,
                tombstone.Revision,
                replayedAtUtc).Value;
        dbContext.AnonymisationTombstones.Add(tombstone);
        dbContext.AnonymisationRestoreReceipts.Add(restoreReceipt);
        await dbContext.SaveChangesAsync();

        dbContext.AnonymisationRestoreReceipts.Remove(restoreReceipt);
        InvalidOperationException receiptError =
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => dbContext.SaveChangesAsync());
        Assert.Contains("append-only", receiptError.Message);
        dbContext.Entry(restoreReceipt).State = EntityState.Detached;

        dbContext.AnonymisationTombstones.Remove(tombstone);
        InvalidOperationException tombstoneError =
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => dbContext.SaveChangesAsync());
        Assert.Contains("cannot be deleted", tombstoneError.Message);
    }

    [Fact]
    public void Model_has_immutable_scoped_correction_receipt_constraints()
    {
        using ReservationsDbContext dbContext = CreateDbContext();
        IEntityType receipt = dbContext.Model.FindEntityType(
            typeof(ReservationDataRightsCorrectionReceipt))!;
        IEntityType designReceipt = dbContext.GetService<IDesignTimeModel>()
            .Model.FindEntityType(typeof(ReservationDataRightsCorrectionReceipt))!;

        Assert.Contains(
            receipt.GetIndexes(),
            index => index.IsUnique && index.Properties.Select(property => property.Name)
                .SequenceEqual([
                    nameof(ReservationDataRightsCorrectionReceipt.ScopeId),
                    nameof(ReservationDataRightsCorrectionReceipt.IdempotencyKey)
                ]));
        Assert.Contains(
            receipt.GetIndexes(),
            index => index.IsUnique && index.Properties.Select(property => property.Name)
                .SequenceEqual([
                    nameof(ReservationDataRightsCorrectionReceipt.ScopeId),
                    nameof(ReservationDataRightsCorrectionReceipt.DetailsChangeEventId)
                ]));
        Assert.Contains(
            designReceipt.GetCheckConstraints(),
            constraint => constraint.Name ==
                "CK_reservation_data_rights_correction_receipts_versions");
        Assert.Contains(
            designReceipt.GetCheckConstraints(),
            constraint => constraint.Name ==
                "CK_reservation_data_rights_correction_receipts_contract");
    }

    [Fact]
    public async Task Correction_receipts_are_append_only_after_insertion()
    {
        await using ReservationsDbContext dbContext = CreateDbContext();
        ReservationDataRightsCorrectionReceipt receipt =
            ReservationDataRightsCorrectionReceipt.Create(
                Guid.NewGuid(),
                "tenant-a",
                Guid.NewGuid(),
                Guid.NewGuid(),
                Guid.NewGuid(),
                approvalRevision: 2,
                Guid.NewGuid(),
                new(
                    PreviousRecordVersion: 3,
                    CurrentRecordVersion: 4,
                    PreviousDetailsRevision: 1,
                    CurrentDetailsRevision: 2,
                    [ReservationDetailsField.PrimaryGuestName],
                    Guid.NewGuid(),
                    Guid.NewGuid(),
                    new DateTimeOffset(2026, 7, 25, 12, 0, 0, TimeSpan.Zero)),
                Guid.NewGuid(),
                Guid.NewGuid()).Value;
        dbContext.DataRightsCorrectionReceipts.Add(receipt);
        await dbContext.SaveChangesAsync();

        dbContext.DataRightsCorrectionReceipts.Remove(receipt);

        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => dbContext.SaveChangesAsync());
        Assert.Equal(
            "Reservation data-rights correction receipts are append-only.",
            exception.Message);
    }

    [Fact]
    public void Model_has_scoped_processing_restriction_lifecycle_and_receipt_constraints()
    {
        using ReservationsDbContext dbContext = CreateDbContext();
        IModel designModel = dbContext.GetService<IDesignTimeModel>().Model;
        IEntityType restriction = dbContext.Model.FindEntityType(
            typeof(ReservationProcessingRestriction))!;
        IEntityType designRestriction = designModel.FindEntityType(
            typeof(ReservationProcessingRestriction))!;
        IEntityType projection = dbContext.Model.FindEntityType(
            typeof(ReservationProcessingRestrictionProjection))!;
        IEntityType designProjection = designModel.FindEntityType(
            typeof(ReservationProcessingRestrictionProjection))!;
        IEntityType receipt = dbContext.Model.FindEntityType(
            typeof(ReservationProcessingRestrictionReceipt))!;
        IEntityType designReceipt = designModel.FindEntityType(
            typeof(ReservationProcessingRestrictionReceipt))!;

        Assert.True(restriction.FindProperty(
            nameof(ReservationProcessingRestriction.Version))!.IsConcurrencyToken);
        Assert.True(projection.FindProperty(
            nameof(ReservationProcessingRestrictionProjection.Revision))!
            .IsConcurrencyToken);
        Assert.Contains(
            restriction.GetIndexes(),
            index => index.IsUnique &&
                index.Properties.Select(property => property.Name).SequenceEqual([
                    nameof(ReservationProcessingRestriction.ScopeId),
                    nameof(ReservationProcessingRestriction.PropertyId),
                    nameof(ReservationProcessingRestriction.ReservationId),
                    nameof(ReservationProcessingRestriction.ApplyCaseId),
                    nameof(ReservationProcessingRestriction.ApplyApprovalRevision)
                ]));
        Assert.Contains(
            receipt.GetIndexes(),
            index => index.IsUnique &&
                index.Properties.Select(property => property.Name).SequenceEqual([
                    nameof(ReservationProcessingRestrictionReceipt.ScopeId),
                    nameof(ReservationProcessingRestrictionReceipt.IdempotencyKey)
                ]));
        Assert.Contains(
            designRestriction.GetCheckConstraints(),
            constraint => constraint.Name ==
                "CK_reservation_processing_restrictions_lifecycle");
        Assert.Contains(
            designProjection.GetCheckConstraints(),
            constraint => constraint.Name ==
                "CK_reservation_processing_restriction_state_effective");
        Assert.Contains(
            designReceipt.GetCheckConstraints(),
            constraint => constraint.Name ==
                "CK_reservation_processing_restriction_receipts_versions");
    }

    [Fact]
    public async Task Processing_restriction_receipts_are_append_only_after_insertion()
    {
        await using ReservationsDbContext dbContext = CreateDbContext();
        ReservationProcessingRestrictionReceipt receipt =
            ReservationProcessingRestrictionReceipt.Create(
                Guid.NewGuid(),
                "tenant-a",
                Guid.NewGuid(),
                Guid.NewGuid(),
                ReservationProcessingRestrictionAction.Apply,
                Guid.NewGuid(),
                Guid.NewGuid(),
                Guid.NewGuid(),
                approvalRevision: 2,
                selectedReservationVersion: 3,
                contractVersion: 1,
                resultingRestrictionVersion: 1,
                resultingProjectionRevision: 1,
                effectiveRestricted: true,
                Guid.NewGuid(),
                new DateTimeOffset(2026, 7, 25, 12, 0, 0, TimeSpan.Zero)).Value;
        dbContext.ProcessingRestrictionReceipts.Add(receipt);
        await dbContext.SaveChangesAsync();

        dbContext.ProcessingRestrictionReceipts.Remove(receipt);

        InvalidOperationException exception =
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => dbContext.SaveChangesAsync());
        Assert.Equal(
            "Reservation processing-restriction receipts are append-only.",
            exception.Message);
    }

    [Fact]
    public void Model_has_scoped_hold_receipt_and_operation_lock_invariants()
    {
        using ReservationsDbContext dbContext = CreateDbContext();
        IModel designModel = dbContext.GetService<IDesignTimeModel>().Model;
        IEntityType hold = dbContext.Model.FindEntityType(
            typeof(ReservationDataHold))!;
        IEntityType designHold = designModel.FindEntityType(
            typeof(ReservationDataHold))!;
        IEntityType receipt = dbContext.Model.FindEntityType(
            typeof(ReservationDataHoldReceipt))!;
        IEntityType designReceipt = designModel.FindEntityType(
            typeof(ReservationDataHoldReceipt))!;
        IEntityType operationLock = dbContext.Model.FindEntityType(
            typeof(ReservationOperationLock))!;
        IEntityType designOperationLock = designModel.FindEntityType(
            typeof(ReservationOperationLock))!;

        Assert.True(hold.FindProperty(
            nameof(ReservationDataHold.Version))!.IsConcurrencyToken);
        Assert.True(operationLock.FindProperty(
            nameof(ReservationOperationLock.Revision))!.IsConcurrencyToken);
        Assert.Contains(
            receipt.GetIndexes(),
            index => index.IsUnique &&
                index.Properties.Select(property => property.Name).SequenceEqual([
                    nameof(ReservationDataHoldReceipt.ScopeId),
                    nameof(ReservationDataHoldReceipt.IdempotencyKey)
                ]));
        Assert.Contains(
            receipt.GetIndexes(),
            index => index.IsUnique &&
                index.Properties.Select(property => property.Name).SequenceEqual([
                    nameof(ReservationDataHoldReceipt.ScopeId),
                    nameof(ReservationDataHoldReceipt.HoldId),
                    nameof(ReservationDataHoldReceipt.Action)
                ]));
        Assert.Contains(
            operationLock.GetIndexes(),
            index => index.IsUnique &&
                index.Properties.Select(property => property.Name).SequenceEqual([
                    nameof(ReservationOperationLock.ScopeId),
                    nameof(ReservationOperationLock.ReservationId)
                ]));
        Assert.Contains(
            designHold.GetCheckConstraints(),
            constraint => constraint.Name ==
                "CK_reservation_data_holds_lifecycle");
        Assert.Contains(
            designReceipt.GetCheckConstraints(),
            constraint => constraint.Name ==
                "CK_reservation_data_hold_receipts_versions");
        Assert.Contains(
            designOperationLock.GetCheckConstraints(),
            constraint => constraint.Name ==
                "CK_reservation_operation_locks_revision");
    }

    [Fact]
    public async Task Data_hold_receipts_are_append_only_after_insertion()
    {
        await using ReservationsDbContext dbContext = CreateDbContext();
        DateTimeOffset now =
            new(2026, 7, 25, 20, 0, 0, TimeSpan.Zero);
        ReservationDataHold hold = ReservationDataHold.Place(
            Guid.NewGuid(),
            "tenant-a",
            Guid.NewGuid(),
            Guid.NewGuid(),
            "regulatory-request",
            "user:privacy",
            now).Value;
        ReservationDataHoldReceipt receipt =
            ReservationDataHoldReceipt.Create(
                Guid.NewGuid(),
                "tenant-a",
                Guid.NewGuid(),
                hold,
                ReservationDataHoldAction.Place,
                selectedReservationVersion: 3,
                selectedDetailsRevision: 2,
                now).Value;
        dbContext.DataHolds.Add(hold);
        dbContext.DataHoldReceipts.Add(receipt);
        await dbContext.SaveChangesAsync();

        dbContext.DataHoldReceipts.Remove(receipt);

        InvalidOperationException exception =
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => dbContext.SaveChangesAsync());
        Assert.Equal(
            "Reservation data-hold receipts are append-only.",
            exception.Message);
    }

    [Fact]
    public void Model_has_scoped_inventory_projection_children_and_versions()
    {
        using ReservationsDbContext dbContext = CreateDbContext();
        IEntityType unitEntity = dbContext.Model.FindEntityType(typeof(ReservationInventoryUnitProjection))!;
        IEntityType allocationEntity = dbContext.Model.FindEntityType(typeof(ReservationInventoryAllocationProjection))!;
        IEntityType allocationUnitEntity = dbContext.Model.FindEntityType(typeof(ReservationInventoryAllocationUnitProjection))!;
        IForeignKey parent = Assert.Single(
            allocationUnitEntity.GetForeignKeys(),
            candidate => candidate.PrincipalEntityType == allocationEntity);

        Assert.True(unitEntity.FindProperty(nameof(ReservationInventoryUnitProjection.UnitVersion))!.IsConcurrencyToken);
        Assert.Equal(["ScopeId", "AllocationId"], parent.Properties.Select(property => property.Name));
        Assert.Equal(["ScopeId", "Id"], parent.PrincipalKey.Properties.Select(property => property.Name));
        Assert.Equal(DeleteBehavior.Cascade, parent.DeleteBehavior);
    }

    [Fact]
    public void Model_has_indexed_revision_bound_arrival_reminders()
    {
        using ReservationsDbContext dbContext = CreateDbContext();
        IEntityType reminder = dbContext.Model.FindEntityType(typeof(ReservationArrivalReminder))!;
        IEntityType property = dbContext.Model.FindEntityType(typeof(ReservationPropertyProjection))!;

        Assert.True(reminder.FindProperty(nameof(ReservationArrivalReminder.Version))!.IsConcurrencyToken);
        Assert.True(property.FindProperty(nameof(ReservationPropertyProjection.TopologySourceVersion))!.IsConcurrencyToken);
        Assert.True(property.FindProperty(nameof(ReservationPropertyProjection.PolicySourceVersion))!.IsConcurrencyToken);
        Assert.Contains(
            reminder.GetIndexes(),
            index => index.IsUnique && index.Properties.Select(item => item.Name).SequenceEqual([
                nameof(ReservationArrivalReminder.ScopeId),
                nameof(ReservationArrivalReminder.ReservationId),
                nameof(ReservationArrivalReminder.DetailsRevision),
                nameof(ReservationArrivalReminder.TimeZoneId),
                nameof(ReservationArrivalReminder.LeadTimeMinutes)
            ]));
        Assert.Contains(
            reminder.GetIndexes(),
            index => index.Properties.Select(item => item.Name).SequenceEqual([
                nameof(ReservationArrivalReminder.ScopeId),
                nameof(ReservationArrivalReminder.State),
                nameof(ReservationArrivalReminder.DueAtUtc)
            ]));
    }

    private static ReservationsDbContext CreateDbContext()
    {
        DbContextOptions<ReservationsDbContext> options = new DbContextOptionsBuilder<ReservationsDbContext>()
            .UseInMemoryDatabase($"reservations-model-{Guid.NewGuid():N}")
            .Options;
        return new(options, new TestScopeContext());
    }

    private static string? ColumnType(IEntityType entityType, string propertyName) =>
        entityType.FindProperty(propertyName)?.FindAnnotation(RelationalAnnotationNames.ColumnType)?.Value as string;

    private sealed class TestScopeContext : IScopeContext
    {
        public bool IsEnabled => true;
        public string ScopeId => "tenant-a";
    }
}
