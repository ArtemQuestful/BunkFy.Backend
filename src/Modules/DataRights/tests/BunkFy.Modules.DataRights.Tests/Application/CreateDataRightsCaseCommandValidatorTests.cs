namespace BunkFy.Modules.DataRights.Tests.Application;

using BunkFy.Modules.DataRights.Application.Commands;
using BunkFy.Modules.DataRights.Application.Models;
using BunkFy.Modules.DataRights.Application.Validation;
using BunkFy.Modules.DataRights.Contracts;
using Xunit;

[Trait("Category", "Unit")]
public sealed class CreateDataRightsCaseCommandValidatorTests
{
    [Theory]
    [InlineData(DataRightsOperation.AccessExport, true)]
    [InlineData(DataRightsOperation.Correction, true)]
    [InlineData(DataRightsOperation.Restriction, true)]
    [InlineData(DataRightsOperation.Anonymisation, true)]
    [InlineData(DataRightsOperation.AccessExport | DataRightsOperation.Correction, false)]
    public void Staff_rights_operations_match_supported_owner_capabilities(
        DataRightsOperation operations,
        bool expectedValid)
    {
        CreateDataRightsCaseCommand command = new(
            Guid.NewGuid(),
            DataRightsCaseScope.Staff,
            operations,
            operations == DataRightsOperation.Restriction
                ? DataRightsRestrictionDirective.Apply
                : DataRightsRestrictionDirective.Unknown,
            DataRightsRequesterRelationship.ControllerInitiated,
            "user:operator");

        string[] errors = new CreateDataRightsCaseCommandValidator()
            .Validate(command)
            .ToArray();

        Assert.Equal(expectedValid, errors.Length == 0);
    }

    [Theory]
    [InlineData(
        DataRightsOperation.Restriction,
        DataRightsRestrictionDirective.Unknown,
        false)]
    [InlineData(
        DataRightsOperation.Restriction,
        DataRightsRestrictionDirective.Apply,
        true)]
    [InlineData(
        DataRightsOperation.Restriction,
        DataRightsRestrictionDirective.Release,
        true)]
    [InlineData(
        DataRightsOperation.AccessExport,
        DataRightsRestrictionDirective.Apply,
        false)]
    public void Restriction_directive_must_match_requested_operations(
        DataRightsOperation operations,
        DataRightsRestrictionDirective directive,
        bool expectedValid)
    {
        CreateDataRightsCaseCommand command = new(
            Guid.NewGuid(),
            DataRightsCaseScope.ForProperty(Guid.NewGuid()),
            operations,
            directive,
            DataRightsRequesterRelationship.ControllerInitiated,
            "user:operator");

        string[] errors = new CreateDataRightsCaseCommandValidator()
            .Validate(command)
            .ToArray();

        Assert.Equal(expectedValid, errors.Length == 0);
    }

    [Fact]
    public void Operation_id_is_required()
    {
        CreateDataRightsCaseCommand command = new(
            Guid.Empty,
            DataRightsCaseScope.Staff,
            DataRightsOperation.AccessExport,
            DataRightsRestrictionDirective.Unknown,
            DataRightsRequesterRelationship.ControllerInitiated,
            "user:operator");

        Assert.Contains(
            "OperationId is required.",
            new CreateDataRightsCaseCommandValidator()
                .Validate(command));
    }
}
