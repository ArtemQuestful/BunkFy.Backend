namespace BunkFy.Modules.Properties.Application.Ports;

using BunkFy.Modules.Properties.Contracts;

public interface IPropertyTimeZoneRevisionReader
{
    Task<PropertyTimeZoneRevisionReadModel?> GetAsync(
        Guid propertyId,
        Guid operationId,
        CancellationToken cancellationToken);
}

public sealed record PropertyTimeZoneRevisionReadModel(
    Guid RevisionId,
    string ScopeId,
    Guid PropertyId,
    Guid OperationId,
    PropertyTimeZoneChangeKind ChangeKind,
    string RequestedTimeZoneId,
    string? PreviousTimeZoneId,
    string TimeZoneId,
    string CatalogVersion,
    long ExpectedVersion,
    long ResultVersion,
    string ActorId,
    DateTimeOffset OccurredAtUtc);
