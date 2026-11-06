namespace BunkFy.Modules.Workspaces.Persistence;

using BunkFy.Modules.DataRights.Contracts;
using BunkFy.Modules.Properties.Contracts;
using BunkFy.Modules.Workspaces.Application.Ports;
using BunkFy.Modules.Workspaces.Contracts;
using BunkFy.Modules.Workspaces.Persistence.Repositories;
using BunkFy.Modules.Workspaces.Persistence.TenantTermination;
using Gma.Framework.Cqrs;
using Gma.Framework.Cqrs.Infrastructure;
using Gma.Framework.Cqrs.UnitOfWork;
using Gma.Framework.Messaging;
using Gma.Framework.Persistence.EntityFrameworkCore;
using Gma.Framework.ProjectionRebuild;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

public static class DependencyInjection
{
    public static IHostApplicationBuilder AddWorkspacesPersistence(
        this IHostApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.AddWorkspacesTerminationAdmissionPersistence();
        builder.Services.TryAddScoped<
            IWorkspaceTerminationFenceRepository,
            WorkspaceTerminationFenceRepository>();
        builder.Services.TryAddScoped<
            IWorkspaceTenantDestructionOwner,
            WorkspaceTenantDestructionOwner>();
        builder.Services.TryAddScoped<
            IWorkspaceStaffOnboardingRepository,
            WorkspaceStaffOnboardingRepository>();
        builder.Services.TryAddScoped<
            IWorkspaceStaffOnboardingRestorationSuppressionReader,
            WorkspaceStaffOnboardingRestorationSuppressionReader>();
        builder.Services.TryAddScoped<
            IWorkspaceStaffOnboardingIdentityAnchorSubjectMutationFence,
            WorkspaceStaffOnboardingIdentityAnchorSubjectMutationFence>();
        builder.Services.TryAddScoped<
            IWorkspaceStaffIdentityAnchorCutoverSourceReader,
            WorkspaceStaffIdentityAnchorCutoverSourceReader>();
        builder.Services.TryAddScoped<
            IWorkspaceStaffIdentityAnchorSweepRepository,
            WorkspaceStaffIdentityAnchorSweepRepository>();
        builder.Services.TryAddScoped<
            IWorkspaceStaffHistoricalNoProvisionReceiptRepository,
            WorkspaceStaffHistoricalNoProvisionReceiptRepository>();
        builder.Services.TryAddScoped<
            IWorkspaceIdentityAnchorCutoverExecutionBoundary,
            WorkspaceIdentityAnchorCutoverExecutionBoundary>();
        builder.Services.TryAddScoped<
            IWorkspaceStaffDeferredClaimWithdrawalRepository,
            WorkspaceStaffDeferredClaimWithdrawalRepository>();
        builder.Services.TryAddScoped<
            IWorkspaceStaffOnboardingRetentionRepository,
            WorkspaceStaffOnboardingRetentionRepository>();
        builder.Services.TryAddScoped<
            IWorkspaceStaffOnboardingRetentionExecutionRepository,
            WorkspaceStaffOnboardingRetentionExecutionRepository>();
        builder.Services.TryAddScoped<
            IWorkspaceStaffOnboardingRetentionExecutionLock,
            WorkspaceStaffOnboardingRetentionExecutionLock>();
        builder.Services.TryAddScoped<
            IWorkspaceStaffOnboardingCorrectionReceiptRepository,
            WorkspaceStaffOnboardingCorrectionReceiptRepository>();
        builder.Services.TryAddScoped<
            IWorkspaceStaffOnboardingRecoveryReceiptRepository,
            WorkspaceStaffOnboardingRecoveryReceiptRepository>();
        builder.Services.TryAddScoped<
            IWorkspaceStaffOnboardingRecoveryOperationLock,
            WorkspaceStaffOnboardingRecoveryOperationLock>();
        builder.Services.TryAddScoped<
            IWorkspaceStaffOnboardingProcessingRestrictionRepository,
            WorkspaceStaffOnboardingProcessingRestrictionRepository>();
        builder.Services.TryAddScoped<
            IWorkspaceStaffOnboardingProcessingRestrictionProjectionRepository,
            WorkspaceStaffOnboardingProcessingRestrictionProjectionRepository>();
        builder.Services.TryAddScoped<
            IWorkspaceStaffOnboardingOperationLock,
            WorkspaceStaffOnboardingOperationLock>();
        builder.Services.TryAddScoped<
            IWorkspaceStaffOnboardingSerializedReadBoundary,
            WorkspaceStaffOnboardingSerializedReadBoundary>();
        builder.Services.TryAddScoped<
            IWorkspaceStaffOnboardingDataRightsCorrectionTargetReader,
            WorkspaceStaffOnboardingDataRightsCorrectionTargetReader>();
        builder.Services.TryAddScoped<
            IWorkspaceStaffAccessProcessRepository,
            WorkspaceStaffAccessProcessRepository>();
        builder.Services.TryAddScoped<
            IWorkspaceStaffAccessOperationLock,
            WorkspaceStaffAccessOperationLock>();
        builder.Services.TryAddScoped<
            IWorkspaceStaffAccessPlanRepository,
            WorkspaceStaffAccessPlanRepository>();
        builder.Services.TryAddScoped<
            IWorkspaceStaffRetentionCorrelationRepository,
            WorkspaceStaffRetentionCorrelationRepository>();
        builder.Services.TryAddScoped<
            IWorkspaceStaffCorrelationAnonymisationRepository,
            WorkspaceStaffCorrelationAnonymisationRepository>();
        builder.Services.TryAddScoped<
            IWorkspaceStaffCorrelationOperationLock,
            WorkspaceStaffCorrelationOperationLock>();
        builder.Services.TryAddScoped<
            IWorkspaceCrossGraphMutationLock,
            WorkspaceCrossGraphMutationLock>();
        builder.Services.TryAddEnumerable(
            ServiceDescriptor.Scoped<
                IDataRightsSubjectDiscoveryContributor,
                WorkspacesDataRightsDiscoveryContributor>());
        WorkspacesDataRightsExportSchema.EnsureValid();
        builder.Services.TryAddEnumerable(
            ServiceDescriptor.Scoped<
                IDataRightsSubjectExportContributor,
                WorkspacesDataRightsExportContributor>());
        builder.Services.TryAddEnumerable(
            ServiceDescriptor.Scoped<
                ITenantTerminationExportContributor,
                WorkspacesTenantTerminationExportContributor>());
        builder.Services.TryAddScoped<
            IWorkspacePropertyProjectionRepository,
            WorkspacePropertyProjectionRepository>();
        builder.Services.TryAddScoped<
            IProjectionRebuildWriter<PropertyTopologyProjectionExport>,
            WorkspacePropertiesProjectionRebuildWriter>();
        builder.Services.TryAddEnumerable(ServiceDescriptor.Scoped(
            typeof(ICommandPipelineBehavior<,>),
            typeof(WorkspacesPersistenceRetryBehavior<,>)));
        builder.Services.MoveCommandUnitOfWorkBehaviorToEnd();
        builder.Services.TryAddEnumerable(
            ServiceDescriptor.Scoped<IUnitOfWork, WorkspacesUnitOfWork>());
        builder.Services.TryAddEnumerable(
            ServiceDescriptor.Scoped<IOutboxWriter, WorkspacesOutboxWriter>());
        builder.Services.TryAddEnumerable(
            ServiceDescriptor.Scoped<IOutboxStore, WorkspacesOutboxStore>());
        builder.Services.TryAddEnumerable(
            ServiceDescriptor.Scoped<IInboxStore, WorkspacesInboxStore>());
        builder.Services.TryAddEnumerable(
            ServiceDescriptor.Scoped<IProjectionRebuildCheckpointStore,
                WorkspaceProjectionRebuildCheckpointStore>());
        builder.Services.TryAddEnumerable(
            ServiceDescriptor.Scoped<IProjectionRebuildTransactionBoundary,
                WorkspaceProjectionRebuildTransactionBoundary>());
        return builder;
    }

    public static IHostApplicationBuilder
        AddWorkspacesTerminationAdmissionPersistence(
            this IHostApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Services.AddPersistenceOptions(builder.Configuration);
        builder.Services.TryAddModuleDbContext<WorkspacesDbContext>(options =>
            options.UseConfiguredProvider(
                builder.Configuration,
                WorkspacesMigrations.SqlServerAssembly,
                WorkspacesMigrations.PostgreSqlAssembly,
                WorkspacesMigrations.Schema,
                WorkspacesMigrations.HistoryTable));
        builder.Services.TryAddScoped<
            IWorkspaceTerminationFenceReader,
            WorkspaceTerminationFenceRepository>();
        return builder;
    }
}
