namespace BunkFy.Modules.DataRights.Tests.Contracts;

using BunkFy.Modules.DataRights.Contracts;
using BunkFy.Modules.DataRights.Domain.Models;
using BunkFy.Modules.Properties.Contracts;
using Gma.Framework.Messaging;
using Gma.Framework.ModuleComposition;
using Gma.Framework.Permissions;
using Gma.Framework.Tasks;
using Xunit;

[Trait("Category", "Unit")]
public sealed class DataRightsModuleMetadataTests
{
    [Fact]
    public void Descriptor_exposes_scoped_permissions_and_one_tenant_profile()
    {
        IReadOnlyCollection<ModulePermissionDescriptor> permissions =
            DataRightsModuleMetadata.Descriptor.GetPermissions();

        Assert.Equal(
            new[]
            {
                DataRightsAdminPermissionCodes.Read,
                DataRightsAdminPermissionCodes.Create,
                DataRightsAdminPermissionCodes.Discover,
                DataRightsAdminPermissionCodes.Review,
                DataRightsAdminPermissionCodes.Decide,
                DataRightsAdminPermissionCodes.Execute,
                DataRightsAdminPermissionCodes.Export,
                DataRightsAdminPermissionCodes.DownloadExport,
                DataRightsAdminPermissionCodes.Restrict,
                DataRightsAdminPermissionCodes.Erase,
                DataRightsAdminPermissionCodes.TerminateTenant,
                DataRightsAdminPermissionCodes.TenantTerminationRead,
                DataRightsAdminPermissionCodes.TenantTerminationRequest,
                DataRightsAdminPermissionCodes.TenantTerminationApprove,
                DataRightsAdminPermissionCodes.TenantTerminationExecute,
                DataRightsAdminPermissionCodes.TenantTerminationExportDownload,
                DataRightsAdminPermissionCodes.TenantTerminationExportConfirm,
                DataRightsAdminPermissionCodes.TenantTerminationRetry,
                DataRightsAdminPermissionCodes.TenantTerminationCancel,
                DataRightsAdminPermissionCodes.TenantTerminationRecover,
                DataRightsAdminPermissionCodes.Manage
            }.Order(StringComparer.Ordinal),
            permissions.Select(permission => permission.Code)
                .Order(StringComparer.Ordinal));
        Assert.All(permissions, permission =>
        {
            Assert.Equal(PermissionScopeRequirement.Scoped, permission.ScopeRequirement);
            Assert.Equal(PermissionScopeGrantPolicy.Descendants, permission.ScopeGrantPolicy);
        });
        ModuleProfileDescriptor profile = Assert.Single(
            DataRightsModuleMetadata.Descriptor.GetCompositionProfiles());
        Assert.Equal(DataRightsProfiles.DefaultName, profile.ProfileName);
        Assert.Equal(15, DataRightsModuleMetadata.Descriptor.GetSubscriptions().Count);
        Assert.Contains(
            DataRightsModuleMetadata.Descriptor.GetSubscriptions(),
            subscription =>
                subscription.EventType ==
                    PropertyProcessingPolicyActivatedIntegrationEvent.EventType &&
                subscription.ProducerModule == PropertiesModuleMetadata.Name);
        Assert.Contains(
            DataRightsModuleMetadata.Descriptor.GetSubscriptions(),
            subscription =>
                subscription.EventType ==
                    PropertyProcessingSuspendedIntegrationEvent.EventType &&
                subscription.ProducerModule == PropertiesModuleMetadata.Name);
        Assert.Contains(
            DataRightsModuleMetadata.Descriptor.GetSubscriptions(),
            subscription =>
                subscription.EventType ==
                    DataRightsAnonymisationExecutionPreparedIntegrationEvent.EventType &&
                subscription.ProducerModule == DataRightsModuleMetadata.Name);
        Assert.Contains(
            DataRightsModuleMetadata.Descriptor.GetSubscriptions(),
            subscription =>
                subscription.EventType ==
                    DataRightsAnonymisationExecutionPreparedIntegrationEventV2.EventType &&
                subscription.ProducerModule == DataRightsModuleMetadata.Name);
        Assert.Contains(
            DataRightsModuleMetadata.Descriptor.GetSubscriptions(),
            subscription =>
                subscription.EventType ==
                    DataRightsAnonymisationWorkItemTerminalIntegrationEvent.EventType &&
                subscription.ProducerModule == DataRightsModuleMetadata.Name);
        Assert.Contains(
            DataRightsModuleMetadata.Descriptor.GetSubscriptions(),
            subscription =>
                subscription.EventType ==
                    DataRightsAnonymisationWorkItemTerminalIntegrationEventV2.EventType &&
                subscription.ProducerModule == DataRightsModuleMetadata.Name);
        Assert.Contains(
            DataRightsModuleMetadata.Descriptor.GetSubscriptions(),
            subscription =>
                subscription.EventType ==
                    DataRightsExportArtifactRequestedIntegrationEvent.EventType &&
                subscription.ProducerModule == DataRightsModuleMetadata.Name);
        Assert.Contains(
            DataRightsModuleMetadata.Descriptor.GetSubscriptions(),
            subscription =>
                subscription.EventType ==
                    TenantTerminationCoordinationRequestedIntegrationEvent.EventType &&
                subscription.ProducerModule == DataRightsModuleMetadata.Name &&
                subscription.HandlerName ==
                    DataRightsModuleMetadata
                        .TenantTerminationCoordinationHandlerName);
        Assert.Contains(
            DataRightsModuleMetadata.Descriptor.GetSubscriptions(),
            subscription =>
                subscription.EventType ==
                    DataRightsCorrectionAppliedIntegrationEvent.EventType &&
                subscription.ProducerModule ==
                    DataRightsModuleMetadata.GuestsProducerModuleName &&
                subscription.HandlerName ==
                    DataRightsModuleMetadata.GuestCorrectionAppliedHandlerName);
        Assert.Contains(
            DataRightsModuleMetadata.Descriptor.GetSubscriptions(),
            subscription =>
                subscription.EventType ==
                    DataRightsCorrectionAppliedIntegrationEvent.EventType &&
                subscription.ProducerModule ==
                    DataRightsModuleMetadata.ReservationsProducerModuleName &&
                subscription.HandlerName ==
                    DataRightsModuleMetadata.ReservationCorrectionAppliedHandlerName);
        Assert.Contains(
            DataRightsModuleMetadata.Descriptor.GetSubscriptions(),
            subscription =>
                subscription.EventType ==
                    DataRightsTenantCorrectionAppliedIntegrationEvent.EventType &&
                subscription.ProducerModule ==
                    DataRightsModuleMetadata.StaffProducerModuleName &&
                subscription.HandlerName ==
                    DataRightsModuleMetadata.StaffCorrectionAppliedHandlerName);
        Assert.Contains(
            DataRightsModuleMetadata.Descriptor.GetSubscriptions(),
            subscription =>
                subscription.EventType ==
                    DataRightsTenantCorrectionAppliedIntegrationEvent.EventType &&
                subscription.ProducerModule ==
                    DataRightsModuleMetadata.WorkspacesProducerModuleName &&
                subscription.HandlerName ==
                    DataRightsModuleMetadata
                        .WorkspacesCorrectionAppliedHandlerName);
        Assert.Equal(13, DataRightsModuleMetadata.Descriptor.GetTasks().Count);
        Assert.Contains(
            DataRightsModuleMetadata.Descriptor.GetTasks(),
            task => task.Name == ExecuteDataRightsAnonymisationPayload.TaskName);
        Assert.Contains(
            DataRightsModuleMetadata.Descriptor.GetTasks(),
            task =>
                task.Name ==
                ExecuteDataRightsAnonymisationPayloadV2.TaskName);
        Assert.Contains(
            DataRightsModuleMetadata.Descriptor.GetTasks(),
            task => task.Name == GenerateDataRightsExportPayload.TaskName);
        Assert.Contains(
            DataRightsModuleMetadata.Descriptor.GetTasks(),
            task =>
                task.Name ==
                DeleteExpiredDataRightsExportArtifactPayload.TaskName);
        Assert.Contains(
            DataRightsModuleMetadata.Descriptor.GetTasks(),
            task =>
                task.Name ==
                ExecuteTenantTerminationOwnerWorkPayload.TaskName);
        Assert.Contains(
            DataRightsModuleMetadata.Descriptor.GetTasks(),
            task =>
                task.Name ==
                ExecuteTenantTerminationExportOwnerWorkPayload.TaskName);
        Assert.Contains(
            DataRightsModuleMetadata.Descriptor.GetTasks(),
            task =>
                task.Name ==
                ExecuteGlobalTenantTerminationOwnerWorkPayload.TaskName);
        Assert.Contains(
            DataRightsModuleMetadata.Descriptor.GetTasks(),
            task =>
                task.Name ==
                GenerateTenantTerminationExportArtifactPayload.TaskName);
        Assert.Contains(
            DataRightsModuleMetadata.Descriptor.GetTasks(),
            task =>
                task.Name ==
                DeleteExpiredTenantTerminationExportArtifactPayload.TaskName);
        Assert.Contains(
            DataRightsModuleMetadata.Descriptor.GetTasks(),
            task =>
                task.Name ==
                DeleteExpiredTenantTerminationExportFragmentPayload.TaskName);
        Assert.Contains(
            DataRightsModuleMetadata.Descriptor.GetTasks(),
            task => task.Name == VerifyTenantTerminationPayload.TaskName);
        Assert.Contains(
            DataRightsModuleMetadata.Descriptor.GetTasks(),
            task => task.Name ==
                DispatchDataRightsResponseDeadlineAlertsPayload.TaskName);
    }

    [Fact]
    public void Contract_and_domain_lifecycle_values_remain_cast_compatible()
    {
        Assert.Equal(
            Enum.GetValues<DataRightsOperation>().Select(value => (int)value),
            Enum.GetValues<DataRightsCaseOperation>().Select(value => (int)value));
        Assert.Equal(
            Enum.GetValues<DataRightsCaseStatus>().Select(value => (int)value),
            Enum.GetValues<DataRightsCaseState>().Select(value => (int)value));
        Assert.Equal(
            Enum.GetValues<DataRightsDecisionOutcome>().Select(value => (int)value),
            Enum.GetValues<DataRightsCaseDecision>().Select(value => (int)value));
        Assert.Equal(
            Enum.GetValues<DataRightsDecisionReason>().Select(value => (int)value),
            Enum.GetValues<DataRightsCaseDecisionReason>().Select(value => (int)value));
        Assert.Equal(
            Enum.GetValues<DataRightsExecutionWorkItemStatus>().Select(value => (int)value),
            Enum.GetValues<DataRightsExecutionWorkItemState>().Select(value => (int)value));
        Assert.Equal(
            Enum.GetValues<DataRightsExportArtifactStatus>().Select(value => (int)value),
            Enum.GetValues<DataRightsExportArtifactState>().Select(value => (int)value));
        Assert.Equal(
            Enum.GetValues<TenantTerminationExportArtifactStatus>().Select(value => (int)value),
            Enum.GetValues<TenantTerminationExportArtifactState>().Select(value => (int)value));
    }

    [Fact]
    public void Tenant_export_handoff_contract_exposes_only_bounded_receipt_data()
    {
        string[] expectedProperties =
        [
            "ArtifactId",
            "ArtifactVersion",
            "AvailableAtUtc",
            "ConfirmationRevision",
            "Confirmed",
            "ConfirmedArtifactVersion",
            "ConfirmedAtUtc",
            "ConfirmedBy",
            "ExpiresAtUtc",
            "ExportOperationRevision",
            "FragmentCount",
            "FragmentSetSha256",
            "FrozenRevisionSha256",
            "RecordCount",
            "Status"
        ];
        Assert.Equal(
            expectedProperties,
            typeof(TenantTerminationExportHandoffDto)
                .GetProperties()
                .Select(property => property.Name)
                .Order(StringComparer.Ordinal));
    }
}
