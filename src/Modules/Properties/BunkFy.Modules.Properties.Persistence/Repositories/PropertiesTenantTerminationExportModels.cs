namespace BunkFy.Modules.Properties.Persistence.Repositories;

using BunkFy.Modules.Properties.Application.Ports;
using BunkFy.Modules.Properties.Domain.Aggregates;
using BunkFy.Modules.Properties.Domain.Entities;

[AttributeUsage(AttributeTargets.Property)]
internal sealed class PropertiesTenantExportFieldAttribute(string fieldId)
    : Attribute
{
    public string FieldId { get; } = fieldId;
}

internal sealed record PropertiesPropertyTenantExport(
    [property: PropertiesTenantExportField("properties.scope-id")]
    string ScopeId,
    [property: PropertiesTenantExportField("properties.property-id")]
    Guid PropertyId,
    [property: PropertiesTenantExportField("properties.property-name")]
    string Name,
    [property: PropertiesTenantExportField("properties.property-code")]
    string Code,
    [property: PropertiesTenantExportField("properties.time-zone-id")]
    string TimeZoneId,
    [property: PropertiesTenantExportField("properties.property-status")]
    PropertyState Status,
    [property: PropertiesTenantExportField("properties.processing-status")]
    PropertyProcessingState ProcessingStatus,
    [property: PropertiesTenantExportField("properties.governance-policy")]
    PropertiesGovernancePolicyTenantExport? GovernancePolicy,
    [property: PropertiesTenantExportField("properties.property-version")]
    long Version,
    [property: PropertiesTenantExportField("properties.projection-ordinal")]
    long ProjectionOrdinal,
    [property: PropertiesTenantExportField("properties.property-created-at")]
    DateTimeOffset CreatedAtUtc,
    [property: PropertiesTenantExportField("properties.property-updated-at")]
    DateTimeOffset? UpdatedAtUtc,
    [property: PropertiesTenantExportField("properties.property-retired-at")]
    DateTimeOffset? RetiredAtUtc);

internal sealed record PropertiesGovernancePolicyTenantExport(
    string OperatingCountryCode,
    string PolicyId,
    int PolicyVersion,
    string DataRegionId,
    string TransferProfileId,
    string RetentionPolicyId,
    int RetentionPolicyVersion,
    string ContentSha256,
    DateTimeOffset PolicyEffectiveAtUtc,
    DateTimeOffset PolicyExpiresAtUtc,
    DateTimeOffset ActivatedAtUtc);

internal sealed record PropertiesPropertyMutationOperationTenantExport(
    [property: PropertiesTenantExportField("properties.scope-id")]
    string ScopeId,
    [property: PropertiesTenantExportField("properties.property-id")]
    Guid PropertyId,
    [property: PropertiesTenantExportField("properties.record-id")]
    Guid OperationId,
    [property: PropertiesTenantExportField(
        "properties.property-mutation-operation")]
    PropertiesPropertyMutationOperationStateTenantExport State);

internal sealed record PropertiesPropertyMutationOperationStateTenantExport(
    PropertyMutationResourceKind ResourceKind,
    Guid ResourceId,
    PropertyMutationKind Kind,
    long ExpectedVersion,
    string RequestFingerprint,
    BunkFy.Modules.Properties.Contracts.PropertyStatus? ResultStatus,
    BunkFy.Modules.Properties.Contracts.PropertyProcessingStatus?
        ResultProcessingStatus,
    Guid? ResultRoomId,
    BunkFy.Modules.Properties.Contracts.RoomStatus? ResultRoomStatus,
    Guid? ResultBedId,
    BunkFy.Modules.Properties.Contracts.BedStatus? ResultBedStatus,
    int? ResultAffectedBedCount,
    long ResultVersion,
    long ResultResourceVersion,
    DateTimeOffset CompletedAtUtc);

internal sealed record PropertiesPropertyTimeZoneOperationTenantExport(
    [property: PropertiesTenantExportField("properties.scope-id")]
    string ScopeId,
    [property: PropertiesTenantExportField("properties.property-id")]
    Guid PropertyId,
    [property: PropertiesTenantExportField("properties.record-id")]
    Guid OperationId,
    [property: PropertiesTenantExportField(
        "properties.property-time-zone-operation")]
    PropertiesPropertyTimeZoneOperationStateTenantExport State,
    [property: PropertiesTenantExportField(
        "properties.staff-actor-reference")]
    string ActorId,
    [property: PropertiesTenantExportField("properties.occurred-at")]
    DateTimeOffset OccurredAtUtc);

internal sealed record PropertiesPropertyTimeZoneOperationStateTenantExport(
    Guid RevisionId,
    BunkFy.Modules.Properties.Contracts.PropertyTimeZoneChangeKind ChangeKind,
    string RequestedTimeZoneId,
    string? PreviousTimeZoneId,
    string TimeZoneId,
    string CatalogVersion,
    long ExpectedVersion,
    long ResultVersion);

internal sealed record PropertiesGovernanceAcknowledgementTenantExport(
    [property: PropertiesTenantExportField("properties.scope-id")]
    string ScopeId,
    [property: PropertiesTenantExportField("properties.property-id")]
    Guid PropertyId,
    [property: PropertiesTenantExportField(
        "properties.governance-acknowledgement-id")]
    string AcknowledgementId,
    [property: PropertiesTenantExportField(
        "properties.governance-acknowledgement-version")]
    int AcknowledgementVersion);

internal sealed record PropertiesRoomTenantExport(
    [property: PropertiesTenantExportField("properties.scope-id")]
    string ScopeId,
    [property: PropertiesTenantExportField("properties.property-id")]
    Guid PropertyId,
    [property: PropertiesTenantExportField("properties.room-id")]
    Guid RoomId,
    [property: PropertiesTenantExportField("properties.room-name")]
    string Name,
    [property: PropertiesTenantExportField("properties.building-label")]
    string? BuildingLabel,
    [property: PropertiesTenantExportField("properties.floor-label")]
    string? FloorLabel,
    [property: PropertiesTenantExportField("properties.room-status")]
    RoomState Status,
    [property: PropertiesTenantExportField("properties.room-version")]
    long Version,
    [property: PropertiesTenantExportField("properties.room-created-at")]
    DateTimeOffset CreatedAtUtc,
    [property: PropertiesTenantExportField("properties.room-updated-at")]
    DateTimeOffset? UpdatedAtUtc,
    [property: PropertiesTenantExportField("properties.room-retired-at")]
    DateTimeOffset? RetiredAtUtc);

internal sealed record PropertiesBedTenantExport(
    [property: PropertiesTenantExportField("properties.scope-id")]
    string ScopeId,
    [property: PropertiesTenantExportField("properties.property-id")]
    Guid PropertyId,
    [property: PropertiesTenantExportField("properties.room-id")]
    Guid RoomId,
    [property: PropertiesTenantExportField("properties.bed-id")]
    Guid BedId,
    [property: PropertiesTenantExportField("properties.bed-label")]
    string Label,
    [property: PropertiesTenantExportField("properties.bed-status")]
    BedState Status,
    [property: PropertiesTenantExportField("properties.bed-version")]
    long Version,
    [property: PropertiesTenantExportField("properties.bed-created-at")]
    DateTimeOffset CreatedAtUtc,
    [property: PropertiesTenantExportField("properties.bed-updated-at")]
    DateTimeOffset? UpdatedAtUtc,
    [property: PropertiesTenantExportField("properties.bed-retired-at")]
    DateTimeOffset? RetiredAtUtc);

internal sealed record PropertiesGovernanceRevisionTenantExport(
    [property: PropertiesTenantExportField("properties.scope-id")]
    string ScopeId,
    [property: PropertiesTenantExportField(
        "properties.governance-revision-id")]
    Guid RevisionId,
    [property: PropertiesTenantExportField("properties.property-id")]
    Guid PropertyId,
    [property: PropertiesTenantExportField("properties.property-version")]
    long PropertyVersion,
    [property: PropertiesTenantExportField("properties.governance-action")]
    PropertyGovernanceRevisionAction Action,
    [property: PropertiesTenantExportField(
        "properties.governance-decision-code")]
    string DecisionReasonCode,
    [property: PropertiesTenantExportField("properties.governance-previous")]
    PropertiesGovernanceCoordinatesTenantExport? Previous,
    [property: PropertiesTenantExportField("properties.governance-current")]
    PropertiesGovernanceCoordinatesTenantExport? Current,
    [property: PropertiesTenantExportField(
        "properties.staff-actor-reference")]
    string ActorId,
    [property: PropertiesTenantExportField("properties.occurred-at")]
    DateTimeOffset OccurredAtUtc);

internal sealed record PropertiesGovernanceCoordinatesTenantExport(
    string OperatingCountryCode,
    string PolicyId,
    int PolicyVersion,
    string DataRegionId,
    string TransferProfileId,
    string RetentionPolicyId,
    int RetentionPolicyVersion,
    string ContentSha256,
    string AcknowledgementSetSha256);
