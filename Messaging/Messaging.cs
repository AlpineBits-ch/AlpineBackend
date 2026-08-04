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
    /// <summary>Cooldowns for a saga write that lost the optimistic-concurrency race.</summary>
    public static readonly TimeSpan[] SagaConcurrencyRetryDelays =
    [
        25.Milliseconds(), 50.Milliseconds(), 100.Milliseconds(), 250.Milliseconds(),
        500.Milliseconds(), 1.Seconds(), 2.Seconds(), 4.Seconds(), 6.Seconds(), 8.Seconds(),
    ];

   public static WolverineOptions ConfigureWolverine(this WolverineOptions opts, bool useEfCore = true)
{

  
   opts.PersistMessagesWithPostgresql(Env.Database.ConnectionString(), "public");
   
    
    // Saga writes are guarded by optimistic concurrency in the database: the lightweight saga
    // storage updates with "set version = @version + 1 where id = @id and version = @version" and
    // throws when that matches no row.
    opts
        .Policies.OnException<SagaConcurrencyException>()
        .RetryWithCooldown(SagaConcurrencyRetryDelays)
        .Then.MoveToErrorQueue();

    opts
        .Policies.OnException<TimeoutException>()
        .RetryWithCooldown(100.Milliseconds(), 1.Seconds(), 5.Seconds())
        .Then.MoveToErrorQueue();

    opts
        .Policies.OnException<NpgsqlException>()
        .RetryWithCooldown(500.Milliseconds(), 5.Seconds(), 30.Seconds())
        .Then.MoveToErrorQueue();
    
    
        opts.CodeGeneration.TypeLoadMode = TypeLoadMode.Static;
        opts.Policies.UseDurableInboxOnAllListeners();
        opts.Policies.UseDurableLocalQueues();
        opts.Policies.UseDurableOutboxOnAllSendingEndpoints();
    
    
    opts.MultipleHandlerBehavior = MultipleHandlerBehavior.Separated;
    opts.Durability.MessageIdentity = MessageIdentity.IdAndDestination;

    opts.Durability.InboxStaleTime = 30.Minutes();
    opts.Durability.OutboxStaleTime = 30.Minutes();
    opts.Policies.AutoApplyTransactions();

    if (useEfCore)
    {
       
      opts.UseEntityFrameworkCoreTransactions();
        
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
    
    opts.Services.AddResourceSetupOnStartup();

    return opts;
}
}