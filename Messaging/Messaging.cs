using AppEnvironment;
using JasperFx.CodeGeneration;
using JasperFx.Core;
using JasperFx.Resources;
using Npgsql;
using Wolverine;
using Wolverine.EntityFrameworkCore;
using Wolverine.ErrorHandling;
using Wolverine.Postgresql;
using Wolverine.RabbitMQ;
using Wolverine.Transports;
using IEventSource = Domain.IEventSource;

namespace Messaging;

public static class Messaging
{
   public static WolverineOptions ConfigureWolverine(this WolverineOptions opts, bool useEfCore = true, bool setupResources = true)
{
    var skipPersistence = Environment.GetEnvironmentVariable("WOLVERINE_SKIP_PERSISTENCE")
        ?.Equals("true", StringComparison.OrdinalIgnoreCase) == true;

    if (!skipPersistence)
    {
        opts.PersistMessagesWithPostgresql(Env.Database.ConnectionString(), "public");
    }
    
    opts
        .Policies.OnException<TimeoutException>()
        .RetryWithCooldown(100.Milliseconds(), 1.Seconds(), 5.Seconds())
        .Then.MoveToErrorQueue();

    opts
        .Policies.OnException<NpgsqlException>()
        .RetryWithCooldown(500.Milliseconds(), 5.Seconds(), 30.Seconds())
        .Then.MoveToErrorQueue();
    
    if (!skipPersistence)
    {
        opts.CodeGeneration.TypeLoadMode = TypeLoadMode.Static;
        opts.Policies.UseDurableInboxOnAllListeners();
        opts.Policies.UseDurableLocalQueues();
        opts.Policies.UseDurableOutboxOnAllSendingEndpoints();
    }
    
    opts.MultipleHandlerBehavior = MultipleHandlerBehavior.Separated;
    opts.Durability.MessageIdentity = MessageIdentity.IdAndDestination;

    opts.Durability.InboxStaleTime = 30.Minutes();
    opts.Durability.OutboxStaleTime = 30.Minutes();

    if (useEfCore)
    {
        opts.Policies.AutoApplyTransactions();
        if (!skipPersistence)
        {
            opts.UseEntityFrameworkCoreTransactions();
        }
    }
 
    opts.PublishDomainEventsFromEntityFrameworkCore<IEventSource>(x => x.GetDomainEvents());
    opts.UseRabbitMq(rabbit =>
    {
        rabbit.HostName = Env.RabbitMq.HostName;
        rabbit.Port = Env.RabbitMq.Port;
        rabbit.UserName = Env.RabbitMq.UserName;
        rabbit.Password = Env.RabbitMq.Password;
        rabbit.RequestedHeartbeat = TimeSpan.FromSeconds(10);
    }).UseConventionalRouting(NamingSource.FromHandlerType, convention =>
    {
    }).AutoProvision();
    opts.Policies.DisableConventionalLocalRouting();
    
    if (setupResources && !skipPersistence)
        opts.Services.AddResourceSetupOnStartup();

    return opts;
}
}