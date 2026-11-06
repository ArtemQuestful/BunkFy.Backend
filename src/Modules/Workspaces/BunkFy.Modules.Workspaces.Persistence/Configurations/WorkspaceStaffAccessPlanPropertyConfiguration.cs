namespace BunkFy.Modules.Workspaces.Persistence.Configurations;

using BunkFy.Modules.Workspaces.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

internal sealed class WorkspaceStaffAccessPlanPropertyConfiguration
    : IEntityTypeConfiguration<WorkspaceStaffAccessPlanProperty>
{
    private const string EmptyGuid =
        "00000000-0000-0000-0000-000000000000";

    public void Configure(EntityTypeBuilder<WorkspaceStaffAccessPlanProperty> builder)
    {
        builder.ToTable("staff_access_plan_properties", table =>
            table.HasCheckConstraint(
                "CK_workspaces_staff_access_plan_properties_coordinates",
                $"\"PlanId\" <> '{EmptyGuid}' AND " +
                $"\"PropertyId\" <> '{EmptyGuid}' AND " +
                "char_length(\"ScopeId\") > 0 AND " +
                "\"ScopeId\" = btrim(\"ScopeId\") AND " +
                "\"ScopeId\" !~ '[[:space:][:cntrl:]]'"));
        builder.HasKey(property => new
        {
            property.ScopeId,
            property.PlanId,
            property.PropertyId
        });
        builder.Property(property => property.ScopeId).HasMaxLength(128).IsRequired();
        builder.HasIndex(property => new
        {
            property.ScopeId,
            property.PropertyId,
            property.PlanId
        });
    }
}
