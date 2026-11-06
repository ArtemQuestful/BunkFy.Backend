namespace BunkFy.Modules.Guests.Persistence;

using BunkFy.Modules.DataRights.Contracts;
using BunkFy.Modules.Guests.Application.Ports;
using BunkFy.Modules.Guests.Contracts;
using BunkFy.Modules.Guests.Persistence.Repositories;
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
    public static IHostApplicationBuilder AddGuestsPersistence(this IHostApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Services.AddPersistenceOptions(builder.Configuration);
        builder.Services.TryAddModuleDbContext<GuestsDbContext>(options =>
            options.UseConfiguredProvider(
                builder.Configuration,
                GuestsMigrations.SqlServerAssembly,
                GuestsMigrations.PostgreSqlAssembly,
                GuestsMigrations.Schema,
                GuestsMigrations.HistoryTable));
        builder.Services.TryAddScoped<IGuestProfileRepository, GuestProfileRepository>();
        builder.Services.TryAddScoped<
            IGuestManagementOperationRepository,
            GuestManagementOperationRepository>();
        builder.Services.TryAddScoped<IGuestDataHoldRepository, GuestDataHoldRepository>();
        builder.Services.TryAddScoped<
            IGuestAnonymisationEligibilityRepository,
            GuestAnonymisationEligibilityRepository>();
        builder.Services.TryAddScoped<
            IGuestAnonymisationRepository,
            GuestAnonymisationRepository>();
        builder.Services.TryAddScoped<
            IGuestAnonymisationRestoreRepository,
            GuestAnonymisationRestoreRepository>();
        builder.Services.TryAddScoped<IGuestOperationLock, GuestOperationLockRepository>();
        builder.Services.TryAddScoped<
            IGuestAnonymisationExecutionBoundary,
            GuestAnonymisationExecutionBoundary>();
        builder.Services.TryAddScoped<
            IGuestRetentionCandidateRepository,
            GuestRetentionCandidateRepository>();
        builder.Services.TryAddScoped<
            IGuestRetentionExecutionRepository,
            GuestRetentionExecutionRepository>();
        builder.Services.TryAddScoped<
            IGuestDataRightsCorrectionReceiptRepository,
            GuestDataRightsCorrectionReceiptRepository>();
        builder.Services.TryAddScoped<
            IGuestProcessingRestrictionProjectionRepository,
            GuestProcessingRestrictionProjectionRepository>();
        builder.Services.TryAddScoped<
            IGuestProcessingRestrictionRepository,
            GuestProcessingRestrictionRepository>();
        builder.Services.TryAddScoped<
            IGuestProcessingRestrictionGate,
            GuestProcessingRestrictionGate>();
        builder.Services.TryAddScoped<
            IGuestProcessingRestrictionProjectionExportSource,
            GuestProcessingRestrictionProjectionExportSource>();
        builder.Services.TryAddScoped<IGuestProfileEligibilityProjectionExportSource, GuestProfileEligibilityProjectionExportSource>();
        builder.Services.TryAddScoped<IGuestPropertyProjectionRepository, GuestPropertyProjectionRepository>();
        builder.Services.TryAddScoped<IGuestStayHistoryRepository, GuestStayHistoryRepository>();
        builder.Services.TryAddEnumerable(
            ServiceDescriptor.Scoped<
                IDataRightsSubjectDiscoveryContributor,
                GuestDataRightsDiscoveryContributor>());
        GuestDataRightsExportSchema.EnsureValid();
        builder.Services.TryAddEnumerable(
            ServiceDescriptor.Scoped<
                IDataRightsSubjectExportContributor,
                GuestDataRightsExportContributor>());
        GuestsTenantTerminationExportSchema.EnsureValid();
        builder.Services.TryAddEnumerable(
            ServiceDescriptor.Scoped<
                ITenantTerminationContributor,
                GuestsTenantTerminationContributor>());
        builder.Services.TryAddEnumerable(
            ServiceDescriptor.Scoped<
                ITenantTerminationExportContributor,
                GuestsTenantTerminationContributor>());
        builder.Services.TryAddScoped<
            IGuestsPropertiesProjectionRebuildWriter,
            GuestsPropertiesProjectionRebuildWriter>();
        builder.Services.TryAddScoped<IProjectionRebuildWriter<ReservationGuestStayProjectionExport>, GuestStayHistoryProjectionRebuildWriter>();
        builder.Services.TryAddEnumerable(ServiceDescriptor.Scoped(
            typeof(ICommandPipelineBehavior<,>),
            typeof(GuestsPersistenceRetryBehavior<,>)));
        builder.Services.TryAddEnumerable(ServiceDescriptor.Scoped(
            typeof(ICommandPipelineBehavior<,>),
            typeof(GuestsPersistenceAdmissionBehavior<,>)));
        builder.Services.MoveCommandUnitOfWorkBehaviorToEnd();
        builder.Services.TryAddEnumerable(ServiceDescriptor.Scoped<IUnitOfWork, GuestsUnitOfWork>());
        builder.Services.TryAddEnumerable(ServiceDescriptor.Scoped<IOutboxWriter, GuestsOutboxWriter>());
        builder.Services.TryAddEnumerable(ServiceDescriptor.Scoped<IOutboxStore, GuestsOutboxStore>());
        builder.Services.TryAddEnumerable(ServiceDescriptor.Scoped<IInboxStore, GuestsInboxStore>());
        builder.Services.TryAddEnumerable(ServiceDescriptor.Scoped<IProjectionRebuildCheckpointStore, GuestsProjectionRebuildCheckpointStore>());
        builder.Services.TryAddEnumerable(ServiceDescriptor.Scoped<IProjectionRebuildTransactionBoundary, GuestsProjectionRebuildTransactionBoundary>());
        return builder;
    }
}
