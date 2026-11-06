namespace BunkFy.Modules.Ingestion.Tests.Persistence;

using BunkFy.Modules.Ingestion.Application.Ports;
using BunkFy.Modules.Ingestion.Contracts;
using BunkFy.Modules.Ingestion.Domain.Connections;
using BunkFy.Modules.Ingestion.Domain.Controls;
using BunkFy.Modules.Ingestion.Domain.Credentials;
using BunkFy.Modules.Ingestion.Domain.DataRights;
using BunkFy.Modules.Ingestion.Domain.LegalHolds;
using BunkFy.Modules.Ingestion.Domain.Proposals;
using BunkFy.Modules.Ingestion.Domain.Receipts;
using BunkFy.Modules.Ingestion.Domain.Reprocessing;
using BunkFy.Modules.Ingestion.Domain.Reservations;
using BunkFy.Modules.Ingestion.Domain.Runs;
using BunkFy.Modules.Ingestion.Persistence;
using BunkFy.Modules.Ingestion.Persistence.Repositories;
using BunkFy.Modules.Ingestion.Persistence.TenantTermination;
using BunkFy.Modules.Properties.Contracts;
using Gma.Framework.Pagination;
using Gma.Framework.Scoping;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Xunit;

[Trait("Category", "Unit")]
public sealed class IngestionModelTests
{
    [Fact]
    public void Source_graph_records_declare_authoritative_database_invariants()
    {
        using IngestionDbContext dbContext = CreateDbContext();
        IModel model = dbContext.GetService<IDesignTimeModel>().Model;

        AssertConstraints(
            model.FindEntityType(typeof(ReservationSourceLink))!,
            "CK_reservation_source_links_coordinates",
            "CK_reservation_source_links_observation_shape",
            "CK_reservation_source_links_applied_shape",
            "CK_reservation_source_links_lifecycle",
            "CK_reservation_source_links_anonymised_shape");
        AssertConstraints(
            model.FindEntityType(typeof(ReservationDispatch))!,
            "CK_reservation_dispatches_coordinates",
            "CK_reservation_dispatches_kind_shape",
            "CK_reservation_dispatches_lifecycle",
            "CK_reservation_dispatches_sensitive_history_lifecycle",
            "CK_reservation_dispatches_anonymised_shape");
        AssertConstraints(
            model.FindEntityType(typeof(ChangeProposal))!,
            "CK_change_proposals_coordinates",
            "CK_change_proposals_reason_code",
            "CK_change_proposals_lifecycle",
            "CK_change_proposals_sensitive_history_lifecycle",
            "CK_change_proposals_anonymised_shape");
        AssertConstraints(
            model.FindEntityType(typeof(ObservationReprocessingAttempt))!,
            "CK_observation_reprocessing_attempts_coordinates",
            "CK_observation_reprocessing_attempts_counters",
            "CK_observation_reprocessing_attempts_lifecycle");
    }

    [Fact]
    public void Connection_management_operations_are_scoped_and_immutable()
    {
        using IngestionDbContext dbContext = CreateDbContext();
        IEntityType operation = dbContext.Model.FindEntityType(
            typeof(IngestionConnectionManagementOperation))!;

        Assert.NotEmpty(operation.GetDeclaredQueryFilters());
        Assert.Equal(
            [
                nameof(IngestionConnectionManagementOperation.ScopeId),
                nameof(IngestionConnectionManagementOperation.ConnectionId),
                nameof(IngestionConnectionManagementOperation.Id)
            ],
            operation.FindPrimaryKey()!.Properties
                .Select(property => property.Name)
                .ToArray());
        Assert.Equal(
            64,
            operation.FindProperty(nameof(
                IngestionConnectionManagementOperation.RequestFingerprint))!
                .GetMaxLength());
        Assert.True(operation.FindProperty(nameof(
                IngestionConnectionManagementOperation.RequestFingerprint))!
            .IsFixedLength());
        IForeignKey connectionOwnership = Assert.Single(
            operation.GetForeignKeys(),
            foreignKey => foreignKey.PrincipalEntityType.ClrType ==
                typeof(AdapterConnection));
        Assert.Equal(
            [
                nameof(IngestionConnectionManagementOperation.ScopeId),
                nameof(IngestionConnectionManagementOperation.ConnectionId)
            ],
            connectionOwnership.Properties
                .Select(property => property.Name)
                .ToArray());
        Assert.Equal(DeleteBehavior.Restrict, connectionOwnership.DeleteBehavior);
        IEntityType designOperation = dbContext.GetService<IDesignTimeModel>()
            .Model.FindEntityType(
                typeof(IngestionConnectionManagementOperation))!;
        ICheckConstraint outcomeConstraint = Assert.Single(
            designOperation.GetCheckConstraints(),
            constraint => constraint.Name ==
                "CK_ingestion_connection_management_operations_outcome");
        Assert.Contains("\"Kind\" = 1", outcomeConstraint.Sql, StringComparison.Ordinal);
        Assert.Contains(
            "\"Kind\" IN (2, 5, 6, 7)",
            outcomeConstraint.Sql,
            StringComparison.Ordinal);
        Assert.Contains(
            "\"Kind\" = 8",
            outcomeConstraint.Sql,
            StringComparison.Ordinal);
        Assert.Contains(
            "\"Id\" <> \"ConnectionId\"",
            outcomeConstraint.Sql,
            StringComparison.Ordinal);
        Assert.Contains(
            "\"Kind\" IN (3, 4, 9)",
            outcomeConstraint.Sql,
            StringComparison.Ordinal);
        Assert.Contains(
            "\"ResultVersion\" <= \"ExpectedVersion\" + 1",
            outcomeConstraint.Sql,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Mutable_aggregates_use_concurrency_tokens()
    {
        using IngestionDbContext dbContext = CreateDbContext();

        Assert.True(dbContext.Model.FindEntityType(typeof(AdapterConnection))!
            .FindProperty(nameof(AdapterConnection.Version))!.IsConcurrencyToken);
        Assert.True(dbContext.Model.FindEntityType(typeof(AdapterConnection))!
            .FindProperty(nameof(AdapterConnection.RemoteLeaseEpoch))!.IsConcurrencyToken);
        Assert.True(dbContext.Model.FindEntityType(typeof(IngestionRun))!
            .FindProperty(nameof(IngestionRun.Version))!.IsConcurrencyToken);
        Assert.True(dbContext.Model.FindEntityType(typeof(ChangeProposal))!
            .FindProperty(nameof(ChangeProposal.Version))!.IsConcurrencyToken);
        Assert.True(dbContext.Model.FindEntityType(typeof(ReservationSourceLink))!
            .FindProperty(nameof(ReservationSourceLink.Version))!.IsConcurrencyToken);
        Assert.True(dbContext.Model.FindEntityType(typeof(ReservationDispatch))!
            .FindProperty(nameof(ReservationDispatch.Version))!.IsConcurrencyToken);
        Assert.True(dbContext.Model.FindEntityType(typeof(ObservationReceipt))!
            .FindProperty(nameof(ObservationReceipt.RawPayloadVersion))!.IsConcurrencyToken);
        Assert.True(dbContext.Model.FindEntityType(typeof(ObservationReprocessingAttempt))!
            .FindProperty(nameof(ObservationReprocessingAttempt.Version))!.IsConcurrencyToken);
        Assert.True(dbContext.Model.FindEntityType(typeof(ObservationReprocessingOutput))!
            .FindProperty(nameof(ObservationReprocessingOutput.Version))!.IsConcurrencyToken);
        Assert.Equal(
            1L,
            dbContext.Model.FindEntityType(typeof(ObservationReprocessingOutput))!
                .FindProperty(nameof(ObservationReprocessingOutput.Version))!
                .GetDefaultValue());
        Assert.True(dbContext.Model.FindEntityType(typeof(LegalHold))!
            .FindProperty(nameof(LegalHold.Version))!.IsConcurrencyToken);
        Assert.True(dbContext.Model.FindEntityType(typeof(IngestionPropertyProjection))!
            .FindProperty(nameof(IngestionPropertyProjection.RetentionFenceVersion))!.IsConcurrencyToken);
        Assert.True(dbContext.Model.FindEntityType(typeof(AdapterIngressTenantControl))!
            .FindProperty(nameof(AdapterIngressTenantControl.Version))!.IsConcurrencyToken);
        Assert.True(dbContext.Model.FindEntityType(typeof(AdapterIngressGlobalControl))!
            .FindProperty(nameof(AdapterIngressGlobalControl.Version))!.IsConcurrencyToken);
        Assert.True(dbContext.Model.FindEntityType(typeof(IngestionTenantRevision))!
            .FindProperty(nameof(IngestionTenantRevision.Revision))!.IsConcurrencyToken);
        Assert.NotEmpty(dbContext.Model
            .FindEntityType(typeof(IngestionTenantRevision))!
            .GetDeclaredQueryFilters());
        Assert.Equal(
            [nameof(IngestionTenantRevision.ScopeId)],
            dbContext.Model.FindEntityType(typeof(IngestionTenantRevision))!
                .FindPrimaryKey()!
                .Properties
                .Select(property => property.Name)
                .ToArray());
    }

    [Fact]
    public void Tenant_destruction_state_is_scoped_constrained_and_concurrent()
    {
        using IngestionDbContext dbContext = CreateDbContext();
        IEntityType revision = dbContext.Model.FindEntityType(
            typeof(IngestionTenantRevision))!;
        IEntityType operation = dbContext.Model.FindEntityType(
            typeof(IngestionTenantDestroyOperation))!;
        IEntityType receipt = dbContext.Model.FindEntityType(
            typeof(IngestionTenantDestroyReceipt))!;

        Assert.NotNull(revision.FindProperty(
            nameof(IngestionTenantRevision.LifecycleStatus)));
        Assert.NotNull(revision.FindProperty(
            nameof(IngestionTenantRevision.DestroyOperationId)));
        Assert.True(operation.FindProperty(
            nameof(IngestionTenantDestroyOperation.ConcurrencyVersion))!
            .IsConcurrencyToken);
        Assert.NotEmpty(operation.GetDeclaredQueryFilters());
        Assert.NotEmpty(receipt.GetDeclaredQueryFilters());
        Assert.Contains(operation.GetIndexes(), index =>
            index.IsUnique &&
            index.Properties.Select(property => property.Name)
                .SequenceEqual(
                    [nameof(IngestionTenantDestroyOperation.ScopeId)]));
        Assert.Contains(receipt.GetIndexes(), index =>
            index.IsUnique &&
            index.Properties.Select(property => property.Name)
                .SequenceEqual(
                    [nameof(IngestionTenantDestroyReceipt.ScopeId)]));
        Assert.Equal(
            IngestionTenantDestroyOperation.InitialRemovalProofSha256.Length,
            operation.FindProperty(
                nameof(IngestionTenantDestroyOperation.RemovalProofSha256))!
                .GetMaxLength());
        Assert.Equal(
            IngestionTenantDestroyOperation
                .InitialRawPayloadProofSha256.Length,
            operation.FindProperty(nameof(
                    IngestionTenantDestroyOperation
                        .RawPayloadRemovalProofSha256))!
                .GetMaxLength());
    }

    [Fact]
    public async Task Tenant_ingress_control_is_scope_filtered_while_global_stop_is_shared()
    {
        string databaseName = $"ingestion-controls-{Guid.NewGuid():N}";
        DateTimeOffset now = new(2026, 7, 28, 12, 0, 0, TimeSpan.Zero);

        await using (IngestionDbContext tenantA = CreateDbContext(databaseName, "tenant-a"))
        {
            tenantA.AdapterIngressTenantControls.Add(
                AdapterIngressTenantControl.CreateSuspended(
                    "tenant-a",
                    "security.review",
                    "user:tenant-a",
                    now).Value);
            tenantA.AdapterIngressGlobalControls.Add(
                AdapterIngressGlobalControl.CreateStopped(
                    "incident.active",
                    "admin:operator",
                    now).Value);
            await tenantA.SaveChangesAsync();
        }

        await using (IngestionDbContext tenantB = CreateDbContext(databaseName, "tenant-b"))
        {
            Assert.Empty(await tenantB.AdapterIngressTenantControls.ToArrayAsync());
            Assert.Single(await tenantB.AdapterIngressGlobalControls.ToArrayAsync());
            tenantB.AdapterIngressTenantControls.Add(
                AdapterIngressTenantControl.CreateSuspended(
                    "tenant-b",
                    "credential.compromise",
                    "user:tenant-b",
                    now.AddMinutes(1)).Value);
            await tenantB.SaveChangesAsync();
        }

        await using IngestionDbContext tenantARead = CreateDbContext(databaseName, "tenant-a");
        AdapterIngressTenantControl tenantControl =
            Assert.Single(await tenantARead.AdapterIngressTenantControls.ToArrayAsync());
        Assert.Equal("tenant-a", tenantControl.ScopeId);
        Assert.Single(await tenantARead.AdapterIngressGlobalControls.ToArrayAsync());
    }

    [Fact]
    public void Anonymisation_execution_and_restore_state_is_scoped()
    {
        using IngestionDbContext dbContext = CreateDbContext();
        IEntityType tombstone = dbContext.Model.FindEntityType(
            typeof(IngestionAnonymisationTombstone))!;
        IEntityType ownerReceipt = dbContext.Model.FindEntityType(
            typeof(IngestionAnonymisationReceipt))!;
        IEntityType fingerprint = dbContext.Model.FindEntityType(
            typeof(IngestionAnonymisationFingerprint))!;
        IEntityType plan = dbContext.Model.FindEntityType(
            typeof(IngestionAnonymisationRecordPlanEntry))!;

        Assert.True(tombstone
            .FindProperty(nameof(IngestionAnonymisationTombstone.Revision))!
            .IsConcurrencyToken);
        Assert.Contains(tombstone.GetIndexes(), index =>
            index.Properties.Select(property => property.Name)
                .SequenceEqual(
                    [nameof(IngestionAnonymisationTombstone.State)]));
        Assert.Contains(ownerReceipt.GetForeignKeys(), foreignKey =>
            foreignKey.PrincipalEntityType.ClrType ==
                typeof(IngestionAnonymisationTombstone) &&
            foreignKey.Properties.Select(property => property.Name)
                .SequenceEqual(["ScopeId", "SourceLinkId"]));
        Assert.Contains(ownerReceipt.GetIndexes(), index =>
            index.IsUnique &&
            index.Properties.Select(property => property.Name)
                .SequenceEqual(["ScopeId", "IdempotencyKey"]));
        Assert.Contains(ownerReceipt.GetIndexes(), index =>
            index.IsUnique &&
            index.Properties.Select(property => property.Name)
                .SequenceEqual(["ScopeId", "WorkItemId"]));
        Assert.Contains(fingerprint.GetForeignKeys(), foreignKey =>
            foreignKey.PrincipalEntityType.ClrType ==
                typeof(IngestionAnonymisationTombstone) &&
            foreignKey.Properties.Select(property => property.Name)
                .SequenceEqual(["ScopeId", "TombstoneId"]));
        Assert.Contains(fingerprint.GetIndexes(), index =>
            index.IsUnique &&
            index.Properties.Select(property => property.Name)
                .SequenceEqual(
                    [
                        "ScopeId",
                        nameof(IngestionAnonymisationFingerprint.Purpose),
                        nameof(IngestionAnonymisationFingerprint.KeyVersion),
                        nameof(IngestionAnonymisationFingerprint.Sha256)
                    ]));
        Assert.Contains(plan.GetForeignKeys(), foreignKey =>
            foreignKey.PrincipalEntityType.ClrType ==
                typeof(IngestionAnonymisationTombstone) &&
            foreignKey.Properties.Select(property => property.Name)
                .SequenceEqual(["ScopeId", "TombstoneId"]));
        Assert.Contains(plan.GetIndexes(), index =>
            index.IsUnique &&
            index.Properties.Select(property => property.Name)
                .SequenceEqual(
                    ["ScopeId", "TombstoneId", "Kind", "RecordId"]));
        Assert.DoesNotContain(
            dbContext.Model.GetEntityTypes(),
            entity => entity.ClrType.Name == "IngestionSourceOperationLock");
    }

    [Fact]
    public void Reprocessing_attempts_outputs_and_derived_receipts_are_scope_linked()
    {
        using IngestionDbContext dbContext = CreateDbContext();
        IEntityType receipt = dbContext.Model.FindEntityType(typeof(ObservationReceipt))!;
        IEntityType attempt = dbContext.Model.FindEntityType(typeof(ObservationReprocessingAttempt))!;
        IEntityType output = dbContext.Model.FindEntityType(typeof(ObservationReprocessingOutput))!;

        Assert.Contains(attempt.GetForeignKeys(), foreignKey =>
            foreignKey.Properties.Select(property => property.Name).SequenceEqual(["ScopeId", "SourceReceiptId"]));
        Assert.Contains(receipt.GetForeignKeys(), foreignKey =>
            foreignKey.Properties.Select(property => property.Name).SequenceEqual(["ScopeId", "SourceReceiptId"]));
        Assert.Contains(receipt.GetForeignKeys(), foreignKey =>
            foreignKey.Properties.Select(property => property.Name)
                .SequenceEqual(["ScopeId", "ReprocessingAttemptId"]));
        Assert.Contains(output.GetIndexes(), index => index.IsUnique &&
            index.Properties.Select(property => property.Name)
                .SequenceEqual(["ScopeId", "AttemptId", "OutputIndex"]));
    }

    [Fact]
    public void Receipts_have_operation_and_source_deduplication_barriers()
    {
        using IngestionDbContext dbContext = CreateDbContext();
        IEntityType receipt = dbContext.Model.FindEntityType(typeof(ObservationReceipt))!;

        Assert.Contains(receipt.GetIndexes(), index =>
            index.IsUnique && index.Properties.Select(property => property.Name)
                .SequenceEqual(["ScopeId", "ConnectionId", "OperationId"]));
        Assert.Contains(receipt.GetIndexes(), index =>
            index.IsUnique && index.Properties.Select(property => property.Name)
                .SequenceEqual(["ScopeId", "ConnectionId", "DeduplicationKey"]));
        Assert.Contains(receipt.GetIndexes(), index =>
            !index.IsUnique && index.Properties.Select(property => property.Name)
                .SequenceEqual(["ScopeId", "ConnectionId", "ExternalId", "ReceivedAtUtc"]));
    }

    [Fact]
    public void Run_and_receipt_relationships_include_scope()
    {
        using IngestionDbContext dbContext = CreateDbContext();
        IEntityType run = dbContext.Model.FindEntityType(typeof(IngestionRun))!;
        IEntityType receipt = dbContext.Model.FindEntityType(typeof(ObservationReceipt))!;

        Assert.Contains(run.GetForeignKeys(), foreignKey =>
            foreignKey.Properties.Select(property => property.Name).SequenceEqual(["ScopeId", "ConnectionId"]));
        Assert.Contains(receipt.GetForeignKeys(), foreignKey =>
            foreignKey.Properties.Select(property => property.Name).SequenceEqual(["ScopeId", "RunId"]));
    }

    [Fact]
    public void Task_and_remote_lease_identities_are_unique_and_receipt_run_is_optional()
    {
        using IngestionDbContext dbContext = CreateDbContext();
        IEntityType run = dbContext.Model.FindEntityType(typeof(IngestionRun))!;
        IEntityType receipt = dbContext.Model.FindEntityType(typeof(ObservationReceipt))!;

        Assert.Contains(run.GetIndexes(), index =>
            index.IsUnique && index.GetFilter() == "\"ExecutionKind\" = 1" &&
            index.Properties.Select(property => property.Name)
                .SequenceEqual(["ScopeId", "TaskRunId", "TaskAttempt"]));
        Assert.Contains(run.GetIndexes(), index =>
            index.IsUnique && index.GetFilter() == "\"ExecutionKind\" = 2" &&
            index.Properties.Select(property => property.Name)
                .SequenceEqual(["ScopeId", "RemoteLeaseId"]));
        Assert.True(run.FindProperty(nameof(IngestionRun.TaskRunId))!.IsNullable);
        Assert.True(run.FindProperty(nameof(IngestionRun.TaskAttempt))!.IsNullable);
        Assert.True(receipt.FindProperty(nameof(ObservationReceipt.RunId))!.IsNullable);
    }

    [Fact]
    public void Polling_schedule_is_complete_and_active_runs_are_unique_per_connection()
    {
        using IngestionDbContext dbContext = CreateDbContext();
        IEntityType connection = dbContext.Model.FindEntityType(typeof(AdapterConnection))!;
        IEntityType run = dbContext.Model.FindEntityType(typeof(IngestionRun))!;

        Assert.NotNull(connection.FindProperty(nameof(AdapterConnection.PollingIntervalSeconds)));
        Assert.NotNull(connection.FindProperty(nameof(AdapterConnection.PollingScheduleMaxAttempts)));
        Assert.NotNull(connection.FindProperty(nameof(AdapterConnection.PollingScheduleConfiguredAtUtc)));
        Assert.Contains(run.GetIndexes(), index =>
            index.IsUnique &&
            index.GetFilter() == "\"State\" = 1" &&
            index.Properties.Select(property => property.Name).SequenceEqual(["ScopeId", "ConnectionId"]));
    }

    [Fact]
    public void Ingress_credentials_are_scope_and_connection_owned_without_token_material()
    {
        using IngestionDbContext dbContext = CreateDbContext();
        IEntityType credential = dbContext.Model.FindEntityType(typeof(AdapterIngressCredential))!;

        Assert.True(credential.FindProperty(nameof(AdapterIngressCredential.Version))!.IsConcurrencyToken);
        Assert.Equal(AdapterIngressCredential.SecretHashLength,
            credential.FindProperty(nameof(AdapterIngressCredential.SecretHash))!.GetMaxLength());
        Assert.False(credential.FindProperty(nameof(AdapterIngressCredential.AdapterType))!.IsNullable);
        Assert.False(credential.FindProperty(nameof(AdapterIngressCredential.AdapterProtocolVersion))!.IsNullable);
        Assert.False(credential.FindProperty(nameof(AdapterIngressCredential.ConfigurationSchemaVersion))!.IsNullable);
        Assert.False(credential.FindProperty(nameof(AdapterIngressCredential.SourceSystem))!.IsNullable);
        Assert.Contains(credential.GetForeignKeys(), foreignKey =>
            foreignKey.Properties.Select(property => property.Name)
                .SequenceEqual(["ScopeId", "ConnectionId"]) &&
            foreignKey.PrincipalEntityType.ClrType == typeof(AdapterConnection));
        Assert.Contains(credential.GetIndexes(), index =>
            index.Properties.Select(property => property.Name)
                .SequenceEqual(["ScopeId", "ConnectionId", "State", "ExpiresAtUtc"]));
        Assert.Contains(credential.GetIndexes(), index =>
            index.IsUnique && index.GetFilter() == "\"State\" = 1" &&
            index.Properties.Select(property => property.Name)
                .SequenceEqual(["ScopeId", "ConnectionId", "Slot"]));
        Assert.Contains(credential.GetIndexes(), index =>
            index.Properties.Select(property => property.Name)
                .SequenceEqual(["ScopeId", "SourceSystem", "CreatedAtUtc"]));
    }

    [Fact]
    public void Receipt_adapter_provenance_is_an_optional_owned_snapshot()
    {
        using IngestionDbContext dbContext = CreateDbContext();
        IEntityType receipt = dbContext.Model.FindEntityType(typeof(ObservationReceipt))!;
        INavigation provenance = receipt.FindNavigation(nameof(ObservationReceipt.AdapterProvenance))!;
        IEntityType owned = provenance.TargetEntityType;

        Assert.True(owned.IsOwned());
        Assert.False(owned.FindProperty(nameof(ObservationAdapterProvenance.AdapterType))!.IsNullable);
        Assert.False(owned.FindProperty(nameof(ObservationAdapterProvenance.AdapterProtocolVersion))!.IsNullable);
        Assert.False(owned.FindProperty(nameof(ObservationAdapterProvenance.ConfigurationSchemaVersion))!.IsNullable);
        Assert.False(owned.FindProperty(nameof(ObservationAdapterProvenance.SourceSystem))!.IsNullable);
        Assert.True(owned.FindProperty(nameof(ObservationAdapterProvenance.CredentialId))!.IsNullable);
        Assert.True(owned.FindProperty(nameof(ObservationAdapterProvenance.CustomerOwner))!.IsNullable);
    }

    [Fact]
    public void Property_projection_and_rebuild_checkpoints_are_scope_owned()
    {
        using IngestionDbContext dbContext = CreateDbContext();
        IEntityType property = dbContext.Model.FindEntityType(typeof(IngestionPropertyProjection))!;
        IEntityType checkpoint = dbContext.Model.FindEntityType(typeof(IngestionProjectionRebuildCheckpoint))!;

        Assert.Equal(["Id"], property.FindPrimaryKey()!.Properties.Select(item => item.Name));
        Assert.Contains(property.GetKeys(), key => key.Properties.Select(item => item.Name)
            .SequenceEqual(["ScopeId", "Id"]));
        Assert.Equal(
            ["ScopeId", "ProjectionName", "RunId"],
            checkpoint.FindPrimaryKey()!.Properties.Select(item => item.Name));
    }

    [Fact]
    public void Reservation_links_and_dispatches_have_scoped_deduplication_barriers()
    {
        using IngestionDbContext dbContext = CreateDbContext();
        IEntityType link = dbContext.Model.FindEntityType(typeof(ReservationSourceLink))!;
        IEntityType dispatch = dbContext.Model.FindEntityType(typeof(ReservationDispatch))!;

        Assert.Contains(link.GetIndexes(), index =>
            index.IsUnique && index.Properties.Select(property => property.Name)
                .SequenceEqual(["ScopeId", "ConnectionId", "SourceSystem", "SourceReference"]));
        Assert.Contains(dispatch.GetIndexes(), index =>
            index.IsUnique && index.Properties.Select(property => property.Name)
                .SequenceEqual(["ScopeId", "TriggerKind", "TriggerId"]));
        Assert.Contains(dispatch.GetForeignKeys(), foreignKey =>
            foreignKey.Properties.Select(property => property.Name).SequenceEqual(["ScopeId", "SourceLinkId"]));
        Assert.Contains(dispatch.GetForeignKeys(), foreignKey =>
            foreignKey.Properties.Select(property => property.Name).SequenceEqual(["ScopeId", "ReceiptId"]));
    }

    [Fact]
    public async Task Proposal_reader_is_property_scoped_and_projects_contract_status()
    {
        await using IngestionDbContext dbContext = CreateDbContext();
        Guid propertyId = Guid.NewGuid();
        DateTimeOffset now = new(2026, 8, 4, 12, 0, 0, TimeSpan.Zero);
        ChangeProposal proposal = ChangeProposal.Create(
            Guid.NewGuid(), "tenant-a", propertyId, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            Guid.NewGuid(), 2, "test", "{\"change\":true}", now).Value;
        ChangeProposal olderProposal = ChangeProposal.Create(
            Guid.NewGuid(), "tenant-a", propertyId, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            Guid.NewGuid(), 1, "older", "{\"change\":false}", now.AddMinutes(-1)).Value;
        dbContext.ChangeProposals.AddRange(proposal, olderProposal);
        await dbContext.SaveChangesAsync();
        ChangeProposalReader reader = new(dbContext);

        ChangeProposalDto? found = await reader.GetAsync(propertyId, proposal.Id, CancellationToken.None);
        ChangeProposalListResponse list = await reader.ListAsync(
            propertyId,
            ChangeProposalStatus.Pending,
            new PageRequest(1, 1),
            CancellationToken.None);
        ChangeProposalListResponse lastPage = await reader.ListAsync(
            propertyId,
            ChangeProposalStatus.Pending,
            new PageRequest(2, 1),
            CancellationToken.None);

        Assert.NotNull(found);
        Assert.Equal(ChangeProposalStatus.Pending, found.Status);
        Assert.Equal("test", found.ReasonCode);
        Assert.Equal(SensitiveHistoryStatus.Available, found.SensitiveHistoryStatus);
        Assert.NotNull(found.Diff);
        Assert.Single(list.Proposals);
        Assert.Equal("test", Assert.Single(list.Proposals).ReasonCode);
        Assert.True(list.HasMore);
        Assert.Single(lastPage.Proposals);
        Assert.False(lastPage.HasMore);
        Assert.Null(await reader.GetAsync(Guid.NewGuid(), proposal.Id, CancellationToken.None));
    }

    [Fact]
    public async Task Operations_reader_is_property_scoped_and_projects_connection_status()
    {
        await using IngestionDbContext dbContext = CreateDbContext();
        Guid propertyId = Guid.NewGuid();
        AdapterConnection connection = AdapterConnection.Create(
            Guid.NewGuid(), "tenant-a", propertyId, "fake.http",
            BunkFy.Adapter.Abstractions.AdapterExecutionMode.Polling,
            IngestionConflictPolicy.SuggestionsOnly,
            "configuration://main", null, DateTimeOffset.UtcNow).Value;
        AdapterConnection secondConnection = AdapterConnection.Create(
            Guid.NewGuid(), "tenant-a", propertyId, "mailbox.parser",
            BunkFy.Adapter.Abstractions.AdapterExecutionMode.Polling,
            IngestionConflictPolicy.SuggestionsOnly,
            "configuration://mailbox", null, DateTimeOffset.UtcNow).Value;
        dbContext.AdapterConnections.AddRange(connection, secondConnection);
        await dbContext.SaveChangesAsync();
        IngestionOperationsReader reader = new(dbContext);

        AdapterConnectionDto? found = await reader.GetConnectionAsync(
            propertyId, connection.Id, CancellationToken.None);
        AdapterConnectionListResponse list = await reader.ListConnectionsAsync(
            propertyId, AdapterConnectionStatus.Enabled, new PageRequest(1, 1), CancellationToken.None);
        AdapterConnectionListResponse lastPage = await reader.ListConnectionsAsync(
            propertyId, AdapterConnectionStatus.Enabled, new PageRequest(2, 1), CancellationToken.None);

        Assert.NotNull(found);
        Assert.Equal(AdapterConnectionStatus.Enabled, found.Status);
        Assert.Single(list.Connections);
        Assert.True(list.HasMore);
        Assert.Single(lastPage.Connections);
        Assert.False(lastPage.HasMore);
        Assert.Null(await reader.GetConnectionAsync(Guid.NewGuid(), connection.Id, CancellationToken.None));
    }

    [Fact]
    public async Task Credential_reader_uses_bounded_lookahead()
    {
        await using IngestionDbContext dbContext = CreateDbContext();
        DateTimeOffset now = new(2026, 8, 4, 12, 0, 0, TimeSpan.Zero);
        Guid connectionId = Guid.NewGuid();
        byte[] secretHash = new byte[AdapterIngressCredential.SecretHashLength];
        AdapterIngressCredential first = AdapterIngressCredential.Create(
            Guid.NewGuid(), "tenant-a", connectionId, "fake.http", 1, 1, "booking",
            1, "primary", AdapterIngressCredential.Sha256HashAlgorithm, secretHash,
            now.AddDays(30), "user:operator", now).Value;
        AdapterIngressCredential second = AdapterIngressCredential.Create(
            Guid.NewGuid(), "tenant-a", connectionId, "fake.http", 1, 1, "booking",
            2, "rotation", AdapterIngressCredential.Sha256HashAlgorithm, secretHash,
            now.AddDays(30), "user:operator", now.AddMinutes(-1)).Value;
        dbContext.AdapterIngressCredentials.AddRange(first, second);
        await dbContext.SaveChangesAsync();
        AdapterIngressCredentialRepository repository = new(dbContext);

        AdapterIngressCredentialListResponse firstPage = await repository.ListAsync(
            connectionId, new PageRequest(1, 1), CancellationToken.None);
        AdapterIngressCredentialListResponse lastPage = await repository.ListAsync(
            connectionId, new PageRequest(2, 1), CancellationToken.None);

        Assert.Single(firstPage.Credentials);
        Assert.True(firstPage.HasMore);
        Assert.Single(lastPage.Credentials);
        Assert.False(lastPage.HasMore);
    }

    [Fact]
    public async Task Connection_health_projects_latest_run_and_receipt_retention_facts()
    {
        await using IngestionDbContext dbContext = CreateDbContext();
        DateTimeOffset now = new(2026, 7, 12, 12, 0, 0, TimeSpan.Zero);
        Guid propertyId = Guid.NewGuid();
        AdapterConnection connection = AdapterConnection.Create(
            Guid.NewGuid(), "tenant-a", propertyId, "fake.http",
            BunkFy.Adapter.Abstractions.AdapterExecutionMode.Polling,
            IngestionConflictPolicy.SuggestionsOnly,
            "configuration://main", null, now.AddHours(-4)).Value;
        Assert.True(connection.ConfigurePollingSchedule(300, 3, 1, now.AddHours(-2)).IsSuccess);
        IngestionRun succeeded = IngestionRun.Start(
            Guid.NewGuid(), "tenant-a", connection.Id, propertyId, Guid.NewGuid(), 1, null, now.AddHours(-3)).Value;
        Assert.True(succeeded.Complete(
            BunkFy.Adapter.Abstractions.AdapterRunOutcome.Succeeded,
            1, 1, 0, "cursor-1", null, succeeded.Version, now.AddHours(-2)).IsSuccess);
        IngestionRun failed = IngestionRun.Start(
            Guid.NewGuid(), "tenant-a", connection.Id, propertyId, Guid.NewGuid(), 1, "cursor-1", now.AddHours(-1)).Value;
        Assert.True(failed.Complete(
            BunkFy.Adapter.Abstractions.AdapterRunOutcome.Failed,
            0, 0, 0, null, "provider.unavailable", failed.Version, now.AddMinutes(-30)).IsSuccess);
        ObservationReceipt pending = CreateReceipt(connection, now.AddHours(-2), now.AddHours(1));
        ObservationReceipt expired = CreateReceipt(connection, now.AddHours(-3), now.AddHours(-1));
        Assert.True(expired.Reject("invalid", now.AddHours(-2)).IsSuccess);
        ObservationReceipt purging = CreateReceipt(connection, now.AddHours(-4), now.AddHours(-2));
        Assert.True(purging.Reject("invalid", now.AddHours(-3)).IsSuccess);
        Assert.True(purging.BeginRawPayloadPurge(
            Guid.NewGuid(), now.AddMinutes(-10), now.AddMinutes(-25)).IsSuccess);
        dbContext.AddRange(connection, succeeded, failed, pending, expired, purging);
        await dbContext.SaveChangesAsync();
        IngestionOperationsReader reader = new(dbContext);

        AdapterConnectionHealthDto? health = await reader.GetConnectionHealthAsync(
            propertyId, connection.Id, now, CancellationToken.None);

        Assert.NotNull(health);
        Assert.Equal(AdapterConnectionOperationalState.LastRunFailed, health.OperationalState);
        Assert.Equal(failed.Id, health.LatestRunId);
        Assert.Equal(IngestionRunStatus.Failed, health.LatestRunStatus);
        Assert.Equal("provider.unavailable", health.LatestRunErrorCode);
        Assert.Equal(now.AddHours(-2), health.LastSuccessfulRunAtUtc);
        Assert.Equal(1, health.PendingReceiptCount);
        Assert.Equal(2, health.RejectedReceiptCount);
        Assert.Equal(1, health.ExpiredRawPayloadCount);
        Assert.Equal(0, health.ProtectedRawPayloadCount);
        Assert.Equal(1, health.PurgingRawPayloadCount);
        Assert.Equal(0, health.DueSensitiveHistoryCount);
        Assert.Equal(0, health.RedactedSensitiveHistoryCount);
        Assert.Equal(300, health.PollingIntervalSeconds);
        Assert.Equal(3, health.PollingScheduleMaxAttempts);
        Assert.Equal(now.AddHours(-2), health.PollingScheduleConfiguredAtUtc);
        Assert.Equal(now.AddMinutes(-55), health.NextRunExpectedAtUtc);
        Assert.True(health.RunExpected);
        Assert.Null(await reader.GetConnectionHealthAsync(
            Guid.NewGuid(), connection.Id, now, CancellationToken.None));
    }

    [Fact]
    public async Task Polling_schedule_reader_discovers_only_enabled_schedules_across_scopes()
    {
        string databaseName = $"ingestion-schedules-{Guid.NewGuid():N}";
        AdapterConnection tenantA = CreateScheduledConnection("tenant-a", enabled: true);
        AdapterConnection paused = CreateScheduledConnection("tenant-a", enabled: false);
        AdapterConnection tenantB = CreateScheduledConnection("tenant-b", enabled: true);

        await using (IngestionDbContext context = CreateDbContext(databaseName, "tenant-a"))
        {
            context.AddRange(tenantA, paused);
            await context.SaveChangesAsync();
        }

        await using (IngestionDbContext context = CreateDbContext(databaseName, "tenant-b"))
        {
            context.Add(tenantB);
            await context.SaveChangesAsync();
        }

        await using IngestionDbContext readContext = CreateDbContext(databaseName, "tenant-a");
        AdapterPollingScheduleReader reader = new(readContext);
        IReadOnlyList<BunkFy.Modules.Ingestion.Application.Ports.AdapterPollingScheduleDefinition> schedules =
            await reader.ListActiveAsync(CancellationToken.None);
        BunkFy.Modules.Ingestion.Application.Ports.AdapterPollingScheduleDefinition[] streamedSchedules =
            await reader.StreamActiveAsync(CancellationToken.None)
                .ToArrayAsync(CancellationToken.None);

        Assert.Equal(2, schedules.Count);
        Assert.Equal(schedules, streamedSchedules);
        Assert.Contains(schedules, schedule => schedule.ScopeId == "tenant-a" && schedule.ConnectionId == tenantA.Id);
        Assert.Contains(schedules, schedule => schedule.ScopeId == "tenant-b" && schedule.ConnectionId == tenantB.Id);
        Assert.DoesNotContain(schedules, schedule => schedule.ConnectionId == paused.Id);
    }

    [Fact]
    public async Task Property_projection_ignores_stale_source_versions()
    {
        await using IngestionDbContext dbContext = CreateDbContext();
        IngestionPropertyProjectionRepository repository = new(dbContext);
        Guid propertyId = Guid.NewGuid();

        await repository.ApplyTopologyAsync(
            new("tenant-a", propertyId, "Current", "current", true, 2),
            CancellationToken.None);
        await repository.ApplyTopologyAsync(
            new("tenant-a", propertyId, "Stale", "stale", false, 1),
            CancellationToken.None);
        await dbContext.SaveChangesAsync();

        IngestionPropertyProjection property = await dbContext.PropertyProjections.SingleAsync();
        Assert.Equal("Current", property.Name);
        Assert.True(property.IsActive);
        Assert.Equal(2, property.TopologySourceVersion);
    }

    [Fact]
    public async Task Property_projection_orders_topology_and_policy_streams_independently()
    {
        await using IngestionDbContext dbContext = CreateDbContext();
        IngestionPropertyProjectionRepository repository = new(dbContext);
        Guid propertyId = Guid.NewGuid();
        PropertyGovernancePolicyBinding binding = CreateGovernancePolicyBinding();

        await repository.ApplyTopologyAsync(
            new("tenant-a", propertyId, "Current", "current", true, 3),
            CancellationToken.None);
        await repository.ApplyPolicyAsync(
            new("tenant-a", propertyId, PropertyProcessingStatus.Enabled, binding, 2),
            CancellationToken.None);
        await repository.ApplyTopologyAsync(
            new("tenant-a", propertyId, "Stale", "stale", false, 2),
            CancellationToken.None);
        await repository.ApplyPolicyAsync(
            new("tenant-a", propertyId, PropertyProcessingStatus.Suspended, binding, 1),
            CancellationToken.None);
        await dbContext.SaveChangesAsync();

        IngestionPropertyProjection property = await dbContext.PropertyProjections
            .Include(item => item.GovernancePolicy)
            .SingleAsync();
        Assert.Equal("Current", property.Name);
        Assert.True(property.IsActive);
        Assert.Equal(PropertyProcessingStatus.Enabled, property.ProcessingStatus);
        Assert.NotNull(property.GovernancePolicy);
        Assert.Equal(3, property.TopologySourceVersion);
        Assert.Equal(2, property.PolicySourceVersion);
    }

    [Fact]
    public async Task Newer_policy_event_does_not_suppress_an_older_topology_event()
    {
        await using IngestionDbContext dbContext = CreateDbContext();
        IngestionPropertyProjectionRepository repository = new(dbContext);
        Guid propertyId = Guid.NewGuid();
        PropertyGovernancePolicyBinding binding = CreateGovernancePolicyBinding();

        await repository.ApplyPolicyAsync(
            new("tenant-a", propertyId, PropertyProcessingStatus.Enabled, binding, 4),
            CancellationToken.None);
        await repository.ApplyTopologyAsync(
            new("tenant-a", propertyId, "Property", "property", true, 2),
            CancellationToken.None);
        await dbContext.SaveChangesAsync();

        IngestionPropertyProjection property = await dbContext.PropertyProjections.SingleAsync();
        Assert.Equal("Property", property.Name);
        Assert.True(property.IsActive);
        Assert.Equal(PropertyProcessingStatus.Enabled, property.ProcessingStatus);
        Assert.Equal(2, property.TopologySourceVersion);
        Assert.Equal(4, property.PolicySourceVersion);
    }

    [Fact]
    public async Task Raw_payload_claim_excludes_active_proposals_but_not_terminal_proposals()
    {
        await using IngestionDbContext dbContext = CreateDbContext();
        DateTimeOffset now = new(2026, 7, 12, 12, 0, 0, TimeSpan.Zero);
        AdapterConnection connection = AdapterConnection.Create(
            Guid.NewGuid(), "tenant-a", Guid.NewGuid(), "fake.http",
            BunkFy.Adapter.Abstractions.AdapterExecutionMode.Polling,
            IngestionConflictPolicy.SuggestionsOnly,
            "configuration://main", null, now.AddDays(-40)).Value;
        ObservationReceipt pendingReceipt = CreateReceipt(connection, now.AddDays(-40), now.AddDays(-10));
        ObservationReceipt applyingReceipt = CreateReceipt(connection, now.AddDays(-40), now.AddDays(-10));
        ObservationReceipt terminalReceipt = CreateReceipt(connection, now.AddDays(-40), now.AddDays(-10));
        ObservationReceipt anonymisedReceipt = CreateReceipt(
            connection,
            now.AddDays(-40),
            now.AddDays(-10));
        Assert.True(pendingReceipt.MarkProcessed(now.AddDays(-39)).IsSuccess);
        Assert.True(applyingReceipt.MarkProcessed(now.AddDays(-39)).IsSuccess);
        Assert.True(terminalReceipt.MarkProcessed(now.AddDays(-39)).IsSuccess);
        Assert.True(anonymisedReceipt.MarkProcessed(now.AddDays(-39)).IsSuccess);
        Guid anonymisationClaimId = Guid.NewGuid();
        Assert.True(anonymisedReceipt.BeginAnonymisation(
            anonymisationClaimId,
            now.AddHours(-1)).IsSuccess);

        ChangeProposal pending = CreateProposal(connection, pendingReceipt, now.AddDays(-39));
        ChangeProposal applying = CreateProposal(connection, applyingReceipt, now.AddDays(-39));
        Assert.True(applying.BeginApply("staff:42", Guid.NewGuid(), applying.Version, now.AddDays(-1)).IsSuccess);
        ChangeProposal terminal = CreateProposal(connection, terminalReceipt, now.AddDays(-39));
        Assert.True(terminal.Reject(
            "staff:42", "Source is outdated", terminal.Version, now.AddDays(89), now.AddDays(-1)).IsSuccess);
        IngestionPropertyProjectionRepository properties = new(dbContext);
        await properties.ApplyTopologyAsync(new(
            "tenant-a", connection.PropertyId, "Held property", "held-property", true, 1),
            CancellationToken.None);
        dbContext.AddRange(
            connection,
            pendingReceipt,
            applyingReceipt,
            terminalReceipt,
            anonymisedReceipt,
            pending,
            applying,
            terminal);
        await dbContext.SaveChangesAsync();

        IReadOnlyList<RawPayloadPurgeCandidate> claimed =
            await ClaimRawPayloadsAsync(
                new RawPayloadRetentionRepository(dbContext, properties),
                Guid.NewGuid(),
                now,
                now.AddMinutes(-15),
                10);

        Assert.Equal(terminalReceipt.Id, Assert.Single(claimed).ReceiptId);
        Assert.Equal(RawPayloadRetentionState.Available, pendingReceipt.RawPayloadRetentionState);
        Assert.Equal(RawPayloadRetentionState.Available, applyingReceipt.RawPayloadRetentionState);
        Assert.Equal(RawPayloadRetentionState.Purging, terminalReceipt.RawPayloadRetentionState);
        Assert.Equal(
            RawPayloadRetentionState.Purging,
            anonymisedReceipt.RawPayloadRetentionState);
        Assert.Equal(
            anonymisationClaimId,
            anonymisedReceipt.RawPayloadPurgeClaimId);
        await dbContext.SaveChangesAsync();
        dbContext.ChangeTracker.Clear();
        AdapterConnectionHealthDto? health = await new IngestionOperationsReader(dbContext).GetConnectionHealthAsync(
            connection.PropertyId,
            connection.Id,
            now,
            CancellationToken.None);
        Assert.NotNull(health);
        Assert.Equal(0, health.ExpiredRawPayloadCount);
        Assert.Equal(2, health.ProtectedRawPayloadCount);
    }

    [Fact]
    public async Task Sensitive_history_redaction_is_due_ordered_bounded_and_preserves_active_records()
    {
        await using IngestionDbContext dbContext = CreateDbContext();
        DateTimeOffset now = new(2026, 7, 12, 12, 0, 0, TimeSpan.Zero);
        Guid propertyId = Guid.NewGuid();
        AdapterConnection connection = AdapterConnection.Create(
            Guid.NewGuid(), "tenant-a", propertyId, "fake.http",
            BunkFy.Adapter.Abstractions.AdapterExecutionMode.Polling,
            IngestionConflictPolicy.SuggestionsOnly,
            "configuration://main", null, now.AddDays(-100)).Value;
        Guid connectionId = connection.Id;
        ObservationReceipt dueReceipt = CreateReceipt(
            connection,
            now.AddDays(-100),
            now.AddDays(30));
        ObservationReceipt activeReceipt = CreateReceipt(
            connection,
            now.AddDays(-100),
            now.AddDays(30));
        ChangeProposal dueProposal = ChangeProposal.Create(
            Guid.NewGuid(), "tenant-a", propertyId, connectionId,
            dueReceipt.Id, Guid.NewGuid(), dueReceipt.RawPayloadFileId,
            1, "staff-conflict", "{\"guest\":\"Sensitive Proposal\"}",
            now.AddDays(-100)).Value;
        Assert.True(dueProposal.Reject(
            "staff:42", "Outdated", dueProposal.Version, now.AddDays(-10), now.AddDays(-100).AddMinutes(1)).IsSuccess);
        ChangeProposal activeProposal = ChangeProposal.Create(
            Guid.NewGuid(), "tenant-a", propertyId, connectionId,
            activeReceipt.Id, Guid.NewGuid(), activeReceipt.RawPayloadFileId,
            1, "active", "{\"guest\":\"Still Needed\"}",
            now.AddDays(-100)).Value;
        ReservationDispatch dueDispatch = CreateDispatch(propertyId, connectionId, now.AddDays(-100));
        Assert.True(dueDispatch.Complete(
            ReservationDispatchState.Applied,
            Guid.NewGuid(),
            detailsRevision: 2,
            reservationVersion: 3,
            errorCode: null,
            now.AddDays(-20),
            now.AddDays(-100).AddMinutes(1)).IsSuccess);
        ReservationDispatch futureDispatch = CreateDispatch(propertyId, connectionId, now.AddDays(-1));
        Assert.True(futureDispatch.Complete(
            ReservationDispatchState.Applied,
            Guid.NewGuid(),
            detailsRevision: 2,
            reservationVersion: 3,
            errorCode: null,
            now.AddDays(1),
            now.AddHours(-1)).IsSuccess);
        IngestionPropertyProjectionRepository properties = new(dbContext);
        await properties.ApplyTopologyAsync(new(
            "tenant-a", propertyId, "Retention property", "retention-property", true, 1),
            CancellationToken.None);
        dbContext.AddRange(
            connection,
            dueReceipt,
            activeReceipt,
            dueProposal,
            activeProposal,
            dueDispatch,
            futureDispatch);
        await dbContext.SaveChangesAsync();
        SensitiveHistoryRetentionRepository repository = new(dbContext, properties);

        SensitiveHistoryRedactionBatchResult first =
            await RedactSensitiveHistoryAsync(repository, now, 1);
        await dbContext.SaveChangesAsync();
        dbContext.ChangeTracker.Clear();
        SensitiveHistoryRedactionBatchResult second =
            await RedactSensitiveHistoryAsync(repository, now, 10);
        await dbContext.SaveChangesAsync();
        dbContext.ChangeTracker.Clear();

        dueDispatch = await dbContext.ReservationDispatches.SingleAsync(item => item.Id == dueDispatch.Id);
        futureDispatch = await dbContext.ReservationDispatches.SingleAsync(item => item.Id == futureDispatch.Id);
        dueProposal = await dbContext.ChangeProposals.SingleAsync(item => item.Id == dueProposal.Id);
        activeProposal = await dbContext.ChangeProposals.SingleAsync(item => item.Id == activeProposal.Id);

        Assert.Equal(new SensitiveHistoryRedactionBatchResult(0, 1), first);
        Assert.Equal(new SensitiveHistoryRedactionBatchResult(1, 0), second);
        Assert.Null(dueDispatch.NormalizedSnapshot);
        Assert.Null(dueProposal.Diff);
        Assert.NotNull(activeProposal.Diff);
        Assert.NotNull(futureDispatch.NormalizedSnapshot);
        Assert.Equal("staff-conflict", dueProposal.ReasonCode);
        Assert.Equal("Outdated", dueProposal.DecisionReason);
    }

    [Fact]
    public async Task Overlapping_legal_holds_block_retention_and_health_counts_are_exclusive()
    {
        await using IngestionDbContext dbContext = CreateDbContext();
        DateTimeOffset now = new(2026, 7, 12, 12, 0, 0, TimeSpan.Zero);
        Guid propertyId = Guid.NewGuid();
        AdapterConnection connection = AdapterConnection.Create(
            Guid.NewGuid(), "tenant-a", propertyId, "fake.http",
            BunkFy.Adapter.Abstractions.AdapterExecutionMode.Polling,
            IngestionConflictPolicy.SuggestionsOnly,
            "configuration://main", null, now.AddDays(-100)).Value;
        ObservationReceipt receipt = CreateReceipt(connection, now.AddDays(-100), now.AddDays(-10));
        Assert.True(receipt.MarkProcessed(now.AddDays(-99)).IsSuccess);
        ChangeProposal proposal = CreateProposal(connection, receipt, now.AddDays(-99));
        Assert.True(proposal.Reject(
            "staff:42", "Outdated", proposal.Version, now.AddDays(-10), now.AddDays(-98)).IsSuccess);
        ReservationDispatch dispatch = CreateDispatch(propertyId, connection.Id, now.AddDays(-100));
        Assert.True(dispatch.Complete(
            ReservationDispatchState.Applied,
            Guid.NewGuid(),
            detailsRevision: 2,
            reservationVersion: 3,
            errorCode: null,
            now.AddDays(-10),
            now.AddDays(-99)).IsSuccess);
        LegalHold firstHold = LegalHold.Place(
            Guid.NewGuid(), "tenant-a", propertyId, "Matter A", "user:legal", now.AddDays(-5)).Value;
        LegalHold secondHold = LegalHold.Place(
            Guid.NewGuid(), "tenant-a", propertyId, "Matter B", "user:legal", now.AddDays(-4)).Value;
        IngestionPropertyProjectionRepository properties = new(dbContext);
        await properties.ApplyTopologyAsync(new(
            "tenant-a", propertyId, "Held property", "held-property", true, 1),
            CancellationToken.None);
        dbContext.AddRange(connection, receipt, proposal, dispatch, firstHold, secondHold);
        await dbContext.SaveChangesAsync();
        RawPayloadRetentionRepository rawRetention = new(dbContext, properties);
        SensitiveHistoryRetentionRepository historyRetention = new(dbContext, properties);

        IReadOnlyList<RawPayloadPurgeCandidate> heldRaw =
            await ClaimRawPayloadsAsync(
                rawRetention,
                Guid.NewGuid(),
                now,
                now.AddMinutes(-15),
                10);
        SensitiveHistoryRedactionBatchResult heldHistory =
            await RedactSensitiveHistoryAsync(historyRetention, now, 10);
        AdapterConnectionHealthDto health = (await new IngestionOperationsReader(dbContext)
            .GetConnectionHealthAsync(propertyId, connection.Id, now, CancellationToken.None))!;

        Assert.Empty(heldRaw);
        Assert.Equal(new SensitiveHistoryRedactionBatchResult(0, 0), heldHistory);
        Assert.Equal(0, health.ExpiredRawPayloadCount);
        Assert.Equal(0, health.ProtectedRawPayloadCount);
        Assert.Equal(1, health.HeldExpiredRawPayloadCount);
        Assert.Equal(0, health.DueSensitiveHistoryCount);
        Assert.Equal(2, health.HeldDueSensitiveHistoryCount);
        Assert.Equal(2, health.ActiveLegalHoldCount);

        Assert.True(firstHold.Release(1, "user:legal", "Matter A closed", now.AddDays(-2)).IsSuccess);
        await dbContext.SaveChangesAsync();
        Assert.Empty(await ClaimRawPayloadsAsync(
            rawRetention,
            Guid.NewGuid(),
            now,
            now.AddMinutes(-15),
            10));

        Assert.True(secondHold.Release(1, "user:legal", "Matter B closed", now.AddDays(-1)).IsSuccess);
        await dbContext.SaveChangesAsync();
        IReadOnlyList<RawPayloadPurgeCandidate> releasedRaw =
            await ClaimRawPayloadsAsync(
                rawRetention,
                Guid.NewGuid(),
                now,
                now.AddMinutes(-15),
                10);
        SensitiveHistoryRedactionBatchResult releasedHistory =
            await RedactSensitiveHistoryAsync(historyRetention, now, 10);
        await dbContext.SaveChangesAsync();

        Assert.Equal(receipt.Id, Assert.Single(releasedRaw).ReceiptId);
        Assert.Equal(new SensitiveHistoryRedactionBatchResult(1, 1), releasedHistory);
        Assert.Equal(2, (await dbContext.PropertyProjections.SingleAsync()).RetentionFenceVersion);
    }

    [Fact]
    public async Task Retention_rechecks_candidates_after_discovery()
    {
        await using IngestionDbContext dbContext = CreateDbContext();
        DateTimeOffset now =
            new(2026, 7, 12, 12, 0, 0, TimeSpan.Zero);
        Guid propertyId = Guid.NewGuid();
        AdapterConnection connection = AdapterConnection.Create(
            Guid.NewGuid(), "tenant-a", propertyId, "fake.http",
            BunkFy.Adapter.Abstractions.AdapterExecutionMode.Polling,
            IngestionConflictPolicy.SuggestionsOnly,
            "configuration://main", null, now.AddDays(-100)).Value;
        ObservationReceipt receipt = CreateReceipt(
            connection,
            now.AddDays(-100),
            now.AddDays(-10));
        Assert.True(receipt.MarkProcessed(now.AddDays(-99)).IsSuccess);
        ChangeProposal proposal = CreateProposal(
            connection,
            receipt,
            now.AddDays(-99));
        Assert.True(proposal.Reject(
            "staff:42",
            "Outdated",
            proposal.Version,
            now.AddDays(-10),
            now.AddDays(-98)).IsSuccess);
        ReservationDispatch dispatch = CreateDispatch(
            propertyId,
            connection.Id,
            now.AddDays(-100));
        Assert.True(dispatch.Complete(
            ReservationDispatchState.Applied,
            Guid.NewGuid(),
            detailsRevision: 2,
            reservationVersion: 3,
            errorCode: null,
            now.AddDays(-10),
            now.AddDays(-99)).IsSuccess);
        IngestionPropertyProjectionRepository properties = new(dbContext);
        await properties.ApplyTopologyAsync(new(
            "tenant-a",
            propertyId,
            "Retention property",
            "retention-property",
            true,
            1), CancellationToken.None);
        dbContext.AddRange(connection, receipt, proposal, dispatch);
        await dbContext.SaveChangesAsync();
        RawPayloadRetentionRepository raw = new(dbContext, properties);
        SensitiveHistoryRetentionRepository history = new(
            dbContext,
            properties);
        Guid claimId = Guid.NewGuid();

        IReadOnlyList<RawPayloadPurgeClaimCandidate> rawCandidates =
            await raw.FindClaimCandidatesAsync(
                claimId,
                now,
                now.AddMinutes(-15),
                10,
                CancellationToken.None);
        IReadOnlyList<SensitiveHistoryRedactionCandidate>
            historyCandidates = await history.FindRedactionCandidatesAsync(
                now,
                10,
                CancellationToken.None);
        Assert.Single(rawCandidates);
        Assert.Equal(2, historyCandidates.Count);

        LegalHold hold = LegalHold.Place(
            Guid.NewGuid(),
            "tenant-a",
            propertyId,
            "New matter",
            "user:legal",
            now).Value;
        dbContext.LegalHolds.Add(hold);
        await dbContext.SaveChangesAsync();

        IReadOnlyList<RawPayloadPurgeCandidate> claimed =
            await raw.ClaimSelectedAsync(
                rawCandidates.Select(candidate => candidate.ReceiptId)
                    .ToArray(),
                claimId,
                now,
                now.AddMinutes(-15),
                CancellationToken.None);
        SensitiveHistoryRedactionBatchResult redacted =
            await history.RedactSelectedAsync(
                historyCandidates
                    .Where(candidate => candidate.Kind ==
                        SensitiveHistoryRecordKind.Proposal)
                    .Select(candidate => candidate.RecordId)
                    .ToArray(),
                historyCandidates
                    .Where(candidate => candidate.Kind ==
                        SensitiveHistoryRecordKind.Dispatch)
                    .Select(candidate => candidate.RecordId)
                    .ToArray(),
                now,
                CancellationToken.None);

        Assert.Empty(claimed);
        Assert.Equal(
            new SensitiveHistoryRedactionBatchResult(0, 0),
            redacted);
        Assert.Equal(
            RawPayloadRetentionState.Available,
            receipt.RawPayloadRetentionState);
        Assert.NotNull(proposal.Diff);
        Assert.NotNull(dispatch.NormalizedSnapshot);
    }

    private static async Task<IReadOnlyList<RawPayloadPurgeCandidate>>
        ClaimRawPayloadsAsync(
        RawPayloadRetentionRepository repository,
        Guid claimId,
        DateTimeOffset nowUtc,
        DateTimeOffset staleClaimBeforeUtc,
        int batchSize)
    {
        IReadOnlyList<RawPayloadPurgeClaimCandidate> candidates =
            await repository.FindClaimCandidatesAsync(
                claimId,
                nowUtc,
                staleClaimBeforeUtc,
                batchSize,
                CancellationToken.None);
        return await repository.ClaimSelectedAsync(
            candidates.Select(candidate => candidate.ReceiptId).ToArray(),
            claimId,
            nowUtc,
            staleClaimBeforeUtc,
            CancellationToken.None);
    }

    private static async Task<SensitiveHistoryRedactionBatchResult>
        RedactSensitiveHistoryAsync(
        SensitiveHistoryRetentionRepository repository,
        DateTimeOffset nowUtc,
        int batchSize)
    {
        IReadOnlyList<SensitiveHistoryRedactionCandidate> candidates =
            await repository.FindRedactionCandidatesAsync(
                nowUtc,
                batchSize,
                CancellationToken.None);
        return await repository.RedactSelectedAsync(
            candidates
                .Where(candidate =>
                    candidate.Kind == SensitiveHistoryRecordKind.Proposal)
                .Select(candidate => candidate.RecordId)
                .ToArray(),
            candidates
                .Where(candidate =>
                    candidate.Kind == SensitiveHistoryRecordKind.Dispatch)
                .Select(candidate => candidate.RecordId)
                .ToArray(),
            nowUtc,
            CancellationToken.None);
    }

    private static IngestionDbContext CreateDbContext()
    {
        DbContextOptions<IngestionDbContext> options = new DbContextOptionsBuilder<IngestionDbContext>()
            .UseInMemoryDatabase($"ingestion-model-{Guid.NewGuid():N}")
            .Options;
        return new IngestionDbContext(options, new TestScopeContext());
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

    private static PropertyGovernancePolicyBinding CreateGovernancePolicyBinding()
    {
        DateTimeOffset activatedAtUtc = new(2026, 7, 22, 12, 0, 0, TimeSpan.Zero);
        return new(
            "GB",
            "gb-hostel",
            1,
            "eu-west-2",
            "uk-no-transfer",
            "guest-operational",
            1,
            new string('a', PropertiesContractLimits.ContentSha256Length),
            activatedAtUtc.AddDays(-1),
            activatedAtUtc.AddDays(30),
            activatedAtUtc,
            []);
    }

    private static IngestionDbContext CreateDbContext(string databaseName, string scopeId)
    {
        DbContextOptions<IngestionDbContext> options = new DbContextOptionsBuilder<IngestionDbContext>()
            .UseInMemoryDatabase(databaseName)
            .Options;
        return new IngestionDbContext(options, new TestScopeContext(scopeId));
    }

    private static AdapterConnection CreateScheduledConnection(string scopeId, bool enabled)
    {
        AdapterConnection connection = AdapterConnection.Create(
            Guid.NewGuid(), scopeId, Guid.NewGuid(), "fake.http",
            BunkFy.Adapter.Abstractions.AdapterExecutionMode.Polling,
            IngestionConflictPolicy.SuggestionsOnly,
            "configuration://main", null, DateTimeOffset.UtcNow).Value;
        Assert.True(connection.ConfigurePollingSchedule(300, 3, 1, DateTimeOffset.UtcNow).IsSuccess);
        if (!enabled)
        {
            Assert.True(connection.Disable(2, DateTimeOffset.UtcNow).IsSuccess);
        }

        return connection;
    }

    private static ObservationReceipt CreateReceipt(
        AdapterConnection connection,
        DateTimeOffset receivedAtUtc,
        DateTimeOffset retainUntilUtc)
    {
        Guid id = Guid.NewGuid();
        return ObservationReceipt.Create(
            id,
            "tenant-a",
            connection.PropertyId,
            connection.Id,
            runId: null,
            Guid.NewGuid(),
            "reservation.v1",
            $"booking-{id:N}",
            id.ToString("N"),
            $"reservation.v1|booking-{id:N}|{id:N}",
            new string('a', ObservationReceipt.ContentHashLength),
            TestObservationCountryPolicyEvidence.Create(receivedAtUtc),
            id,
            retainUntilUtc,
            receivedAtUtc,
            receivedAtUtc,
            receivedAtUtc).Value;
    }

    private static ChangeProposal CreateProposal(
        AdapterConnection connection,
        ObservationReceipt receipt,
        DateTimeOffset createdAtUtc) => ChangeProposal.Create(
            Guid.NewGuid(), receipt.ScopeId, connection.PropertyId, connection.Id, receipt.Id,
            Guid.NewGuid(), receipt.RawPayloadFileId, 1, "test", "{\"change\":true}", createdAtUtc).Value;

    private static ReservationDispatch CreateDispatch(
        Guid propertyId,
        Guid connectionId,
        DateTimeOffset createdAtUtc) => ReservationDispatch.Create(
            Guid.NewGuid(), "tenant-a", Guid.NewGuid(), ReservationDispatchTriggerKind.Observation,
            Guid.NewGuid(), Guid.NewGuid(), connectionId, propertyId, reservationId: null,
            ReservationDispatchKind.Create, sourceRevision: "1", sourceSequence: 1,
            "{\"guest\":\"Sensitive Dispatch\"}", expectedDetailsRevision: null, createdAtUtc).Value;

    private sealed class TestScopeContext(string scopeId = "tenant-a") : IScopeContext
    {
        public bool IsEnabled => true;
        public string ScopeId => scopeId;
    }
}
