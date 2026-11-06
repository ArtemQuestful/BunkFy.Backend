namespace BunkFy.Modules.Workspaces.Domain;

using Gma.Framework.Domain;
using Gma.Framework.Naming;

public sealed class WorkspaceStaffAccessPlanProperty : IScopedEntity
{
    private WorkspaceStaffAccessPlanProperty() { }

    internal WorkspaceStaffAccessPlanProperty(string scopeId, Guid planId, Guid propertyId)
    {
        this.ScopeId = TenantIds.Normalize(scopeId);
        if (planId == Guid.Empty)
        {
            throw new ArgumentException(
                "A workspace staff access-plan property requires a plan id.",
                nameof(planId));
        }

        if (propertyId == Guid.Empty)
        {
            throw new ArgumentException(
                "A workspace staff access-plan property requires a property id.",
                nameof(propertyId));
        }

        this.PlanId = planId;
        this.PropertyId = propertyId;
    }

    public string ScopeId { get; private set; } = string.Empty;
    public Guid PlanId { get; private set; }
    public Guid PropertyId { get; private set; }
}
