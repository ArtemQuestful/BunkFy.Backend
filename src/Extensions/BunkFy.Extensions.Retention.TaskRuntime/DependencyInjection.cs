namespace BunkFy.Extensions.Retention.TaskRuntime;

using BunkFy.Modules.Retention.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

public static class DependencyInjection
{
    public static IServiceCollection AddBunkFyRetentionTaskRuntimeRecovery(
        this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.Replace(ServiceDescriptor.Scoped<
            IRetentionRunRetryExecutor,
            RetentionTaskRuntimeRunRetryExecutor>());
        return services;
    }
}
