using Npgsql;
using static System.Environment;

namespace AppEnvironment;

public static class Env
{
    public static readonly RabbitMQConfig RabbitMq = new RabbitMQConfig()
    {
        HostName = GetEnvironmentVariable("RABBITMQ_HOST") ?? "localhost",
        Port = int.Parse(GetEnvironmentVariable("RABBITMQ_PORT") ?? "5672"),
        UserName = GetEnvironmentVariable("RABBITMQ_USERNAME") ?? "admin",
        Password = GetEnvironmentVariable("RABBITMQ_PASSWORD") ?? "admin"
    };
    
    public static readonly ScyllaConfig Scylla = new();

    public static readonly DatabaseConfig Database = new();
    
    public static readonly RedisConfig Redis = new();
    
    public static readonly CloudflareConfig CloudflareConfig = new();
    public static readonly MicrosoftGraph MicrosoftGraph = new();
    public static readonly AuthConfiguration AuthConfiguration = new();

    public static string PersonalAccessToken => GetEnvironmentVariable("PERSONAL_ACCESS_TOKEN") ?? string.Empty;
    
    public static string GoogleServiceAccountJsonBase64 => GetEnvironmentVariable("GOOGLE_SERVICE_ACCOUNT_JSON_BASE_64") ?? string.Empty;
    public static string FireBaseServiceAccountJsonBase64 => GetEnvironmentVariable("FIREBASE_SEVRICE_ACCOUNT_JSON_BASE_64") ?? string.Empty;
    
    public static GeneralConfiguration GeneralConfiguration = new();

}

public class RabbitMQConfig
{
    public string HostName { get; set; }
    public int Port { get; set; }
    public string UserName { get; set; }
    public string Password { get; set; }
    
}

public class ScyllaConfig
{
    public string Host { get; set; } = GetEnvironmentVariable("SCYLLA_HOST") ?? "localhost";
    public int Port { get; set; } = int.Parse(GetEnvironmentVariable("SCYLLA_PORT") ?? "9042");
    public string UserName { get; set; } = GetEnvironmentVariable("SCYLLA_USERNAME") ?? "scylla";
    public string Password { get; set; } = GetEnvironmentVariable("SCYLLA_PASSWORD") ?? "scylla";
}

public class DatabaseConfig
{
    public bool Enabled { get; set; }
    public string DatabaseHostname { get; set; } = Environment.GetEnvironmentVariable("DATABASE_HOSTNAME") ?? "localhost";
    public string DatabasePort { get; set; } = Environment.GetEnvironmentVariable("DATABASE_PORT") ?? "5433";
    public string DatabaseName { get; set; } = Environment.GetEnvironmentVariable("DATABASE_NAME") ?? "postgres";
    public string? DatabaseSchemaName { get; set; } = "public";
    public string DatabaseUsername { get; set; } = Environment.GetEnvironmentVariable("DATABASE_USERNAME") ?? "postgres";
    public string DatabasePassword { get; set; } = Environment.GetEnvironmentVariable("DATABASE_PASSWORD") ?? "postgres";

    public int PoolSize { get; set; } = 50;

    public string ConnectionString(int pool = -1, bool usePooling = true)
    {
        var poolSize = pool == -1 ? PoolSize : pool;
        NpgsqlConnectionStringBuilder builder = new NpgsqlConnectionStringBuilder()
        {
            Host = DatabaseHostname,
            Port = int.Parse(DatabasePort),
            Database = DatabaseName,
            Username = DatabaseUsername,
            Password = DatabasePassword,
            Pooling = usePooling,
            MaxPoolSize = poolSize,
            NoResetOnClose = true,
            MaxAutoPrepare = 0,
            ConnectionIdleLifetime = 20,
        };
        return builder.ConnectionString;
    }
}

public class RedisConfig
{
    public string Host { get; set; } = Environment.GetEnvironmentVariable("REDIS_HOST") ?? "localhost";
    
    public string UserName { get; set; } = Environment.GetEnvironmentVariable("REDIS_USERNAME") ?? "";
    public string Password { get; set; } = Environment.GetEnvironmentVariable("REDIS_PASSWORD") ?? "devpassword";
    public string Port { get; set; } = Environment.GetEnvironmentVariable("REDIS_PORT") ?? "6379";

    public string ConnectionString => $"{Host}:{Port},password={Password}";

}



public class CloudflareConfig
{
    public string AppName { get; set; } = "echo";
    public string AppId { get; set; } = GetEnvironmentVariable("CLOUDFLARE_APP_ID") ?? "mock_app_id";
    public string ApiToken { get; set; } = GetEnvironmentVariable("CLOUDFLARE_API_TOKEN") ?? "mock_tocken";
}

public class MicrosoftGraph
{
    public string ClientId { get; set; } = Environment.GetEnvironmentVariable("MICROSOFT_GRAPH_CLIENT_ID") ?? "";
    public string ClientSecret { get; set; } = Environment.GetEnvironmentVariable("MICROSOFT_GRAPH_CLIENT_SECRET") ?? "";
}

public class AuthConfiguration
{
    public bool RequireUserEmailVerification { get; set; } = 
        (Environment.GetEnvironmentVariable("AUTH_REQUIRE_USER_EMAIL_VERIFICATION")?.Equals("true", StringComparison.OrdinalIgnoreCase) ?? true);


    public string IdentitySecretPassword { get; set; } = GetEnvironmentVariable("IDENTITY_KEY_PASSWORD") ?? "devpassword";
    public string IdentitySigningCert { get; set; } = GetEnvironmentVariable("IDENTITY_SIGNING_CERT") ?? string.Empty;
}

public class GeneralConfiguration
{
    public bool IsUserHashGenerationEnabled { get; set; } = (GetEnvironmentVariable("IS_USER_HASH_GENERATION_ENABLED")?.Equals("true", StringComparison.OrdinalIgnoreCase) ?? true);
    public string InstanceUrl { get; set; } = GetEnvironmentVariable("INSTANCE_URL") ?? "https://venta.gg";
}