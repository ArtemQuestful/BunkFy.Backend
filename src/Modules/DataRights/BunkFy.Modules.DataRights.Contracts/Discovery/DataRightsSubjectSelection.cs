namespace BunkFy.Modules.DataRights.Contracts;

public sealed record DataRightsSubjectSelectionRequest(
    string TenantId,
    DataRightsCaseType CaseType,
    Guid? PropertyId,
    DataRightsSubjectCoordinate Coordinate);

public enum DataRightsSubjectSelectionValidationStatus
{
    Unknown = 0,
    Valid = 1,
    NotFound = 2,
    Stale = 3,
    ScopeUnavailable = 4,
    RetryRequired = 5
}

public sealed record DataRightsSubjectSelectionValidation(
    DataRightsSubjectSelectionValidationStatus Status,
    DataRightsSubjectCoordinate? Coordinate)
{
    public static DataRightsSubjectSelectionValidation Valid(
        DataRightsSubjectCoordinate coordinate) =>
        new(DataRightsSubjectSelectionValidationStatus.Valid, coordinate);

    public static DataRightsSubjectSelectionValidation NotFound() =>
        new(DataRightsSubjectSelectionValidationStatus.NotFound, null);

    public static DataRightsSubjectSelectionValidation Stale() =>
        new(DataRightsSubjectSelectionValidationStatus.Stale, null);

    public static DataRightsSubjectSelectionValidation ScopeUnavailable() =>
        new(DataRightsSubjectSelectionValidationStatus.ScopeUnavailable, null);

    public static DataRightsSubjectSelectionValidation RetryRequired() =>
        new(DataRightsSubjectSelectionValidationStatus.RetryRequired, null);
}
