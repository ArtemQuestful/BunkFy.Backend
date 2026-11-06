namespace BunkFy.Modules.Properties.Contracts;

using Gma.Framework.Messaging;

public static class PropertiesIntegrationSubjects
{
    public static string PropertyCreated => CreatePropertyCreated();
    public static string PropertyUpdated => CreatePropertyUpdated();
    public static string PropertyTimeZoneChanged =>
        CreatePropertyTimeZoneChanged();
    public static string PropertyRetired => CreatePropertyRetired();
    public static string PropertyProcessingPolicyActivated => CreatePropertyProcessingPolicyActivated();
    public static string PropertyProcessingSuspended => CreatePropertyProcessingSuspended();
    public static string RoomCreated => CreateRoomCreated();
    public static string RoomUpdated => CreateRoomUpdated();
    public static string RoomRetired => CreateRoomRetired();
    public static string BedAdded => CreateBedAdded();
    public static string BedUpdated => CreateBedUpdated();
    public static string BedRetired => CreateBedRetired();
    public static string BedRetirementFinalized => CreateBedRetirementFinalized();
    public static string BedRetirementFinalizationRejected => CreateBedRetirementFinalizationRejected();
    public static string RoomRetirementFinalized => CreateRoomRetirementFinalized();
    public static string RoomRetirementFinalizationRejected => CreateRoomRetirementFinalizationRejected();

    public static string CreatePropertyCreated(string subjectPrefix = IntegrationEventNaming.DefaultSubjectPrefix) =>
        IntegrationEventNaming.CreateSubject(subjectPrefix, PropertiesModuleMetadata.Name, PropertyCreatedIntegrationEvent.EventType, PropertyCreatedIntegrationEvent.EventVersion);

    public static string CreatePropertyUpdated(string subjectPrefix = IntegrationEventNaming.DefaultSubjectPrefix) =>
        IntegrationEventNaming.CreateSubject(subjectPrefix, PropertiesModuleMetadata.Name, PropertyUpdatedIntegrationEvent.EventType, PropertyUpdatedIntegrationEvent.EventVersion);

    public static string CreatePropertyTimeZoneChanged(
        string subjectPrefix = IntegrationEventNaming.DefaultSubjectPrefix) =>
        IntegrationEventNaming.CreateSubject(
            subjectPrefix,
            PropertiesModuleMetadata.Name,
            PropertyTimeZoneChangedIntegrationEvent.EventType,
            PropertyTimeZoneChangedIntegrationEvent.EventVersion);

    public static string CreatePropertyRetired(string subjectPrefix = IntegrationEventNaming.DefaultSubjectPrefix) =>
        IntegrationEventNaming.CreateSubject(subjectPrefix, PropertiesModuleMetadata.Name, PropertyRetiredIntegrationEvent.EventType, PropertyRetiredIntegrationEvent.EventVersion);

    public static string CreatePropertyProcessingPolicyActivated(string subjectPrefix = IntegrationEventNaming.DefaultSubjectPrefix) =>
        IntegrationEventNaming.CreateSubject(subjectPrefix, PropertiesModuleMetadata.Name, PropertyProcessingPolicyActivatedIntegrationEvent.EventType, PropertyProcessingPolicyActivatedIntegrationEvent.EventVersion);

    public static string CreatePropertyProcessingSuspended(string subjectPrefix = IntegrationEventNaming.DefaultSubjectPrefix) =>
        IntegrationEventNaming.CreateSubject(subjectPrefix, PropertiesModuleMetadata.Name, PropertyProcessingSuspendedIntegrationEvent.EventType, PropertyProcessingSuspendedIntegrationEvent.EventVersion);

    public static string CreateRoomCreated(string subjectPrefix = IntegrationEventNaming.DefaultSubjectPrefix) =>
        IntegrationEventNaming.CreateSubject(subjectPrefix, PropertiesModuleMetadata.Name, RoomCreatedIntegrationEvent.EventType, RoomCreatedIntegrationEvent.EventVersion);

    public static string CreateRoomUpdated(string subjectPrefix = IntegrationEventNaming.DefaultSubjectPrefix) =>
        IntegrationEventNaming.CreateSubject(subjectPrefix, PropertiesModuleMetadata.Name, RoomUpdatedIntegrationEvent.EventType, RoomUpdatedIntegrationEvent.EventVersion);

    public static string CreateRoomRetired(string subjectPrefix = IntegrationEventNaming.DefaultSubjectPrefix) =>
        IntegrationEventNaming.CreateSubject(subjectPrefix, PropertiesModuleMetadata.Name, RoomRetiredIntegrationEvent.EventType, RoomRetiredIntegrationEvent.EventVersion);

    public static string CreateBedAdded(string subjectPrefix = IntegrationEventNaming.DefaultSubjectPrefix) =>
        IntegrationEventNaming.CreateSubject(subjectPrefix, PropertiesModuleMetadata.Name, BedAddedIntegrationEvent.EventType, BedAddedIntegrationEvent.EventVersion);

    public static string CreateBedUpdated(string subjectPrefix = IntegrationEventNaming.DefaultSubjectPrefix) =>
        IntegrationEventNaming.CreateSubject(subjectPrefix, PropertiesModuleMetadata.Name, BedUpdatedIntegrationEvent.EventType, BedUpdatedIntegrationEvent.EventVersion);

    public static string CreateBedRetired(string subjectPrefix = IntegrationEventNaming.DefaultSubjectPrefix) =>
        IntegrationEventNaming.CreateSubject(subjectPrefix, PropertiesModuleMetadata.Name, BedRetiredIntegrationEvent.EventType, BedRetiredIntegrationEvent.EventVersion);

    public static string CreateBedRetirementFinalized(string subjectPrefix = IntegrationEventNaming.DefaultSubjectPrefix) =>
        IntegrationEventNaming.CreateSubject(subjectPrefix, PropertiesModuleMetadata.Name, BedRetirementFinalizedIntegrationEvent.EventType, BedRetirementFinalizedIntegrationEvent.EventVersion);

    public static string CreateBedRetirementFinalizationRejected(string subjectPrefix = IntegrationEventNaming.DefaultSubjectPrefix) =>
        IntegrationEventNaming.CreateSubject(subjectPrefix, PropertiesModuleMetadata.Name, BedRetirementFinalizationRejectedIntegrationEvent.EventType, BedRetirementFinalizationRejectedIntegrationEvent.EventVersion);

    public static string CreateRoomRetirementFinalized(string subjectPrefix = IntegrationEventNaming.DefaultSubjectPrefix) =>
        IntegrationEventNaming.CreateSubject(subjectPrefix, PropertiesModuleMetadata.Name, RoomRetirementFinalizedIntegrationEvent.EventType, RoomRetirementFinalizedIntegrationEvent.EventVersion);

    public static string CreateRoomRetirementFinalizationRejected(string subjectPrefix = IntegrationEventNaming.DefaultSubjectPrefix) =>
        IntegrationEventNaming.CreateSubject(subjectPrefix, PropertiesModuleMetadata.Name, RoomRetirementFinalizationRejectedIntegrationEvent.EventType, RoomRetirementFinalizationRejectedIntegrationEvent.EventVersion);
}
