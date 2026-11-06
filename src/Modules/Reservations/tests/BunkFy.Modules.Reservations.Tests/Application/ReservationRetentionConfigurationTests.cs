namespace BunkFy.Modules.Reservations.Tests;

using BunkFy.Modules.Reservations.Application;
using BunkFy.Modules.Reservations.Application.Contributors;
using BunkFy.Modules.Retention.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

[Trait("Category", "Unit")]
public sealed class ReservationRetentionConfigurationTests
{
    [Fact]
    public void Defaults_are_bounded_and_valid()
    {
        ReservationRetentionOptions options = new();

        ValidateOptionsResult validation =
            new ReservationRetentionOptionsValidator()
                .Validate(null, options);

        Assert.True(validation.Succeeded);
        Assert.Equal(60, options.IntervalMinutes);
        Assert.Equal(100, options.ScanSize);
        Assert.Equal(25, options.MutationBatchSize);
        Assert.Equal(1000, ReservationRetentionOptions.MaximumScanSize);
    }

    [Fact]
    public void Scan_size_cannot_exceed_repository_bound()
    {
        ReservationRetentionOptions options = new()
        {
            ScanSize = ReservationRetentionOptions.MaximumScanSize + 1,
            MutationBatchSize = 1
        };

        ValidateOptionsResult validation =
            new ReservationRetentionOptionsValidator()
                .Validate(null, options);

        Assert.True(validation.Failed);
        Assert.Contains(
            validation.Failures,
            failure => failure.Contains(
                $"between 1 and {ReservationRetentionOptions.MaximumScanSize}",
                StringComparison.Ordinal));
    }

    [Fact]
    public void Mutation_batch_cannot_exceed_scan_size()
    {
        ReservationRetentionOptions options = new()
        {
            ScanSize = 10,
            MutationBatchSize = 11
        };

        ValidateOptionsResult validation =
            new ReservationRetentionOptionsValidator()
                .Validate(null, options);

        Assert.True(validation.Failed);
        Assert.Contains(
            validation.Failures,
            failure => failure.Contains(
                "cannot exceed ScanSize",
                StringComparison.Ordinal));
    }

    [Fact]
    public void Application_registration_adds_one_retention_contributor()
    {
        ServiceCollection services = new();

        services.AddReservationsApplication();
        services.AddReservationsApplication();

        Assert.Single(
            services,
            descriptor =>
                descriptor.ServiceType ==
                    typeof(IRetentionExecutionContributor) &&
                descriptor.ImplementationType ==
                    typeof(ReservationRetentionContributor));
        Assert.Single(
            services,
            descriptor =>
                descriptor.ServiceType ==
                    typeof(IValidateOptions<
                        ReservationRetentionOptions>) &&
                descriptor.ImplementationType ==
                    typeof(ReservationRetentionOptionsValidator));
    }
}
