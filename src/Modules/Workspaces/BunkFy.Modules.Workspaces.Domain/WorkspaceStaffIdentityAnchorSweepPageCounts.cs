namespace BunkFy.Modules.Workspaces.Domain;

public sealed record WorkspaceStaffIdentityAnchorSweepPageCounts(
    long ScannedCount,
    long NoAnchorCount,
    long RemovedCount,
    long ObservedCount,
    long AlreadyObservedCount,
    long DeferredCount,
    long ConflictCount,
    long PassOneCommittedCount,
    long ResolutionRecordConfirmedCount)
{
    public static readonly WorkspaceStaffIdentityAnchorSweepPageCounts Empty =
        new(0, 0, 0, 0, 0, 0, 0, 0, 0);

    public long SettledCount =>
        this.ObservedCount + this.AlreadyObservedCount;

    public long BacklogCount => this.DeferredCount + this.ConflictCount;

    internal bool IsValid()
    {
        if (this.ScannedCount < 0 ||
            this.NoAnchorCount < 0 ||
            this.RemovedCount < 0 ||
            this.ObservedCount < 0 ||
            this.AlreadyObservedCount < 0 ||
            this.DeferredCount < 0 ||
            this.ConflictCount < 0 ||
            this.PassOneCommittedCount < 0 ||
            this.ResolutionRecordConfirmedCount < 0)
        {
            return false;
        }

        try
        {
            long classified = checked(
                this.NoAnchorCount +
                this.RemovedCount +
                this.ObservedCount +
                this.AlreadyObservedCount +
                this.DeferredCount +
                this.ConflictCount);
            return classified == this.ScannedCount &&
                this.PassOneCommittedCount <= this.ScannedCount &&
                this.ResolutionRecordConfirmedCount <= this.ScannedCount;
        }
        catch (OverflowException)
        {
            return false;
        }
    }
}
