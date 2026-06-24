using Alba;
using Identity.Application.Controllers;
using Identity.Infrastructure.Persistence;
using JasperFx.CodeGeneration;
using JasperFx.Resources;
using JasperFx.RuntimeCompiler;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.IdentityModel.Tokens;
using Wolverine;

namespace Identity.Tests;

[SetUpFixture]
public class AppFixture
{
    public static IAlbaHost Host { get; private set; } = null!;

    [OneTimeSetUp]
    public async Task SetUp()
    {
        Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", "Development");
        Environment.SetEnvironmentVariable("AUTH_REQUIRE_USER_EMAIL_VERIFICATION", "false");
        
        // Point at test database - defaults match local dev, CI overrides via env vars
        Environment.SetEnvironmentVariable("DATABASE_HOSTNAME", 
            Environment.GetEnvironmentVariable("DATABASE_HOSTNAME") ?? "localhost");
        Environment.SetEnvironmentVariable("DATABASE_PORT", 
            Environment.GetEnvironmentVariable("DATABASE_PORT") ?? "5433");
        Environment.SetEnvironmentVariable("DATABASE_NAME", 
            Environment.GetEnvironmentVariable("DATABASE_NAME") ?? "identity_test");
        Environment.SetEnvironmentVariable("DATABASE_USERNAME", 
            Environment.GetEnvironmentVariable("DATABASE_USERNAME") ?? "postgres");
        Environment.SetEnvironmentVariable("DATABASE_PASSWORD", 
            Environment.GetEnvironmentVariable("DATABASE_PASSWORD") ?? "postgres");

        Host = await AlbaHost.For<AuthenticationController>(x =>
        {
            var projectFolder = Path.GetFullPath(
                Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "Identity.Application"));
            x.UseContentRoot(projectFolder);
            x.UseEnvironment("Development");

            x.ConfigureServices(services =>
            {
                services.AddSingleton<IAssemblyGenerator, AssemblyGenerator>();

                // Remove Wolverine resource setup hosted service
                services.Where(s => s.ImplementationType?.Name.Contains("ResourceSetup") == true)
                    .ToList()
                    .ForEach(s => services.Remove(s));

                // Swap Redis for in-memory cache
                services.RemoveAll(typeof(IDistributedCache));
                services.AddDistributedMemoryCache();

                services.RunWolverineInSoloMode();
                services.DisableAllExternalWolverineTransports();
                
               
            });
        });

        await Host.SetupResources();
    }

    [OneTimeTearDown]
    public async Task TearDown()
    {
        if (Host != null)
        {
            await Host.StopAsync();
            Host.Dispose();
        }
    }
}