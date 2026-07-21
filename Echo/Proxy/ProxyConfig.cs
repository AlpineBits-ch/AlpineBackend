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
        
        new RouteConfig
        {
            RouteId = "federation-route",
            ClusterId = "federation-cluster",
            Match = new RouteMatch { Path = "/api/v1/federation/{**catch-all}" }
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
        var identity  = Environment.GetEnvironmentVariable("Services__Identity")  ?? "http://_http.identity.default.svc.cluster.local";
        var guild     = Environment.GetEnvironmentVariable("Services__Guild")     ?? "http://_http.guild.default.svc.cluster.local";
        var messaging = Environment.GetEnvironmentVariable("Services__Messaging") ?? "http://_http.messaging.default.svc.cluster.local";
        var social    = Environment.GetEnvironmentVariable("Services__Social")    ?? "http://_http.social.default.svc.cluster.local";
        var federation    = Environment.GetEnvironmentVariable("Services__Federation")    ?? "http://federation.default.svc.cluster.local";
        var isle    = Environment.GetEnvironmentVariable("Services__Isle")    ?? "http://isle.default.svc.cluster.local:8080";

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
        }
        
        };
    }
}