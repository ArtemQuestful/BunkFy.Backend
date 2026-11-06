namespace BunkFy.Modules.Stations.Contracts;

using Gma.Framework.Modules;
using Gma.Framework.Permissions;

/// <summary>Station management permission; runtime PIN actors are never primary Auth principals.</summary>
public static class StationsModuleMetadata
{
    public const string Name = "stations";
    public const string Schema = "stations";

    public static ModuleDescriptor Descriptor { get; } = ModuleDescriptor.Create(Name)
        .WithSchema(Schema)
        .WithPermissions(
        [
            new ModulePermissionDescriptor(StationsPermissionCodes.Manage,
                "Manage shared stations, staff registration and PIN recovery.",
                PermissionScopeRequirement.Scoped, PermissionScopeGrantPolicy.Descendants)
        ])
        .Build();
}
