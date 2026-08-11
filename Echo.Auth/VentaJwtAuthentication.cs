using AppEnvironment;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;

namespace Echo.Auth;

/// <summary>The one place a service is told how to validate a Venta access token.</summary>
public static class VentaJwtAuthentication
{
    /// <summary>Registers JWT bearer authentication against the Venta identity provider.</summary>
    /// <param name="webSocketPath">
    /// Path prefix on which a token may arrive as <c>?access_token=</c> instead of in the
    /// Authorization header, because browser WebSocket clients cannot set headers.
    /// </param>
    public static AuthenticationBuilder AddVentaJwtBearer(
        this IServiceCollection services,
        string? webSocketPath = null)
    {
        return services
            .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(options =>
            {
                // Where the signing keys come from - NOT the issuer.
                options.Authority = Env.GeneralConfiguration.InstanceUrl;
                options.RequireHttpsMetadata = false;

                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidIssuers = AcceptedIssuers(),

                    // Every service accepts any token this issuer signed.
                    ValidateAudience = false,
                };

                if (webSocketPath is not null)
                {
                    options.Events = new JwtBearerEvents
                    {
                        OnMessageReceived = context =>
                        {
                            var accessToken = context.Request.Query["access_token"];

                            if (!string.IsNullOrEmpty(accessToken)
                                && context.HttpContext.Request.Path.StartsWithSegments(webSocketPath))
                            {
                                context.Token = accessToken;
                            }

                            return Task.CompletedTask;
                        },
                    };
                }
            });
    }

    /// <summary>Every spelling of the issuer that must be accepted.</summary>
    public static string[] AcceptedIssuers()
    {
        var configured = Env.AuthConfiguration.IssuerUrl.TrimEnd('/');

        return [configured, configured + "/"];
    }
}
