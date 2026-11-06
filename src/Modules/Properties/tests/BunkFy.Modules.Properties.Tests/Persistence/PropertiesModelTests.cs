namespace BunkFy.Modules.Properties.Tests;

using System.Security.Cryptography;
using System.Text;
using BunkFy.Modules.Properties.Domain.Aggregates;
using BunkFy.Modules.Properties.Domain.Entities;
using BunkFy.Modules.Properties.Persistence;
using BunkFy.Modules.Properties.Persistence.TenantTermination;
using BunkFy.TimeZones;
using Gma.Framework.Domain;
using Gma.Framework.Scoping;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Xunit;

[Trait("Category", "Unit")]
public sealed class PropertiesModelTests
{
    [Fact]
    public void Every_properties_scope_shaped_entity_is_explicitly_scoped()
    {
        using PropertiesDbContext context = CreateDbContext();

        string[] unclassified = context.Model.GetEntityTypes()
            .Where(entity =>
                !entity.IsOwned() &&
                entity.ClrType.Namespace?.StartsWith(
                    "BunkFy.Modules.Properties.",
                    StringComparison.Ordinal) == true &&
                entity.FindProperty(nameof(IScopedEntity.ScopeId)) is not null &&
                !typeof(IScopedEntity).IsAssignableFrom(entity.ClrType))
            .Select(entity => entity.ClrType.FullName ?? entity.Name)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Empty(unclassified);
    }

    [Fact]
    public void Governance_revisions_are_tenant_filtered_and_constrained()
    {
        using PropertiesDbContext context = CreateDbContext();
        IModel designModel = context.GetService<IDesignTimeModel>().Model;
        IEntityType revision = designModel.FindEntityType(
            typeof(PropertyGovernanceRevision))!;

        Assert.NotEmpty(revision.GetDeclaredQueryFilters());
        Assert.Equal(
            [nameof(PropertyGovernanceRevision.Id)],
            revision.FindPrimaryKey()!.Properties.Select(property =>
                property.Name));
        Assert.Equal(
            [
                "CK_property_governance_revision_action",
                "CK_property_governance_revision_coordinates",
                "CK_property_governance_revision_evidence",
                "CK_property_governance_revision_occurred_at",
                "CK_property_governance_revision_policy",
                "CK_property_governance_revision_text",
                "CK_property_governance_revision_version"
            ],
            revision.GetCheckConstraints()
                .Select(constraint => constraint.Name)
                .Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Room_model_has_scope_aware_property_foreign_key()
    {
        using PropertiesDbContext dbContext = CreateDbContext();

        IEntityType roomEntity = dbContext.Model.FindEntityType(typeof(Room))!;
        IEntityType propertyEntity = dbContext.Model.FindEntityType(typeof(Property))!;
        IForeignKey foreignKey = Assert.Single(roomEntity.GetForeignKeys(), candidate => candidate.PrincipalEntityType == propertyEntity);

        Assert.Equal(["ScopeId", "PropertyId"], foreignKey.Properties.Select(property => property.Name));
        Assert.Equal(["ScopeId", "Id"], foreignKey.PrincipalKey.Properties.Select(property => property.Name));
        Assert.Equal(DeleteBehavior.Restrict, foreignKey.DeleteBehavior);
    }

    [Fact]
    public void Topology_versions_are_concurrency_tokens_and_cursor_is_generated()
    {
        using PropertiesDbContext dbContext = CreateDbContext();

        IEntityType propertyEntity = dbContext.Model.FindEntityType(typeof(Property))!;
        IEntityType roomEntity = dbContext.Model.FindEntityType(typeof(Room))!;
        IEntityType bedEntity = dbContext.Model.FindEntityType(typeof(Bed))!;

        Assert.True(propertyEntity.FindProperty(nameof(Property.Version))!.IsConcurrencyToken);
        Assert.True(roomEntity.FindProperty(nameof(Room.Version))!.IsConcurrencyToken);
        Assert.True(bedEntity.FindProperty(nameof(Bed.Version))!.IsConcurrencyToken);
        Assert.Equal(
            ValueGenerated.OnAdd,
            propertyEntity.FindProperty(nameof(Property.ProjectionOrdinal))!.ValueGenerated);
        Assert.Contains(
            propertyEntity.GetIndexes(),
            index => index.IsUnique && index.Properties.Select(property => property.Name).SequenceEqual([nameof(Property.ProjectionOrdinal)]));
        Assert.Contains(
            propertyEntity.GetIndexes(),
            index => index.Properties.Select(property => property.Name)
                .SequenceEqual([
                    nameof(Property.ScopeId),
                    nameof(Property.ProjectionOrdinal),
                    nameof(Property.Id)]) &&
                string.Equals(
                    index.GetDatabaseName(),
                    "IX_properties_scope_projection_ordinal_id",
                    StringComparison.Ordinal));
    }

    [Fact]
    public void Operation_locks_are_scope_unique_and_concurrency_guarded()
    {
        using PropertiesDbContext dbContext = CreateDbContext();
        IModel designModel = dbContext.GetService<IDesignTimeModel>().Model;

        AssertOperationLock(
            designModel.FindEntityType(typeof(PropertyOperationLock))!,
            nameof(PropertyOperationLock.PropertyId),
            "CK_property_operation_locks_coordinate",
            "CK_property_operation_locks_revision");
        AssertOperationLock(
            designModel.FindEntityType(typeof(RoomOperationLock))!,
            nameof(RoomOperationLock.RoomId),
            "CK_room_operation_locks_coordinate",
            "CK_room_operation_locks_revision");
    }

    [Fact]
    public void Property_mutation_operations_are_scoped_constrained_and_cascade()
    {
        using PropertiesDbContext dbContext = CreateDbContext();
        IModel designModel = dbContext.GetService<IDesignTimeModel>().Model;
        IEntityType operation = designModel.FindEntityType(
            typeof(PropertyMutationOperation))!;
        IEntityType property = designModel.FindEntityType(typeof(Property))!;

        Assert.Equal(
            ["ScopeId", "ResourceKind", "ResourceId", "Id"],
            operation.FindPrimaryKey()!.Properties.Select(
                item => item.Name));
        Assert.NotEmpty(operation.GetDeclaredQueryFilters());
        IForeignKey foreignKey = Assert.Single(
            operation.GetForeignKeys(),
            candidate => candidate.PrincipalEntityType == property);
        Assert.Equal(DeleteBehavior.Cascade, foreignKey.DeleteBehavior);
        Assert.Equal(
            ["ScopeId", "PropertyId"],
            foreignKey.Properties.Select(item => item.Name));
        Assert.Contains(
            operation.GetCheckConstraints(),
            constraint => constraint.Name ==
                "CK_properties_property_mutation_operations_fingerprint");
        Assert.Contains(
            operation.GetCheckConstraints(),
            constraint => constraint.Name ==
                "CK_properties_property_mutation_operations_versions");
        Assert.Contains(
            operation.GetCheckConstraints(),
            constraint => constraint.Name ==
                "CK_properties_property_mutation_operations_kind" &&
                constraint.Sql == "\"Kind\" IN (1, 2, 3, 4, 5, 6, 7, 8, 9)");
        Assert.Contains(
            operation.GetCheckConstraints(),
            constraint => constraint.Name ==
                "CK_properties_property_mutation_operations_resource");
        Assert.Contains(
            operation.GetCheckConstraints(),
            constraint => constraint.Name ==
                "CK_properties_property_mutation_operations_status");
        Assert.True(operation.FindProperty("ResultStatus")!.IsNullable);
        Assert.True(operation.FindProperty("ResultRoomId")!.IsNullable);
        Assert.True(operation.FindProperty("ResultBedId")!.IsNullable);
        Assert.True(operation.FindProperty("ResultBedStatus")!.IsNullable);
        Assert.True(operation.FindProperty("ResultAffectedBedCount")!.IsNullable);
        Assert.False(
            operation.FindProperty("ResultResourceVersion")!.IsNullable);
        Assert.Contains(
            operation.GetCheckConstraints(),
            constraint => constraint.Name ==
                "CK_properties_property_mutation_operations_status" &&
                constraint.Sql!.Contains(
                    "\"Kind\" IN (7, 9)",
                    StringComparison.Ordinal) &&
                constraint.Sql.Contains(
                    "\"ResultAffectedBedCount\" BETWEEN 1 AND 100",
                    StringComparison.Ordinal));
        Assert.Contains(
            operation.GetCheckConstraints(),
            constraint => constraint.Name ==
                "CK_properties_property_mutation_operations_versions" &&
                constraint.Sql!.Contains(
                    "\"ResultResourceVersion\" = \"ExpectedVersion\" + " +
                    "\"ResultAffectedBedCount\"",
                    StringComparison.Ordinal));
    }

    [Fact]
    public void Property_time_zone_operations_are_scoped_append_only_ledger_rows()
    {
        using PropertiesDbContext dbContext = CreateDbContext();
        IModel designModel = dbContext.GetService<IDesignTimeModel>().Model;
        IEntityType operation = designModel.FindEntityType(
            typeof(PropertyTimeZoneOperation))!;
        IEntityType property = designModel.FindEntityType(typeof(Property))!;
        IEntityType propertyLock = designModel.FindEntityType(
            typeof(PropertyOperationLock))!;
        IEntityType catalogEntry = designModel.FindEntityType(
            typeof(PropertyTimeZoneCatalogEntry))!;
        IEntityType catalogResolution = designModel.FindEntityType(
            typeof(PropertyTimeZoneCatalogResolution))!;

        Assert.Equal(
            ["ScopeId", "PropertyId", "OperationId"],
            operation.FindPrimaryKey()!.Properties.Select(item => item.Name));
        Assert.NotEmpty(operation.GetDeclaredQueryFilters());
        IForeignKey foreignKey = Assert.Single(
            operation.GetForeignKeys(),
            candidate => candidate.PrincipalEntityType == property);
        Assert.Equal(DeleteBehavior.Restrict, foreignKey.DeleteBehavior);
        Assert.Equal(
            ["ScopeId", "PropertyId"],
            foreignKey.Properties.Select(item => item.Name));
        IForeignKey lockForeignKey = Assert.Single(
            operation.GetForeignKeys(),
            candidate => candidate.PrincipalEntityType == propertyLock);
        Assert.Equal(DeleteBehavior.Restrict, lockForeignKey.DeleteBehavior);
        Assert.Equal(
            ["ScopeId", "PropertyId"],
            lockForeignKey.Properties.Select(item => item.Name));
        IForeignKey catalogForeignKey = Assert.Single(
            operation.GetForeignKeys(),
            candidate => candidate.PrincipalEntityType == catalogEntry);
        Assert.Equal(DeleteBehavior.Restrict, catalogForeignKey.DeleteBehavior);
        Assert.Equal(
            ["CatalogVersion", "TimeZoneId"],
            catalogForeignKey.Properties.Select(item => item.Name));
        IForeignKey resolutionForeignKey = Assert.Single(
            operation.GetForeignKeys(),
            candidate => candidate.PrincipalEntityType == catalogResolution);
        Assert.Equal(
            DeleteBehavior.Restrict,
            resolutionForeignKey.DeleteBehavior);
        Assert.Equal(
            ["CatalogVersion", "RequestedTimeZoneId", "TimeZoneId"],
            resolutionForeignKey.Properties.Select(item => item.Name));
        Assert.Contains(
            operation.GetIndexes(),
            index => index.IsUnique &&
                index.Properties.Select(item => item.Name)
                    .SequenceEqual(["ScopeId", "RevisionId"]));
        Assert.DoesNotContain(
            operation.GetIndexes(),
            index => index.Properties.Select(item => item.Name)
                .SequenceEqual(["ScopeId", "OperationId"]));
        Assert.Equal(
            [
                "CK_properties_property_time_zone_operation_change",
                "CK_properties_property_time_zone_operation_ids",
                "CK_properties_property_time_zone_operation_text"
            ],
            operation.GetCheckConstraints()
                .Select(constraint => constraint.Name)
                .Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Property_time_zone_catalog_seed_is_versioned_append_only_and_matches_embedded_catalog()
    {
        using PropertiesDbContext dbContext = CreateDbContext();
        IModel designModel = dbContext.GetService<IDesignTimeModel>().Model;
        IEntityType catalogEntry = designModel.FindEntityType(
            typeof(PropertyTimeZoneCatalogEntry))!;
        IEntityType catalogResolution = designModel.FindEntityType(
            typeof(PropertyTimeZoneCatalogResolution))!;

        Assert.NotEmpty(PropertyTimeZoneCatalogSeed.AllVersions);
        Assert.Equal(
            PropertyTimeZoneCatalogSeed.AllVersions.Count,
            PropertyTimeZoneCatalogSeed.AllVersions
                .Select(version => version.CatalogVersion)
                .Distinct(StringComparer.Ordinal)
                .Count());
        PropertyTimeZoneCatalogVersionSeed current =
            PropertyTimeZoneCatalogSeed.AllVersions[^1];
        Assert.Equal(
            PropertyTimeZoneCatalogSeed.CurrentCatalogVersion,
            current.CatalogVersion);
        Assert.Equal(
            TimeZoneCatalog.Default.CatalogVersion,
            current.CatalogVersion);
        Assert.Equal(
            PropertyTimeZoneCatalogSeed.CurrentCanonicalIdCount,
            current.CanonicalIds.Count);
        Assert.Equal(
            TimeZoneCatalog.Default.CanonicalIds,
            current.CanonicalIds);
        Assert.Equal(
            PropertyTimeZoneCatalogSeed.CurrentCanonicalIdsSha256,
            ComputeCanonicalIdsSha256(current.CanonicalIds));
        Assert.Equal(
            PropertyTimeZoneCatalogSeed.CurrentCanonicalIdsSha256,
            current.CanonicalIdsSha256);
        Assert.Equal(
            PropertyTimeZoneCatalogSeed.CurrentResolutionCount,
            current.Resolutions.Count);
        Assert.Equal(
            TimeZoneCatalog.Default.Resolutions.Select(resolution =>
                (resolution.RequestedTimeZoneId,
                    resolution.CanonicalTimeZoneId)),
            current.Resolutions.Select(resolution =>
                (resolution.RequestedTimeZoneId,
                    resolution.CanonicalTimeZoneId)));
        Assert.Equal(
            PropertyTimeZoneCatalogSeed.CurrentResolutionsSha256,
            ComputeResolutionsSha256(current.Resolutions));
        Assert.Equal(
            PropertyTimeZoneCatalogSeed.CurrentResolutionsSha256,
            current.ResolutionsSha256);
        Assert.Equal(
            PropertyTimeZoneCatalogSeed.AllVersions.Sum(version =>
                version.CanonicalIds.Count),
            PropertyTimeZoneCatalogSeed.AllEntries.Count);
        Assert.Equal(
            PropertyTimeZoneCatalogSeed.AllVersions.Sum(version =>
                version.Resolutions.Count),
            PropertyTimeZoneCatalogSeed.AllResolutions.Count);
        Assert.All(
            PropertyTimeZoneCatalogSeed.AllVersions,
            version =>
            {
                Assert.NotEmpty(version.CanonicalIds);
                Assert.NotEmpty(version.Resolutions);
                Assert.Equal(
                    version.CanonicalIds.Count,
                    version.CanonicalIds.Distinct(StringComparer.Ordinal)
                        .Count());
                Assert.Equal(
                    version.Resolutions.Count,
                    version.Resolutions.Select(resolution =>
                            resolution.RequestedTimeZoneId)
                        .Distinct(StringComparer.Ordinal)
                        .Count());
                Assert.Equal(
                    version.CanonicalIdsSha256,
                    ComputeCanonicalIdsSha256(version.CanonicalIds));
                Assert.Equal(
                    version.ResolutionsSha256,
                    ComputeResolutionsSha256(version.Resolutions));
                Assert.Equal(
                    Enumerable.Range(1, version.CanonicalIds.Count),
                    PropertyTimeZoneCatalogSeed.AllEntries
                        .Where(entry => entry.CatalogVersion ==
                            version.CatalogVersion)
                        .Select(entry => entry.Ordinal));
                Assert.Equal(
                    Enumerable.Range(1, version.Resolutions.Count),
                    PropertyTimeZoneCatalogSeed.AllResolutions
                        .Where(resolution => resolution.CatalogVersion ==
                            version.CatalogVersion)
                        .Select(resolution => resolution.Ordinal));
            });
        Assert.Equal(
            ["CatalogVersion", "TimeZoneId"],
            catalogEntry.FindPrimaryKey()!.Properties.Select(item =>
                item.Name));
        Assert.Contains(
            catalogEntry.GetIndexes(),
            index => index.IsUnique && index.Properties
                .Select(item => item.Name)
                .SequenceEqual(["CatalogVersion", "Ordinal"]));
        Assert.Equal(
            ["CatalogVersion", "RequestedTimeZoneId"],
            catalogResolution.FindPrimaryKey()!.Properties.Select(item =>
                item.Name));
        Assert.Contains(
            catalogResolution.GetKeys(),
            key => key.Properties.Select(item => item.Name).SequenceEqual(
                [
                    "CatalogVersion",
                    "RequestedTimeZoneId",
                    "CanonicalTimeZoneId"
                ]));
        IForeignKey resolutionPrimaryForeignKey = Assert.Single(
            catalogResolution.GetForeignKeys());
        Assert.Equal(DeleteBehavior.Restrict, resolutionPrimaryForeignKey
            .DeleteBehavior);
        Assert.Equal(
            ["CatalogVersion", "CanonicalTimeZoneId"],
            resolutionPrimaryForeignKey.Properties.Select(item => item.Name));
    }

    [Fact]
    public void Tenant_revision_is_scope_keyed_and_concurrency_guarded()
    {
        using PropertiesDbContext dbContext = CreateDbContext();

        IEntityType revisionEntity = dbContext.Model.FindEntityType(
            typeof(PropertiesTenantRevision))!;
        IEntityType designRevisionEntity = dbContext
            .GetService<IDesignTimeModel>()
            .Model
            .FindEntityType(typeof(PropertiesTenantRevision))!;

        Assert.Equal(
            [nameof(PropertiesTenantRevision.ScopeId)],
            revisionEntity.FindPrimaryKey()!.Properties.Select(
                property => property.Name));
        Assert.True(
            revisionEntity.FindProperty(
                nameof(PropertiesTenantRevision.Revision))!
                .IsConcurrencyToken);
        Assert.NotEmpty(revisionEntity.GetDeclaredQueryFilters());
        Assert.Contains(
            designRevisionEntity.GetCheckConstraints(),
            constraint => string.Equals(
                constraint.Name,
                "CK_properties_tenant_revision_positive",
                StringComparison.Ordinal));
        Assert.Contains(
            designRevisionEntity.GetCheckConstraints(),
            constraint => string.Equals(
                constraint.Name,
                "CK_properties_tenant_revision_lifecycle",
                StringComparison.Ordinal));
    }

    [Fact]
    public void Tenant_destruction_progress_and_receipt_are_scope_unique_and_constrained()
    {
        using PropertiesDbContext dbContext = CreateDbContext();
        IModel designModel = dbContext.GetService<IDesignTimeModel>().Model;
        IEntityType operation = designModel.FindEntityType(
            typeof(PropertiesTenantDestroyOperation))!;
        IEntityType receipt = designModel.FindEntityType(
            typeof(PropertiesTenantDestroyReceipt))!;

        Assert.Equal(
            [nameof(PropertiesTenantDestroyOperation.OperationId)],
            operation.FindPrimaryKey()!.Properties.Select(
                property => property.Name));
        Assert.True(operation.FindProperty(
            nameof(PropertiesTenantDestroyOperation.ConcurrencyVersion))!
            .IsConcurrencyToken);
        Assert.Contains(
            operation.GetIndexes(),
            index => index.IsUnique &&
                index.Properties.Select(property => property.Name)
                    .SequenceEqual([
                        nameof(PropertiesTenantDestroyOperation.ScopeId)
                    ]));
        Assert.Contains(
            operation.GetCheckConstraints(),
            constraint => constraint.Name ==
                "CK_properties_tenant_destroy_operation_batch");
        Assert.Contains(
            operation.GetCheckConstraints(),
            constraint => constraint.Name ==
                "CK_properties_tenant_destroy_operation_progress");

        Assert.Equal(
            [nameof(PropertiesTenantDestroyReceipt.OperationId)],
            receipt.FindPrimaryKey()!.Properties.Select(
                property => property.Name));
        Assert.Contains(
            receipt.GetIndexes(),
            index => index.IsUnique &&
                index.Properties.Select(property => property.Name)
                    .SequenceEqual([
                        nameof(PropertiesTenantDestroyReceipt.ScopeId)
                    ]));
        Assert.Contains(
            receipt.GetCheckConstraints(),
            constraint => constraint.Name ==
                "CK_properties_tenant_destroy_receipt_progress");
    }

    private static PropertiesDbContext CreateDbContext()
    {
        DbContextOptions<PropertiesDbContext> options = new DbContextOptionsBuilder<PropertiesDbContext>()
            .UseInMemoryDatabase($"properties-model-{Guid.NewGuid():N}")
            .Options;

        return new PropertiesDbContext(options, new TestScopeContext());
    }

    private static string ComputeCanonicalIdsSha256(
        IEnumerable<string> canonicalIds)
    {
        string payload = string.Join('\n', canonicalIds) + '\n';
        return Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(payload)))
            .ToLowerInvariant();
    }

    private static string ComputeResolutionsSha256(
        IEnumerable<PropertyTimeZoneCatalogResolutionVersionEntrySeed>
            resolutions)
    {
        string payload = string.Join(
            '\n',
            resolutions.Select(resolution =>
                $"{resolution.RequestedTimeZoneId}=" +
                resolution.CanonicalTimeZoneId));
        return Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(payload)))
            .ToLowerInvariant();
    }

    private static void AssertOperationLock(
        IEntityType entity,
        string resourceProperty,
        string coordinateConstraint,
        string revisionConstraint)
    {
        Assert.Equal(
            ["Id"],
            entity.FindPrimaryKey()!.Properties.Select(
                property => property.Name));
        Assert.True(entity.FindProperty("Revision")!.IsConcurrencyToken);
        Assert.Contains(
            entity.GetKeys(),
            key => key.Properties.Select(property => property.Name)
                .SequenceEqual(["ScopeId", "Id"]));
        Assert.Contains(
            entity.GetIndexes(),
            index => index.IsUnique &&
                index.Properties.Select(property => property.Name)
                    .SequenceEqual(["ScopeId", resourceProperty]));
        Assert.Contains(
            entity.GetCheckConstraints(),
            constraint => constraint.Name == coordinateConstraint);
        Assert.Contains(
            entity.GetCheckConstraints(),
            constraint => constraint.Name == revisionConstraint);
    }

    private sealed class TestScopeContext : IScopeContext
    {
        public bool IsEnabled => true;
        public string ScopeId => "tenant-a";
    }
}
