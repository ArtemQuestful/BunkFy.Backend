namespace BunkFy.Modules.Staff.Persistence;

using BunkFy.Modules.DataRights.Contracts;
using BunkFy.Modules.Properties.Contracts;
using BunkFy.Modules.Staff.Application.Ports;
using BunkFy.Modules.Staff.Contracts;
using BunkFy.Modules.Staff.Persistence.Repositories;
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
    public static IHostApplicationBuilder AddStaffPersistence(this IHostApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Services.AddPersistenceOptions(builder.Configuration);
        builder.Services.TryAddModuleDbContext<StaffDbContext>(options => options.UseConfiguredProvider(
            builder.Configuration, StaffMigrations.SqlServerAssembly, StaffMigrations.PostgreSqlAssembly,
            StaffMigrations.Schema, StaffMigrations.HistoryTable));
        builder.Services.TryAddScoped<IStaffMemberRepository, StaffMemberRepository>();
        builder.Services.TryAddScoped<IStaffStationEligibilitySource, StaffStationEligibilitySource>();
        builder.Services.TryAddScoped<IStaffStationLabelReader, StaffStationLabelReader>();
        builder.Services.TryAddScoped<
            IStaffIdentityProvisioningAnchorRepository,
            StaffIdentityProvisioningAnchorRepository>();
        builder.Services.TryAddScoped<
            IStaffIdentityProvisioningAnchorResolutionRepository,
            StaffIdentityProvisioningAnchorResolutionRepository>();
        builder.Services.TryAddScoped<
            IStaffMemberMutationOperationRepository,
            StaffMemberMutationOperationRepository>();
        builder.Services.TryAddScoped<
            IStaffOnboardingProvisioningOperationRepository,
            StaffOnboardingProvisioningOperationRepository>();
        builder.Services.TryAddScoped<
            IStaffDataRightsCorrectionReceiptRepository,
            StaffDataRightsCorrectionReceiptRepository>();
        builder.Services.TryAddScoped<
            IStaffAnonymisationRepository,
            StaffAnonymisationRepository>();
        builder.Services.TryAddScoped<
            IStaffAnonymisationRestoreRepository,
            StaffAnonymisationRestoreRepository>();
        builder.Services.TryAddScoped<
            IStaffAnonymisationRestoreStateReader,
            StaffAnonymisationRestoreStateReader>();
        builder.Services.TryAddScoped<
            IStaffDataRightsAuthorityReader,
            StaffDataRightsAuthorityReader>();
        builder.Services.TryAddScoped<
            IStaffOperationalIdentityReader,
            StaffOperationalIdentityReader>();
        builder.Services.TryAddScoped<
            IStaffOnboardingStateVerifier,
            StaffOnboardingStateVerifier>();
        builder.Services.TryAddScoped<
            IStaffProcessingRestrictionRepository,
            StaffProcessingRestrictionRepository>();
        builder.Services.TryAddScoped<
            IStaffProcessingRestrictionProjectionRepository,
            StaffProcessingRestrictionProjectionRepository>();
        builder.Services.TryAddScoped<
            IStaffEmploymentGovernanceRepository,
            StaffEmploymentGovernanceRepository>();
        builder.Services.TryAddScoped<
            IStaffDataHoldRepository,
            StaffDataHoldRepository>();
        builder.Services.TryAddScoped<
            IStaffOperationLock,
            StaffOperationLockRepository>();
        builder.Services.TryAddScoped<
            IStaffCreationOperationLock,
            StaffCreationOperationLock>();
        builder.Services.TryAddScoped<
            IStaffRetentionCandidateRepository,
            StaffRetentionCandidateRepository>();
        builder.Services.TryAddScoped<
            IStaffRetentionExecutionRepository,
            StaffRetentionExecutionRepository>();
        builder.Services.TryAddScoped<
            IStaffProcessingRestrictionGate,
            StaffProcessingRestrictionGate>();
        builder.Services.TryAddScoped<IStaffPropertyAudienceReader, StaffPropertyAudienceReader>();
        builder.Services.TryAddScoped<
            IStaffNotificationRecipientResolver,
            StaffNotificationRecipientResolver>();
        builder.Services.TryAddScoped<IStaffPropertyProjectionRepository, StaffPropertyProjectionRepository>();
        builder.Services.TryAddEnumerable(
            ServiceDescriptor.Scoped<
                IDataRightsSubjectDiscoveryContributor,
                StaffDataRightsDiscoveryContributor>());
        StaffDataRightsExportSchema.EnsureValid();
        builder.Services.TryAddEnumerable(
            ServiceDescriptor.Scoped<
                IDataRightsSubjectExportContributor,
                StaffDataRightsExportContributor>());
        StaffTenantTerminationExportSchema.EnsureValid();
        builder.Services.TryAddEnumerable(
            ServiceDescriptor.Scoped<
                ITenantTerminationContributor,
                StaffTenantTerminationContributor>());
        builder.Services.TryAddEnumerable(
            ServiceDescriptor.Scoped<
                ITenantTerminationExportContributor,
                StaffTenantTerminationContributor>());
        builder.Services.TryAddScoped<IProjectionRebuildWriter<PropertyTopologyProjectionExport>,
            StaffPropertiesProjectionRebuildWriter>();
        builder.Services.TryAddEnumerable(ServiceDescriptor.Scoped(
            typeof(ICommandPipelineBehavior<,>),
            typeof(StaffPersistenceRetryBehavior<,>)));
        builder.Services.TryAddEnumerable(ServiceDescriptor.Scoped(
            typeof(ICommandPipelineBehavior<,>),
            typeof(StaffPersistenceAdmissionBehavior<,>)));
        builder.Services.MoveCommandUnitOfWorkBehaviorToEnd();
        builder.Services.TryAddEnumerable(ServiceDescriptor.Scoped<IUnitOfWork, StaffUnitOfWork>());
        builder.Services.TryAddEnumerable(ServiceDescriptor.Scoped<IOutboxWriter, StaffOutboxWriter>());
        builder.Services.TryAddEnumerable(ServiceDescriptor.Scoped<IOutboxStore, StaffOutboxStore>());
        builder.Services.TryAddEnumerable(ServiceDescriptor.Scoped<IInboxStore, StaffInboxStore>());
        builder.Services.TryAddEnumerable(ServiceDescriptor.Scoped<IProjectionRebuildCheckpointStore,
            StaffProjectionRebuildCheckpointStore>());
        builder.Services.TryAddEnumerable(ServiceDescriptor.Scoped<IProjectionRebuildTransactionBoundary,
            StaffProjectionRebuildTransactionBoundary>());
        return builder;
    }
}
