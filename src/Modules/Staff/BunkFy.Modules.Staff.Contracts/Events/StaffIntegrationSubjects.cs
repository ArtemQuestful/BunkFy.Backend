namespace BunkFy.Modules.Staff.Contracts;

using Gma.Framework.Messaging;

public static class StaffIntegrationSubjects
{
    public static string CreateMemberCreated(string prefix = IntegrationEventNaming.DefaultSubjectPrefix) =>
        IntegrationEventNaming.CreateSubject(prefix, StaffModuleMetadata.Name, StaffMemberCreatedIntegrationEvent.EventType, StaffMemberCreatedIntegrationEvent.EventVersion);

    public static string CreateMemberUpdated(string prefix = IntegrationEventNaming.DefaultSubjectPrefix) =>
        IntegrationEventNaming.CreateSubject(prefix, StaffModuleMetadata.Name, StaffMemberUpdatedIntegrationEvent.EventType, StaffMemberUpdatedIntegrationEvent.EventVersion);

    public static string CreateLifecycleChanged(string prefix = IntegrationEventNaming.DefaultSubjectPrefix) =>
        IntegrationEventNaming.CreateSubject(prefix, StaffModuleMetadata.Name, StaffMemberLifecycleChangedIntegrationEvent.EventType, StaffMemberLifecycleChangedIntegrationEvent.EventVersion);

    public static string CreateMemberAnonymised(string prefix = IntegrationEventNaming.DefaultSubjectPrefix) =>
        IntegrationEventNaming.CreateSubject(prefix, StaffModuleMetadata.Name, StaffMemberAnonymisedIntegrationEvent.EventType, StaffMemberAnonymisedIntegrationEvent.EventVersion);

    public static string CreateAuthSubjectChanged(string prefix = IntegrationEventNaming.DefaultSubjectPrefix) =>
        IntegrationEventNaming.CreateSubject(prefix, StaffModuleMetadata.Name, StaffAuthSubjectChangedIntegrationEvent.EventType, StaffAuthSubjectChangedIntegrationEvent.EventVersion);

    public static string CreatePropertyAssignmentChanged(string prefix = IntegrationEventNaming.DefaultSubjectPrefix) =>
        IntegrationEventNaming.CreateSubject(prefix, StaffModuleMetadata.Name, StaffPropertyAssignmentChangedIntegrationEvent.EventType, StaffPropertyAssignmentChangedIntegrationEvent.EventVersion);

    public static string CreateIdentityProvisioningAnchorCreated(
        string prefix = IntegrationEventNaming.DefaultSubjectPrefix) =>
        IntegrationEventNaming.CreateSubject(
            prefix,
            StaffModuleMetadata.Name,
            StaffIdentityProvisioningAnchorCreatedIntegrationEvent.EventType,
            StaffIdentityProvisioningAnchorCreatedIntegrationEvent
                .EventVersion);
}
