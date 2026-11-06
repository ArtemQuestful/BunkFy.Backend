namespace BunkFy.Modules.DataRights.Tests.Application;

using BunkFy.Modules.DataRights.Application.Commands;
using BunkFy.Modules.DataRights.Application.Validation;
using BunkFy.Modules.DataRights.Contracts;
using Xunit;

[Trait("Category", "Unit")]
public sealed class TenantTerminationExportCommandValidatorTests
{
    private static readonly string Digest = new('a', 64);

    [Fact]
    public void Confirmation_requires_exact_coordinates_proofs_and_operator()
    {
        ConfirmTenantTerminationExportCommandValidator validator = new();
        ConfirmTenantTerminationExportCommand valid = new(
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            ExportOperationRevision: 2,
            ExpectedProcessVersion: 3,
            ExpectedArtifactVersion: 4,
            Digest,
            Digest,
            "admin-api:operator");

        Assert.Empty(validator.Validate(valid));
        Assert.NotEmpty(validator.Validate(valid with
        {
            CaseId = Guid.Empty,
            ExportOperationRevision = 0
        }));
        Assert.NotEmpty(validator.Validate(valid with
        {
            FragmentSetSha256 = new string('A', 64)
        }));
        Assert.NotEmpty(validator.Validate(valid with
        {
            ActorId = TenantTerminationCoordination.ExecutorActorId
        }));
    }

    [Fact]
    public void Download_requires_exact_coordinates_and_actor()
    {
        PrepareTenantTerminationExportDownloadCommandValidator validator =
            new();
        PrepareTenantTerminationExportDownloadCommand valid = new(
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            "admin-cli:operator");

        Assert.Empty(validator.Validate(valid));
        Assert.NotEmpty(validator.Validate(valid with
        {
            ArtifactId = Guid.Empty
        }));
        Assert.NotEmpty(validator.Validate(valid with
        {
            ActorId = string.Empty
        }));
    }
}
