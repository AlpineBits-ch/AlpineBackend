using AppEnvironment;
using Cassandra;
using Cassandra.Mapping;
using Messaging.Domain.Entities;

namespace Messaging.Infrastructure.Persistence;

public class ScyllaContext : IAsyncDisposable
{
    private readonly ISession _session;

    private ScyllaContext(ISession session, IMapper mapper)
    {
        _session = session;
        Mapper = mapper;
    }

    public IMapper Mapper { get; private set; }

    public async ValueTask DisposeAsync()
    {
        await _session.ShutdownAsync();
    }

    public static ScyllaContext CreateDebug()
    {
        return new ScyllaContext(default, default);
    }

    public static async Task<ScyllaContext> CreateAsync()
    {
        var cluster = Cluster.Builder()
            .AddContactPoints(Env.Scylla.Host)
            .WithCredentials(Env.Scylla.UserName, Env.Scylla.Password)
            .WithPoolingOptions(new PoolingOptions().SetCoreConnectionsPerHost(HostDistance.Local, 10))
            .Build();

        var session = await cluster.ConnectAsync();
        session.CreateKeyspaceIfNotExists("messaging");
        session.ChangeKeyspace("messaging");


        await RunMigrationsAsync(session);
        await session.UserDefinedTypes.DefineAsync(
            UdtMap.For<MinimalAttachment>("minimal_attachment")
                .Map(a => a.Id, "id")
                .Map(a => a.FileName, "file_name")
                .Map(a => a.ContentType, "content_type")
                .Map(a => a.CreatedAt, "created_at")
                .Map(a => a.UpdatedAt, "updated_at")
                .Map(a => a.ThumbnailUrl, "thumbnail_url")
        );

        var config = new MappingConfiguration();


        config.Define(
            new Map<Reaction>()
                .TableName("reactions")
                .PartitionKey(r => r.ContextId)
                .PartitionKey(r => r.MessageId)
                .ClusteringKey(r => r.Emoji, SortOrder.Ascending)
                .ClusteringKey(r => r.UserId, SortOrder.Ascending)
                .Column(r => r.ContextId, cm => cm.WithName("context_id"))
                .Column(r => r.MessageId, cm => cm.WithName("message_id"))
                .Column(r => r.Emoji, cm => cm.WithName("emoji"))
                .Column(r => r.UserId, cm => cm.WithName("user_id"))
                .Column(r => r.CreatedAt, cm => cm.WithName("created_at"))
                .Column(r => r.ConversationId, cm => cm.WithName("conversation_id"))
                .Column(r => r.ChannelId, cm => cm.WithName("channel_id")));
        
        config.Define(
            new Map<Message>()
                .TableName("messages")
                .PartitionKey(m => m.ContextId)
                .ClusteringKey(m => m.CreatedAt, SortOrder.Descending)
                .ClusteringKey(m => m.Id, SortOrder.Ascending)
                .Column(m => m.ContextId, cm => cm.WithName("context_id"))
                .Column(m => m.Id, cm => cm.WithName("message_id"))
                .Column(m => m.AuthorId, cm => cm.WithName("author_id"))
                .Column(m => m.Content, cm => cm.WithName("content"))
                .Column(m => m.CreatedAt, cm => cm.WithName("created_at"))
                .Column(m => m.UpdatedAt, cm => cm.WithName("updated_at"))
                .Column(m => m.InReplyTo, cm => cm.WithName("in_reply_to"))
                .Column(m => m.SenderDeviceId, cm => cm.WithName("sender_device_id"))
                .Column(m => m.MlsEpoch, cm => cm.WithName("mls_epoch"))
                .Column(m => m.MlsSequenceNumber, cm => cm.WithName("mls_sequence_number"))
                .Column(m => m.ConversationId, cm => cm.WithName("conversation_id"))
                .Column(m => m.ChannelId, cm => cm.WithName("channel_id"))
                .Column(m => m.Mentions, cm => cm.WithName("mentions"))
                .Column(m => m.ReadReceipts, cm => cm.WithName("read_receipts"))
                .Column(m => m.Type, cm => cm.WithName("message_type").WithDbType<string>())
                .Column(m => m.Attachments, cm => cm.WithName("attachments").AsFrozen())
                .Column(m => m.EncryptionState, cm => cm.WithName("encryption_state").WithDbType<string>()));

        config.Define(
            new Map<MinimalAttachment>()
                .Column(a => a.Id, cm => cm.WithName("id"))
                .Column(a => a.FileName, cm => cm.WithName("file_name"))
                .Column(a => a.ContentType, cm => cm.WithName("content_type"))
                .Column(a => a.CreatedAt, cm => cm.WithName("created_at"))
                .Column(a => a.UpdatedAt, cm => cm.WithName("updated_at"))
                .Column(a => a.ThumbnailUrl, cm => cm.WithName("thumbnail_url")));

        var mapper = new Mapper(session, config);
        return new ScyllaContext(session, mapper);
    }

    private static async Task RunMigrationsAsync(ISession session)
    {
        await session.ExecuteAsync(new SimpleStatement(
            "CREATE TYPE IF NOT EXISTS minimal_attachment (" +
            "  id text," +
            "  created_at timestamp, " +
            "  updated_at timestamp, " +
            "  file_name text," +
            "  content_type text," +
            "  thumbnail_url text" +
            ");"));

        await session.ExecuteAsync(new SimpleStatement(
            "CREATE TABLE IF NOT EXISTS messages (" +
            "context_id text, " +
            "created_at timestamp, " +
            "message_id text, " +
            "author_id text, " +
            "encryption_state text," +
            "content blob, " +
            "conversation_id text," +
            "in_reply_to text," +
            "channel_id text," +
            "message_type text, " +
            "sender_device_id text, " + // which device sent this
            "mls_epoch bigint, " + // epoch when message was sent
            "mls_sequence_number bigint, " +
            "mentions list<text>, " +
            "attachments list<frozen<minimal_attachment>>, " +
            "updated_at timestamp, " +
            "PRIMARY KEY (context_id, created_at, message_id)" +
            ") WITH CLUSTERING ORDER BY (created_at DESC, message_id ASC);"));


        await session.ExecuteAsync(new SimpleStatement(@"
            CREATE TABLE IF NOT EXISTS reactions (
            context_id   text,
            message_id   text,
            emoji        text,
            user_id      text,
            conversation_id text,
            channel_id   text,
            created_at   timestamp,
            PRIMARY KEY ((context_id, message_id), emoji, user_id)
    )
"));


        await session.ExecuteAsync(new SimpleStatement("CREATE INDEX IF NOT EXISTS ON messages (message_id);"));
        try
        {
            await session.ExecuteAsync(new SimpleStatement("ALTER TABLE messages ADD in_reply_to text;"));
        }
        catch (InvalidQueryException)
        {
            // The column already exists. Log the event and safely ignore it.
        }

        await session.ExecuteAsync(new SimpleStatement("CREATE INDEX IF NOT EXISTS ON messages (in_reply_to);"));
        try
        {
            await session.ExecuteAsync(new SimpleStatement(
                "ALTER TABLE messages ADD sender_device_id text;"));
        }
        catch (InvalidQueryException)
        {
        }

        try
        {
            await session.ExecuteAsync(new SimpleStatement(
                "ALTER TABLE messages ADD mls_epoch bigint;"));
        }
        catch (InvalidQueryException)
        {
        }

        try
        {
            await session.ExecuteAsync(new SimpleStatement(
                "ALTER TABLE messages ADD mls_sequence_number bigint;"));
        }
        catch (InvalidQueryException)
        {
        }
    }
}