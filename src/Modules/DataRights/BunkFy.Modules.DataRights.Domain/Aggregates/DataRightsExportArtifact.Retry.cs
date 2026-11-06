namespace BunkFy.Modules.DataRights.Domain.Aggregates;

using BunkFy.Modules.DataRights.Domain.Errors;
using BunkFy.Modules.DataRights.Domain.Models;
using Gma.Framework.Results;

public sealed partial class DataRightsExportArtifact
{
    public bool IsRetryReplay(long baseVersion) =>
        baseVersion > 0 && this.LastRetryBaseVersion == baseVersion;

    public Result RequestRetry(
        long expectedVersion,
        DateTimeOffset requestedAtUtc)
    {
        if (this.IsRetryReplay(expectedVersion))
        {
            return Result.Success();
        }

        if (expectedVersion <= 0 || requestedAtUtc == default)
        {
            return Result.Failure(
                DataRightsDomainErrors.ExportArtifactGenerationInvalid);
        }

        if (this.Version != expectedVersion)
        {
            return Result.Failure(DataRightsDomainErrors.VersionConflict);
        }

        if (this.State != DataRightsExportArtifactState.Failed)
        {
            return Result.Failure(
                DataRightsDomainErrors.ExportArtifactTransitionInvalid);
        }

        if (requestedAtUtc < this.RequestedAtUtc ||
            requestedAtUtc >= this.ExpiresAtUtc)
        {
            return Result.Failure(DataRightsDomainErrors.TimestampInvalid);
        }

        this.State = DataRightsExportArtifactState.Requested;
        this.GenerationActor = null;
        this.GenerationRunId = null;
        this.GenerationAttempt = null;
        this.GenerationStartedAtUtc = null;
        this.FailureCode = null;
        this.LastRetryBaseVersion = expectedVersion;
        this.Version++;
        return Result.Success();
    }
}
