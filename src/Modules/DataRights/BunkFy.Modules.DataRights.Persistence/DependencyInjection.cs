namespace BunkFy.Modules.DataRights.Persistence;

using BunkFy.Modules.DataRights.Application.Ports;
using BunkFy.Modules.DataRights.Persistence.Repositories;
using BunkFy.Modules.DataRights.Persistence.Security;
using BunkFy.Modules.Properties.Contracts;
using Gma.Framework.Cqrs;
using Gma.Framework.Cqrs.Infrastructure;
using Gma.Framework.Cqrs.UnitOfWork;
using Gma.Framework.Messaging;
using Gma.Framework.Observability;
using Gma.Framework.Persistence.EntityFrameworkCore;
using Gma.Framework.ProjectionRebuild;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

public static class DependencyInjection
{
    public static IHostApplicationBuilder AddDataRightsPersistence(this IHostApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Services.AddSecuritySignalCore();
        builder.Services.TryAddEnumerable(
            ServiceDescriptor.Singleton<
                ISecuritySignalDefinitionSource,
                DataRightsExportSecuritySignalDefinitions>());
        builder.Services.AddPersistenceOptions(builder.Configuration);
        builder.Services.TryAddModuleDbContext<DataRightsDbContext>(options =>
            options.UseConfiguredProvider(
                builder.Configuration,
                DataRightsMigrations.SqlServerAssembly,
                DataRightsMigrations.PostgreSqlAssembly,
                DataRightsMigrations.Schema,
                DataRightsMigrations.HistoryTable));
        builder.Services.TryAddScoped<IDataRightsCaseRepository, DataRightsCaseRepository>();
        builder.Services.TryAddScoped<
            IDataRightsCaseIdentityRepository,
            DataRightsCaseRepository>();
        builder.Services.TryAddScoped<
            IDataRightsOperationLock,
            DataRightsOperationLock>();
        builder.Services.TryAddScoped<
            IDataRightsResponseDeadlineAlertRepository,
            DataRightsResponseDeadlineAlertRepository>();
        builder.Services.TryAddScoped<ITenantTerminationCaseRepository, DataRightsCaseRepository>();
        builder.Services.TryAddScoped<ITenantTerminationOperatorStatusRepository, TenantTerminationRepository>();
        builder.Services.TryAddScoped<
            IDataRightsCorrectionExecutionRepository,
            DataRightsCorrectionExecutionRepository>();
        builder.Services.TryAddScoped<
            IDataRightsExportArtifactRepository,
            DataRightsExportArtifactRepository>();
        builder.Services.TryAddScoped<
            IDataRightsExportAuditSink,
            DataRightsExportAuditSink>();
        builder.Services.TryAddScoped<
            IDataRightsExecutionBatchRepository,
            DataRightsExecutionBatchRepository>();
        builder.Services.TryAddScoped<
            IDataRightsExecutionWorkItemRepository,
            DataRightsExecutionWorkItemRepository>();
        builder.Services.TryAddScoped<
            IDataRightsProcessingLedgerRepository,
            DataRightsProcessingLedgerRepository>();
        builder.Services.TryAddScoped<
            IDataRightsRestoreCheckpointRepository,
            DataRightsRestoreCheckpointRepository>();
        builder.Services.TryAddScoped<
            ITenantTerminationRepository,
            TenantTerminationRepository>();
        builder.Services.TryAddScoped<
            ITenantTerminationExportFragmentRepository,
            TenantTerminationExportFragmentRepository>();
        builder.Services.TryAddScoped<
            ITenantTerminationExportArtifactRepository,
            TenantTerminationExportArtifactRepository>();
        builder.Services.TryAddScoped<
            ITenantTerminationTerminalReceiptRepository,
            TenantTerminationTerminalReceiptRepository>();
        AddProtectedLedgerServices(builder);
        AddProtectedExportServices(builder);
        builder.Services.TryAddScoped<
            IDataRightsPropertyProjectionRepository,
            DataRightsPropertyProjectionRepository>();
        builder.Services.TryAddScoped<
            IProjectionRebuildWriter<PropertyTopologyProjectionExport>,
            DataRightsPropertiesProjectionRebuildWriter>();
        builder.Services.TryAddEnumerable(ServiceDescriptor.Scoped(
            typeof(ICommandPipelineBehavior<,>),
            typeof(DataRightsPersistenceRetryBehavior<,>)));
        builder.Services.MoveCommandUnitOfWorkBehaviorToEnd();
        builder.Services.TryAddEnumerable(
            ServiceDescriptor.Scoped<IUnitOfWork, DataRightsUnitOfWork>());
        builder.Services.TryAddEnumerable(
            ServiceDescriptor.Scoped<IOutboxWriter, DataRightsOutboxWriter>());
        builder.Services.TryAddEnumerable(
            ServiceDescriptor.Scoped<IOutboxStore, DataRightsOutboxStore>());
        builder.Services.TryAddEnumerable(
            ServiceDescriptor.Scoped<IInboxStore, DataRightsInboxStore>());
        builder.Services.TryAddEnumerable(
            ServiceDescriptor.Scoped<
                IProjectionRebuildCheckpointStore,
                DataRightsProjectionRebuildCheckpointStore>());
        builder.Services.TryAddEnumerable(
            ServiceDescriptor.Scoped<
                IProjectionRebuildTransactionBoundary,
                DataRightsProjectionRebuildTransactionBoundary>());
        return builder;
    }

    public static IHostApplicationBuilder AddDataRightsRestoreReadinessGate(
        this IHostApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Services.TryAddSingleton<
            DataRightsRestoreReadinessState>();
        builder.Services.TryAddSingleton<IDataRightsRestoreReadiness>(
            services => services.GetRequiredService<
                DataRightsRestoreReadinessState>());
        builder.Services.TryAddSingleton<
            DataRightsRestoreReadinessHealthCheck>();
        builder.Services.TryAddEnumerable(
            ServiceDescriptor.Singleton<
                IConfigureOptions<Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckServiceOptions>,
                DataRightsRestoreHealthCheckOptionsSetup>());
        builder.Services.TryAddEnumerable(
            ServiceDescriptor.Singleton<
                IHostedService,
                DataRightsRestoreStartupGate>());
        return builder;
    }

    private static void AddProtectedLedgerServices(
        IHostApplicationBuilder builder)
    {
        bool isProduction = builder.Environment.IsProduction();
        IConfigurationSection pseudonymisationSection =
            builder.Configuration.GetSection(
                DataRightsPseudonymisationOptions.SectionName);
        bool useDevelopmentPseudonymisationKey =
            !isProduction && !pseudonymisationSection.Exists();
        builder.Services
            .AddOptions<DataRightsPseudonymisationOptions>()
            .Bind(pseudonymisationSection)
            .PostConfigure(options =>
            {
                if (useDevelopmentPseudonymisationKey)
                {
                    options.ActiveKeyVersion = 1;
                    options.Keys[1] =
                        DataRightsPseudonymisationOptions.DevelopmentKeyBase64;
                }
            })
            .ValidateOnStart();
        builder.Services.TryAddEnumerable(ServiceDescriptor.Singleton<
            IValidateOptions<DataRightsPseudonymisationOptions>>(
            new DataRightsPseudonymisationOptionsValidator(isProduction)));
        builder.Services.TryAddSingleton<
            IDataRightsRecordPseudonymizer,
            HmacDataRightsRecordPseudonymizer>();

        IConfigurationSection replayEnvelopeSection =
            builder.Configuration.GetSection(
                DataRightsReplayEnvelopeOptions.SectionName);
        bool useDevelopmentReplayEnvelopeKey =
            !isProduction && !replayEnvelopeSection.Exists();
        builder.Services
            .AddOptions<DataRightsReplayEnvelopeOptions>()
            .Bind(replayEnvelopeSection)
            .PostConfigure(options =>
            {
                if (useDevelopmentReplayEnvelopeKey)
                {
                    options.ActiveKeyVersion = 1;
                    options.Keys[1] =
                        DataRightsReplayEnvelopeOptions.DevelopmentKeyBase64;
                }
            })
            .ValidateOnStart();
        builder.Services.TryAddEnumerable(ServiceDescriptor.Singleton<
            IValidateOptions<DataRightsReplayEnvelopeOptions>>(
            new DataRightsReplayEnvelopeOptionsValidator(isProduction)));
        builder.Services.TryAddSingleton<
            IDataRightsReplayEnvelopeProtector,
            AesGcmDataRightsReplayEnvelopeProtector>();

        IConfigurationSection ledgerDeltaSection =
            builder.Configuration.GetSection(
                DataRightsLedgerDeltaOptions.SectionName);
        bool useDevelopmentLedgerDelta =
            !isProduction && !ledgerDeltaSection.Exists();
        builder.Services
            .AddOptions<DataRightsLedgerDeltaOptions>()
            .Bind(ledgerDeltaSection)
            .PostConfigure(options =>
            {
                if (useDevelopmentLedgerDelta)
                {
                    options.Provider = DataRightsLedgerDeltaProvider.LocalFile;
                    options.LocalFilePath = Path.Combine(
                        builder.Environment.ContentRootPath,
                        ".data",
                        "data-rights-ledger-delta");
                    options.ActiveIntegrityKeyVersion = 1;
                    options.IntegrityKeys[1] =
                        DataRightsLedgerDeltaOptions
                            .DevelopmentIntegrityKeyBase64;
                }
            })
            .ValidateOnStart();
        builder.Services.TryAddEnumerable(ServiceDescriptor.Singleton<
            IValidateOptions<DataRightsLedgerDeltaOptions>>(
            new DataRightsLedgerDeltaOptionsValidator(isProduction)));
        builder.Services.TryAddSingleton(TimeProvider.System);
        AddTenantTerminationReplayServices(builder, isProduction);

        DataRightsLedgerDeltaProvider configuredProvider =
            useDevelopmentLedgerDelta
                ? DataRightsLedgerDeltaProvider.LocalFile
                : ParseLedgerDeltaProvider(
                    ledgerDeltaSection[nameof(
                        DataRightsLedgerDeltaOptions.Provider)]);
        if (configuredProvider == DataRightsLedgerDeltaProvider.LocalFile)
        {
            builder.Services.TryAddSingleton<
                LocalFileDataRightsLedgerDeltaStore>();
            builder.Services.TryAddSingleton<IDataRightsLedgerDeltaStore>(
                services => services.GetRequiredService<
                    LocalFileDataRightsLedgerDeltaStore>());
            builder.Services.TryAddSingleton<IDataRightsRestoreScopeSource>(
                services => services.GetRequiredService<
                    LocalFileDataRightsLedgerDeltaStore>());
        }

        builder.Services.TryAddSingleton<
            IDataRightsLedgerDeltaStore,
            MissingDataRightsLedgerDeltaStore>();
        builder.Services.TryAddSingleton<
            IDataRightsRestoreScopeSource,
            MissingDataRightsRestoreScopeSource>();
        builder.Services.TryAddEnumerable(
            ServiceDescriptor.Singleton<
                IHostedService,
                DataRightsLedgerDeltaStartupValidator>());
    }

    private static void AddTenantTerminationReplayServices(
        IHostApplicationBuilder builder,
        bool isProduction)
    {
        IConfigurationSection section = builder.Configuration.GetSection(
            DataRightsTenantTerminationReplayOptions.SectionName);
        bool useDevelopmentProvider = !isProduction && !section.Exists();
        builder.Services
            .AddOptions<DataRightsTenantTerminationReplayOptions>()
            .Bind(section)
            .PostConfigure(options =>
            {
                if (useDevelopmentProvider)
                {
                    options.Provider =
                        DataRightsTenantTerminationReplayProvider.LocalFile;
                    options.LocalFilePath = Path.Combine(
                        builder.Environment.ContentRootPath,
                        ".data",
                        "tenant-termination-replay");
                }
            })
            .ValidateOnStart();
        builder.Services.TryAddEnumerable(ServiceDescriptor.Singleton<
            IValidateOptions<DataRightsTenantTerminationReplayOptions>>(
            new DataRightsTenantTerminationReplayOptionsValidator(
                isProduction)));

        DataRightsTenantTerminationReplayProvider provider =
            useDevelopmentProvider
                ? DataRightsTenantTerminationReplayProvider.LocalFile
                : ParseTenantTerminationReplayProvider(
                    section[nameof(
                        DataRightsTenantTerminationReplayOptions.Provider)]);
        if (provider ==
            DataRightsTenantTerminationReplayProvider.LocalFile)
        {
            builder.Services.TryAddSingleton<
                LocalFileTenantTerminationReplayStore>();
            builder.Services.TryAddSingleton<ITenantTerminationReplayStore>(
                services => services.GetRequiredService<
                    LocalFileTenantTerminationReplayStore>());
        }

        builder.Services.TryAddSingleton<ITenantTerminationReplayStore,
            MissingTenantTerminationReplayStore>();
        builder.Services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IHostedService,
                DataRightsTenantTerminationReplayStartupValidator>());
    }

    private static void AddProtectedExportServices(
        IHostApplicationBuilder builder)
    {
        bool isProduction = builder.Environment.IsProduction();
        IConfigurationSection section = builder.Configuration.GetSection(
            DataRightsExportArtifactOptions.SectionName);
        bool useDevelopmentKey = !isProduction && !section.Exists();
        builder.Services
            .AddOptions<DataRightsExportArtifactOptions>()
            .Bind(section)
            .PostConfigure(options =>
            {
                if (useDevelopmentKey)
                {
                    options.ActiveKeyVersion = 1;
                    options.Keys[1] =
                        DataRightsExportArtifactOptions.DevelopmentKeyBase64;
                }
            })
            .ValidateOnStart();
        builder.Services.TryAddEnumerable(ServiceDescriptor.Singleton<
            IValidateOptions<DataRightsExportArtifactOptions>>(
            new DataRightsExportArtifactOptionsValidator(isProduction)));
        builder.Services.TryAddEnumerable(ServiceDescriptor.Singleton<
            IValidateOptions<Gma.Framework.FileManagement.FileManagementOptions>,
            DataRightsExportArtifactStorageOptionsValidator>());
        builder.Services.TryAddSingleton<
            IDataRightsExportEnvelopeProtector,
            AesGcmDataRightsExportEnvelopeProtector>();
        builder.Services.TryAddScoped<ProtectedDataRightsExportObjectWriter>();
        builder.Services.TryAddScoped<ProtectedDataRightsExportObjectReader>();
        builder.Services.TryAddSingleton<
            IDataRightsExportArtifactPolicy,
            DataRightsExportArtifactPolicy>();
        builder.Services.TryAddScoped<
            IDataRightsExportArtifactGenerator,
            ProtectedDataRightsExportArtifactGenerator>();
        builder.Services.TryAddScoped<
            ITenantTerminationExportFragmentGenerator,
            ProtectedTenantTerminationExportFragmentGenerator>();
        builder.Services.TryAddScoped<
            ITenantTerminationExportArtifactGenerator,
            ProtectedTenantTerminationExportArtifactGenerator>();
        builder.Services.TryAddScoped<
            IDataRightsExportArtifactReader,
            ProtectedDataRightsExportArtifactReader>();
        builder.Services.TryAddScoped<
            ITenantTerminationExportArtifactReader,
            ProtectedTenantTerminationExportArtifactReader>();
        builder.Services.TryAddScoped<
            IDataRightsExportArtifactObjectStore,
            DataRightsExportArtifactObjectStore>();
        builder.Services.TryAddScoped<
            ITenantTerminationExportObjectStore,
            DataRightsExportArtifactObjectStore>();
    }

    private static DataRightsLedgerDeltaProvider ParseLedgerDeltaProvider(
        string? value) =>
        Enum.TryParse(value, ignoreCase: true, out DataRightsLedgerDeltaProvider provider)
            ? provider
            : DataRightsLedgerDeltaProvider.Unknown;

    private static DataRightsTenantTerminationReplayProvider
        ParseTenantTerminationReplayProvider(string? value) =>
        Enum.TryParse(
            value,
            ignoreCase: true,
            out DataRightsTenantTerminationReplayProvider provider)
            ? provider
            : DataRightsTenantTerminationReplayProvider.Unknown;
}
