namespace BunkFy.Modules.Reservations.Api;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;

internal sealed class ReservationsNoStoreStartupFilter : IStartupFilter
{
    private static readonly PathString ReservationsPath = new("/api/reservations");

    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
    {
        ArgumentNullException.ThrowIfNull(next);

        return applicationBuilder =>
        {
            applicationBuilder.Use(nextMiddleware => context =>
            {
                if (context.Request.Path.StartsWithSegments(ReservationsPath))
                {
                    MarkNoStore(context.Response);
                    context.Response.OnStarting(static state =>
                    {
                        MarkNoStore((HttpResponse)state);
                        return Task.CompletedTask;
                    }, context.Response);
                }

                return nextMiddleware(context);
            });

            next(applicationBuilder);
        };
    }

    private static void MarkNoStore(HttpResponse response)
    {
        response.Headers.CacheControl = "no-store";
        response.Headers.Pragma = "no-cache";
        response.Headers.Expires = "0";
    }
}
