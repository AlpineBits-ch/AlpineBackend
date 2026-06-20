using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Hosting;

namespace AppEnvironment;

public static class SentryInstance
{
    public static WebApplicationBuilder AddErrorReporting(this WebApplicationBuilder builder)
    {
        builder.WebHost.UseSentry(o =>
        {
            o.Dsn = Env.SentryUrl;
            o.Debug = true;
        });        
        return builder;
    }
}