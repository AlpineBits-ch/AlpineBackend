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
    /// <summary>
    /// Cooldowns for a saga write that lost the optimistic-concurrency race.
    ///
    /// <para><b>The length matters more than the values.</b> In the worst case a fan-out's replies
    /// all land together, and because the storage serialises them exactly one writer wins per round
    /// - so the last straggler in an N-way fan-out needs N-1 retries. Fewer attempts than that and
    /// the tail is dead-lettered, which drops the acknowledgement and reproduces the very bug this
    /// policy exists to fix, only under load instead of always. <c>SagaFanOutRetryTests</c> pins the
    /// list against the widest saga so adding a ninth participant fails a test rather than
    /// silently eating one reply in eight.</para>
    ///
    /// <para>The delays climb steeply rather than staying flat because the cooldown is measured from
    /// each message's own failure, and identical short delays keep a contending set in lockstep -
    /// they collide, all wait 50ms, and collide again. Spreading them apart lets the herd
    /// disperse. Wolverine's cooldown list takes fixed values with no jitter, so separation is the
    /// only lever available here; if contention ever outgrows it, the answer is to stop accumulating
    /// in saga state rather than to add more delays.</para>
    ///
    /// <para>Total worst case is a little under 20 seconds, which sits far inside both saga
    /// deadlines (an hour each).</para>
    /// </summary>
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
    // throws when that matches no row. Two responses for the same saga arriving together - which is
    // the normal case for a fan-out, where eight services answer at once - therefore end with one
    // committing and the other throwing.
    //
    // Without this policy nothing retried that throw, so the losing ack was simply discarded. The
    // symptom was a fan-out countdown reading 7,7,6,5,4,3,2 instead of 7,6,5,4,3,2,1: two handlers
    // had both loaded the saga at eight pending, each removed itself, and only one write survived.
    // The saga could then never reach zero, so a completed export resolved as Partial naming a
    // service that had in fact answered, and its fragment was lost with it.
    //
    // Retrying is the whole fix: the retry re-executes the handler, which re-reads the saga, so it
    // removes its own service from the *current* pending list rather than a stale copy. This is
    // enforced by the database rather than by a listener setting, so unlike making the saga queues
    // sequential it stays correct with more than one replica of the service.
    //
    // Declared first so it is matched before the broader NpgsqlException policy below. The cooldown
    // is short because contention is short: the window is one saga row write, and the losing writer
    // only has to wait for the winner to commit.
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