namespace BunkFy.Modules.Inventory.Persistence;

using BunkFy.Modules.DataRights.Contracts;
using BunkFy.Modules.Inventory.Application.Ports;
using BunkFy.Modules.Inventory.Contracts;
using BunkFy.Modules.Inventory.Persistence.Repositories;
using BunkFy.Modules.Properties.Contracts;
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
    public static IHostApplicationBuilder AddInventoryPersistence(this IHostApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Services.AddPersistenceOptions(builder.Configuration);
        builder.Services.TryAddModuleDbContext<InventoryDbContext>(options =>
            options.UseConfiguredProvider(
                builder.Configuration,
                InventoryMigrations.SqlServerAssembly,
                InventoryMigrations.PostgreSqlAssembly,
                InventoryMigrations.Schema,
                InventoryMigrations.HistoryTable));

        builder.Services.TryAddScoped<IInventoryTopologyRepository, InventoryTopologyRepository>();
        builder.Services.TryAddScoped<IRoomInventoryConfigurationRepository, RoomInventoryConfigurationRepository>();
        builder.Services.TryAddScoped<IInventoryReadRepository, InventoryReadRepository>();
        builder.Services.TryAddScoped<BunkFy.Modules.Inventory.Contracts.Stations.IStationInventoryLabelReader, StationInventoryLabelReader>();
        builder.Services.TryAddScoped<IInventoryRetirementContextReadRepository, InventoryRetirementContextReadRepository>();
        builder.Services.TryAddScoped<
            IInventoryBusinessDateProvider,
            InventoryBusinessDateProvider>();
        builder.Services.TryAddScoped<IInventoryAvailabilityRepository, InventoryAvailabilityRepository>();
        builder.Services.TryAddScoped<
            IInventoryAvailabilitySelectionFence,
            InventoryAvailabilitySelectionFence>();
        builder.Services.TryAddScoped<
            IManualInventoryBlockGroupRepository,
            ManualInventoryBlockGroupRepository>();
        builder.Services.TryAddScoped<IManualInventoryBlockRepository, ManualInventoryBlockRepository>();
        builder.Services.TryAddScoped<
            IInventoryManagementOperationRepository,
            InventoryManagementOperationRepository>();
        builder.Services.TryAddScoped<
            IInventoryManagementLock,
            InventoryManagementLock>();
        builder.Services.TryAddScoped<IInventoryAvailabilityProjectionExportSource, InventoryAvailabilityProjectionExportSource>();
        builder.Services.TryAddScoped<IInventoryAllocationRepository, InventoryAllocationRepository>();
        builder.Services.TryAddScoped<IInventoryAllocationAmendmentDecisionRepository, InventoryAllocationAmendmentDecisionRepository>();
        builder.Services.TryAddScoped<
            IInventoryAllocationRequestLock,
            InventoryAllocationRequestLock>();
        builder.Services.TryAddScoped<
            IInventoryAllocationOperationLock,
            InventoryAllocationOperationLockRepository>();
        builder.Services.TryAddScoped<
            IInventoryAllocationAnonymisationRepository,
            InventoryAllocationAnonymisationRepository>();
        builder.Services.TryAddScoped<
            IInventoryAllocationAnonymisationRestoreRepository,
            InventoryAllocationAnonymisationRepository>();
        builder.Services.TryAddEnumerable(
            ServiceDescriptor.Scoped<
                IDataRightsSubjectDiscoveryContributor,
                InventoryDataRightsDiscoveryContributor>());
        InventoryDataRightsExportSchema.EnsureValid();
        builder.Services.TryAddEnumerable(
            ServiceDescriptor.Scoped<
                IDataRightsSubjectExportContributor,
                InventoryDataRightsExportContributor>());
        InventoryTenantTerminationExportSchema.EnsureValid();
        builder.Services.TryAddEnumerable(
            ServiceDescriptor.Scoped<
                ITenantTerminationContributor,
                InventoryTenantTerminationContributor>());
        builder.Services.TryAddEnumerable(
            ServiceDescriptor.Scoped<
                ITenantTerminationExportContributor,
                InventoryTenantTerminationContributor>());
        builder.Services.TryAddEnumerable(ServiceDescriptor.Scoped(
            typeof(ICommandPipelineBehavior<,>),
            typeof(InventoryPersistenceRetryBehavior<,>)));
        builder.Services.TryAddEnumerable(ServiceDescriptor.Scoped(
            typeof(ICommandPipelineBehavior<,>),
            typeof(InventoryPersistenceAdmissionBehavior<,>)));
        builder.Services.TryAddEnumerable(ServiceDescriptor.Scoped(
            typeof(IQueryPipelineBehavior<,>),
            typeof(InventoryRepeatableReadQueryBehavior<,>)));
        builder.Services.MoveCommandUnitOfWorkBehaviorToEnd();
        builder.Services.TryAddScoped<IBedRetirementRepository, BedRetirementRepository>();
        builder.Services.TryAddScoped<IRoomRetirementRepository, RoomRetirementRepository>();
        builder.Services.TryAddScoped<IProjectionRebuildWriter<PropertyTopologyProjectionExport>, InventoryTopologyProjectionRebuildWriter>();
        builder.Services.TryAddEnumerable([
            ServiceDescriptor.Scoped<IUnitOfWork, InventoryUnitOfWork>(),
            ServiceDescriptor.Scoped<IInboxStore, InventoryInboxStore>(),
            ServiceDescriptor.Scoped<IOutboxWriter, InventoryOutboxWriter>(),
            ServiceDescriptor.Scoped<IOutboxStore, InventoryOutboxStore>(),
            ServiceDescriptor.Scoped<IProjectionRebuildCheckpointStore, InventoryProjectionRebuildCheckpointStore>(),
            ServiceDescriptor.Scoped<IProjectionRebuildTransactionBoundary, InventoryProjectionRebuildTransactionBoundary>()
        ]);

        return builder;
    }
}
