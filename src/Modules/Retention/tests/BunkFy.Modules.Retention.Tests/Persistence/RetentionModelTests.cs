namespace BunkFy.Modules.Retention.Tests.Persistence;

using BunkFy.Modules.Retention.Application.Ports;
using BunkFy.Modules.Retention.Domain.Aggregates;
using BunkFy.Modules.Retention.Domain.Models;
using BunkFy.Modules.Retention.Persistence;
using BunkFy.Modules.Retention.Persistence.Repositories;
using BunkFy.Modules.Retention.Persistence.TenantTermination;
using Gma.Framework.Scoping;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Xunit;

[Trait("Category", "Unit")]
public sealed class RetentionModelTests
{
    [Fact]
    public void Tenant_owned_state_and_revision_are_scope_filtered()
    {
        using RetentionDbContext context = CreateContext();

        AssertScopeFiltered<RetentionExecution>(context);
        AssertScopeFiltered<RetentionScheduleState>(context);
        AssertScopeFiltered<RetentionRunRetryRequest>(context);
        AssertScopeFiltered<RetentionTenantProjection>(context);
        AssertScopeFiltered<RetentionPropertyProjection>(context);
        AssertScopeFiltered<RetentionTenantRevision>(context);
        AssertScopeFiltered<RetentionTenantDestroyOperation>(context);
        AssertScopeFiltered<RetentionTenantDestroyReceipt>(context);
    }

    [Fact]
    public void Recovery_requests_and_schedule_runs_are_uniquely_indexed()
    {
        using RetentionDbContext context = CreateContext();
        IModel model = context.GetService<IDesignTimeModel>().Model;
        IEntityType request = model.FindEntityType(
            typeof(RetentionRunRetryRequest))!;
        IEntityType schedule = model.FindEntityType(
            typeof(RetentionScheduleState))!;

        Assert.Contains(
            request.GetIndexes(),
            index => index.IsUnique && index.Properties.Select(
                property => property.Name).SequenceEqual(
                [
                    nameof(RetentionRunRetryRequest.ScopeId),
                    nameof(RetentionRunRetryRequest.RunId),
                    nameof(RetentionRunRetryRequest.EvidenceVersion)
                ]));
        Assert.Contains(
            schedule.GetIndexes(),
            index => index.IsUnique && index.Properties.Select(
                property => property.Name).SequenceEqual(
                [
                    nameof(RetentionScheduleState.ScopeId),
                    nameof(RetentionScheduleState.LastExecutionId)
                ]));
    }

    [Fact]
    public void Control_plane_state_has_complete_model_owned_constraints()
    {
        using RetentionDbContext context = CreateContext();
        IModel model = context.GetService<IDesignTimeModel>().Model;

        AssertConstraints(
            model.FindEntityType(typeof(RetentionExecution))!,
            "CK_retention_executions_coordinate",
            "CK_retention_executions_keys",
            "CK_retention_executions_versions",
            "CK_retention_executions_time",
            "CK_retention_executions_state");
        AssertConstraints(
            model.FindEntityType(typeof(RetentionScheduleState))!,
            "CK_retention_schedule_state_coordinates",
            "CK_retention_schedule_state_versions",
            "CK_retention_schedule_state_time",
            "CK_retention_schedule_state_target",
            "CK_retention_schedule_state_result");
        AssertConstraints(
            model.FindEntityType(typeof(RetentionRunRetryRequest))!,
            "CK_retention_run_retry_request_coordinates",
            "CK_retention_run_retry_request_versions",
            "CK_retention_run_retry_request_target",
            "CK_retention_run_retry_request_state",
            "CK_retention_run_retry_request_timestamps");
    }

    [Fact]
    public void Scope_projections_have_complete_model_owned_constraints()
    {
        using RetentionDbContext context = CreateContext();
        IModel model = context.GetService<IDesignTimeModel>().Model;

        AssertConstraints(
            model.FindEntityType(typeof(RetentionTenantProjection))!,
            "CK_retention_tenant_projection_coordinates",
            "CK_retention_tenant_projection_version");
        AssertConstraints(
            model.FindEntityType(typeof(RetentionPropertyProjection))!,
            "CK_retention_property_projection_coordinates",
            "CK_retention_property_projection_versions",
            "CK_retention_property_projection_topology",
            "CK_retention_property_projection_policy",
            "CK_retention_property_projection_known");
    }

    [Fact]
    public void Tenant_revision_is_scope_keyed_and_concurrency_guarded()
    {
        using RetentionDbContext context = CreateContext();
        IEntityType revision = context.Model.FindEntityType(
            typeof(RetentionTenantRevision))!;
        IEntityType designRevision = context
            .GetService<IDesignTimeModel>()
            .Model
            .FindEntityType(typeof(RetentionTenantRevision))!;

        Assert.Equal(
            [nameof(RetentionTenantRevision.ScopeId)],
            revision.FindPrimaryKey()!.Properties.Select(
                property => property.Name));
        Assert.True(
            revision.FindProperty(nameof(RetentionTenantRevision.Revision))!
                .IsConcurrencyToken);
        Assert.Contains(
            designRevision.GetCheckConstraints(),
            constraint => string.Equals(
                constraint.Name,
                "CK_retention_tenant_revision_positive",
                StringComparison.Ordinal));
        Assert.Contains(
            designRevision.GetCheckConstraints(),
            constraint => string.Equals(
                constraint.Name,
                "CK_retention_tenant_revision_lifecycle",
                StringComparison.Ordinal));
    }

    [Fact]
    public void Tenant_destruction_progress_is_bounded_and_receipt_is_unique_per_scope()
    {
        using RetentionDbContext context = CreateContext();
        IModel model = context.GetService<IDesignTimeModel>().Model;
        IEntityType operation = model.FindEntityType(
            typeof(RetentionTenantDestroyOperation))!;
        IEntityType receipt = model.FindEntityType(
            typeof(RetentionTenantDestroyReceipt))!;

        Assert.Contains(
            operation.GetCheckConstraints(),
            constraint => string.Equals(
                constraint.Name,
                "CK_retention_tenant_destroy_operation_batch",
                StringComparison.Ordinal));
        Assert.Contains(
            receipt.GetIndexes(),
            index => index.IsUnique && index.Properties.Select(
                property => property.Name).SequenceEqual(
                    [nameof(RetentionTenantDestroyReceipt.ScopeId)]));
    }

    [Fact]
    public async Task Health_projection_exposes_the_exact_task_run_coordinate()
    {
        await using RetentionDbContext context = CreateContext();
        Guid runId = Guid.NewGuid();
        DateTimeOffset startedAtUtc =
            new(2026, 8, 4, 12, 0, 0, TimeSpan.Zero);
        RetentionExecution execution = RetentionExecution.Start(
            runId,
            "tenant-a",
            "retention",
            "execution-history",
            RetentionExecutionTargetKind.Tenant,
            propertyId: null,
            executionPolicyVersion: 1,
            attempt: 1,
            startedAtUtc,
            startedAtUtc.AddMinutes(5)).Value;
        RetentionScheduleState schedule = new(
            execution,
            startedAtUtc.AddHours(1));
        context.ScheduleStates.Add(schedule);
        RetentionRunRetryRequest currentRetry =
            RetentionRunRetryRequest.Create(
                Guid.NewGuid(),
                Guid.NewGuid(),
                "tenant-a",
                runId,
                "retention",
                "execution-history",
                RetentionExecutionTargetKind.Tenant,
                propertyId: null,
                executionPolicyVersion: 1,
                evidenceVersion: schedule.Version,
                startedAtUtc.AddMinutes(1),
                scheduledAtUtc: null).Value;
        RetentionRunRetryRequest staleRetry = RetentionRunRetryRequest.Create(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "tenant-a",
            runId,
            "retention",
            "execution-history",
            RetentionExecutionTargetKind.Tenant,
            propertyId: null,
            executionPolicyVersion: 1,
            evidenceVersion: schedule.Version + 1,
            startedAtUtc.AddMinutes(2),
            scheduledAtUtc: null).Value;
        context.RunRetryRequests.AddRange(currentRetry, staleRetry);
        await context.SaveChangesAsync();

        RetentionScheduleStateRepository repository = new(context);
        RetentionScheduleStateSnapshot snapshot = Assert.Single(
            await repository.ListAsync(CancellationToken.None));

        Assert.Equal(runId, snapshot.LastExecutionId);
        Assert.Equal(currentRetry.Id, snapshot.Retry?.RequestId);
        Assert.Equal(
            runId,
            (await repository.GetAsync(
                "tenant-a",
                "retention",
                "execution-history",
                propertyId: null,
                executionPolicyVersion: 1,
                CancellationToken.None))?.LastExecutionId);
        Assert.Null(await repository.GetAsync(
            "tenant-b",
            "retention",
            "execution-history",
            propertyId: null,
            executionPolicyVersion: 1,
            CancellationToken.None));
        Assert.Null(await repository.GetAsync(
            "tenant-a",
            "retention",
            "execution-history",
            propertyId: null,
            executionPolicyVersion: 2,
            CancellationToken.None));
    }

    private static void AssertScopeFiltered<TEntity>(
        RetentionDbContext context)
    {
        IEntityType entity = context.Model.FindEntityType(typeof(TEntity))!;
        Assert.NotEmpty(entity.GetDeclaredQueryFilters());
    }

    private static void AssertConstraints(
        IEntityType entity,
        params string[] expectedNames)
    {
        string[] actualNames = entity.GetCheckConstraints()
            .Select(constraint => constraint.Name!)
            .Order(StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(
            expectedNames.Order(StringComparer.Ordinal),
            actualNames);
    }

    private static RetentionDbContext CreateContext()
    {
        DbContextOptions<RetentionDbContext> options =
            new DbContextOptionsBuilder<RetentionDbContext>()
                .UseInMemoryDatabase(
                    $"retention-model-{Guid.NewGuid():N}")
                .Options;
        return new(options, new TestScopeContext());
    }

    private sealed class TestScopeContext : IScopeContext
    {
        public bool IsEnabled => true;
        public string ScopeId => "tenant-a";
    }
}
