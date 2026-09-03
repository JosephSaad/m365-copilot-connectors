// ---------------------------------------------------------------------------
// MongoPushSource.cs
// Reads a MongoDB collection and yields one item per document.
//
// Three refusals live here, and each one is a case where reading on would be
// silently wrong rather than loudly broken:
//
// 1. A VIEW rather than a collection. A Mongo view can carry $redact against
//    $$USER_ROLES, or a $match on the caller, which is per-caller enforcement:
//    the documents this crawl reads would be the crawl identity's documents and
//    not every reader's. That is the refusal CDP-1/CDP-2 make for a Ranger mask
//    and the Oracle connector makes for a VPD policy. The driver cannot tell a
//    redacting view from a plain one, so the refusal is on views as a class.
//
// 2. An ENCRYPTED FIELD. CSFLE and Queryable Encryption store ciphertext, which
//    indexes without complaint and is useless to every reader. This is not a
//    leak - it is the failure mode that wastes a pilot - and nothing downstream
//    can tell ciphertext from text, so it has to be caught here.
//
// 3. NO RESUME MARKER. An ObjectId encodes creation time, not modification
//    time, so it cannot carry a watermark. This source reads in full every run
//    and says so, rather than implying an incremental read it cannot perform.
//    RequiresOrderedCommit is false for that reason: there is no marker for
//    out-of-order completion to move past.
// ---------------------------------------------------------------------------

namespace MongoGraphPush;

using System.Runtime.CompilerServices;
using MongoDB.Bson;
using MongoDB.Driver;
using PushCore;

/// <summary>Reads a MongoDB collection and yields one item per document.</summary>
public sealed class MongoPushSource : IPushSource
{
    private readonly PushSourceContext context;

    private int skipped;

    /// <summary>Initializes a new instance of the <see cref="MongoPushSource"/> class.</summary>
    /// <param name="context">Configuration, credential and logger.</param>
    public MongoPushSource(PushSourceContext context)
    {
        this.context = context;
    }

    /// <inheritdoc/>
    public int Skipped => this.skipped;

    /// <inheritdoc/>
    /// <remarks>See the file header, refusal 3: no marker, so nothing to outrun.</remarks>
    public bool RequiresOrderedCommit => false;

    /// <inheritdoc/>
    public async IAsyncEnumerable<PushItem> ReadAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        PushOptions options = this.context.Options;

        string secret = string.Empty;
        string? name = options.KeyVault.SecretName(MongoRecordsPushConnector.PasswordKey);

        if (!string.IsNullOrWhiteSpace(name) && this.context.Secrets is not null)
        {
            secret = await this.context.Secrets.GetSecretAsync(name, cancellationToken);
        }

        MongoUrl url = MongoUrl.Create(options.DataSource.Server);
        MongoClientSettings settings = MongoClientSettings.FromUrl(url);

        if (!string.IsNullOrWhiteSpace(options.DataSource.SqlUserId) && secret.Length > 0)
        {
            settings.Credential = MongoCredential.CreateCredential(
                options.DataSource.Database, options.DataSource.SqlUserId, secret);
        }

        settings.ConnectTimeout = TimeSpan.FromSeconds(options.DataSource.ConnectTimeoutSeconds);

        // A crawl is a long read of the whole corpus and has no freshness
        // requirement a secondary cannot meet, so it should not compete with
        // application traffic on the primary.
        settings.ReadPreference = ReadPreference.SecondaryPreferred;

        var client = new MongoClient(settings);
        IMongoDatabase database = client.GetDatabase(options.DataSource.Database);

        await this.RefuseViewAsync(database, options.Source.ItemView, cancellationToken);

        IMongoCollection<BsonDocument> collection =
            database.GetCollection<BsonDocument>(options.Source.ItemView);

        FilterDefinition<BsonDocument> filter = options.DataSource.SoftDeleteEnabled
            ? Builders<BsonDocument>.Filter.Ne("isDeleted", true)
            : Builders<BsonDocument>.Filter.Empty;

        var find = collection.Find(filter).Sort(Builders<BsonDocument>.Sort.Ascending("_id"));

        if (options.Source.MaxItems > 0)
        {
            find = find.Limit(options.Source.MaxItems);
        }

        using IAsyncCursor<BsonDocument> cursor = await find.ToCursorAsync(cancellationToken);

        int ordinal = 0;

        while (await cursor.MoveNextAsync(cancellationToken))
        {
            foreach (BsonDocument document in cursor.Current)
            {
                ordinal++;

                PushItem? mapped;

                try
                {
                    mapped = MongoDocumentMapper.Map(document, options);
                }
                catch (Exception ex)
                {
                    // The ordinal locates the document without logging any of
                    // its content, which is the same contract the relational
                    // sources honour.
                    throw new InvalidOperationException(
                        $"Document {ordinal} could not be mapped. " +
                        "The document's content is deliberately not logged; find it in the collection by ordinal.",
                        ex);
                }

                if (mapped is null)
                {
                    this.skipped++;
                    continue;
                }

                yield return mapped;
            }
        }
    }

    /// <inheritdoc/>
    public ValueTask OnItemCommittedAsync(PushItem item, CancellationToken cancellationToken)
    {
        // Nothing to record: no marker. See the file header.
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc/>
    public ValueTask OnCrawlCompletedAsync(CancellationToken cancellationToken)
    {
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc/>
    public ValueTask DisposeAsync()
    {
        // The cursor is scoped to the iterator and the client holds a pooled
        // connection it closes itself.
        return ValueTask.CompletedTask;
    }

    /// <summary>Refuses a view, which may enforce per caller. See the file header.</summary>
    private async Task RefuseViewAsync(
        IMongoDatabase database, string name, CancellationToken cancellationToken)
    {
        var filter = new BsonDocument("name", name);

        using IAsyncCursor<BsonDocument> cursor = await database.ListCollectionsAsync(
            new ListCollectionsOptions { Filter = filter }, cancellationToken);

        List<BsonDocument> found = await cursor.ToListAsync(cancellationToken);

        if (found.Count == 0)
        {
            throw new InvalidOperationException(
                $"Collection '{name}' does not exist in database '{database.DatabaseNamespace.DatabaseName}'. " +
                "The run stops rather than indexing nothing and reporting success.");
        }

        string type = found[0].GetValue("type", "collection").AsString;

        if (!string.Equals(type, "view", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        throw new InvalidOperationException(
            $"'{name}' is a MongoDB view, and this connector reads collections only. A view can apply " +
            "$redact against $$USER_ROLES or match on the caller, which means the documents this crawl " +
            "reads would be the crawl identity's documents rather than every reader's - and indexing them " +
            "would publish one identity's view to everyone granted the item. The driver cannot distinguish " +
            "a redacting view from a plain one, so views are refused as a class. Point this connector at " +
            "the underlying collection, or materialise the view into one. There is no setting that " +
            "disables this.");
    }
}
