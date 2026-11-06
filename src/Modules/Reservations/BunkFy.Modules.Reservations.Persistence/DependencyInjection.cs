namespace BunkFy.Modules.Reservations.Persistence;

using BunkFy.Modules.DataRights.Contracts;
using BunkFy.Modules.Guests.Contracts;
using BunkFy.Modules.Inventory.Contracts;
using BunkFy.Modules.Properties.Contracts;
using BunkFy.Modules.Reservations.Application.Ports;
using BunkFy.Modules.Reservations.Persistence.Repositories;
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
    public static IHostApplicationBuilder AddReservationsPersistence(this IHostApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Services.AddPersistenceOptions(builder.Configuration);

        builder.Services.TryAddModuleDbContext<ReservationsDbContext>(options =>
            options.UseConfiguredProvider(
                builder.Configuration,
                ReservationsMigrations.SqlServerAssembly,
                ReservationsMigrations.PostgreSqlAssembly,
                ReservationsMigrations.Schema,
                ReservationsMigrations.HistoryTable));

        builder.Services.TryAddScoped<IReservationRepository, ReservationRepository>();
        builder.Services.TryAddScoped<IStationDueArrivalRepository, StationDueArrivalRepository>();
        builder.Services.TryAddScoped<IStationDueDepartureRepository, StationDueDepartureRepository>();
        builder.Services.TryAddScoped<
            IReservationOperationsSnapshotReader,
            ReservationOperationsSnapshotReader>();
        builder.Services.TryAddScoped<
            IReservationGuestRecordLinkProcessRepository,
            ReservationGuestRecordLinkProcessRepository>();
        builder.Services.TryAddScoped<
            IReservationDataRightsCorrectionReceiptRepository,
            ReservationDataRightsCorrectionReceiptRepository>();
        builder.Services.TryAddScoped<
            IReservationProcessingRestrictionRepository,
            ReservationProcessingRestrictionRepository>();
        builder.Services.TryAddScoped<
            IReservationProcessingRestrictionProjectionRepository,
            ReservationProcessingRestrictionProjectionRepository>();
        builder.Services.TryAddScoped<
            IReservationDataHoldRepository,
            ReservationDataHoldRepository>();
        builder.Services.TryAddScoped<
            IReservationOperationLock,
            ReservationOperationLockRepository>();
        builder.Services.TryAddScoped<
            IReservationAnonymisationEligibilityRepository,
            ReservationAnonymisationEligibilityRepository>();
        builder.Services.TryAddScoped<
            IReservationRetentionCandidateRepository,
            ReservationRetentionCandidateRepository>();
        builder.Services.TryAddScoped<
            IReservationRetentionExecutionRepository,
            ReservationRetentionExecutionRepository>();
        builder.Services.TryAddScoped<ReservationAnonymisationRepository>();
        builder.Services.TryAddScoped<
            IReservationAnonymisationRepository>(services =>
            services.GetRequiredService<
                ReservationAnonymisationRepository>());
        builder.Services.TryAddScoped<
            IReservationAnonymisationRestoreRepository>(services =>
            services.GetRequiredService<
                ReservationAnonymisationRepository>());
        builder.Services.TryAddScoped<
            IReservationProcessingRestrictionProjectionRebuildSource,
            ReservationProcessingRestrictionProjectionRebuildSource>();
        builder.Services.TryAddScoped<IReservationDetailsHistoryWriter, ReservationDetailsHistoryWriter>();
        builder.Services.TryAddScoped<IReservationDetailsHistoryReader, ReservationDetailsHistoryReader>();
        builder.Services.TryAddScoped<IReservationExternalOperationRepository, ReservationExternalOperationRepository>();
        builder.Services.TryAddScoped<
            IReservationManagementOperationRepository,
            ReservationManagementOperationRepository>();
        builder.Services.TryAddScoped<
            IReservationStayAmendmentOperationRepository,
            ReservationStayAmendmentOperationRepository>();
        builder.Services.TryAddScoped<IInventoryProjectionRepository, InventoryProjectionRepository>();
        builder.Services.TryAddScoped<IReservationGuestProfileProjectionRepository, ReservationGuestProfileProjectionRepository>();
        builder.Services.TryAddScoped<ReservationArrivalReminderRepository>();
        builder.Services.TryAddScoped<IReservationArrivalReminderRepository>(services =>
            services.GetRequiredService<ReservationArrivalReminderRepository>());
        builder.Services.TryAddScoped<IReservationPropertyPolicyRepository>(services =>
            services.GetRequiredService<ReservationArrivalReminderRepository>());
        builder.Services.TryAddScoped<IProjectionRebuildWriter<InventoryAvailabilityProjectionExport>, InventoryProjectionRebuildWriter>();
        builder.Services.TryAddScoped<IProjectionRebuildWriter<GuestProfileEligibilityProjectionExport>, ReservationGuestProfilesProjectionRebuildWriter>();
        builder.Services.TryAddScoped<
            IProjectionRebuildWriter<GuestProcessingRestrictionProjectionExport>,
            ReservationGuestRestrictionsProjectionRebuildWriter>();
        builder.Services.TryAddScoped<IReservationPropertyProjectionRebuildWriter, ReservationPropertyProjectionRebuildWriter>();
        builder.Services.TryAddScoped<
            IProjectionRebuildWriter<
                ReservationProcessingRestrictionProjectionSnapshot>,
            ReservationProcessingRestrictionProjectionRebuildWriter>();
        builder.Services.TryAddScoped<IReservationGuestStayProjectionExportSource, ReservationGuestStayProjectionExportSource>();
        builder.Services.TryAddEnumerable(
            ServiceDescriptor.Scoped<
                IDataRightsSubjectDiscoveryContributor,
                ReservationDataRightsDiscoveryContributor>());
        ReservationDataRightsExportSchema.EnsureValid();
        builder.Services.TryAddEnumerable(
            ServiceDescriptor.Scoped<
                IDataRightsSubjectExportContributor,
                ReservationDataRightsExportContributor>());
        ReservationsTenantTerminationExportSchema.EnsureValid();
        builder.Services.TryAddEnumerable(
            ServiceDescriptor.Scoped<
                ITenantTerminationContributor,
                ReservationsTenantTerminationContributor>());
        builder.Services.TryAddEnumerable(
            ServiceDescriptor.Scoped<
                ITenantTerminationExportContributor,
                ReservationsTenantTerminationContributor>());

        builder.Services.TryAddEnumerable(ServiceDescriptor.Scoped(
            typeof(ICommandPipelineBehavior<,>),
            typeof(ReservationsPersistenceRetryBehavior<,>)));
        builder.Services.TryAddEnumerable(ServiceDescriptor.Scoped(
            typeof(ICommandPipelineBehavior<,>),
            typeof(ReservationsPersistenceAdmissionBehavior<,>)));
        builder.Services.MoveCommandUnitOfWorkBehaviorToEnd();
        builder.Services.TryAddEnumerable(ServiceDescriptor.Scoped<IUnitOfWork, ReservationsUnitOfWork>());
        builder.Services.TryAddEnumerable(ServiceDescriptor.Scoped<IOutboxWriter, ReservationsOutboxWriter>());
        builder.Services.TryAddEnumerable(ServiceDescriptor.Scoped<IOutboxStore, ReservationsOutboxStore>());
        builder.Services.TryAddEnumerable(ServiceDescriptor.Scoped<IInboxStore, ReservationsInboxStore>());
        builder.Services.TryAddEnumerable(ServiceDescriptor.Scoped<IProjectionRebuildCheckpointStore, ReservationsProjectionRebuildCheckpointStore>());
        builder.Services.TryAddEnumerable(ServiceDescriptor.Scoped<IProjectionRebuildTransactionBoundary, ReservationsProjectionRebuildTransactionBoundary>());

        return builder;
    }
}
