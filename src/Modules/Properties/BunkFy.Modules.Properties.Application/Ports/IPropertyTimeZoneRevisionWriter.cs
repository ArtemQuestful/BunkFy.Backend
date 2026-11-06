namespace BunkFy.Modules.Properties.Application.Ports;

using BunkFy.Modules.Properties.Contracts;

public interface IPropertyTimeZoneRevisionWriter
{
    Task AppendAsync(
        PropertyTimeZoneRevisionWriteModel revision,
        CancellationToken cancellationToken);
}

public sealed record PropertyTimeZoneRevisionWriteModel(
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
