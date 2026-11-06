namespace BunkFy.Modules.Retention.Persistence;

using BunkFy.Modules.DataRights.Contracts;
using BunkFy.Modules.Retention.Application.Ports;
using BunkFy.Modules.Retention.Persistence.Repositories;
using Gma.Framework.Cqrs;
using Gma.Framework.Cqrs.Infrastructure;
using Gma.Framework.Cqrs.UnitOfWork;
using Gma.Framework.Messaging;
using Gma.Framework.Persistence.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

public static class DependencyInjection
{
    public static IHostApplicationBuilder AddRetentionPersistence(
        this IHostApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Services.AddPersistenceOptions(builder.Configuration);
        builder.Services.TryAddModuleDbContext<RetentionDbContext>(options =>
            options.UseConfiguredProvider(
                builder.Configuration,
                RetentionMigrations.SqlServerAssembly,
                RetentionMigrations.PostgreSqlAssembly,
                RetentionMigrations.Schema,
                RetentionMigrations.HistoryTable));
        builder.Services.TryAddScoped<
            IRetentionExecutionRepository,
            RetentionExecutionRepository>();
        builder.Services.TryAddScoped<
            IRetentionMutationLock,
            RetentionMutationLock>();
        builder.Services.TryAddScoped<
            IRetentionScopeRepository,
            RetentionScopeRepository>();
        builder.Services.TryAddScoped<RetentionScheduleStateRepository>();
        builder.Services.TryAddScoped<IRetentionScheduleStateRepository>(provider =>
            provider.GetRequiredService<RetentionScheduleStateRepository>());
        builder.Services.TryAddScoped<IRetentionScheduleHealthReader>(provider =>
            provider.GetRequiredService<RetentionScheduleStateRepository>());
        builder.Services.TryAddScoped<
            IRetentionRunRetryRequestRepository,
            RetentionRunRetryRequestRepository>();
        RetentionTenantTerminationExportSchema.EnsureValid();
        builder.Services.TryAddEnumerable(
            ServiceDescriptor.Scoped<
                ITenantTerminationContributor,
                RetentionTenantTerminationContributor>());
        builder.Services.TryAddEnumerable(
            ServiceDescriptor.Scoped<
                ITenantTerminationExportContributor,
                RetentionTenantTerminationContributor>());
        builder.Services.TryAddEnumerable(ServiceDescriptor.Scoped(
            typeof(ICommandPipelineBehavior<,>),
            typeof(RetentionPersistenceAdmissionBehavior<,>)));
        builder.Services.MoveCommandUnitOfWorkBehaviorToEnd();
        builder.Services.TryAddEnumerable(
            ServiceDescriptor.Scoped<IUnitOfWork, RetentionUnitOfWork>());
        builder.Services.TryAddEnumerable(
            ServiceDescriptor.Scoped<IInboxStore, RetentionInboxStore>());
        builder.Services.TryAddEnumerable(
            ServiceDescriptor.Scoped<IOutboxWriter, RetentionOutboxWriter>());
        builder.Services.TryAddEnumerable(
            ServiceDescriptor.Scoped<IOutboxStore, RetentionOutboxStore>());
        return builder;
    }
}
