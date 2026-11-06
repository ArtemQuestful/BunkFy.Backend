namespace BunkFy.Modules.Guests.Tests;

using BunkFy.Modules.Guests.Application.Ports;
using BunkFy.Modules.Guests.Domain.Aggregates;
using BunkFy.Modules.Guests.Domain.DataRights;
using BunkFy.Modules.Guests.Domain.Models;
using BunkFy.Modules.Guests.Persistence;
using BunkFy.Modules.Guests.Persistence.Models;
using BunkFy.Modules.Guests.Persistence.Repositories;
using BunkFy.Modules.Guests.Persistence.TenantTermination;
using BunkFy.Modules.Properties.Contracts;
using Gma.Framework.Domain;
using Gma.Framework.Persistence.EntityFrameworkCore;
using Gma.Framework.Scoping;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Xunit;
using GuestStayRole = BunkFy.Modules.Guests.Contracts.GuestStayRole;
using GuestStayStatus = BunkFy.Modules.Guests.Contracts.GuestStayStatus;
using GuestsModuleMetadata = BunkFy.Modules.Guests.Contracts.GuestsModuleMetadata;

[Trait("Category", "Unit")]
public sealed class GuestsModelTests
{
    [Fact]
    public void Every_guests_scope_shaped_entity_is_scoped_and_filtered()
    {
        using GuestsDbContext dbContext = CreateDbContext();

        IEntityType[] scopeShapedEntities = dbContext.Model.GetEntityTypes()
            .Where(entity =>
                !entity.IsOwned() &&
                entity.ClrType.Namespace?.StartsWith(
                    "BunkFy.Modules.Guests.",
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
    public void Every_guests_tenant_relationship_is_scope_qualified()
    {
        using GuestsDbContext dbContext = CreateDbContext();

        IEntityType[] scopedEntities = dbContext.Model.GetEntityTypes()
            .Where(entity =>
                !entity.IsOwned() &&
                entity.ClrType.Namespace?.StartsWith(
                    "BunkFy.Modules.Guests.",
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
    public void Canonical_profiles_declare_authoritative_database_invariants()
    {
        using GuestsDbContext dbContext = CreateDbContext();
        IEntityType profile = dbContext.GetService<IDesignTimeModel>()
            .Model
            .FindEntityType(typeof(GuestProfile))!;

        AssertConstraints(
            profile,
            "CK_guest_profiles_coordinates",
            "CK_guest_profiles_version",
            "CK_guest_profiles_projection_ordinal",
            "CK_guest_profiles_display_name",
            "CK_guest_profiles_created_by",
            "CK_guest_profiles_last_changed_by",
            "CK_guest_profiles_search_shape",
            "CK_guest_profiles_optional_text",
            "CK_guest_profiles_state_versions",
            "CK_guest_profiles_lifecycle",
            "CK_guest_profiles_timestamps",
            "CK_guest_profiles_anonymised_profile");
    }

    [Fact]
    public void Processing_restriction_graph_declares_authoritative_database_invariants()
    {
        using GuestsDbContext dbContext = CreateDbContext();
        IModel designModel = dbContext.GetService<IDesignTimeModel>().Model;

        IEntityType projection = dbContext.Model.FindEntityType(
            typeof(GuestProcessingRestrictionProjection))!;
        IEntityType designProjection = designModel.FindEntityType(
            typeof(GuestProcessingRestrictionProjection))!;
        Assert.Equal(
            ValueGenerated.OnAdd,
            projection.FindProperty(
                nameof(GuestProcessingRestrictionProjection.ProjectionOrdinal))!
                .ValueGenerated);
        Assert.True(projection.FindProperty(
            nameof(GuestProcessingRestrictionProjection.Revision))!
            .IsConcurrencyToken);
        AssertConstraints(
            designProjection,
            "CK_guest_processing_restrictions_projection_coordinates",
            "CK_guest_processing_restrictions_contract_version",
            "CK_guest_processing_restrictions_revision",
            "CK_guest_processing_restrictions_projection_ordinal",
            "CK_guest_processing_restrictions_transition_timestamp",
            "CK_guest_processing_restrictions_effective_state");

        IEntityType restriction = dbContext.Model.FindEntityType(
            typeof(GuestProcessingRestriction))!;
        IEntityType designRestriction = designModel.FindEntityType(
            typeof(GuestProcessingRestriction))!;
        Assert.True(restriction.FindProperty(
            nameof(GuestProcessingRestriction.Version))!.IsConcurrencyToken);
        Assert.Contains(restriction.GetKeys(), key =>
            key.Properties.Select(property => property.Name).SequenceEqual([
                nameof(GuestProcessingRestriction.ScopeId),
                nameof(GuestProcessingRestriction.Id),
                nameof(GuestProcessingRestriction.PropertyId),
                nameof(GuestProcessingRestriction.GuestId)
            ]));
        IForeignKey projectionReference = Assert.Single(
            restriction.GetForeignKeys());
        Assert.Equal(
            [
                nameof(GuestProcessingRestriction.ScopeId),
                nameof(GuestProcessingRestriction.PropertyId),
                nameof(GuestProcessingRestriction.GuestId)
            ],
            projectionReference.Properties.Select(property => property.Name));
        Assert.Equal(
            [
                nameof(GuestProcessingRestrictionProjection.ScopeId),
                nameof(GuestProcessingRestrictionProjection.PropertyId),
                nameof(GuestProcessingRestrictionProjection.GuestId)
            ],
            projectionReference.PrincipalKey.Properties.Select(
                property => property.Name));
        Assert.Equal(DeleteBehavior.Restrict, projectionReference.DeleteBehavior);
        AssertConstraints(
            designRestriction,
            "CK_guest_processing_restrictions_coordinates",
            "CK_guest_processing_restrictions_audit_text",
            "CK_guest_processing_restrictions_timestamps",
            "CK_guest_processing_restrictions_apply_approval",
            "CK_guest_processing_restrictions_lifecycle");

        IEntityType receipt = dbContext.Model.FindEntityType(
            typeof(GuestProcessingRestrictionReceipt))!;
        IEntityType designReceipt = designModel.FindEntityType(
            typeof(GuestProcessingRestrictionReceipt))!;
        Assert.Single(receipt.GetKeys());
        Assert.Contains(receipt.GetIndexes(), index =>
            index.Properties.Select(property => property.Name).SequenceEqual([
                nameof(GuestProcessingRestrictionReceipt.ScopeId),
                nameof(GuestProcessingRestrictionReceipt.RestrictionId),
                nameof(GuestProcessingRestrictionReceipt.PropertyId),
                nameof(GuestProcessingRestrictionReceipt.GuestId)
            ]));
        IForeignKey restrictionReference = Assert.Single(receipt.GetForeignKeys());
        Assert.Equal(
            [
                nameof(GuestProcessingRestrictionReceipt.ScopeId),
                nameof(GuestProcessingRestrictionReceipt.RestrictionId),
                nameof(GuestProcessingRestrictionReceipt.PropertyId),
                nameof(GuestProcessingRestrictionReceipt.GuestId)
            ],
            restrictionReference.Properties.Select(property => property.Name));
        Assert.Equal(
            [
                nameof(GuestProcessingRestriction.ScopeId),
                nameof(GuestProcessingRestriction.Id),
                nameof(GuestProcessingRestriction.PropertyId),
                nameof(GuestProcessingRestriction.GuestId)
            ],
            restrictionReference.PrincipalKey.Properties.Select(
                property => property.Name));
        Assert.Equal(DeleteBehavior.Restrict, restrictionReference.DeleteBehavior);
        AssertConstraints(
            designReceipt,
            "CK_guest_processing_restriction_receipts_coordinates",
            "CK_guest_processing_restriction_receipts_audit_text",
            "CK_guest_processing_restriction_receipts_timestamp",
            "CK_guest_processing_restriction_receipts_versions");
    }

    [Fact]
    public void Correction_receipts_declare_authoritative_database_invariants()
    {
        using GuestsDbContext dbContext = CreateDbContext();
        IEntityType receipt = dbContext.Model.FindEntityType(
            typeof(GuestDataRightsCorrectionReceipt))!;
        IEntityType designReceipt = dbContext.GetService<IDesignTimeModel>()
            .Model.FindEntityType(typeof(GuestDataRightsCorrectionReceipt))!;

        Assert.Single(receipt.GetKeys());
        Assert.Contains(receipt.GetIndexes(), index => index.IsUnique &&
            index.GetDatabaseName() ==
                "UX_guest_correction_receipts_case_approval" &&
            index.Properties.Select(property => property.Name).SequenceEqual([
                nameof(GuestDataRightsCorrectionReceipt.ScopeId),
                nameof(GuestDataRightsCorrectionReceipt.PropertyId),
                nameof(GuestDataRightsCorrectionReceipt.CaseId),
                nameof(GuestDataRightsCorrectionReceipt.ApprovalRevision)
            ]));
        Assert.Contains(receipt.GetIndexes(), index => index.IsUnique &&
            index.GetDatabaseName() ==
                "UX_guest_correction_receipts_guest_version" &&
            index.Properties.Select(property => property.Name).SequenceEqual([
                nameof(GuestDataRightsCorrectionReceipt.ScopeId),
                nameof(GuestDataRightsCorrectionReceipt.GuestId),
                nameof(GuestDataRightsCorrectionReceipt.CurrentRecordVersion)
            ]));
        IForeignKey profileReference = Assert.Single(receipt.GetForeignKeys());
        Assert.Equal(
            [
                nameof(GuestDataRightsCorrectionReceipt.ScopeId),
                nameof(GuestDataRightsCorrectionReceipt.GuestId)
            ],
            profileReference.Properties.Select(property => property.Name));
        Assert.Equal(
            [nameof(GuestProfile.ScopeId), nameof(GuestProfile.Id)],
            profileReference.PrincipalKey.Properties.Select(
                property => property.Name));
        Assert.Equal(DeleteBehavior.Restrict, profileReference.DeleteBehavior);
        AssertConstraints(
            designReceipt,
            "CK_guest_data_rights_correction_receipts_coordinates",
            "CK_guest_data_rights_correction_receipts_timestamp",
            "CK_guest_data_rights_correction_receipts_contract",
            "CK_guest_data_rights_correction_receipts_approval_revision",
            "CK_guest_data_rights_correction_receipts_versions",
            "CK_guest_data_rights_correction_receipts_changed_fields");
    }

    [Fact]
    public void Anonymisation_proof_graph_declares_authoritative_database_invariants()
    {
        using GuestsDbContext dbContext = CreateDbContext();
        IModel designModel = dbContext.GetService<IDesignTimeModel>().Model;

        IEntityType receipt = dbContext.Model.FindEntityType(
            typeof(GuestAnonymisationReceipt))!;
        Assert.Equal(2, receipt.GetKeys().Count());
        Assert.Contains(receipt.GetIndexes(), index => index.IsUnique &&
            index.GetDatabaseName() ==
                "UX_guest_anonymisation_receipts_event" &&
            index.Properties.Select(property => property.Name).SequenceEqual([
                nameof(GuestAnonymisationReceipt.ScopeId),
                nameof(GuestAnonymisationReceipt.EventId)
            ]));
        IForeignKey profileReference = Assert.Single(receipt.GetForeignKeys());
        Assert.Equal(
            "FK_guest_anonymisation_receipts_guest_profile",
            profileReference.GetConstraintName());
        Assert.Equal(
            [
                nameof(GuestAnonymisationReceipt.ScopeId),
                nameof(GuestAnonymisationReceipt.GuestId)
            ],
            profileReference.Properties.Select(property => property.Name));
        Assert.Equal(
            [nameof(GuestProfile.ScopeId), nameof(GuestProfile.Id)],
            profileReference.PrincipalKey.Properties.Select(
                property => property.Name));
        Assert.Equal(DeleteBehavior.Restrict, profileReference.DeleteBehavior);
        AssertConstraints(
            designModel.FindEntityType(typeof(GuestAnonymisationReceipt))!,
            "CK_guest_anonymisation_receipts_coordinates",
            "CK_guest_anonymisation_receipts_contract",
            "CK_guest_anonymisation_receipts_revisions",
            "CK_guest_anonymisation_receipts_versions",
            "CK_guest_anonymisation_receipts_outcome",
            "CK_guest_anonymisation_receipts_property_count",
            "CK_guest_anonymisation_receipts_digests",
            "CK_guest_anonymisation_receipts_actor",
            "CK_guest_anonymisation_receipts_timestamp");

        IEntityType tombstone = dbContext.Model.FindEntityType(
            typeof(GuestAnonymisationTombstone))!;
        Assert.Equal(2, tombstone.GetKeys().Count());
        Assert.True(tombstone.FindProperty(
            nameof(GuestAnonymisationTombstone.Revision))!.IsConcurrencyToken);
        Assert.Contains(tombstone.GetIndexes(), index => index.IsUnique &&
            index.GetDatabaseName() ==
                "UX_guest_anonymisation_tombstones_ledger_entry" &&
            index.GetFilter() == "\"LedgerEntryId\" IS NOT NULL" &&
            index.Properties.Select(property => property.Name).SequenceEqual([
                nameof(GuestAnonymisationTombstone.ScopeId),
                nameof(GuestAnonymisationTombstone.LedgerEntryId)
            ]));
        IForeignKey tombstoneProfile = Assert.Single(
            tombstone.GetForeignKeys());
        Assert.Equal(
            "FK_guest_anonymisation_tombstones_guest_profile",
            tombstoneProfile.GetConstraintName());
        Assert.Equal(DeleteBehavior.Restrict, tombstoneProfile.DeleteBehavior);
        AssertConstraints(
            designModel.FindEntityType(typeof(GuestAnonymisationTombstone))!,
            "CK_guest_anonymisation_tombstones_coordinates",
            "CK_guest_anonymisation_tombstones_contract",
            "CK_guest_anonymisation_tombstones_revision",
            "CK_guest_anonymisation_tombstones_state",
            "CK_guest_anonymisation_tombstones_authority",
            "CK_guest_anonymisation_tombstones_receipt_digest",
            "CK_guest_anonymisation_tombstones_restore_pair",
            "CK_guest_anonymisation_tombstones_timestamps",
            "CK_guest_anonymisation_tombstones_lifecycle");

        IEntityType restoreReceipt = dbContext.Model.FindEntityType(
            typeof(GuestAnonymisationRestoreReceipt))!;
        Assert.Single(restoreReceipt.GetKeys());
        Assert.Contains(restoreReceipt.GetIndexes(), index => index.IsUnique &&
            index.GetDatabaseName() ==
                "UX_guest_anonymisation_restore_receipts_tombstone" &&
            index.Properties.Select(property => property.Name).SequenceEqual([
                nameof(GuestAnonymisationRestoreReceipt.ScopeId),
                nameof(GuestAnonymisationRestoreReceipt.GuestId)
            ]));
        IForeignKey tombstoneReference = Assert.Single(
            restoreReceipt.GetForeignKeys());
        Assert.Equal(
            "FK_guest_anonymisation_restore_receipts_tombstone",
            tombstoneReference.GetConstraintName());
        Assert.Equal(
            [
                nameof(GuestAnonymisationRestoreReceipt.ScopeId),
                nameof(GuestAnonymisationRestoreReceipt.GuestId)
            ],
            tombstoneReference.Properties.Select(property => property.Name));
        Assert.Equal(
            [
                nameof(GuestAnonymisationTombstone.ScopeId),
                nameof(GuestAnonymisationTombstone.Id)
            ],
            tombstoneReference.PrincipalKey.Properties.Select(
                property => property.Name));
        Assert.Equal(DeleteBehavior.Restrict, tombstoneReference.DeleteBehavior);
        AssertConstraints(
            designModel.FindEntityType(
                typeof(GuestAnonymisationRestoreReceipt))!,
            "CK_guest_anonymisation_restore_receipts_coordinates",
            "CK_guest_anonymisation_restore_receipts_contract",
            "CK_guest_anonymisation_restore_receipts_identity",
            "CK_guest_anonymisation_restore_receipts_versions",
            "CK_guest_anonymisation_restore_receipts_digests",
            "CK_guest_anonymisation_restore_receipts_timestamp");
    }

    [Fact]
    public void Management_operations_are_guest_scoped_constrained_and_cascade_owned()
    {
        using GuestsDbContext dbContext = CreateDbContext();
        IEntityType operation = dbContext.Model.FindEntityType(
            typeof(GuestManagementOperation))!;
        IEntityType designOperation = dbContext.GetService<IDesignTimeModel>()
            .Model
            .FindEntityType(typeof(GuestManagementOperation))!;

        Assert.Equal(
            [
                nameof(GuestManagementOperation.ScopeId),
                nameof(GuestManagementOperation.GuestId),
                nameof(GuestManagementOperation.Id)
            ],
            operation.FindPrimaryKey()!.Properties.Select(property => property.Name));
        Assert.Equal(
            64,
            operation.FindProperty(
                nameof(GuestManagementOperation.RequestFingerprint))!.GetMaxLength());
        Assert.NotEmpty(operation.GetDeclaredQueryFilters());
        Assert.Contains(operation.GetIndexes(), index =>
            index.Properties.Select(property => property.Name).SequenceEqual([
                nameof(GuestManagementOperation.ScopeId),
                nameof(GuestManagementOperation.PropertyId),
                nameof(GuestManagementOperation.CompletedAtUtc),
                nameof(GuestManagementOperation.Id)
            ]));
        IForeignKey ownership = Assert.Single(operation.GetForeignKeys());
        Assert.Equal(DeleteBehavior.Cascade, ownership.DeleteBehavior);
        Assert.Equal(
            [
                nameof(GuestManagementOperation.ScopeId),
                nameof(GuestManagementOperation.GuestId)
            ],
            ownership.Properties.Select(property => property.Name));
        Assert.Contains(
            designOperation.GetCheckConstraints(),
            constraint => constraint.Name ==
                "CK_guest_management_operations_kind");
        Assert.Contains(
            designOperation.GetCheckConstraints(),
            constraint => constraint.Name ==
                "CK_guest_management_operations_versions");
        Assert.Contains(
            designOperation.GetCheckConstraints(),
            constraint => constraint.Name ==
                "CK_guest_management_operations_result");
    }

    [Fact]
    public void Tenant_revision_is_scope_keyed_and_concurrency_guarded()
    {
        using GuestsDbContext dbContext = CreateDbContext();

        IEntityType revisionEntity = dbContext.Model.FindEntityType(
            typeof(GuestsTenantRevision))!;
        IEntityType designRevisionEntity = dbContext
            .GetService<IDesignTimeModel>()
            .Model
            .FindEntityType(typeof(GuestsTenantRevision))!;

        Assert.Equal(
            [nameof(GuestsTenantRevision.ScopeId)],
            revisionEntity.FindPrimaryKey()!.Properties.Select(
                property => property.Name));
        Assert.True(
            revisionEntity.FindProperty(
                nameof(GuestsTenantRevision.Revision))!
                .IsConcurrencyToken);
        Assert.NotEmpty(revisionEntity.GetDeclaredQueryFilters());
        Assert.Contains(
            designRevisionEntity.GetCheckConstraints(),
            constraint => string.Equals(
                constraint.Name,
                "CK_guests_tenant_revision_positive",
                StringComparison.Ordinal));
        Assert.Contains(
            designRevisionEntity.GetCheckConstraints(),
            constraint => string.Equals(
                constraint.Name,
                "CK_guests_tenant_revision_lifecycle",
                StringComparison.Ordinal));
    }

    [Fact]
    public void Tenant_destruction_progress_and_receipt_are_scope_unique_and_constrained()
    {
        using GuestsDbContext dbContext = CreateDbContext();
        IModel designModel = dbContext.GetService<IDesignTimeModel>().Model;
        IEntityType operation = designModel.FindEntityType(
            typeof(GuestsTenantDestroyOperation))!;
        IEntityType receipt = designModel.FindEntityType(
            typeof(GuestsTenantDestroyReceipt))!;

        Assert.Equal(
            [nameof(GuestsTenantDestroyOperation.OperationId)],
            operation.FindPrimaryKey()!.Properties.Select(
                property => property.Name));
        Assert.True(operation.FindProperty(
            nameof(GuestsTenantDestroyOperation.ConcurrencyVersion))!
            .IsConcurrencyToken);
        Assert.Contains(
            operation.GetIndexes(),
            index => index.IsUnique &&
                index.Properties.Select(property => property.Name)
                    .SequenceEqual([
                        nameof(GuestsTenantDestroyOperation.ScopeId)
                    ]));
        Assert.Contains(
            operation.GetCheckConstraints(),
            constraint => constraint.Name ==
                "CK_guests_tenant_destroy_operation_batch");
        Assert.Contains(
            operation.GetCheckConstraints(),
            constraint => constraint.Name ==
                "CK_guests_tenant_destroy_operation_progress");

        Assert.Equal(
            [nameof(GuestsTenantDestroyReceipt.OperationId)],
            receipt.FindPrimaryKey()!.Properties.Select(
                property => property.Name));
        Assert.Contains(
            receipt.GetIndexes(),
            index => index.IsUnique &&
                index.Properties.Select(property => property.Name)
                    .SequenceEqual([
                        nameof(GuestsTenantDestroyReceipt.ScopeId)
                    ]));
        Assert.Contains(
            receipt.GetCheckConstraints(),
            constraint => constraint.Name ==
                "CK_guests_tenant_destroy_receipt_progress");
    }

    [Fact]
    public void Model_has_scoped_non_unique_contact_indexes_and_lifecycle_constraints()
    {
        using GuestsDbContext dbContext = CreateDbContext();
        IEntityType profile = dbContext.Model.FindEntityType(typeof(GuestProfile))!;
        IEntityType designProfile = dbContext.GetService<IDesignTimeModel>().Model.FindEntityType(typeof(GuestProfile))!;

        Assert.True(profile.FindProperty(nameof(GuestProfile.Version))!.IsConcurrencyToken);
        Assert.Equal(ValueGenerated.OnAdd, profile.FindProperty(nameof(GuestProfile.ProjectionOrdinal))!.ValueGenerated);
        Assert.Equal(GuestProfile.ActorIdMaxLength, profile.FindProperty(nameof(GuestProfile.CreatedBy))!.GetMaxLength());
        Assert.NotNull(profile.FindProperty(nameof(GuestProfile.AnonymisedAtUtc)));
        Assert.Contains(profile.GetIndexes(), index => index.IsUnique &&
            index.Properties.Select(item => item.Name).SequenceEqual([
                nameof(GuestProfile.ScopeId),
                nameof(GuestProfile.CreationConfirmationId)
            ]));
        Assert.Contains(profile.GetIndexes(), index => !index.IsUnique && index.Properties.Select(item => item.Name)
            .SequenceEqual([nameof(GuestProfile.ScopeId), nameof(GuestProfile.EmailSearch)]));
        Assert.Contains(profile.GetIndexes(), index => !index.IsUnique && index.Properties.Select(item => item.Name)
            .SequenceEqual([nameof(GuestProfile.ScopeId), nameof(GuestProfile.PhoneSearch)]));
        Assert.Contains(designProfile.GetCheckConstraints(), constraint => constraint.Name == "CK_guest_profiles_lifecycle");
        Assert.Contains(designProfile.GetCheckConstraints(), constraint => constraint.Name == "CK_guest_profiles_created_by");
        IEntityType stay = dbContext.Model.FindEntityType(typeof(GuestStayHistoryEntry))!;
        Assert.NotNull(stay.FindDeclaredQueryFilter(
            ScopeFilterNames.ScopeFilter));
        Assert.True(stay.FindProperty(nameof(GuestStayHistoryEntry.ReservationVersion))!.IsConcurrencyToken);
        Assert.Contains(stay.GetIndexes(), index => index.Properties.Select(item => item.Name)
            .SequenceEqual([
                nameof(GuestStayHistoryEntry.ScopeId),
                nameof(GuestStayHistoryEntry.PropertyId),
                nameof(GuestStayHistoryEntry.IsCurrentParticipant),
                nameof(GuestStayHistoryEntry.GuestId),
                nameof(GuestStayHistoryEntry.Arrival)
            ]));
        Assert.Contains(stay.GetIndexes(), index => index.Properties.Select(item => item.Name)
            .SequenceEqual([
                nameof(GuestStayHistoryEntry.ScopeId),
                nameof(GuestStayHistoryEntry.PropertyId),
                nameof(GuestStayHistoryEntry.GuestId)
            ]));
        IEntityType designStay = dbContext.GetService<IDesignTimeModel>()
            .Model.FindEntityType(typeof(GuestStayHistoryEntry))!;
        Assert.Contains(
            designStay.GetCheckConstraints(),
            constraint => constraint.Name == "CK_guests_stay_history_contract_version");
        IEntityType propertyProjection = dbContext.Model.FindEntityType(
            typeof(GuestPropertyProjection))!;
        Assert.NotNull(propertyProjection.FindDeclaredQueryFilter(
            ScopeFilterNames.ScopeFilter));
        IEntityType dataHold = dbContext.Model.FindEntityType(typeof(GuestDataHold))!;
        Assert.True(dataHold.FindProperty(nameof(GuestDataHold.Version))!.IsConcurrencyToken);
        Assert.Contains(dataHold.GetIndexes(), index =>
            index.Properties.Select(item => item.Name).SequenceEqual([
                nameof(GuestDataHold.ScopeId),
                nameof(GuestDataHold.GuestId),
                nameof(GuestDataHold.State),
                nameof(GuestDataHold.PropertyId),
                nameof(GuestDataHold.Id)
            ]));
        Assert.Contains(dataHold.GetIndexes(), index =>
            index.Properties.Select(item => item.Name).SequenceEqual([
                nameof(GuestDataHold.ScopeId),
                nameof(GuestDataHold.PropertyId),
                nameof(GuestDataHold.GuestId),
                nameof(GuestDataHold.PlacedAtUtc),
                nameof(GuestDataHold.Id)
            ]));
        Assert.Contains(dataHold.GetIndexes(), index =>
            index.Properties.Select(item => item.Name).SequenceEqual([
                nameof(GuestDataHold.ScopeId),
                nameof(GuestDataHold.PropertyId),
                nameof(GuestDataHold.GuestId),
                nameof(GuestDataHold.State),
                nameof(GuestDataHold.PlacedAtUtc),
                nameof(GuestDataHold.Id)
            ]));
        IEntityType designDataHold = dbContext.GetService<IDesignTimeModel>()
            .Model.FindEntityType(typeof(GuestDataHold))!;
        AssertConstraints(
            designDataHold,
            "CK_guest_data_holds_coordinates",
            "CK_guest_data_holds_audit_text",
            "CK_guest_data_holds_timestamps",
            "CK_guest_data_holds_version",
            "CK_guest_data_holds_lifecycle");
        IEntityType dataHoldReceipt =
            dbContext.Model.FindEntityType(typeof(GuestDataHoldReceipt))!;
        Assert.Contains(dataHoldReceipt.GetIndexes(), index => index.IsUnique &&
            index.Properties.Select(item => item.Name).SequenceEqual([
                nameof(GuestDataHoldReceipt.ScopeId),
                nameof(GuestDataHoldReceipt.IdempotencyKey)
            ]));
        Assert.Contains(dataHoldReceipt.GetIndexes(), index => index.IsUnique &&
            index.Properties.Select(item => item.Name).SequenceEqual([
                nameof(GuestDataHoldReceipt.ScopeId),
                nameof(GuestDataHoldReceipt.HoldId),
                nameof(GuestDataHoldReceipt.Action)
            ]));
        IForeignKey holdEvidence = Assert.Single(dataHoldReceipt.GetForeignKeys());
        Assert.Equal(
            [
                nameof(GuestDataHoldReceipt.ScopeId),
                nameof(GuestDataHoldReceipt.HoldId),
                nameof(GuestDataHoldReceipt.PropertyId),
                nameof(GuestDataHoldReceipt.GuestId),
                nameof(GuestDataHoldReceipt.ReasonCode)
            ],
            holdEvidence.Properties.Select(property => property.Name));
        Assert.Equal(
            [
                nameof(GuestDataHold.ScopeId),
                nameof(GuestDataHold.Id),
                nameof(GuestDataHold.PropertyId),
                nameof(GuestDataHold.GuestId),
                nameof(GuestDataHold.ReasonCode)
            ],
            holdEvidence.PrincipalKey.Properties.Select(property => property.Name));
        Assert.Equal(DeleteBehavior.Restrict, holdEvidence.DeleteBehavior);
        IEntityType designDataHoldReceipt = dbContext
            .GetService<IDesignTimeModel>()
            .Model.FindEntityType(typeof(GuestDataHoldReceipt))!;
        AssertConstraints(
            designDataHoldReceipt,
            "CK_guest_data_hold_receipts_coordinates",
            "CK_guest_data_hold_receipts_audit_text",
            "CK_guest_data_hold_receipts_timestamp",
            "CK_guest_data_hold_receipts_versions");
        IEntityType correctionReceipt =
            dbContext.Model.FindEntityType(typeof(GuestDataRightsCorrectionReceipt))!;
        Assert.Contains(correctionReceipt.GetIndexes(), index => index.IsUnique &&
            index.Properties.Select(item => item.Name).SequenceEqual([
                nameof(GuestDataRightsCorrectionReceipt.ScopeId),
                nameof(GuestDataRightsCorrectionReceipt.IdempotencyKey)
            ]));
        IEntityType designReceipt = dbContext.GetService<IDesignTimeModel>()
            .Model.FindEntityType(typeof(GuestDataRightsCorrectionReceipt))!;
        Assert.Contains(
            designReceipt.GetCheckConstraints(),
            constraint => constraint.Name ==
                "CK_guest_data_rights_correction_receipts_versions");
        IEntityType restrictionProjection =
            dbContext.Model.FindEntityType(typeof(GuestProcessingRestrictionProjection))!;
        Assert.True(
            restrictionProjection.FindProperty(
                nameof(GuestProcessingRestrictionProjection.Revision))!.IsConcurrencyToken);
        Assert.Contains(restrictionProjection.GetIndexes(), index =>
            index.Properties.Select(item => item.Name).SequenceEqual([
                nameof(GuestProcessingRestrictionProjection.ScopeId),
                nameof(GuestProcessingRestrictionProjection.PropertyId),
                nameof(GuestProcessingRestrictionProjection.IsRestricted),
                nameof(GuestProcessingRestrictionProjection.GuestId)
            ]));
        IEntityType designRestrictionProjection = dbContext.GetService<IDesignTimeModel>()
            .Model.FindEntityType(typeof(GuestProcessingRestrictionProjection))!;
        Assert.Contains(
            designRestrictionProjection.GetCheckConstraints(),
            constraint => constraint.Name ==
                "CK_guest_processing_restrictions_effective_state");
        IEntityType restriction =
            dbContext.Model.FindEntityType(typeof(GuestProcessingRestriction))!;
        Assert.True(
            restriction.FindProperty(nameof(GuestProcessingRestriction.Version))!.IsConcurrencyToken);
        Assert.Contains(restriction.GetIndexes(), index => index.IsUnique &&
            index.Properties.Select(item => item.Name).SequenceEqual([
                nameof(GuestProcessingRestriction.ScopeId),
                nameof(GuestProcessingRestriction.PropertyId),
                nameof(GuestProcessingRestriction.GuestId),
                nameof(GuestProcessingRestriction.ApplyCaseId),
                nameof(GuestProcessingRestriction.ApplyApprovalRevision)
            ]));
        IEntityType designRestriction = dbContext.GetService<IDesignTimeModel>()
            .Model.FindEntityType(typeof(GuestProcessingRestriction))!;
        Assert.Contains(
            designRestriction.GetCheckConstraints(),
            constraint => constraint.Name ==
                "CK_guest_processing_restrictions_lifecycle");
        IEntityType restrictionReceipt =
            dbContext.Model.FindEntityType(typeof(GuestProcessingRestrictionReceipt))!;
        Assert.Contains(restrictionReceipt.GetIndexes(), index => index.IsUnique &&
            index.Properties.Select(item => item.Name).SequenceEqual([
                nameof(GuestProcessingRestrictionReceipt.ScopeId),
                nameof(GuestProcessingRestrictionReceipt.IdempotencyKey)
            ]));
        IEntityType anonymisationReceipt =
            dbContext.Model.FindEntityType(typeof(GuestAnonymisationReceipt))!;
        Assert.Contains(anonymisationReceipt.GetIndexes(), index => index.IsUnique &&
            index.Properties.Select(item => item.Name).SequenceEqual([
                nameof(GuestAnonymisationReceipt.ScopeId),
                nameof(GuestAnonymisationReceipt.IdempotencyKey)
            ]));
        Assert.Contains(anonymisationReceipt.GetIndexes(), index => index.IsUnique &&
            index.Properties.Select(item => item.Name).SequenceEqual([
                nameof(GuestAnonymisationReceipt.ScopeId),
                nameof(GuestAnonymisationReceipt.CaseId),
                nameof(GuestAnonymisationReceipt.ApprovalRevision),
                nameof(GuestAnonymisationReceipt.OperationRevision),
                nameof(GuestAnonymisationReceipt.GuestId)
            ]));
        IEntityType anonymisationTombstone =
            dbContext.Model.FindEntityType(typeof(GuestAnonymisationTombstone))!;
        Assert.True(anonymisationTombstone.FindProperty(
            nameof(GuestAnonymisationTombstone.Revision))!.IsConcurrencyToken);
        IEntityType operationLock =
            dbContext.Model.FindEntityType(typeof(GuestOperationLock))!;
        Assert.True(operationLock.FindProperty(
            nameof(GuestOperationLock.Revision))!.IsConcurrencyToken);
        Assert.Contains(operationLock.GetIndexes(), index => index.IsUnique &&
            index.Properties.Select(item => item.Name).SequenceEqual([
                nameof(GuestOperationLock.ScopeId),
                nameof(GuestOperationLock.ResourceKind),
                nameof(GuestOperationLock.ResourceId)
            ]));
        IModel designModel = dbContext.GetService<IDesignTimeModel>().Model;
        Assert.Contains(
            designModel.FindEntityType(typeof(GuestAnonymisationReceipt))!.GetCheckConstraints(),
            constraint => constraint.Name == "CK_guest_anonymisation_receipts_versions");
        Assert.Contains(
            designModel.FindEntityType(typeof(GuestAnonymisationTombstone))!.GetCheckConstraints(),
            constraint => constraint.Name == "CK_guest_anonymisation_tombstones_contract");
    }

    [Fact]
    public async Task Plain_projection_roots_are_tenant_filtered_and_write_guarded()
    {
        string databaseName = $"guests-plain-scope-{Guid.NewGuid():N}";
        DbContextOptions<GuestsDbContext> options =
            new DbContextOptionsBuilder<GuestsDbContext>()
                .UseInMemoryDatabase(databaseName)
                .Options;
        Guid propertyId = Guid.NewGuid();
        Guid guestId = Guid.NewGuid();
        Guid reservationId = Guid.NewGuid();

        await using (GuestsDbContext tenantA = new(
            options,
            new TestScopeContext("tenant-a")))
        {
            tenantA.PropertyProjections.Add(new(
                "tenant-a",
                propertyId,
                "Property A",
                "UTC",
                PropertyStatus.Active,
                version: 1));
            tenantA.StayHistory.Add(new(
                "tenant-a",
                guestId,
                reservationId,
                propertyId,
                GuestStayRole.Primary,
                new DateOnly(2026, 1, 1),
                new DateOnly(2026, 1, 2),
                GuestStayStatus.CheckedOut,
                checkedInBusinessDate: null,
                noShowBusinessDate: null,
                new DateOnly(2026, 1, 2),
                isCurrentParticipant: false,
                reservationVersion: 1,
                GuestsModuleMetadata.StayHistoryProjectionVersion));
            await tenantA.SaveChangesAsync();
        }

        await using GuestsDbContext tenantB = new(
            options,
            new TestScopeContext("tenant-b"));
        Assert.Empty(await tenantB.PropertyProjections.ToArrayAsync());
        Assert.Empty(await tenantB.StayHistory.ToArrayAsync());

        tenantB.PropertyProjections.Add(new(
            "tenant-a",
            Guid.NewGuid(),
            "Mismatched property",
            "UTC",
            PropertyStatus.Active,
            version: 1));
        await Assert.ThrowsAsync<ScopeWriteGuardException>(
            () => tenantB.SaveChangesAsync());
        tenantB.ChangeTracker.Clear();

        tenantB.StayHistory.Add(new(
            "tenant-a",
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            GuestStayRole.Primary,
            new DateOnly(2026, 2, 1),
            new DateOnly(2026, 2, 2),
            GuestStayStatus.CheckedOut,
            checkedInBusinessDate: null,
            noShowBusinessDate: null,
            new DateOnly(2026, 2, 2),
            isCurrentParticipant: false,
            reservationVersion: 1,
            GuestsModuleMetadata.StayHistoryProjectionVersion));
        await Assert.ThrowsAsync<ScopeWriteGuardException>(
            () => tenantB.SaveChangesAsync());
    }

    [Fact]
    public async Task Property_projection_orders_topology_and_policy_streams_independently()
    {
        await using GuestsDbContext dbContext = CreateDbContext();
        GuestPropertyProjectionRepository repository =
            new(dbContext, new NoopGuestOperationLock());
        Guid propertyId = Guid.NewGuid();
        PropertyGovernancePolicyBinding binding = CreateGovernanceBinding();

        await repository.ApplyPolicyAsync(
            new("tenant-a", propertyId, PropertyProcessingStatus.Enabled, binding, 4),
            CancellationToken.None);
        await repository.ApplyTopologyAsync(
            new("tenant-a", propertyId, "Property", PropertyStatus.Active, 2),
            CancellationToken.None);
        await repository.ApplyPolicyAsync(
            new("tenant-a", propertyId, PropertyProcessingStatus.Suspended, binding, 3),
            CancellationToken.None);
        await dbContext.SaveChangesAsync();

        GuestPropertyPolicySnapshot snapshot = Assert.IsType<GuestPropertyPolicySnapshot>(
            await repository.GetPolicyAsync(propertyId, CancellationToken.None));
        Assert.True(snapshot.IsKnown);
        Assert.True(snapshot.IsActive);
        Assert.Equal(PropertyProcessingStatus.Enabled, snapshot.ProcessingStatus);
        Assert.Equal(binding.ContentSha256, snapshot.GovernancePolicy!.ContentSha256);
        GuestPropertyProjection projection = await dbContext.PropertyProjections.SingleAsync();
        Assert.Equal(2, projection.TopologySourceVersion);
        Assert.Equal(4, projection.PolicySourceVersion);
    }

    [Fact]
    public async Task Property_policy_lookup_does_not_read_a_foreign_projection()
    {
        string databaseName = $"guests-property-scope-{Guid.NewGuid():N}";
        DbContextOptions<GuestsDbContext> options =
            new DbContextOptionsBuilder<GuestsDbContext>()
                .UseInMemoryDatabase(databaseName)
                .Options;
        Guid propertyId = Guid.NewGuid();
        await using (GuestsDbContext tenantB = new(
            options,
            new TestScopeContext("tenant-b")))
        {
            GuestPropertyProjection foreign = new(
                "tenant-b",
                propertyId,
                "Foreign property",
                "UTC",
                PropertyStatus.Active,
                version: 1);
            foreign.ApplyPolicy(
                PropertyProcessingStatus.Enabled,
                CreateGovernanceBinding(),
                sourceVersion: 1);
            tenantB.PropertyProjections.Add(foreign);
            await tenantB.SaveChangesAsync();
        }

        await using GuestsDbContext tenantA = new(
            options,
            new TestScopeContext("tenant-a"));
        GuestPropertyProjectionRepository repository = new(
            tenantA,
            new NoopGuestOperationLock());

        Assert.Null(await repository.GetPolicyAsync(
            propertyId,
            CancellationToken.None));
    }

    [Fact]
    public async Task Data_holds_and_receipts_are_tenant_isolated_and_round_trip()
    {
        string databaseName = $"guests-holds-{Guid.NewGuid():N}";
        DbContextOptions<GuestsDbContext> options =
            new DbContextOptionsBuilder<GuestsDbContext>()
                .UseInMemoryDatabase(databaseName)
                .Options;
        DateTimeOffset placedAtUtc =
            new(2026, 7, 25, 1, 0, 0, TimeSpan.Zero);
        GuestDataHold hold = GuestDataHold.Place(
            Guid.NewGuid(),
            "tenant-a",
            Guid.NewGuid(),
            Guid.NewGuid(),
            "legal-obligation",
            "user:privacy",
            placedAtUtc).Value;
        GuestDataHoldReceipt receipt = GuestDataHoldReceipt.Create(
            Guid.NewGuid(),
            "tenant-a",
            Guid.NewGuid(),
            hold,
            GuestDataHoldAction.Place,
            selectedGuestVersion: 3,
            "user:privacy",
            placedAtUtc).Value;

        await using (GuestsDbContext tenantA =
            new(options, new TestScopeContext("tenant-a")))
        {
            tenantA.DataHolds.Add(hold);
            tenantA.DataHoldReceipts.Add(receipt);
            await tenantA.SaveChangesAsync();
        }

        await using (GuestsDbContext tenantB =
            new(options, new TestScopeContext("tenant-b")))
        {
            Assert.Empty(await tenantB.DataHolds.ToArrayAsync());
            Assert.Empty(await tenantB.DataHoldReceipts.ToArrayAsync());
        }

        await using GuestsDbContext reloaded =
            new(options, new TestScopeContext("tenant-a"));
        GuestDataHold persisted = Assert.Single(await reloaded.DataHolds.ToArrayAsync());
        GuestDataHoldReceipt persistedReceipt =
            Assert.Single(await reloaded.DataHoldReceipts.ToArrayAsync());
        Assert.Equal(hold.Id, persisted.Id);
        Assert.Equal(receipt.IdempotencyKey, persistedReceipt.IdempotencyKey);
    }

    private static PropertyGovernancePolicyBinding CreateGovernanceBinding()
    {
        DateTimeOffset now = new(2026, 7, 22, 12, 0, 0, TimeSpan.Zero);
        return new(
            "GB",
            "gb-hostel",
            1,
            "eu-west-2",
            "uk-no-transfer",
            "guest-operational",
            1,
            new string('a', PropertiesContractLimits.ContentSha256Length),
            now.AddDays(-1),
            now.AddDays(30),
            now,
            []);
    }

    private static GuestsDbContext CreateDbContext()
    {
        DbContextOptions<GuestsDbContext> options = new DbContextOptionsBuilder<GuestsDbContext>()
            .UseInMemoryDatabase($"guests-model-{Guid.NewGuid():N}")
            .Options;
        return new(options, new TestScopeContext());
    }

    private static void AssertConstraints(
        IEntityType entity,
        params string[] expectedNames)
    {
        string[] actualNames = entity.GetCheckConstraints()
            .Select(constraint => constraint.Name!)
            .ToArray();
        Assert.All(expectedNames, expected => Assert.Contains(expected, actualNames));
    }

    private sealed class TestScopeContext(string scopeId = "tenant-a") : IScopeContext
    {
        public bool IsEnabled => true;
        public string ScopeId => scopeId;
    }
}
