namespace BunkFy.Modules.Properties.Persistence;

using BunkFy.Modules.Properties.Application.Ports;
using BunkFy.Modules.Properties.Contracts;
using Gma.Framework.Domain;

public sealed class PropertyTimeZoneOperation : IScopedEntity
{
    private PropertyTimeZoneOperation() { }

    internal PropertyTimeZoneOperation(
        PropertyTimeZoneRevisionWriteModel revision)
    {
        this.RevisionId = revision.RevisionId;
        this.ScopeId = revision.ScopeId;
        this.PropertyId = revision.PropertyId;
        this.OperationId = revision.OperationId;
        this.ChangeKind = revision.ChangeKind;
        this.RequestedTimeZoneId = revision.RequestedTimeZoneId;
        this.PreviousTimeZoneId = revision.PreviousTimeZoneId;
        this.TimeZoneId = revision.TimeZoneId;
        this.CatalogVersion = revision.CatalogVersion;
        this.ExpectedVersion = revision.ExpectedVersion;
        this.ResultVersion = revision.ResultVersion;
        this.ActorId = revision.ActorId;
        this.OccurredAtUtc = revision.OccurredAtUtc;
    }

    public Guid RevisionId { get; private set; }
    public string ScopeId { get; private set; } = string.Empty;
    public Guid PropertyId { get; private set; }
    public Guid OperationId { get; private set; }
    public PropertyTimeZoneChangeKind ChangeKind { get; private set; }
    public string RequestedTimeZoneId { get; private set; } = string.Empty;
    public string? PreviousTimeZoneId { get; private set; }
    public string TimeZoneId { get; private set; } = string.Empty;
    public string CatalogVersion { get; private set; } = string.Empty;
    public long ExpectedVersion { get; private set; }
    public long ResultVersion { get; private set; }
    public string ActorId { get; private set; } = string.Empty;
    public DateTimeOffset OccurredAtUtc { get; private set; }

    internal PropertyTimeZoneRevisionReadModel ToReadModel() => new(
        this.RevisionId,
        this.ScopeId,
        this.PropertyId,
        this.OperationId,
        this.ChangeKind,
        this.RequestedTimeZoneId,
        this.PreviousTimeZoneId,
        this.TimeZoneId,
        this.CatalogVersion,
        this.ExpectedVersion,
        this.ResultVersion,
        this.ActorId,
        this.OccurredAtUtc);
}
