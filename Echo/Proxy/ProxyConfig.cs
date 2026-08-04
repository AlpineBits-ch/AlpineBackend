using Echo.RateLimiter;
using Yarp.ReverseProxy.Configuration;
using Yarp.ReverseProxy.Transforms;

namespace Echo.Proxy;

public static class ProxyConfig
{
    public static IReadOnlyList<RouteConfig> GetRoutes() => new[]
    {
        new RouteConfig
        {
            RouteId = "identity-route",
            ClusterId = "identity-cluster",
            Match = new RouteMatch { Path = "/api/v1/identity/{**catch-all}" }
        }.WithTransformPathRouteValues(pattern: new PathString("/api/v1/{**catch-all}")),

        new RouteConfig
        {
            RouteId = "social-route",
            ClusterId = "social-cluster",
            Match = new RouteMatch { Path = "/api/v1/social/{**catch-all}" }
        }.WithTransformPathRouteValues(pattern: new PathString("/api/v1/{**catch-all}")),
        new RouteConfig
        {
            RouteId = "isle-route",
            ClusterId = "isle-cluster",
            Match = new RouteMatch { Path = "/api/v1/isle/{**catch-all}" }
        }.WithTransformPathRouteValues(pattern: new PathString("/api/v1/{**catch-all}")),

        new RouteConfig
        {
            RouteId = "messaging-route",
            ClusterId = "messaging-cluster",
            Match = new RouteMatch { Path = "/api/v1/messaging/{**catch-all}" }
        }.WithTransformPathRouteValues(pattern: new PathString("/api/v1/{**catch-all}")),

        new RouteConfig
        {
            RouteId = "guild-route",
            ClusterId = "guild-cluster",
            Match = new RouteMatch { Path = "/api/v1/guild/{**catch-all}" }
        }.WithTransformPathRouteValues(pattern: new PathString("/api/v1/{**catch-all}")),
        
        // Deliberately no path rewrite, unlike every other service route above.
        new RouteConfig
        {
            RouteId = "federation-route",
            ClusterId = "federation-cluster",
            Match = new RouteMatch { Path = "/api/v1/federation/{**catch-all}" }
        },

        new RouteConfig
        {
            RouteId = "bots-route",
            ClusterId = "bots-cluster",
            Match = new RouteMatch { Path = "/api/v1/bots/{**catch-all}" }
        }.WithTransformPathRouteValues(pattern: new PathString("/api/v1/{**catch-all}")),

        // Bot developer portal (static page served from the Bots service's own wwwroot).
        new RouteConfig
        {
            RouteId = "bots-portal-root-route",
            ClusterId = "bots-cluster",
            Match = new RouteMatch { Path = "/bots-portal" }
        }.WithTransformPathRouteValues(pattern: new PathString("/")),

        new RouteConfig
        {
            RouteId = "bots-portal-route",
            ClusterId = "bots-cluster",
            Match = new RouteMatch { Path = "/bots-portal/{**catch-all}" }
        }.WithTransformPathRouteValues(pattern: new PathString("/{**catch-all}")),

        // Discord-compat surface keeps Discord's own path shape (no service-segment rewrite)
        // so a minimally-modified Discord bot library can point its base URL here directly.
        new RouteConfig
        {
            RouteId = "discord-compat-route",
            ClusterId = "bots-cluster",
            Match = new RouteMatch { Path = "/api/discord/v10/{**catch-all}" }
        },

        // Webhook execution keeps Discord's own path shape for the same reason the Discord-compat
        // surface above does: an existing "Discord webhook" integration (GitHub, Grafana, Sentry,
        // any CI) then works by changing the host and nothing else.
        new RouteConfig
        {
            RouteId = "webhook-execute-route",
            ClusterId = "guild-cluster",
            Match = new RouteMatch { Path = "/api/webhooks/{webhookId}/{token}" }
        },

        // Link preview media (docs/specs/message-previews.md).
        new RouteConfig
        {
            RouteId = "previews-route",
            ClusterId = "unfurl-cluster",
            Match = new RouteMatch { Path = "/api/v1/previews/{**catch-all}" },
            Metadata = new Dictionary<string, string> { [RateLimitConfigFilter.ExemptMetadataKey] = "true" },
        },

        new RouteConfig
        {
            RouteId = "imports-route",
            ClusterId = "imports-cluster",
            Match = new RouteMatch { Path = "/api/v1/imports/{**catch-all}" }
        }.WithTransformPathRouteValues(pattern: new PathString("/api/v1/{**catch-all}")),
        new RouteConfig
        {
            RouteId = "identity-connect-route",
            ClusterId = "identity-connect-cluster",
            Match = new RouteMatch { Path = "/connect/{**catch-all}" }
        }.WithTransformXForwarded(  headerPrefix: "X-Forwarded-",
            xDefault: ForwardedTransformActions.Append,
            xHost: ForwardedTransformActions.Set,
            xFor: ForwardedTransformActions.Append,
            xProto: ForwardedTransformActions.Append),
        
        new RouteConfig
        {
            RouteId = "identity-openid-config-route",
            ClusterId = "identity-oauth-cluster",
            Match = new RouteMatch { Path = "/.well-known/openid-configuration" }
        }.WithTransformXForwarded(  headerPrefix: "X-Forwarded-",
            xDefault: ForwardedTransformActions.Append,
            xHost: ForwardedTransformActions.Set,
            xFor: ForwardedTransformActions.Append,
            xProto: ForwardedTransformActions.Append),
        
        new RouteConfig
        {
            RouteId = "identity-jwks-route",
            ClusterId = "identity-oauth-cluster",
            Match = new RouteMatch { Path = "/.well-known/jwks" }
        }.WithTransformXForwarded(  headerPrefix: "X-Forwarded-",
            xDefault: ForwardedTransformActions.Append,
            xHost: ForwardedTransformActions.Set,
            xFor: ForwardedTransformActions.Append,
            xProto: ForwardedTransformActions.Append),
        
        new RouteConfig
        {
            RouteId = "federation-document-route",
            ClusterId = "federation-document-cluster",
            Match = new RouteMatch { Path = "/.well-known/federation" }
        }.WithTransformXForwarded(  headerPrefix: "X-Forwarded-",
            xDefault: ForwardedTransformActions.Append,
            xHost: ForwardedTransformActions.Set,
            xFor: ForwardedTransformActions.Append,
            xProto: ForwardedTransformActions.Append),

        new RouteConfig
        {
            RouteId = "federation-handshake-route",
            ClusterId = "federation-cluster",
            Match = new RouteMatch { Path = "/.well-known/federation/handshake" }
        }.WithTransformXForwarded(  headerPrefix: "X-Forwarded-",
            xDefault: ForwardedTransformActions.Append,
            xHost: ForwardedTransformActions.Set,
            xFor: ForwardedTransformActions.Append,
            xProto: ForwardedTransformActions.Append),

        new RouteConfig
        {
            RouteId = "federation-admin-route",
            ClusterId = "federation-cluster",
            Match = new RouteMatch { Path = "/api/v1/admin/federation/{**catch-all}" }
        },
    };

    public static IReadOnlyList<ClusterConfig> GetClusters()
    {
        var identity  = Environment.GetEnvironmentVariable("Services__Identity")  ?? "http://identity.default.svc.cluster.local";
        var guild     = Environment.GetEnvironmentVariable("Services__Guild")     ?? "http://guild.default.svc.cluster.local";
        var messaging = Environment.GetEnvironmentVariable("Services__Messaging") ?? "http://messaging.default.svc.cluster.local";
        var social    = Environment.GetEnvironmentVariable("Services__Social")    ?? "http://social.default.svc.cluster.local";
        var federation    = Environment.GetEnvironmentVariable("Services__Federation")    ?? "http://federation.default.svc.cluster.local";
        var isle    = Environment.GetEnvironmentVariable("Services__Isle")    ?? "http://isle.default.svc.cluster.local:8080";
        var bots    = Environment.GetEnvironmentVariable("Services__Bots")    ?? "http://bots.default.svc.cluster.local";
        var imports = Environment.GetEnvironmentVariable("Services__Import") ?? "http://import.default.svc.cluster.local";
        var unfurl = Environment.GetEnvironmentVariable("Services__Unfurl") ?? "http://unfurl.default.svc.cluster.local";

        return new[]
        {
        new ClusterConfig
        {
            ClusterId = "guild-cluster",
            Destinations = new Dictionary<string, DestinationConfig>
            {
                { "dest1", new DestinationConfig { Address = guild } }
            },
            SessionAffinity = new SessionAffinityConfig
                {
                    Enabled = true,
                    Policy = "Cookie",
                    FailurePolicy = "Redistribute",
                    AffinityKeyName = "guild_affinity",
                    Cookie = new SessionAffinityCookieConfig
                    {
                        Path = "/",
                        HttpOnly = true,
                        SecurePolicy = CookieSecurePolicy.Always,
                        IsEssential = true,
                        SameSite = SameSiteMode.None,
                    }
                },
            HealthCheck = new HealthCheckConfig
            {
                Passive = new PassiveHealthCheckConfig
                {
                    Enabled = true,
                    Policy = "TransportFailureRate",
                    ReactivationPeriod = TimeSpan.FromSeconds(10)
                },
                Active = new ActiveHealthCheckConfig()
                {
                    Path = "guild/health",
                    Timeout = TimeSpan.FromSeconds(10),
                    Interval = TimeSpan.FromSeconds(15),
                }
            },
        },
        new ClusterConfig
        {
            ClusterId = "identity-connect-cluster",
            Destinations = new Dictionary<string, DestinationConfig>
            {
                { "dest1", new DestinationConfig { Address = identity } }
            },
            HealthCheck = new HealthCheckConfig
            {
                Passive = new PassiveHealthCheckConfig
                {
                    Enabled = true,
                    Policy = "TransportFailureRate",
                    ReactivationPeriod = TimeSpan.FromSeconds(10)
                },
                Active = new ActiveHealthCheckConfig()
                {
                    Path = "identity/health",
                    Timeout = TimeSpan.FromSeconds(10),
                    Interval = TimeSpan.FromSeconds(15),
                }
            },
        },
        new ClusterConfig
        {
            ClusterId = "identity-oauth-cluster",
            Destinations = new Dictionary<string, DestinationConfig>
            {
                { "dest1", new DestinationConfig { Address = identity } }
            },
            HealthCheck = new HealthCheckConfig
            {
                Passive = new PassiveHealthCheckConfig
                {
                    Enabled = true,
                    Policy = "TransportFailureRate",
                    ReactivationPeriod = TimeSpan.FromSeconds(10)
                },
                Active = new ActiveHealthCheckConfig()
                {
                    Path = "identity/health",
                    Timeout = TimeSpan.FromSeconds(10),
                    Interval = TimeSpan.FromSeconds(15),
                }
            },
        },
        new ClusterConfig
        {
            ClusterId = "federation-document-cluster",
            Destinations = new Dictionary<string, DestinationConfig>
            {
                { "dest1", new DestinationConfig { Address = federation } }
            },
            HealthCheck = new HealthCheckConfig
            {
                Passive = new PassiveHealthCheckConfig
                {
                    Enabled = true,
                    Policy = "TransportFailureRate",
                    ReactivationPeriod = TimeSpan.FromSeconds(10)
                },
                Active = new ActiveHealthCheckConfig()
                {
                    Path = "federation/health",
                    Timeout = TimeSpan.FromSeconds(10),
                    Interval = TimeSpan.FromSeconds(15),
                }
            },
        },
        new ClusterConfig
        {
            ClusterId = "federation-cluster",
            Destinations = new Dictionary<string, DestinationConfig>
            {
                { "dest1", new DestinationConfig { Address = federation } }
            },
            HealthCheck = new HealthCheckConfig
            {
                Passive = new PassiveHealthCheckConfig
                {
                    Enabled = true,
                    Policy = "TransportFailureRate",
                    ReactivationPeriod = TimeSpan.FromSeconds(10)
                },
                Active = new ActiveHealthCheckConfig()
                {
                    Path = "federation/health",
                    Timeout = TimeSpan.FromSeconds(10),
                    Interval = TimeSpan.FromSeconds(15),
                }
            },
        },
        new ClusterConfig
        {
            ClusterId = "identity-cluster",
            Destinations = new Dictionary<string, DestinationConfig>
            {
                { "dest1", new DestinationConfig { Address = identity } }
            },
            HealthCheck = new HealthCheckConfig
            {
                Passive = new PassiveHealthCheckConfig
                {
                    Enabled = true,
                    Policy = "TransportFailureRate",
                    ReactivationPeriod = TimeSpan.FromSeconds(10)
                },
                Active = new ActiveHealthCheckConfig()
                {
                    Path = "identity/health",
                    Timeout = TimeSpan.FromSeconds(10),
                    Interval = TimeSpan.FromSeconds(15),
                }
            },
        },
        new ClusterConfig
        {
            ClusterId = "messaging-cluster",
            Destinations = new Dictionary<string, DestinationConfig>
            {
                { "messaging-dest", new DestinationConfig { Address = messaging } }
            },
            SessionAffinity = new SessionAffinityConfig
            {
                Enabled = true,
                Policy = "Cookie",
                FailurePolicy = "Redistribute",
                AffinityKeyName = "messaging_affinity",
                Cookie = new SessionAffinityCookieConfig
                {
                    Path = "/",
                    HttpOnly = true,
                    SecurePolicy = CookieSecurePolicy.Always,
                    IsEssential = true,
                    SameSite = SameSiteMode.None,
                }
            },
            HealthCheck = new HealthCheckConfig
            {
                Passive = new PassiveHealthCheckConfig
                {
                    Enabled = true,
                    Policy = "TransportFailureRate",
                    ReactivationPeriod = TimeSpan.FromSeconds(10)
                },
                Active = new ActiveHealthCheckConfig()
                {
                    Path = "messaging/health",
                    Timeout = TimeSpan.FromSeconds(10),
                    Interval = TimeSpan.FromSeconds(15),
                }
            },
        },
        new ClusterConfig
        {
            ClusterId = "social-cluster",
            Destinations = new Dictionary<string, DestinationConfig>
            {
                { "dest1", new DestinationConfig { Address = social } }
            },
            HealthCheck = new HealthCheckConfig()
            {
                Active = new ActiveHealthCheckConfig()
                {
                    Path = "social/health",
                    Timeout = TimeSpan.FromSeconds(10),
                    Interval = TimeSpan.FromSeconds(15),
                },
                Passive = new PassiveHealthCheckConfig
                {
                    Enabled = true,
                    Policy = "TransportFailureRate",
                    ReactivationPeriod = TimeSpan.FromSeconds(10)
                },
            }
        },
        
        
        
        new ClusterConfig
        {
        ClusterId = "isle-cluster",
        Destinations = new Dictionary<string, DestinationConfig>
        {
            { "dest1", new DestinationConfig { Address = isle } }
        },
        HealthCheck = new HealthCheckConfig()
        {
            Active = new ActiveHealthCheckConfig()
            {
                Path = "isle/health",
                Timeout = TimeSpan.FromSeconds(10),
                Interval = TimeSpan.FromSeconds(15),
            },
            Passive = new PassiveHealthCheckConfig
            {
                Enabled = true,
                Policy = "TransportFailureRate",
                ReactivationPeriod = TimeSpan.FromSeconds(10)
            },
        }
        },

        new ClusterConfig
        {
            ClusterId = "bots-cluster",
            Destinations = new Dictionary<string, DestinationConfig>
            {
                { "dest1", new DestinationConfig { Address = bots } }
            },
            HealthCheck = new HealthCheckConfig
            {
                Passive = new PassiveHealthCheckConfig
                {
                    Enabled = true,
                    Policy = "TransportFailureRate",
                    ReactivationPeriod = TimeSpan.FromSeconds(10)
                },
                Active = new ActiveHealthCheckConfig()
                {
                    Path = "bots/health",
                    Timeout = TimeSpan.FromSeconds(10),
                    Interval = TimeSpan.FromSeconds(15),
                }
            },
        },

        new ClusterConfig
        {
            ClusterId = "unfurl-cluster",
            Destinations = new Dictionary<string, DestinationConfig>
            {
                { "dest1", new DestinationConfig { Address = unfurl } }
            },
            HealthCheck = new HealthCheckConfig
            {
                Passive = new PassiveHealthCheckConfig
                {
                    Enabled = true,
                    Policy = "TransportFailureRate",
                    ReactivationPeriod = TimeSpan.FromSeconds(10)
                },
                Active = new ActiveHealthCheckConfig()
                {
                    Path = "unfurl/health",
                    Timeout = TimeSpan.FromSeconds(10),
                    Interval = TimeSpan.FromSeconds(15),
                }
            },
        },

        new ClusterConfig
        {
            ClusterId = "imports-cluster",
            Destinations = new Dictionary<string, DestinationConfig>
            {
                { "dest1", new DestinationConfig { Address = imports } }
            },
            HealthCheck = new HealthCheckConfig
            {
                Passive = new PassiveHealthCheckConfig
                {
                    Enabled = true,
                    Policy = "TransportFailureRate",
                    ReactivationPeriod = TimeSpan.FromSeconds(10)
                },
                Active = new ActiveHealthCheckConfig()
                {
                    // Singular: Import.Application maps "/import/health" (the route prefix is
                    // plural, the health endpoint is not).
                    Path = "import/health",
                    Timeout = TimeSpan.FromSeconds(10),
                    Interval = TimeSpan.FromSeconds(15),
                }
            },
        }

        };
    }
}