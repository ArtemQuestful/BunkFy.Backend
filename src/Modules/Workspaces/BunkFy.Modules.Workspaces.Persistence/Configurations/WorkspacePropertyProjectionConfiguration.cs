namespace BunkFy.Modules.Workspaces.Persistence.Configurations;

using BunkFy.Modules.Properties.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

internal sealed class WorkspacePropertyProjectionConfiguration
    : IEntityTypeConfiguration<WorkspacePropertyProjection>
{
    private const string EmptyGuid =
        "00000000-0000-0000-0000-000000000000";

    public void Configure(EntityTypeBuilder<WorkspacePropertyProjection> builder)
    {
        builder.ToTable("property_projection", table =>
        {
            table.HasCheckConstraint(
                "CK_workspaces_property_projection_coordinates",
                $"\"Id\" <> '{EmptyGuid}' AND char_length(\"ScopeId\") > 0 AND " +
                "\"ScopeId\" = btrim(\"ScopeId\") AND " +
                "\"ScopeId\" !~ '[[:space:][:cntrl:]]'");
            table.HasCheckConstraint(
                "CK_workspaces_property_projection_version",
                "\"Version\" >= 1");
            table.HasCheckConstraint(
                "CK_workspaces_property_projection_state",
                "\"Status\" IN (1, 2) AND " +
                "(\"Status\" = 2 OR \"Name\" IS NOT NULL)");
            table.HasCheckConstraint(
                "CK_workspaces_property_projection_name",
                "\"Name\" IS NULL OR (char_length(\"Name\") > 0 AND " +
                "\"Name\" = btrim(\"Name\") AND \"Name\" !~ '[[:cntrl:]]')");
        });
        builder.HasKey(property => new { property.ScopeId, property.Id });
        builder.Property(property => property.ScopeId).HasMaxLength(128).IsRequired();
        builder.Property(property => property.Name)
            .HasMaxLength(PropertiesContractLimits.PropertyNameMaxLength);
        builder.Property(property => property.Status).HasConversion<int>().IsRequired();
        builder.Property(property => property.Version).IsConcurrencyToken().IsRequired();
        builder.HasIndex(property => new { property.ScopeId, property.Status, property.Id });
    }
}
