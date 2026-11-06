namespace BunkFy.Modules.DataRights.Contracts;

using BunkFy.Modules.Properties.Contracts;
using Gma.Framework.Messaging;
using Gma.Framework.ModuleComposition;
using Gma.Framework.Modules;
using Gma.Framework.Permissions;
using Gma.Framework.Tasks;

public static class DataRightsModuleMetadata
{
    public const string Name = "data-rights";
    public const string Schema = "data-rights";
    public const string PropertyCreatedHandlerName = "property-created";
    public const string PropertyUpdatedHandlerName = "property-updated";
    public const string PropertyRetiredHandlerName = "property-retired";
    public const string PropertyProcessingPolicyActivatedHandlerName =
        "property-processing-policy-activated";
    public const string PropertyProcessingSuspendedHandlerName =
        "property-processing-suspended";
    public const string AnonymisationExecutionPreparedHandlerName =
        "anonymisation-execution-prepared";
    public const string AnonymisationExecutionPreparedV2HandlerName =
        "anonymisation-execution-prepared-v2";
    public const string AnonymisationWorkItemTerminalHandlerName =
        "anonymisation-work-item-terminal";
    public const string AnonymisationWorkItemTerminalV2HandlerName =
        "anonymisation-work-item-terminal-v2";
    public const string ExportArtifactRequestedHandlerName =
        "export-artifact-requested";
    public const string TenantTerminationCoordinationHandlerName =
        "tenant-termination-coordination-requested";
    public const string GuestCorrectionAppliedHandlerName =
        "guest-correction-applied";
    public const string ReservationCorrectionAppliedHandlerName =
        "reservation-correction-applied";
    public const string StaffCorrectionAppliedHandlerName =
        "staff-correction-applied";
    public const string WorkspacesCorrectionAppliedHandlerName =
        "workspaces-correction-applied";
    public const string GuestsProducerModuleName = "guests";
    public const string ReservationsProducerModuleName = "reservations";
    public const string StaffProducerModuleName = "staff";
    public const string WorkspacesProducerModuleName = "workspaces";
    public const string PropertiesProjectionName = "properties";
    public const int PropertiesProjectionVersion = 1;
    public const string ProjectionWorkerGroup = "projection-workers";
    public const string AnonymisationWorkerGroup = "data-rights-workers";
    public const string ExportWorkerGroup = "data-rights-workers";
    public const string TenantTerminationWorkerGroup =
        "tenant-termination-workers";
    public const string DeadlineAlertWorkerGroup = "data-rights-workers";

    public static ModuleDescriptor Descriptor { get; } = ModuleDescriptor
        .Create(Name)
        .WithSchema(Schema)
        .WithPermissions([
            Permission(DataRightsAdminPermissionCodes.Read, "Read data-rights cases."),
            Permission(DataRightsAdminPermissionCodes.Create, "Create data-rights cases."),
            Permission(DataRightsAdminPermissionCodes.Discover, "Run sensitive record discovery."),
            Permission(DataRightsAdminPermissionCodes.Review, "Review discovered records and case scope."),
            Permission(DataRightsAdminPermissionCodes.Decide, "Approve or deny data-rights operations."),
            Permission(DataRightsAdminPermissionCodes.Execute, "Execute approved data-rights work."),
            Permission(DataRightsAdminPermissionCodes.Export, "Generate protected data exports."),
            Permission(DataRightsAdminPermissionCodes.DownloadExport, "Download protected data exports."),
            Permission(DataRightsAdminPermissionCodes.Restrict, "Apply or release processing restrictions."),
            Permission(DataRightsAdminPermissionCodes.Erase, "Execute erasure or anonymisation."),
            Permission(DataRightsAdminPermissionCodes.TerminateTenant, "Execute tenant termination."),
            Permission(DataRightsAdminPermissionCodes.TenantTerminationRead, "Read tenant-termination operator status."),
            Permission(DataRightsAdminPermissionCodes.TenantTerminationRequest, "Request tenant termination."),
            Permission(DataRightsAdminPermissionCodes.TenantTerminationApprove, "Approve or deny tenant termination."),
            Permission(DataRightsAdminPermissionCodes.TenantTerminationExecute, "Start approved tenant termination."),
            Permission(DataRightsAdminPermissionCodes.TenantTerminationExportDownload, "Download a reviewed tenant-termination export."),
            Permission(DataRightsAdminPermissionCodes.TenantTerminationExportConfirm, "Confirm the reviewed tenant export and authorize destruction."),
            Permission(DataRightsAdminPermissionCodes.TenantTerminationRetry, "Retry blocked or failed tenant termination."),
            Permission(DataRightsAdminPermissionCodes.TenantTerminationCancel, "Cancel reversible tenant termination."),
            Permission(DataRightsAdminPermissionCodes.TenantTerminationRecover, "Recover tenant-termination coordination."),
            Permission(DataRightsAdminPermissionCodes.Manage, "Manage data-rights case lifecycle.")
        ])
        .WithSubscription<PropertyCreatedIntegrationEvent>(
            PropertiesModuleMetadata.Name,
            PropertyCreatedHandlerName)
        .WithSubscription<PropertyUpdatedIntegrationEvent>(
            PropertiesModuleMetadata.Name,
            PropertyUpdatedHandlerName)
        .WithSubscription<PropertyRetiredIntegrationEvent>(
            PropertiesModuleMetadata.Name,
            PropertyRetiredHandlerName)
        .WithSubscription<PropertyProcessingPolicyActivatedIntegrationEvent>(
            PropertiesModuleMetadata.Name,
            PropertyProcessingPolicyActivatedHandlerName)
        .WithSubscription<PropertyProcessingSuspendedIntegrationEvent>(
            PropertiesModuleMetadata.Name,
            PropertyProcessingSuspendedHandlerName)
        .WithSubscription<DataRightsAnonymisationExecutionPreparedIntegrationEvent>(
            Name,
            AnonymisationExecutionPreparedHandlerName)
        .WithSubscription<DataRightsAnonymisationExecutionPreparedIntegrationEventV2>(
            Name,
            AnonymisationExecutionPreparedV2HandlerName)
        .WithSubscription<DataRightsAnonymisationWorkItemTerminalIntegrationEvent>(
            Name,
            AnonymisationWorkItemTerminalHandlerName)
        .WithSubscription<DataRightsAnonymisationWorkItemTerminalIntegrationEventV2>(
            Name,
            AnonymisationWorkItemTerminalV2HandlerName)
        .WithSubscription<DataRightsExportArtifactRequestedIntegrationEvent>(
            Name,
            ExportArtifactRequestedHandlerName)
        .WithSubscription<
            TenantTerminationCoordinationRequestedIntegrationEvent>(
                Name,
                TenantTerminationCoordinationHandlerName)
        .WithSubscription<DataRightsCorrectionAppliedIntegrationEvent>(
            GuestsProducerModuleName,
            GuestCorrectionAppliedHandlerName)
        .WithSubscription<DataRightsCorrectionAppliedIntegrationEvent>(
            ReservationsProducerModuleName,
            ReservationCorrectionAppliedHandlerName)
        .WithSubscription<DataRightsTenantCorrectionAppliedIntegrationEvent>(
            StaffProducerModuleName,
            StaffCorrectionAppliedHandlerName)
        .WithSubscription<DataRightsTenantCorrectionAppliedIntegrationEvent>(
            WorkspacesProducerModuleName,
            WorkspacesCorrectionAppliedHandlerName)
        .WithTask<RebuildDataRightsPropertiesPayload>()
        .WithTask<ExecuteDataRightsAnonymisationPayload>()
        .WithTask<ExecuteDataRightsAnonymisationPayloadV2>()
        .WithTask<GenerateDataRightsExportPayload>()
        .WithTask<DeleteExpiredDataRightsExportArtifactPayload>()
        .WithTask<ExecuteTenantTerminationOwnerWorkPayload>()
        .WithTask<ExecuteTenantTerminationExportOwnerWorkPayload>()
        .WithTask<ExecuteGlobalTenantTerminationOwnerWorkPayload>()
        .WithTask<GenerateTenantTerminationExportArtifactPayload>()
        .WithTask<DeleteExpiredTenantTerminationExportArtifactPayload>()
        .WithTask<DeleteExpiredTenantTerminationExportFragmentPayload>()
        .WithTask<VerifyTenantTerminationPayload>()
        .WithTask<DispatchDataRightsResponseDeadlineAlertsPayload>()
        .WithProfile(DataRightsProfiles.Default)
        .Build();

    private static ModulePermissionDescriptor Permission(string code, string description) => new(
        code,
        description,
        PermissionScopeRequirement.Scoped,
        PermissionScopeGrantPolicy.Descendants);
}
