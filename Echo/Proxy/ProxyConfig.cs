using AppEnvironment;
using Echo.RateLimiter;
using Yarp.ReverseProxy.Configuration;
using Yarp.ReverseProxy.Transforms;

namespace Echo.Proxy;

public static class ProxyConfig
{
    // Named rather than repeated, because the route and its cluster are both filtered out below on
    // the same condition and a typo in either string would leave half a service wired up.
    private const string BillingRouteId = "billing-route";

    /// <summary>The Stripe webhook, which is the same cluster and the same path prefix as
    /// <see cref="BillingRouteId"/> but must not be rate limited. Named separately because it is
    /// filtered out on the same <c>IsHosted</c> condition and a typo would leave a route pointing at
    /// a cluster that is not there, which YARP refuses to start with.</summary>
    private const string BillingWebhookRouteId = "billing-stripe-webhook-route";

    /// <summary>Public path of the Stripe webhook, and the one string in this file that a third party
    /// has already been configured with (monetization-stripe-architecture.md section 10 records the
    /// destination). The service maps <c>/api/v1/stripe/webhook</c>, so the same segment rewrite as
    /// the billing route applies.</summary>
    private const string BillingWebhookPath = "/api/v1/billing/stripe/webhook";

    private const string BillingClusterId = "billing-cluster";

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
            RouteId = "discovery-route",
            ClusterId = "discovery-cluster",
            Match = new RouteMatch { Path = "/api/v1/discovery/{**catch-all}" }
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

        // Link preview media.
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
            // Cross-origin sign-in.
            CorsPolicy = Sites.SsoCors.PolicyName,
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
            // The discovery document is the first thing any OIDC library fetches, from the
            // partner's own origin. Public by specification.
            CorsPolicy = Sites.SsoCors.PolicyName,
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
            // Signing keys, fetched by the partner's library to validate the id_token. Public.
            CorsPolicy = Sites.SsoCors.PolicyName,
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

        // Billing (docs/specs/monetization.md).
        new RouteConfig
        {
            RouteId = BillingRouteId,
            ClusterId = BillingClusterId,
            Match = new RouteMatch { Path = "/api/v1/billing/{**catch-all}" }
        }.WithTransformPathRouteValues(pattern: new PathString("/api/v1/{**catch-all}")),

        // The Stripe webhook, split out of the route above for one reason: it must not be rate
        // limited.
        new RouteConfig
        {
            RouteId = BillingWebhookRouteId,
            ClusterId = BillingClusterId,
            Match = new RouteMatch { Path = BillingWebhookPath },
            Metadata = new Dictionary<string, string> { [RateLimitConfigFilter.ExemptMetadataKey] = "true" },
        }.WithTransformPathRouteValues(pattern: new PathString("/api/v1/stripe/webhook")),
    }
    // Dropped entirely in selfhost, which is the default and is what nearly every deployment is.
    .Where(route => route.RouteId is not (BillingRouteId or BillingWebhookRouteId) || Env.License.IsHosted)
    .ToArray();

    public static IReadOnlyList<ClusterConfig> GetClusters()
    {
        var identity  = Environment.GetEnvironmentVariable("Services__Identity")  ?? "http://identity.default.svc.cluster.local";
        var guild     = Environment.GetEnvironmentVariable("Services__Guild")     ?? "http://guild.default.svc.cluster.local";
        var messaging = Environment.GetEnvironmentVariable("Services__Messaging") ?? "http://messaging.default.svc.cluster.local";
        var social    = Environment.GetEnvironmentVariable("Services__Social")    ?? "http://social.default.svc.cluster.local";
        var discovery = Environment.GetEnvironmentVariable("Services__Discovery") ?? "http://discovery.default.svc.cluster.local";
        var federation    = Environment.GetEnvironmentVariable("Services__Federation")    ?? "http://federation.default.svc.cluster.local";
        var isle    = Environment.GetEnvironmentVariable("Services__Isle")    ?? "http://isle.default.svc.cluster.local:8080";
        var bots    = Environment.GetEnvironmentVariable("Services__Bots")    ?? "http://bots.default.svc.cluster.local";
        var imports = Environment.GetEnvironmentVariable("Services__Import") ?? "http://import.default.svc.cluster.local";
        var unfurl = Environment.GetEnvironmentVariable("Services__Unfurl") ?? "http://unfurl.default.svc.cluster.local";

        // BILLING_SERVICE_URL rather than a Services__Billing of its own, because that variable
        // already exists: it is half of what Env.License.EnsureConfigured accepts as proof that a
        // `hosted` instance can answer "what has this guild paid for".
        var billing = Env.License.BillingServiceUrl is { Length: > 0 } billingUrl
            ? billingUrl
            : "http://billing.default.svc.cluster.local";

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
            ClusterId = "discovery-cluster",
            Destinations = new Dictionary<string, DestinationConfig>
            {
                { "dest1", new DestinationConfig { Address = discovery } }
            },
            HealthCheck = new HealthCheckConfig()
            {
                Active = new ActiveHealthCheckConfig()
                {
                    Path = "discovery/health",
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
        },

        new ClusterConfig
        {
            ClusterId = BillingClusterId,
            Destinations = new Dictionary<string, DestinationConfig>
            {
                { "dest1", new DestinationConfig { Address = billing } }
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
                    Path = "billing/health",
                    Timeout = TimeSpan.FromSeconds(10),
                    Interval = TimeSpan.FromSeconds(15),
                }
            },
        }

        }
        // Same condition as the route above, and it has to be the same: a cluster with no route is
        // harmless, but a route with no cluster is a startup failure in YARP's config validation.
        .Where(cluster => cluster.ClusterId != BillingClusterId || Env.License.IsHosted)
        .ToArray();
    }
}