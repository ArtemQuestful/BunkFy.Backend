namespace BunkFy.Modules.Staff.Tests;

using Gma.Framework.Scoping;
using BunkFy.Modules.Staff.Application.Ports;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using BunkFy.Modules.Staff.Domain.Aggregates;
using BunkFy.Modules.Staff.Domain.DataRights;
using BunkFy.Modules.Staff.Domain.Entities;
using BunkFy.Modules.Staff.Domain.Governance;
using BunkFy.Modules.Staff.Domain.Models;
using BunkFy.Modules.Staff.Persistence;
using BunkFy.Modules.Staff.Persistence.Models;
using BunkFy.Modules.Staff.Persistence.TenantTermination;
using Xunit;

[Trait("Category", "Unit")]
public sealed class StaffModelTests
{
    [Fact]
    public void Authoritative_staff_tables_declare_database_invariants()
    {
        using StaffDbContext dbContext = CreateDbContext();
        IModel designModel = dbContext.GetService<IDesignTimeModel>().Model;
        IEntityType member = designModel.FindEntityType(typeof(StaffMember))!;
        IEntityType assignment = designModel.FindEntityType(
            typeof(StaffPropertyAssignment))!;

        AssertConstraints(
            member,
            "CK_staff_members_coordinates",
            "CK_staff_members_version",
            "CK_staff_members_display_name",
            "CK_staff_members_created_by",
            "CK_staff_members_last_changed_by",
            "CK_staff_members_search_shape",
            "CK_staff_members_optional_text",
            "CK_staff_members_lifecycle",
            "CK_staff_members_timestamps",
            "CK_staff_members_anonymised_profile");
        AssertConstraints(
            assignment,
            "CK_staff_assignments_coordinates",
            "CK_staff_assignments_actor_shape",
            "CK_staff_assignments_versions",
            "CK_staff_assignments_lifecycle",
            "CK_staff_assignments_dates",
            "CK_staff_assignments_timestamps");
        AssertIndex(
            assignment,
            "UX_staff_assignments_current_property",
            "\"IsCurrent\"");
        AssertIndex(
            assignment,
            "UX_staff_assignments_current_primary",
            "\"IsCurrent\" AND \"IsPrimary\"");
    }

    [Fact]
    public void Tenant_revision_is_scope_keyed_and_concurrency_guarded()
    {
        using StaffDbContext dbContext = CreateDbContext();

        IEntityType revisionEntity = dbContext.Model.FindEntityType(
            typeof(StaffTenantRevision))!;
        IEntityType designRevisionEntity = dbContext
            .GetService<IDesignTimeModel>()
            .Model
            .FindEntityType(typeof(StaffTenantRevision))!;

        Assert.Equal(
            [nameof(StaffTenantRevision.ScopeId)],
            revisionEntity.FindPrimaryKey()!.Properties.Select(
                property => property.Name));
        Assert.True(
            revisionEntity.FindProperty(
                nameof(StaffTenantRevision.Revision))!
                .IsConcurrencyToken);
        Assert.NotEmpty(revisionEntity.GetDeclaredQueryFilters());
        Assert.Contains(
            designRevisionEntity.GetCheckConstraints(),
            constraint => string.Equals(
                constraint.Name,
                "CK_staff_tenant_revision_positive",
                StringComparison.Ordinal));
        Assert.Contains(
            designRevisionEntity.GetCheckConstraints(),
            constraint => string.Equals(
                constraint.Name,
                "CK_staff_tenant_revision_lifecycle",
                StringComparison.Ordinal));
    }

    [Fact]
    public void Tenant_destruction_progress_and_receipt_are_scope_unique_and_constrained()
    {
        using StaffDbContext dbContext = CreateDbContext();
        IModel designModel = dbContext.GetService<IDesignTimeModel>().Model;
        IEntityType operation = designModel.FindEntityType(
            typeof(StaffTenantDestroyOperation))!;
        IEntityType receipt = designModel.FindEntityType(
            typeof(StaffTenantDestroyReceipt))!;

        Assert.Equal(
            [nameof(StaffTenantDestroyOperation.OperationId)],
            operation.FindPrimaryKey()!.Properties.Select(
                property => property.Name));
        Assert.True(operation.FindProperty(
            nameof(StaffTenantDestroyOperation.ConcurrencyVersion))!
            .IsConcurrencyToken);
        Assert.Contains(
            operation.GetIndexes(),
            index => index.IsUnique &&
                index.Properties.Select(property => property.Name)
                    .SequenceEqual([
                        nameof(StaffTenantDestroyOperation.ScopeId)
                    ]));
        Assert.Contains(
            operation.GetCheckConstraints(),
            constraint => constraint.Name ==
                "CK_staff_tenant_destroy_operation_batch");
        Assert.Contains(
            operation.GetCheckConstraints(),
            constraint => constraint.Name ==
                "CK_staff_tenant_destroy_operation_progress");

        Assert.Equal(
            [nameof(StaffTenantDestroyReceipt.OperationId)],
            receipt.FindPrimaryKey()!.Properties.Select(
                property => property.Name));
        Assert.Contains(
            receipt.GetIndexes(),
            index => index.IsUnique &&
                index.Properties.Select(property => property.Name)
                    .SequenceEqual([
                        nameof(StaffTenantDestroyReceipt.ScopeId)
                    ]));
        Assert.Contains(
            receipt.GetCheckConstraints(),
            constraint => constraint.Name ==
                "CK_staff_tenant_destroy_receipt_progress");
    }

    [Fact]
    public void Member_mutation_operations_are_scoped_immutable_member_receipts()
    {
        using StaffDbContext dbContext = CreateDbContext();
        IEntityType operation = dbContext.GetService<IDesignTimeModel>()
            .Model
            .FindEntityType(typeof(StaffMemberMutationOperation))!;

        Assert.Equal(
            [
                nameof(StaffMemberMutationOperation.ScopeId),
                nameof(StaffMemberMutationOperation.StaffMemberId),
                nameof(StaffMemberMutationOperation.Id)
            ],
            operation.FindPrimaryKey()!.Properties.Select(
                property => property.Name));
        Assert.Contains(
            operation.GetForeignKeys(),
            foreignKey =>
                foreignKey.DeleteBehavior == DeleteBehavior.Cascade &&
                foreignKey.Properties.Select(property => property.Name)
                    .SequenceEqual([
                        nameof(StaffMemberMutationOperation.ScopeId),
                        nameof(StaffMemberMutationOperation.StaffMemberId)
                    ]));
        Assert.Contains(
            operation.GetCheckConstraints(),
            constraint => constraint.Name ==
                "CK_staff_member_mutation_operations_versions");
        Assert.Contains(
            operation.GetCheckConstraints(),
            constraint => constraint.Name ==
                "CK_staff_member_mutation_operations_fingerprint");
        ICheckConstraint statusConstraint = Assert.Single(
            operation.GetCheckConstraints(),
            constraint => constraint.Name ==
                "CK_staff_member_mutation_operations_status");
        ICheckConstraint kindConstraint = Assert.Single(
            operation.GetCheckConstraints(),
            constraint => constraint.Name ==
                "CK_staff_member_mutation_operations_kind");
        Assert.Contains("1, 2, 3", statusConstraint.Sql);
        Assert.Contains("1, 2, 3, 4, 5, 6, 7, 8", kindConstraint.Sql);
        Assert.Contains(
            operation.GetIndexes(),
            index => index.IsUnique &&
                index.Properties.Select(property => property.Name)
                    .SequenceEqual([
                        nameof(StaffMemberMutationOperation.ScopeId),
                        nameof(StaffMemberMutationOperation.Id)
                    ]) &&
                string.Equals(
                    index.GetFilter(),
                    "\"Kind\" = 8",
                    StringComparison.Ordinal));
    }

    [Fact]
    public async Task Member_mutation_operation_updates_are_rejected_by_the_context_guard()
    {
        await using StaffDbContext dbContext = CreateDbContext();
        StaffMember member = StaffMember.Create(
            Guid.NewGuid(),
            "tenant-a",
            "Ada Operator",
            legalName: null,
            "ada@example.test",
            workPhone: null,
            "EMP-100",
            "Manager",
            "Operations",
            authSubjectId: null,
            "user:owner",
            Guid.NewGuid(),
            new DateTimeOffset(2026, 8, 7, 12, 0, 0, TimeSpan.Zero)).Value;
        StaffMemberMutationOperation operation = new(
            new StaffMemberMutationOperationRecord(
                Guid.NewGuid(),
                member.ScopeId,
                member.Id,
                StaffMemberMutationKind.ProfileUpdate,
                member.Version,
                new string('a', 64),
                BunkFy.Modules.Staff.Contracts.StaffStatus.Active,
                member.Version,
                new DateTimeOffset(2026, 8, 7, 12, 1, 0, TimeSpan.Zero)));
        dbContext.StaffMembers.Add(member);
        dbContext.MemberMutationOperations.Add(operation);
        await dbContext.SaveChangesAsync();
        dbContext.Entry(operation)
            .Property(item => item.ExpectedVersion)
            .CurrentValue++;

        InvalidOperationException failure = await Assert.ThrowsAsync<
            InvalidOperationException>(() => dbContext.SaveChangesAsync());

        Assert.Contains("append-only", failure.Message);
    }

    [Fact]
    public void Model_has_scoped_unique_correlations_concurrency_and_assignment_constraints()
    {
        using StaffDbContext dbContext = CreateDbContext();
        IEntityType member = dbContext.Model.FindEntityType(typeof(StaffMember))!;
        IEntityType designMember = dbContext.GetService<IDesignTimeModel>().Model.FindEntityType(typeof(StaffMember))!;
        IEntityType assignment = dbContext.Model.FindEntityType(typeof(StaffPropertyAssignment))!;
        IEntityType designAssignment = dbContext.GetService<IDesignTimeModel>().Model
            .FindEntityType(typeof(StaffPropertyAssignment))!;
        IEntityType receipt =
            dbContext.Model.FindEntityType(typeof(StaffDataRightsCorrectionReceipt))!;
        IEntityType designReceipt = dbContext.GetService<IDesignTimeModel>()
            .Model
            .FindEntityType(typeof(StaffDataRightsCorrectionReceipt))!;
        IEntityType restriction = dbContext.Model.FindEntityType(
            typeof(StaffProcessingRestriction))!;
        IEntityType designRestriction = dbContext.GetService<IDesignTimeModel>()
            .Model
            .FindEntityType(typeof(StaffProcessingRestriction))!;
        IEntityType restrictionProjection = dbContext.Model.FindEntityType(
            typeof(StaffProcessingRestrictionProjection))!;
        IEntityType designRestrictionProjection =
            dbContext.GetService<IDesignTimeModel>()
                .Model
                .FindEntityType(
                    typeof(StaffProcessingRestrictionProjection))!;
        IEntityType restrictionReceipt = dbContext.Model.FindEntityType(
            typeof(StaffProcessingRestrictionReceipt))!;
        IEntityType designRestrictionReceipt =
            dbContext.GetService<IDesignTimeModel>()
                .Model
                .FindEntityType(
                    typeof(StaffProcessingRestrictionReceipt))!;

        Assert.True(member.FindProperty(nameof(StaffMember.Version))!.IsConcurrencyToken);
        Assert.Equal(ValueGenerated.OnAdd, member.FindProperty(nameof(StaffMember.ProjectionOrdinal))!.ValueGenerated);
        Assert.Contains(member.GetIndexes(), index => index.IsUnique && index.Properties.Select(item => item.Name)
            .SequenceEqual([nameof(StaffMember.ScopeId), nameof(StaffMember.AuthSubjectId)]));
        Assert.Contains(member.GetIndexes(), index => index.IsUnique && index.Properties.Select(item => item.Name)
            .SequenceEqual([nameof(StaffMember.ScopeId), nameof(StaffMember.EmployeeNumberSearch)]));
        Assert.NotNull(assignment.FindProperty(nameof(StaffPropertyAssignment.ScopeId)));
        Assert.Contains(designMember.GetCheckConstraints(), item => item.Name == "CK_staff_members_lifecycle");
        Assert.Contains(designAssignment.GetCheckConstraints(), item => item.Name == "CK_staff_assignments_lifecycle");
        Assert.Equal(
            StaffDataRightsCorrectionReceipt.DigestLength,
            receipt.FindProperty(
                nameof(StaffDataRightsCorrectionReceipt.RequestSha256))!
                .GetMaxLength());
        Assert.Contains(receipt.GetIndexes(), index =>
            index.IsUnique &&
            index.Properties.Select(item => item.Name).SequenceEqual([
                nameof(StaffDataRightsCorrectionReceipt.ScopeId),
                nameof(StaffDataRightsCorrectionReceipt.ExecutionId)
            ]));
        Assert.Contains(
            designReceipt.GetCheckConstraints(),
            constraint =>
                constraint.Name ==
                    "CK_staff_data_rights_correction_receipts_versions");
        Assert.True(restriction.FindProperty(
            nameof(StaffProcessingRestriction.Version))!.IsConcurrencyToken);
        Assert.True(restrictionProjection.FindProperty(
            nameof(StaffProcessingRestrictionProjection.Revision))!
            .IsConcurrencyToken);
        Assert.Equal(
            ValueGenerated.OnAdd,
            restrictionProjection.FindProperty(
                nameof(StaffProcessingRestrictionProjection.ProjectionOrdinal))!
                .ValueGenerated);
        Assert.Contains(
            restrictionReceipt.GetIndexes(),
            index =>
                index.IsUnique &&
                index.Properties.Select(item => item.Name).SequenceEqual([
                    nameof(StaffProcessingRestrictionReceipt.ScopeId),
                    nameof(StaffProcessingRestrictionReceipt.IdempotencyKey)
                ]));
        Assert.Contains(
            designRestriction.GetCheckConstraints(),
            constraint =>
                constraint.Name ==
                    "CK_staff_processing_restrictions_lifecycle");
        Assert.Contains(
            designRestrictionProjection.GetCheckConstraints(),
            constraint =>
                constraint.Name ==
                    "CK_staff_processing_restrictions_effective_state");
        Assert.Contains(
            designRestrictionReceipt.GetCheckConstraints(),
            constraint =>
                constraint.Name ==
                    "CK_staff_processing_restriction_receipts_versions");
    }

    [Fact]
    public async Task Correction_receipts_are_append_only()
    {
        await using StaffDbContext dbContext = CreateDbContext();
        StaffDataRightsCorrectionReceipt receipt =
            StaffDataRightsCorrectionReceipt.Create(
                Guid.NewGuid(),
                "tenant-a",
                Guid.NewGuid(),
                Guid.NewGuid(),
                approvalRevision: 2,
                Guid.NewGuid(),
                selectedRecordVersion: 3,
                currentRecordVersion: 4,
                [StaffProfileField.DisplayName],
                new string('a', StaffDataRightsCorrectionReceipt.DigestLength),
                Guid.NewGuid(),
                Guid.NewGuid(),
                new DateTimeOffset(
                    2026,
                    7,
                    28,
                    12,
                    0,
                    0,
                    TimeSpan.Zero)).Value;
        dbContext.DataRightsCorrectionReceipts.Add(receipt);
        await dbContext.SaveChangesAsync();
        dbContext.Entry(receipt).State = EntityState.Modified;

        InvalidOperationException failure =
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => dbContext.SaveChangesAsync());

        Assert.Contains("append-only", failure.Message);
    }

    [Fact]
    public async Task Restriction_receipts_are_append_only()
    {
        await using StaffDbContext dbContext = CreateDbContext();
        StaffProcessingRestrictionReceipt receipt =
            StaffProcessingRestrictionReceipt.Create(
                Guid.NewGuid(),
                "tenant-a",
                Guid.NewGuid(),
                Guid.NewGuid(),
                StaffProcessingRestrictionAction.Apply,
                Guid.NewGuid(),
                Guid.NewGuid(),
                approvalRevision: 2,
                selectedStaffVersion: 3,
                restrictionContractVersion: 1,
                resultingRestrictionVersion: 1,
                resultingProjectionRevision: 1,
                effectiveRestricted: true,
                "staff:privacy",
                Guid.NewGuid(),
                new DateTimeOffset(
                    2026,
                    7,
                    29,
                    12,
                    0,
                    0,
                    TimeSpan.Zero)).Value;
        dbContext.ProcessingRestrictionReceipts.Add(receipt);
        await dbContext.SaveChangesAsync();
        dbContext.Entry(receipt).State = EntityState.Modified;

        InvalidOperationException failure =
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => dbContext.SaveChangesAsync());

        Assert.Contains("append-only", failure.Message);
    }

    [Fact]
    public async Task Anonymisation_proof_is_tenant_bound_and_receipts_are_append_only()
    {
        await using StaffDbContext dbContext = CreateDbContext();
        IModel runtime = dbContext.Model;
        IModel design = dbContext.GetService<IDesignTimeModel>().Model;
        IEntityType receipt = runtime.FindEntityType(
            typeof(StaffAnonymisationReceipt))!;
        IEntityType restoreReceipt = runtime.FindEntityType(
            typeof(StaffAnonymisationRestoreReceipt))!;
        IEntityType tombstone = runtime.FindEntityType(
            typeof(StaffAnonymisationTombstone))!;

        Assert.Contains(receipt.GetIndexes(), index =>
            index.IsUnique &&
            index.Properties.Select(property => property.Name)
                .SequenceEqual([
                    nameof(StaffAnonymisationReceipt.ScopeId),
                    nameof(StaffAnonymisationReceipt.IdempotencyKey)
                ]));
        Assert.Contains(receipt.GetForeignKeys(), foreignKey =>
            foreignKey.Properties.Select(property => property.Name)
                .SequenceEqual([
                    nameof(StaffAnonymisationReceipt.ScopeId),
                    nameof(StaffAnonymisationReceipt.StaffMemberId)
                ]));
        Assert.Contains(
            design.FindEntityType(typeof(StaffAnonymisationReceipt))!
                .GetCheckConstraints(),
            constraint =>
                constraint.Name ==
                "CK_staff_anonymisation_receipts_revisions");
        Assert.Contains(
            design.FindEntityType(typeof(StaffAnonymisationTombstone))!
                .GetCheckConstraints(),
            constraint =>
                constraint.Name ==
                "CK_staff_anonymisation_tombstones_state");
        Assert.Contains(tombstone.GetKeys(), key =>
            key.Properties.Select(property => property.Name)
                .SequenceEqual([
                    nameof(StaffAnonymisationTombstone.ScopeId),
                    nameof(StaffAnonymisationTombstone.Id)
                ]));
        Assert.Contains(restoreReceipt.GetIndexes(), index =>
            index.IsUnique &&
            index.Properties.Select(property => property.Name)
                .SequenceEqual([
                    nameof(StaffAnonymisationRestoreReceipt.ScopeId),
                    nameof(StaffAnonymisationRestoreReceipt.StaffMemberId),
                    nameof(StaffAnonymisationRestoreReceipt.LedgerEntryId)
                ]));
        Assert.Contains(restoreReceipt.GetForeignKeys(), foreignKey =>
            foreignKey.Properties.Select(property => property.Name)
                .SequenceEqual([
                    nameof(StaffAnonymisationRestoreReceipt.ScopeId),
                    nameof(StaffAnonymisationRestoreReceipt.StaffMemberId)
                ]));
        Assert.Contains(
            design.FindEntityType(
                    typeof(StaffAnonymisationRestoreReceipt))!
                .GetCheckConstraints(),
            constraint =>
                constraint.Name ==
                "CK_staff_anonymisation_restore_receipts_identity");

        StaffAnonymisationReceipt proof =
            StaffAnonymisationReceipt.Create(
                Guid.NewGuid(),
                "tenant-a",
                Guid.NewGuid(),
                Guid.NewGuid(),
                approvalRevision: 2,
                operationRevision: 3,
                Guid.NewGuid(),
                selectedStaffVersion: 4,
                resultingStaffVersion: 5,
                selectedOperationLockRevision: 8,
                resultingOperationLockRevision: 9,
                new string('a', 64),
                new string('b', 64),
                Guid.NewGuid(),
                "user:privacy",
                new DateTimeOffset(
                    2026,
                    7,
                    30,
                    12,
                    0,
                    0,
                    TimeSpan.Zero)).Value;
        dbContext.AnonymisationReceipts.Add(proof);
        await dbContext.SaveChangesAsync();
        dbContext.Entry(proof).State = EntityState.Modified;

        InvalidOperationException failure =
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => dbContext.SaveChangesAsync());

        Assert.Contains("append-only", failure.Message);

        dbContext.ChangeTracker.Clear();
        StaffAnonymisationRestoreReceipt restoreProof =
            StaffAnonymisationRestoreReceipt.Create(
                "tenant-a",
                Guid.NewGuid(),
                proof.StaffMemberId,
                ownerReceiptContractVersion: 1,
                proof.Id,
                proof.CanonicalSha256,
                proof.ResultingStaffVersion,
                tombstoneRevision: 1,
                proof.CompletedAtUtc.AddMinutes(1)).Value;
        dbContext.AnonymisationRestoreReceipts.Add(restoreProof);
        dbContext.Entry(restoreProof).State = EntityState.Modified;

        InvalidOperationException restoreFailure =
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => dbContext.SaveChangesAsync());

        Assert.Contains("append-only", restoreFailure.Message);
    }

    [Fact]
    public void Governance_holds_and_operation_lock_have_tenant_first_constraints()
    {
        using StaffDbContext dbContext = CreateDbContext();
        IModel runtime = dbContext.Model;
        IModel design = dbContext.GetService<IDesignTimeModel>().Model;
        IEntityType governance = runtime.FindEntityType(
            typeof(StaffEmploymentGovernance))!;
        IEntityType acknowledgement = runtime.FindEntityType(
            typeof(StaffEmploymentGovernanceAcknowledgement))!;
        IEntityType governanceReceipt = runtime.FindEntityType(
            typeof(StaffEmploymentGovernanceChangeReceipt))!;
        IEntityType hold = runtime.FindEntityType(typeof(StaffDataHold))!;
        IEntityType holdReceipt = runtime.FindEntityType(
            typeof(StaffDataHoldReceipt))!;
        IEntityType operationLock = runtime.FindEntityType(
            typeof(StaffOperationLock))!;

        Assert.True(governance.FindProperty(
            nameof(StaffEmploymentGovernance.Version))!
            .IsConcurrencyToken);
        Assert.Equal(
            [
                "ScopeId",
                "StaffMemberId",
                nameof(
                    StaffEmploymentGovernanceAcknowledgement
                        .AcknowledgementId),
                nameof(
                    StaffEmploymentGovernanceAcknowledgement
                        .AcknowledgementVersion)
            ],
            acknowledgement.FindPrimaryKey()!.Properties
                .Select(property => property.Name));
        Assert.Contains(
            governanceReceipt.GetIndexes(),
            index =>
                index.IsUnique &&
                index.Properties.Select(property => property.Name)
                    .SequenceEqual([
                        nameof(
                            StaffEmploymentGovernanceChangeReceipt
                                .ScopeId),
                        nameof(
                            StaffEmploymentGovernanceChangeReceipt
                                .IdempotencyKey)
                    ]));
        Assert.True(hold.FindProperty(nameof(StaffDataHold.Version))!
            .IsConcurrencyToken);
        Assert.Contains(
            hold.GetIndexes(),
            index => index.Properties.Select(property => property.Name)
                .SequenceEqual([
                    nameof(StaffDataHold.ScopeId),
                    nameof(StaffDataHold.StaffMemberId),
                    nameof(StaffDataHold.State),
                    nameof(StaffDataHold.PlacedAtUtc),
                    nameof(StaffDataHold.Id)
                ]));
        Assert.Contains(
            hold.GetForeignKeys(),
            foreignKey =>
                foreignKey.PrincipalEntityType.ClrType ==
                    typeof(StaffMember) &&
                foreignKey.DeleteBehavior == DeleteBehavior.Restrict &&
                foreignKey.Properties.Select(property => property.Name)
                    .SequenceEqual([
                        nameof(StaffDataHold.ScopeId),
                        nameof(StaffDataHold.StaffMemberId)
                    ]));
        Assert.Contains(
            holdReceipt.GetIndexes(),
            index =>
                index.IsUnique &&
                index.Properties.Select(property => property.Name)
                    .SequenceEqual([
                        nameof(StaffDataHoldReceipt.ScopeId),
                        nameof(StaffDataHoldReceipt.IdempotencyKey)
                    ]));
        Assert.Contains(
            holdReceipt.GetForeignKeys(),
            foreignKey =>
                foreignKey.PrincipalEntityType.ClrType ==
                    typeof(StaffDataHold) &&
                foreignKey.DeleteBehavior == DeleteBehavior.Restrict &&
                foreignKey.Properties.Select(property => property.Name)
                    .SequenceEqual([
                        nameof(StaffDataHoldReceipt.ScopeId),
                        nameof(StaffDataHoldReceipt.HoldId)
                    ]));
        Assert.True(operationLock.FindProperty(
            nameof(StaffOperationLock.Revision))!.IsConcurrencyToken);
        Assert.Contains(
            operationLock.GetIndexes(),
            index =>
                index.IsUnique &&
                index.Properties.Select(property => property.Name)
                    .SequenceEqual([
                        nameof(StaffOperationLock.ScopeId),
                        nameof(StaffOperationLock.StaffMemberId)
                    ]));
        Assert.Contains(
            operationLock.GetForeignKeys(),
            foreignKey =>
                foreignKey.PrincipalEntityType.ClrType ==
                    typeof(StaffMember) &&
                foreignKey.DeleteBehavior == DeleteBehavior.Cascade &&
                foreignKey.Properties.Select(property => property.Name)
                    .SequenceEqual([
                        nameof(StaffOperationLock.ScopeId),
                        nameof(StaffOperationLock.StaffMemberId)
                    ]));
        Assert.Contains(
            governance.GetForeignKeys(),
            foreignKey =>
                foreignKey.PrincipalEntityType.ClrType ==
                    typeof(StaffMember) &&
                foreignKey.DeleteBehavior == DeleteBehavior.Restrict &&
                foreignKey.Properties.Select(property => property.Name)
                    .SequenceEqual([
                        nameof(StaffEmploymentGovernance.ScopeId),
                        nameof(StaffEmploymentGovernance.Id)
                    ]));

        Assert.Contains(
            design.FindEntityType(
                    typeof(StaffEmploymentGovernance))!
                .GetCheckConstraints(),
            constraint =>
                constraint.Name ==
                    "CK_staff_employment_governance_policy");
        Assert.Contains(
            design.FindEntityType(typeof(StaffDataHold))!
                .GetCheckConstraints(),
            constraint =>
                constraint.Name == "CK_staff_data_holds_lifecycle");
        Assert.Contains(
            design.FindEntityType(typeof(StaffOperationLock))!
                .GetCheckConstraints(),
            constraint =>
                constraint.Name ==
                    "CK_staff_operation_locks_revision");
        Assert.Contains(
            design.FindEntityType(typeof(StaffOperationLock))!
                .GetCheckConstraints(),
            constraint =>
                constraint.Name ==
                    "CK_staff_operation_locks_coordinate");
    }

    [Fact]
    public async Task Governance_and_hold_receipts_are_append_only()
    {
        await using StaffDbContext governanceContext = CreateDbContext();
        DateTimeOffset now = new(
            2026,
            7,
            29,
            12,
            0,
            0,
            TimeSpan.Zero);
        StaffEmploymentGovernance governance =
            StaffEmploymentGovernance.Configure(
                "tenant-a",
                Guid.NewGuid(),
                selectedStaffVersion: 3,
                StaffEmploymentGovernanceBinding.Create(
                    "GB",
                    "staff-test",
                    1,
                    "eu-west-2",
                    "uk-no-transfer",
                    "staff-employment",
                    1,
                    new string('a', 64),
                    now.AddDays(-1),
                    now.AddDays(1),
                    now).Value,
                [],
                "user:privacy",
                now).Value;
        StaffEmploymentGovernanceChangeReceipt governanceReceipt =
            StaffEmploymentGovernanceChangeReceipt.Create(
                Guid.NewGuid(),
                "tenant-a",
                Guid.NewGuid(),
                governance,
                previousGovernanceVersion: 0,
                new string('b', 64),
                "user:privacy",
                now).Value;
        governanceContext.EmploymentGovernance.Add(governance);
        governanceContext.EmploymentGovernanceChangeReceipts.Add(
            governanceReceipt);
        await governanceContext.SaveChangesAsync();
        governanceContext.Entry(governanceReceipt).State =
            EntityState.Modified;

        InvalidOperationException governanceFailure =
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => governanceContext.SaveChangesAsync());
        Assert.Contains("append-only", governanceFailure.Message);

        await using StaffDbContext holdContext = CreateDbContext();
        StaffDataHold hold = StaffDataHold.Place(
            Guid.NewGuid(),
            "tenant-a",
            Guid.NewGuid(),
            "dispute",
            "user:privacy",
            now).Value;
        StaffDataHoldReceipt holdReceipt =
            StaffDataHoldReceipt.Create(
                Guid.NewGuid(),
                "tenant-a",
                Guid.NewGuid(),
                hold,
                StaffDataHoldAction.Place,
                selectedStaffVersion: 3,
                "user:privacy",
                now).Value;
        holdContext.DataHolds.Add(hold);
        holdContext.DataHoldReceipts.Add(holdReceipt);
        await holdContext.SaveChangesAsync();
        holdContext.Entry(holdReceipt).State = EntityState.Deleted;

        InvalidOperationException holdFailure =
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => holdContext.SaveChangesAsync());
        Assert.Contains("append-only", holdFailure.Message);
    }

    private static StaffDbContext CreateDbContext() => new(
        new DbContextOptionsBuilder<StaffDbContext>().UseInMemoryDatabase($"staff-{Guid.NewGuid():N}").Options,
        new TestScopeContext());

    private static void AssertConstraints(
        IEntityType entity,
        params string[] expectedNames)
    {
        string[] actualNames = entity.GetCheckConstraints()
            .Select(constraint => constraint.Name!)
            .ToArray();
        Assert.All(expectedNames, expected => Assert.Contains(expected, actualNames));
    }

    private static void AssertIndex(
        IEntityType entity,
        string databaseName,
        string filter)
    {
        IIndex index = Assert.Single(
            entity.GetIndexes(),
            candidate => string.Equals(
                candidate.GetDatabaseName(),
                databaseName,
                StringComparison.Ordinal));
        Assert.True(index.IsUnique);
        Assert.Equal(filter, index.GetFilter());
    }

    private sealed class TestScopeContext : IScopeContext
    {
        public bool IsEnabled => true;
        public string ScopeId => "tenant-a";
    }
}
