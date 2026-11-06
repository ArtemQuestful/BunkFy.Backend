namespace BunkFy.Modules.Retention.Persistence.Configurations;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

internal sealed class RetentionTenantProjectionConfiguration
    : IEntityTypeConfiguration<RetentionTenantProjection>
{
    private const string EmptyGuid =
        "00000000-0000-0000-0000-000000000000";

    public void Configure(EntityTypeBuilder<RetentionTenantProjection> builder)
    {
        builder.ToTable("tenant_projection", table =>
        {
            table.HasCheckConstraint(
                "CK_retention_tenant_projection_coordinates",
                $"\"OrganizationId\" <> '{EmptyGuid}' AND " +
                "char_length(\"ScopeId\") > 0 AND " +
                "\"ScopeId\" = btrim(\"ScopeId\") AND " +
                "\"ScopeId\" !~ '[[:space:][:cntrl:]]'");
            table.HasCheckConstraint(
                "CK_retention_tenant_projection_version",
                "\"SourceVersion\" >= 1");
        });
        builder.HasKey(tenant => tenant.ScopeId);
        builder.Property(tenant => tenant.ScopeId).HasMaxLength(128).IsRequired();
        builder.Property(tenant => tenant.SourceVersion)
            .IsConcurrencyToken()
            .IsRequired();
        builder.HasIndex(tenant => tenant.OrganizationId).IsUnique();
        builder.HasIndex(tenant => new
        {
            tenant.IsActive,
            tenant.ScopeId
        });
    }
}
