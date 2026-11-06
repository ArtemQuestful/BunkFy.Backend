namespace BunkFy.Host.ServiceDefaults.Security.Stations;

using Microsoft.AspNetCore.Builder;

public enum StationOperationClass
{
    Forbidden = 0,
    Eligible = 1
}

public sealed record StationOperationClassMetadata(StationOperationClass OperationClass);

public sealed record StationMutationMetadata;

public sealed record StationAttributionAdapterMetadata(string AdapterName)
{
    public string AdapterName { get; } = string.IsNullOrWhiteSpace(AdapterName)
        ? throw new ArgumentException("A station attribution-adapter name is required.", nameof(AdapterName))
        : AdapterName.Trim();
}

public static class StationEndpointConventionBuilderExtensions
{
    public static TBuilder WithStationOperationClass<TBuilder>(
        this TBuilder builder,
        StationOperationClass operationClass)
        where TBuilder : IEndpointConventionBuilder
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Add(endpointBuilder =>
            endpointBuilder.Metadata.Add(new StationOperationClassMetadata(operationClass)));
        return builder;
    }

    public static TBuilder WithStationMutationAttribution<TBuilder>(
        this TBuilder builder,
        string adapterName)
        where TBuilder : IEndpointConventionBuilder
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Add(endpointBuilder =>
        {
            endpointBuilder.Metadata.Add(new StationMutationMetadata());
            endpointBuilder.Metadata.Add(new StationAttributionAdapterMetadata(adapterName));
        });
        return builder;
    }
}
