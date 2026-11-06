namespace BunkFy.Modules.DataRights.Domain.Aggregates;

using BunkFy.Modules.DataRights.Domain.ValueObjects;
using Gma.Framework.Results;

public sealed partial class DataRightsCase
{
    public bool MatchesCreation(
        DataRightsCaseRequest request,
        string actorId)
    {
        ArgumentNullException.ThrowIfNull(request);
        Result<string> actor = NormalizeActor(actorId);
        return actor.IsSuccess &&
            this.PropertyId == request.PropertyId &&
            this.Kind == request.Kind &&
            this.RequestedOperations == request.RequestedOperations &&
            this.RestrictionAction == request.RestrictionAction &&
            this.RequesterRelationship == request.RequesterRelationship &&
            string.Equals(
                this.CreatedBy,
                actor.Value,
                StringComparison.Ordinal);
    }
}
