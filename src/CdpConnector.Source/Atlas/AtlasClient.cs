// ---------------------------------------------------------------------------
// AtlasClient.cs
// Reading the Atlas catalogue over SPNEGO, as this service's identity.
//
// The shape mirrors RangerPolicyClient deliberately: same Negotiate client,
// same treatment of a 401 or 403 as a credential failure rather than an
// ingestion one, same refusal to put a response body in a log. Two components
// talking to two governance services should not have two personalities.
//
// Paging is the thing to get right. Atlas's basic search takes an offset and a
// limit and will happily return fewer than asked for; the loop below stops when
// a page comes back short OR when a page repeats nothing new, because a server
// that ignores the offset would otherwise spin for ever on page one.
//
// The base URL is required with no default, and that is deliberate. Atlas's
// port varies between CDP topologies and by whether Knox fronts it, and a
// guessed default that happens to be wrong produces a connection error at the
// least helpful moment. Better to make the operator state it.
// ---------------------------------------------------------------------------

namespace CdpConnector.Source.Atlas;

using System.Net;
using System.Text.Json;
using PushCore;
using Serilog;

/// <summary>Reads entities and lineage from Apache Atlas.</summary>
public sealed class AtlasClient : IDisposable
{
    /// <summary>A ceiling on the pager, so a server that ignores the offset cannot spin for ever.</summary>
    private const int MaxEntitiesScanned = 1_000_000;

    /// <summary>How far to walk a lineage graph: table, the step that wrote it, and the table that fed the step.</summary>
    private const int LineageDepth = 2;

    private readonly HttpClient http;
    private readonly string baseUrl;
    private readonly ILogger log;
    private readonly bool ownsClient;

    /// <summary>Initializes a new instance of the <see cref="AtlasClient"/> class.</summary>
    /// <param name="baseUrl">The Atlas base URL, without the /api/atlas/v2 suffix.</param>
    /// <param name="log">Where to report progress.</param>
    public AtlasClient(string baseUrl, ILogger log)
        : this(baseUrl, Hdfs.WebHdfsClient.CreateNegotiatingClient(), log, ownsClient: true)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="AtlasClient"/> class with a supplied client.</summary>
    /// <param name="baseUrl">The Atlas base URL, without the /api/atlas/v2 suffix.</param>
    /// <param name="http">The client to use. A test supplies one over a fake handler.</param>
    /// <param name="log">Where to report progress.</param>
    /// <param name="ownsClient">True when disposing this should dispose the client.</param>
    public AtlasClient(string baseUrl, HttpClient http, ILogger log, bool ownsClient = false)
    {
        this.baseUrl = baseUrl.TrimEnd('/');
        this.http = http;
        this.log = log;
        this.ownsClient = ownsClient;
    }

    /// <summary>Reads every entity of one Atlas type, a page at a time.</summary>
    /// <param name="typeName">The Atlas type, for example hive_table.</param>
    /// <param name="pageSize">How many to ask for per request.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The entities, deleted ones excluded.</returns>
    public async Task<IReadOnlyList<AtlasEntity>> SearchAsync(
        string typeName, int pageSize, CancellationToken cancellationToken)
    {
        var found = new List<AtlasEntity>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        // Atlas caps a page at atlas.search.maxlimit, 10,000 by default, and
        // rejects a negative one.
        int limit = Math.Clamp(pageSize, 1, 10000);

        for (int offset = 0; ; offset += limit)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // GET rather than POST, deliberately. Atlas installs its own CSRF
            // filter in front of non-GET REST calls, and whether it demands a
            // header depends on atlas.rest-csrf.enabled at the cluster - a
            // configuration this connector cannot see and should not depend on.
            // The GET form of basic search takes the same parameters.
            string query =
                $"/api/atlas/v2/search/basic?typeName={Uri.EscapeDataString(typeName)}" +
                $"&excludeDeletedEntities=true&limit={limit}&offset={offset}";

            using JsonDocument page = await this.GetJsonAsync(query, cancellationToken);

            if (!page.RootElement.TryGetProperty("entities", out JsonElement entities) ||
                entities.ValueKind != JsonValueKind.Array)
            {
                break;
            }

            int readable = 0;
            int added = 0;

            foreach (JsonElement element in entities.EnumerateArray())
            {
                AtlasEntity entity = ReadHeader(element);

                // Atlas SCRUBS a hit the caller may not read rather than
                // removing it: Ranger's authorizer blanks the header in place
                // and sets its GUID to "-1", so the array length is unchanged
                // and an unreadable entity arrives as an empty shell. Indexing
                // one would put a nameless item in the catalogue.
                if (entity.Guid.Length == 0 || string.Equals(entity.Guid, "-1", StringComparison.Ordinal))
                {
                    continue;
                }

                readable++;

                if (entity.IsActive && seen.Add(entity.Guid))
                {
                    added++;
                    found.Add(entity);
                }
            }

            int returned = entities.GetArrayLength();

            if (returned == 0)
            {
                break;
            }

            // These two conditions look alike and are not, and conflating them
            // truncates the catalogue silently.
            //
            // A page that returned entities THIS CALLER MAY READ and yet added
            // nothing new is a server ignoring the offset - the loop would spin
            // on page one for ever - so it stops.
            //
            // A page that added nothing because every entity in it was scrubbed
            // is the opposite: the offset advanced correctly and the pages after
            // it are still to come. One restricted database whose tables sort
            // together fills a whole page that way, and stopping there would
            // drop the rest of the lake while reporting a clean crawl.
            if (readable > 0 && added == 0)
            {
                break;
            }

            if (returned < limit)
            {
                break;
            }

            // A server that answers every offset with a full page of scrubbed
            // entities satisfies neither stop condition. Nothing on a real
            // cluster does that, which is exactly why it is worth bounding.
            if (offset + limit >= MaxEntitiesScanned)
            {
                this.log.Warning(
                    "Stopped reading {TypeName} after {Scanned} entities without reaching the end of the " +
                    "catalogue. This is a ceiling on a runaway pager, not a real catalogue size; check " +
                    "Atlas is honouring the offset parameter.",
                    typeName,
                    offset + limit);

                break;
            }
        }

        this.log.Information("Atlas returned {Count} live {TypeName} entit(y/ies).", found.Count, typeName);

        return found;
    }

    /// <summary>Fills in one entity's detail: its columns, classifications and terms.</summary>
    /// <param name="entity">The entity to enrich, as the search returned it.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>A task for the operation.</returns>
    public async Task EnrichAsync(AtlasEntity entity, CancellationToken cancellationToken)
    {
        using JsonDocument? document = await this.TryGetJsonAsync(
            $"/api/atlas/v2/entity/guid/{Uri.EscapeDataString(entity.Guid)}", cancellationToken);

        if (document is null || !document.RootElement.TryGetProperty("entity", out JsonElement element))
        {
            return;
        }

        if (element.TryGetProperty("attributes", out JsonElement attributes))
        {
            entity.Description = FirstNonEmpty(
                entity.Description, Text(attributes, "description"), Text(attributes, "comment"));
            entity.Comment = FirstNonEmpty(entity.Comment, Text(attributes, "comment"));
            entity.Owner = FirstNonEmpty(entity.Owner, Text(attributes, "owner"));
        }

        if (element.TryGetProperty("updateTime", out JsonElement updated) &&
            updated.ValueKind == JsonValueKind.Number)
        {
            entity.UpdatedUtc = DateTimeOffset.FromUnixTimeMilliseconds(updated.GetInt64());
        }

        AddNames(element, "classifications", "typeName", entity.Classifications);
        AddNames(element, "meanings", "displayText", entity.Terms);

        if (element.TryGetProperty("relationshipAttributes", out JsonElement relationships) &&
            relationships.TryGetProperty("columns", out JsonElement columns) &&
            columns.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement column in columns.EnumerateArray())
            {
                string name = Text(column, "displayText");

                if (name.Length > 0)
                {
                    entity.Columns.Add(name);
                }
            }
        }
    }

    /// <summary>
    /// Reads the datasets either side of an entity in the lineage graph.
    ///
    /// It returns NEIGHBOURS rather than names, and the caller decides which of
    /// them the reader of this entry may be told about. That split is the point:
    /// the names come from Atlas, which on a stock CDP cluster shows every
    /// authenticated user every entity, and the entry they are written onto is
    /// granted to the far smaller group Ranger allows on one table.
    ///
    /// The walk goes THROUGH transformation nodes rather than naming them. Hive
    /// records table -> hive_process -> table, so the immediate neighbour of a
    /// table is never another table; it is the step that wrote it, whose name is
    /// the query text.
    /// </summary>
    /// <param name="guid">The entity to describe.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>What feeds it and what it feeds, unfiltered.</returns>
    public async Task<(IReadOnlyList<AtlasNeighbour> Upstream, IReadOnlyList<AtlasNeighbour> Downstream)>
        LineageAsync(string guid, CancellationToken cancellationToken)
    {
        var none = (Upstream: (IReadOnlyList<AtlasNeighbour>)Array.Empty<AtlasNeighbour>(),
                    Downstream: (IReadOnlyList<AtlasNeighbour>)Array.Empty<AtlasNeighbour>());

        using JsonDocument? document = await this.TryGetJsonAsync(
            $"/api/atlas/v2/lineage/{Uri.EscapeDataString(guid)}?direction=BOTH&depth={LineageDepth}",
            cancellationToken);

        if (document is null ||
            !document.RootElement.TryGetProperty("guidEntityMap", out JsonElement map) ||
            !document.RootElement.TryGetProperty("relations", out JsonElement relations) ||
            relations.ValueKind != JsonValueKind.Array)
        {
            return none;
        }

        var feeds = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var fedBy = new Dictionary<string, List<string>>(StringComparer.Ordinal);

        foreach (JsonElement relation in relations.EnumerateArray())
        {
            string from = Text(relation, "fromEntityId");
            string to = Text(relation, "toEntityId");

            if (from.Length == 0 || to.Length == 0)
            {
                continue;
            }

            Link(feeds, from, to);
            Link(fedBy, to, from);
        }

        return (Walk(map, fedBy, guid), Walk(map, feeds, guid));
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (this.ownsClient)
        {
            this.http.Dispose();
        }
    }

    /// <summary>Reads the header fields the basic search returns for an entity.</summary>
    /// <param name="element">One element of the search's entities array.</param>
    /// <returns>The entity, without its detail.</returns>
    public static AtlasEntity ReadHeader(JsonElement element)
    {
        var entity = new AtlasEntity
        {
            Guid = Text(element, "guid"),
            TypeName = Text(element, "typeName"),
            Status = Text(element, "status"),
        };

        if (element.TryGetProperty("attributes", out JsonElement attributes))
        {
            entity.Name = Text(attributes, "name");
            entity.QualifiedName = Text(attributes, "qualifiedName");
            entity.Owner = Text(attributes, "owner");
            entity.Description = FirstNonEmpty(Text(attributes, "description"), Text(attributes, "comment"));
        }

        if (entity.Name.Length == 0)
        {
            entity.Name = Text(element, "displayText");
        }

        AddStrings(element, "classificationNames", entity.Classifications);
        AddStrings(element, "meaningNames", entity.Terms);

        return entity;
    }

    private static void Link(Dictionary<string, List<string>> edges, string from, string to)
    {
        if (!edges.TryGetValue(from, out List<string>? targets))
        {
            targets = new List<string>();
            edges[from] = targets;
        }

        targets.Add(to);
    }

    /// <summary>
    /// Follows one direction of the lineage graph until it reaches datasets.
    ///
    /// A transformation node is stepped over rather than reported, because the
    /// question a catalogue answers is "what data feeds this", not "which query
    /// ran". The visited set makes a cycle - which Atlas permits, a table that
    /// feeds a job that rewrites it - terminate rather than recur.
    /// </summary>
    /// <param name="map">The lineage response's guidEntityMap.</param>
    /// <param name="edges">Adjacency in the direction being walked.</param>
    /// <param name="start">The entity being described.</param>
    /// <returns>The datasets reached, each named once.</returns>
    private static IReadOnlyList<AtlasNeighbour> Walk(
        JsonElement map, Dictionary<string, List<string>> edges, string start)
    {
        var found = new List<AtlasNeighbour>();
        var visited = new HashSet<string>(StringComparer.Ordinal) { start };
        var queue = new Queue<string>();

        queue.Enqueue(start);

        while (queue.Count > 0)
        {
            if (!edges.TryGetValue(queue.Dequeue(), out List<string>? next))
            {
                continue;
            }

            foreach (string guid in next)
            {
                if (!visited.Add(guid))
                {
                    continue;
                }

                AtlasNeighbour? neighbour = ReadNeighbour(map, guid);

                if (neighbour is null)
                {
                    continue;
                }

                if (neighbour.IsTransformation)
                {
                    queue.Enqueue(guid);
                }
                else if (!found.Any(other => string.Equals(
                    other.QualifiedName, neighbour.QualifiedName, StringComparison.OrdinalIgnoreCase)))
                {
                    found.Add(neighbour);
                }
            }
        }

        return found;
    }

    private static AtlasNeighbour? ReadNeighbour(JsonElement map, string guid)
    {
        if (guid.Length == 0 || !map.TryGetProperty(guid, out JsonElement entity))
        {
            return null;
        }

        string qualified = entity.TryGetProperty("attributes", out JsonElement attributes)
            ? Text(attributes, "qualifiedName")
            : string.Empty;

        return new AtlasNeighbour
        {
            Guid = guid,
            TypeName = Text(entity, "typeName"),
            Name = FirstNonEmpty(Text(entity, "displayText"), qualified),
            QualifiedName = qualified,
        };
    }

    private static void Add(IList<string> into, string value)
    {
        if (value.Length > 0 && !into.Contains(value, StringComparer.OrdinalIgnoreCase))
        {
            into.Add(value);
        }
    }

    private static void AddStrings(JsonElement element, string name, IList<string> into)
    {
        if (!element.TryGetProperty(name, out JsonElement array) || array.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (JsonElement value in array.EnumerateArray())
        {
            Add(into, value.GetString() ?? string.Empty);
        }
    }

    private static void AddNames(JsonElement element, string arrayName, string field, IList<string> into)
    {
        if (!element.TryGetProperty(arrayName, out JsonElement array) || array.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (JsonElement value in array.EnumerateArray())
        {
            Add(into, Text(value, field));
        }
    }

    private static string Text(JsonElement element, string name)
    {
        return element.ValueKind == JsonValueKind.Object &&
               element.TryGetProperty(name, out JsonElement value) &&
               value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;
    }

    private static string FirstNonEmpty(params string[] candidates)
    {
        foreach (string candidate in candidates)
        {
            if (!string.IsNullOrWhiteSpace(candidate))
            {
                return candidate;
            }
        }

        return string.Empty;
    }

    /// <summary>
    /// Reads a document the crawl cannot continue without.
    ///
    /// The 400 and 404 that <see cref="TryGetJsonAsync"/> treats as "nothing to
    /// read" are fatal here, and translated so the operator is told what it
    /// actually means. On the search path a 400 is almost always a type name
    /// Atlas has never heard of, which is a configuration mistake and not an
    /// unwell Atlas - and letting a bare HttpRequestException out would say
    /// neither.
    /// </summary>
    /// <param name="path">The Atlas path to read.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The parsed document.</returns>
    private async Task<JsonDocument> GetJsonAsync(string path, CancellationToken cancellationToken)
    {
        HttpResponseMessage response;

        try
        {
            response = await this.SendAsync(HttpMethod.Get, path, content: null, cancellationToken);
        }
        catch (HttpRequestException ex)
            when (ex.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.NotFound)
        {
            throw new InvalidOperationException(
                $"Atlas rejected a catalogue search with {(int)ex.StatusCode!}. The usual cause is a name in " +
                "Settings:AtlasTypes that this cluster's Atlas does not define - check the spelling against " +
                "the type list in Atlas, and remember the names are lower case with underscores, hive_table " +
                "rather than HiveTable. The run stops rather than reporting an empty catalogue as a complete " +
                "one.",
                ex);
        }

        using (response)
        {
            await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken);

            return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        }
    }

    private async Task<JsonDocument?> TryGetJsonAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            using HttpResponseMessage response =
                await this.SendAsync(HttpMethod.Get, path, content: null, cancellationToken);

            await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken);

            return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        }
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            // An entity deleted between the search and the read. Normal in a
            // live catalogue; the caller indexes what it already has.
            return null;
        }
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.BadRequest)
        {
            // Atlas answers 400, not 404, when it is asked for something an
            // entity cannot have. Lineage is the case that matters: the endpoint
            // serves entities deriving from DataSet or Process, and a hive_db
            // derives from neither, so asking about a database is a 400 on a
            // completely healthy cluster.
            //
            // The caller already declines to ask about a database. This is the
            // second line, because the type that provokes it is a property of
            // the cluster's own model rather than of this code: a customer type
            // that turns out not to be a DataSet would otherwise stop a crawl
            // with a message about Atlas's health, and describe every table it
            // had not reached yet as absent.
            this.log.Debug("Atlas answered 400 for {Path}; treating it as nothing to read.", path);

            return null;
        }
    }

    private async Task<HttpResponseMessage> SendAsync(
        HttpMethod method, string path, HttpContent? content, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, this.baseUrl + path) { Content = content };

        HttpResponseMessage response;

        try
        {
            response = await this.http.SendAsync(request, cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException &&
                                   !cancellationToken.IsCancellationRequested)
        {
            throw new InvalidOperationException(
                $"Atlas at {this.baseUrl} could not be reached, so the catalogue cannot be read. " +
                "Check the base URL - Atlas's port differs between CDP topologies and again when Knox fronts " +
                "it - and that this host can reach it.",
                ex);
        }

        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            response.Dispose();

            throw new PushSourceAuthenticationException(
                $"Atlas refused this identity with {(int)response.StatusCode}. The service account needs read " +
                "access to the entities it is to catalogue, and Atlas must accept Kerberos - this connector " +
                "holds no password to offer it.");
        }

        if (!response.IsSuccessStatusCode)
        {
            HttpStatusCode status = response.StatusCode;
            response.Dispose();

            // A 404 or a 400 stays an HttpRequestException so TryGetJsonAsync
            // can treat one entity vanishing between the search and the read, or
            // an endpoint that does not serve this entity's type, as a skip.
            // No body in either message: an Atlas error echoes the request and a
            // Java stack trace, and neither belongs in a log a wider group can
            // read than can read the catalogue.
            if (status is HttpStatusCode.NotFound or HttpStatusCode.BadRequest)
            {
                throw new HttpRequestException(
                    $"Atlas returned {(int)status} for {path}.", inner: null, statusCode: status);
            }

            // Anything else is Atlas being unwell, and it is fatal for the same
            // reason an unreadable Ranger is: a catalogue read that half worked
            // would publish a partial map of the lake and call it complete.
            throw new InvalidOperationException(
                $"Atlas at {this.baseUrl} returned {(int)status}, so the catalogue cannot be read. The run " +
                "stops rather than indexing part of it. Check that the Atlas service is healthy - " +
                "/api/atlas/admin/status answers without authentication and returns ACTIVE on a working " +
                "instance - and that this host may reach it.");
        }

        return response;
    }
}
