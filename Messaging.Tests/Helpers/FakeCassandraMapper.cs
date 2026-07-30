using Cassandra.Mapping;
using Messaging.Domain.Entities;

namespace Messaging.Tests.Helpers;

/// <summary>
/// Hand-rolled Cassandra <see cref="IMapper"/> for unit testing ScyllaMessageRepository without a
/// live cluster - mirrors this suite's no-mocking-framework convention (see FakeMessageBus).
/// </summary>
public class FakeCassandraMapper : IMapper
{
    /// <summary>Rows the next messages fetch returns, in wire order (newest-first).</summary>
    public List<Message> Messages { get; set; } = new();

    /// <summary>Rows every reactions fetch returns, keyed by message id.</summary>
    public Dictionary<string, List<Reaction>> ReactionsByMessageId { get; set; } = new();

    /// <summary>Every (cql, args) pair passed to FetchAsync, in call order.</summary>
    public List<(string Cql, object[] Args)> Fetches { get; } = new();

    public List<object> Inserted { get; } = new();

    public Task<IEnumerable<T>> FetchAsync<T>(string cql, params object[] args)
    {
        Fetches.Add((cql, args));

        if (typeof(T) == typeof(Message))
            return Task.FromResult(ConsumeOnce(Messages.Cast<T>()));

        if (typeof(T) == typeof(Reaction))
        {
            var messageId = args.Length > 1 ? args[1] as string : null;
            var reactions = messageId is not null && ReactionsByMessageId.TryGetValue(messageId, out var found)
                ? found
                : new List<Reaction>();
            return Task.FromResult(ConsumeOnce(reactions.Cast<T>()));
        }

        return Task.FromResult(ConsumeOnce(Enumerable.Empty<T>()));
    }

    public Task<T> FirstOrDefaultAsync<T>(string cql, params object[] args)
    {
        if (typeof(T) == typeof(Message))
        {
            var id = args.FirstOrDefault() as string;
            return Task.FromResult((T)(object)Messages.FirstOrDefault(m => m.Id == id)!);
        }

        return Task.FromResult(default(T)!);
    }

    public Task InsertAsync<T>(T poco, CqlQueryOptions queryOptions)
    {
        if (poco is not null) Inserted.Add(poco);
        if (poco is Message message) Messages.Insert(0, message);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Wraps a sequence so that enumerating it drains it, the way the driver's RowSet behaves.
    /// </summary>
    private static IEnumerable<T> ConsumeOnce<T>(IEnumerable<T> items)
    {
        var queue = new Queue<T>(items);
        return Drain(queue);

        static IEnumerable<T> Drain(Queue<T> queue)
        {
            while (queue.Count > 0) yield return queue.Dequeue();
        }
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Everything below is unused by ScyllaMessageRepository and throws on contact.
    // ══════════════════════════════════════════════════════════════════════════

    public AppliedInfo<T> DeleteIf<T>(Cql cql) => throw new NotImplementedException();
    public AppliedInfo<T> DeleteIf<T>(string cql, params object[] args) => throw new NotImplementedException();
    public AppliedInfo<T> ExecuteConditional<T>(ICqlBatch batch) => throw new NotImplementedException();
    public AppliedInfo<T> ExecuteConditional<T>(ICqlBatch batch, string executionProfile) => throw new NotImplementedException();
    public AppliedInfo<T> InsertIfNotExists<T>(T poco, CqlQueryOptions queryOptions) => throw new NotImplementedException();
    public AppliedInfo<T> InsertIfNotExists<T>(T poco, bool insertNulls, CqlQueryOptions queryOptions) => throw new NotImplementedException();
    public AppliedInfo<T> InsertIfNotExists<T>(T poco, bool insertNulls, int? ttl, CqlQueryOptions queryOptions) => throw new NotImplementedException();
    public AppliedInfo<T> InsertIfNotExists<T>(T poco, string executionProfile, CqlQueryOptions queryOptions) => throw new NotImplementedException();
    public AppliedInfo<T> InsertIfNotExists<T>(T poco, string executionProfile, bool insertNulls, CqlQueryOptions queryOptions) => throw new NotImplementedException();
    public AppliedInfo<T> InsertIfNotExists<T>(T poco, string executionProfile, bool insertNulls, int? ttl, CqlQueryOptions queryOptions) => throw new NotImplementedException();
    public AppliedInfo<T> UpdateIf<T>(Cql cql) => throw new NotImplementedException();
    public AppliedInfo<T> UpdateIf<T>(string cql, params object[] args) => throw new NotImplementedException();
    public ICqlBatch CreateBatch() => throw new NotImplementedException();
    public ICqlBatch CreateBatch(Cassandra.BatchType batchType) => throw new NotImplementedException();
    public IPage<T> FetchPage<T>(Cql cql) => throw new NotImplementedException();
    public IPage<T> FetchPage<T>(CqlQueryOptions queryOptions) => throw new NotImplementedException();
    public IPage<T> FetchPage<T>(int pageSize, byte[] pagingState, string query, object[] args) => throw new NotImplementedException();
    public IEnumerable<T> Fetch<T>(Cql cql) => throw new NotImplementedException();
    public IEnumerable<T> Fetch<T>(CqlQueryOptions queryOptions) => throw new NotImplementedException();
    public IEnumerable<T> Fetch<T>(string cql, params object[] args) => throw new NotImplementedException();
    public Task DeleteAsync<T>(Cql cql) => throw new NotImplementedException();
    public Task DeleteAsync<T>(string cql, params object[] args) => throw new NotImplementedException();
    public Task DeleteAsync<T>(T poco, CqlQueryOptions queryOptions) => throw new NotImplementedException();
    public Task DeleteAsync<T>(T poco, string executionProfile, CqlQueryOptions queryOptions) => throw new NotImplementedException();
    public Task ExecuteAsync(Cql cql) => throw new NotImplementedException();
    public Task ExecuteAsync(ICqlBatch batch) => throw new NotImplementedException();
    public Task ExecuteAsync(ICqlBatch batch, string executionProfile) => throw new NotImplementedException();
    public Task ExecuteAsync(string cql, params object[] args) => throw new NotImplementedException();
    public Task InsertAsync<T>(T poco, bool insertNulls, CqlQueryOptions queryOptions) => throw new NotImplementedException();
    public Task InsertAsync<T>(T poco, bool insertNulls, int? ttl, CqlQueryOptions queryOptions) => throw new NotImplementedException();
    public Task InsertAsync<T>(T poco, string executionProfile, CqlQueryOptions queryOptions) => throw new NotImplementedException();
    public Task InsertAsync<T>(T poco, string executionProfile, bool insertNulls, CqlQueryOptions queryOptions) => throw new NotImplementedException();
    public Task InsertAsync<T>(T poco, string executionProfile, bool insertNulls, int? ttl, CqlQueryOptions queryOptions) => throw new NotImplementedException();
    public Task UpdateAsync<T>(Cql cql) => throw new NotImplementedException();
    public Task UpdateAsync<T>(string cql, params object[] args) => throw new NotImplementedException();
    public Task UpdateAsync<T>(T poco, CqlQueryOptions queryOptions) => throw new NotImplementedException();
    public Task UpdateAsync<T>(T poco, string executionProfile, CqlQueryOptions queryOptions) => throw new NotImplementedException();
    public Task<AppliedInfo<T>> DeleteIfAsync<T>(Cql cql) => throw new NotImplementedException();
    public Task<AppliedInfo<T>> DeleteIfAsync<T>(string cql, params object[] args) => throw new NotImplementedException();
    public Task<AppliedInfo<T>> ExecuteConditionalAsync<T>(ICqlBatch batch) => throw new NotImplementedException();
    public Task<AppliedInfo<T>> ExecuteConditionalAsync<T>(ICqlBatch batch, string executionProfile) => throw new NotImplementedException();
    public Task<AppliedInfo<T>> InsertIfNotExistsAsync<T>(T poco, CqlQueryOptions queryOptions) => throw new NotImplementedException();
    public Task<AppliedInfo<T>> InsertIfNotExistsAsync<T>(T poco, bool insertNulls, CqlQueryOptions queryOptions) => throw new NotImplementedException();
    public Task<AppliedInfo<T>> InsertIfNotExistsAsync<T>(T poco, bool insertNulls, int? ttl, CqlQueryOptions queryOptions) => throw new NotImplementedException();
    public Task<AppliedInfo<T>> InsertIfNotExistsAsync<T>(T poco, string executionProfile, CqlQueryOptions queryOptions) => throw new NotImplementedException();
    public Task<AppliedInfo<T>> InsertIfNotExistsAsync<T>(T poco, string executionProfile, bool insertNulls, CqlQueryOptions queryOptions) => throw new NotImplementedException();
    public Task<AppliedInfo<T>> InsertIfNotExistsAsync<T>(T poco, string executionProfile, bool insertNulls, int? ttl, CqlQueryOptions queryOptions) => throw new NotImplementedException();
    public Task<AppliedInfo<T>> UpdateIfAsync<T>(Cql cql) => throw new NotImplementedException();
    public Task<AppliedInfo<T>> UpdateIfAsync<T>(string cql, params object[] args) => throw new NotImplementedException();
    public Task<IPage<T>> FetchPageAsync<T>(Cql cql) => throw new NotImplementedException();
    public Task<IPage<T>> FetchPageAsync<T>(CqlQueryOptions queryOptions) => throw new NotImplementedException();
    public Task<IPage<T>> FetchPageAsync<T>(int pageSize, byte[] pagingState, string query, object[] args) => throw new NotImplementedException();
    public Task<IEnumerable<T>> FetchAsync<T>(Cql cql) => throw new NotImplementedException();
    public Task<IEnumerable<T>> FetchAsync<T>(CqlQueryOptions queryOptions) => throw new NotImplementedException();
    public Task<T> FirstAsync<T>(Cql cql) => throw new NotImplementedException();
    public Task<T> FirstAsync<T>(string cql, params object[] args) => throw new NotImplementedException();
    public Task<T> FirstOrDefaultAsync<T>(Cql cql) => throw new NotImplementedException();
    public Task<T> SingleAsync<T>(Cql cql) => throw new NotImplementedException();
    public Task<T> SingleAsync<T>(string cql, params object[] args) => throw new NotImplementedException();
    public Task<T> SingleOrDefaultAsync<T>(Cql cql) => throw new NotImplementedException();
    public Task<T> SingleOrDefaultAsync<T>(string cql, params object[] args) => throw new NotImplementedException();
    public T First<T>(Cql cql) => throw new NotImplementedException();
    public T First<T>(string cql, params object[] args) => throw new NotImplementedException();
    public T FirstOrDefault<T>(Cql cql) => throw new NotImplementedException();
    public T FirstOrDefault<T>(string cql, params object[] args) => throw new NotImplementedException();
    public T Single<T>(Cql cql) => throw new NotImplementedException();
    public T Single<T>(string cql, params object[] args) => throw new NotImplementedException();
    public T SingleOrDefault<T>(Cql cql) => throw new NotImplementedException();
    public T SingleOrDefault<T>(string cql, params object[] args) => throw new NotImplementedException();
    public TDatabase ConvertCqlArgument<TValue, TDatabase>(TValue value) => throw new NotImplementedException();
    public void Delete<T>(Cql cql) => throw new NotImplementedException();
    public void Delete<T>(string cql, params object[] args) => throw new NotImplementedException();
    public void Delete<T>(T poco, CqlQueryOptions queryOptions) => throw new NotImplementedException();
    public void Delete<T>(T poco, string executionProfile, CqlQueryOptions queryOptions) => throw new NotImplementedException();
    public void Execute(Cql cql) => throw new NotImplementedException();
    public void Execute(ICqlBatch batch) => throw new NotImplementedException();
    public void Execute(ICqlBatch batch, string executionProfile) => throw new NotImplementedException();
    public void Execute(string cql, params object[] args) => throw new NotImplementedException();
    public void Insert<T>(T poco, CqlQueryOptions queryOptions) => throw new NotImplementedException();
    public void Insert<T>(T poco, bool insertNulls, CqlQueryOptions queryOptions) => throw new NotImplementedException();
    public void Insert<T>(T poco, bool insertNulls, int? ttl, CqlQueryOptions queryOptions) => throw new NotImplementedException();
    public void Insert<T>(T poco, string executionProfile, CqlQueryOptions queryOptions) => throw new NotImplementedException();
    public void Insert<T>(T poco, string executionProfile, bool insertNulls, CqlQueryOptions queryOptions) => throw new NotImplementedException();
    public void Insert<T>(T poco, string executionProfile, bool insertNulls, int? ttl, CqlQueryOptions queryOptions) => throw new NotImplementedException();
    public void Update<T>(Cql cql) => throw new NotImplementedException();
    public void Update<T>(string cql, params object[] args) => throw new NotImplementedException();
    public void Update<T>(T poco, CqlQueryOptions queryOptions) => throw new NotImplementedException();
    public void Update<T>(T poco, string executionProfile, CqlQueryOptions queryOptions) => throw new NotImplementedException();
}
