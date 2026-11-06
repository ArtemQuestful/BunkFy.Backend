namespace BunkFy.Modules.DataRights.Tests.Application;

using BunkFy.Modules.DataRights.Application;
using BunkFy.Modules.DataRights.Application.Handlers;
using BunkFy.Modules.DataRights.Application.Tasks;
using BunkFy.Modules.DataRights.Contracts;
using Gma.Framework.Tasks;
using Gma.Framework.Tenancy;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

[Trait("Category", "Unit")]
public sealed class DataRightsApplicationCompositionTests
{
    [Fact]
    public void Bounded_owner_tasks_declare_exact_handler_timeouts()
    {
        ServiceCollection services = new();

        services.AddDataRightsTaskHandlers();

        Dictionary<
            (string TaskName, int PayloadVersion),
            TaskHandlerRegistration> registrations =
            services
                .Where(descriptor =>
                    descriptor.ServiceType == typeof(TaskHandlerRegistration))
                .Select(descriptor => Assert.IsType<TaskHandlerRegistration>(
                    descriptor.ImplementationInstance))
                .Where(registration => string.Equals(
                    registration.ModuleName,
                    DataRightsModuleMetadata.Name,
                    StringComparison.Ordinal))
                .ToDictionary(
                    registration => (
                        registration.TaskName,
                        registration.PayloadVersion));

        Assert.Equal(13, registrations.Count);
        Assert.Equal(
            DataRightsTaskExecutionPolicy.AnonymisationOwnerHandlerTimeout,
            registrations[(
                ExecuteDataRightsAnonymisationPayload.TaskName,
                ExecuteDataRightsAnonymisationPayload.PayloadVersion)]
                .HandlerTimeout);
        Assert.Equal(
            DataRightsTaskExecutionPolicy.AnonymisationOwnerHandlerTimeout,
            registrations[(
                ExecuteDataRightsAnonymisationPayloadV2.TaskName,
                ExecuteDataRightsAnonymisationPayloadV2.PayloadVersion)]
                .HandlerTimeout);
        Assert.Equal(
            DataRightsTaskExecutionPolicy.TenantTerminationOwnerHandlerTimeout,
            registrations[(
                ExecuteTenantTerminationOwnerWorkPayload.TaskName,
                ExecuteTenantTerminationOwnerWorkPayload.PayloadVersion)]
                .HandlerTimeout);
        Assert.Equal(
            DataRightsTaskExecutionPolicy.TenantTerminationOwnerHandlerTimeout,
            registrations[(
                ExecuteTenantTerminationExportOwnerWorkPayload.TaskName,
                ExecuteTenantTerminationExportOwnerWorkPayload.PayloadVersion)]
                .HandlerTimeout);
        Assert.Equal(
            DataRightsTaskExecutionPolicy.TenantTerminationOwnerHandlerTimeout,
            registrations[(
                ExecuteGlobalTenantTerminationOwnerWorkPayload.TaskName,
                ExecuteGlobalTenantTerminationOwnerWorkPayload.PayloadVersion)]
                .HandlerTimeout);
        Assert.Equal(
            DataRightsTaskExecutionPolicy
                .TenantTerminationVerificationHandlerTimeout,
            registrations[(
                VerifyTenantTerminationPayload.TaskName,
                VerifyTenantTerminationPayload.PayloadVersion)]
                .HandlerTimeout);

        (string TaskName, int PayloadVersion)[] fallbackTasks =
        [
            (RebuildDataRightsPropertiesPayload.TaskName,
                RebuildDataRightsPropertiesPayload.PayloadVersion),
            (GenerateDataRightsExportPayload.TaskName,
                GenerateDataRightsExportPayload.PayloadVersion),
            (DeleteExpiredDataRightsExportArtifactPayload.TaskName,
                DeleteExpiredDataRightsExportArtifactPayload.PayloadVersion),
            (GenerateTenantTerminationExportArtifactPayload.TaskName,
                GenerateTenantTerminationExportArtifactPayload.PayloadVersion),
            (DeleteExpiredTenantTerminationExportArtifactPayload.TaskName,
                DeleteExpiredTenantTerminationExportArtifactPayload.PayloadVersion),
            (DeleteExpiredTenantTerminationExportFragmentPayload.TaskName,
                DeleteExpiredTenantTerminationExportFragmentPayload.PayloadVersion),
            (DispatchDataRightsResponseDeadlineAlertsPayload.TaskName,
                DispatchDataRightsResponseDeadlineAlertsPayload.PayloadVersion)
        ];
        Assert.All(
            fallbackTasks,
            taskName => Assert.Null(registrations[taskName].HandlerTimeout));
        Assert.False(registrations[(
            DeleteExpiredTenantTerminationExportArtifactPayload.TaskName,
            DeleteExpiredTenantTerminationExportArtifactPayload
                .PayloadVersion)].IsTenantScoped());
        Assert.False(registrations[(
            DeleteExpiredTenantTerminationExportFragmentPayload.TaskName,
            DeleteExpiredTenantTerminationExportFragmentPayload
                .PayloadVersion)].IsTenantScoped());
    }

    [Fact]
    public void Handler_timeouts_cover_existing_owner_deadline_contracts()
    {
        Assert.Equal(
            BeginDataRightsAnonymisationWorkItemCommandHandler.OwnerCallTimeout +
                DataRightsTaskExecutionPolicy.CompletionGrace,
            DataRightsTaskExecutionPolicy.AnonymisationOwnerHandlerTimeout);
        Assert.Equal(
            BeginTenantTerminationOwnerWorkCommandHandler.OwnerCallTimeout +
                DataRightsTaskExecutionPolicy.CompletionGrace,
            DataRightsTaskExecutionPolicy.TenantTerminationOwnerHandlerTimeout);
        Assert.Equal(
            TimeSpan.FromTicks(checked(
                BeginTenantTerminationOwnerWorkCommandHandler
                    .OwnerCallTimeout.Ticks *
                TenantTerminationContract.MaximumContributors)) +
                DataRightsTaskExecutionPolicy.CompletionGrace,
            DataRightsTaskExecutionPolicy
                .TenantTerminationVerificationHandlerTimeout);
    }

    [Fact]
    public async Task Public_profile_scheduler_fails_closed_when_task_runtime_is_not_composed()
    {
        ServiceCollection services = new();
        services.AddDataRightsUnavailableTenantTerminationTaskScheduling();
        await using ServiceProvider provider = services.BuildServiceProvider();
        ITenantTerminationTaskScheduler scheduler = provider
            .GetRequiredService<ITenantTerminationTaskScheduler>();
        ITenantTerminationExportRetentionScheduler retention = provider
            .GetRequiredService<ITenantTerminationExportRetentionScheduler>();

        InvalidOperationException failure = await Assert.ThrowsAsync<InvalidOperationException>(
            () => scheduler.EnqueueVerificationAsync(
                "tenant-a",
                Guid.NewGuid(),
                operationRevision: 1,
                CancellationToken.None));

        Assert.Equal(UnavailableTenantTerminationTaskScheduler.ErrorCode, failure.Message);

        InvalidOperationException retentionFailure =
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                retention.EnqueueArtifactCleanupAsync(
                    "tenant-a",
                    Guid.NewGuid(),
                    Guid.NewGuid(),
                    operationRevision: 1,
                    new DateTimeOffset(
                        2026,
                        8,
                        16,
                        12,
                        0,
                        0,
                        TimeSpan.Zero),
                    CancellationToken.None));
        Assert.Equal(
            UnavailableTenantTerminationTaskScheduler.ErrorCode,
            retentionFailure.Message);
    }
}
