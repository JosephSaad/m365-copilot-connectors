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

            int before = found.Count;

            foreach (JsonElement element in entities.EnumerateArray())
            {
                AtlasEntity entity = ReadHeader(element);

                // Atlas SCRUBS a hit the caller may not read rather than
                // removing it: Ranger's authorizer blanks the header in place
                // and sets its GUID to "-1", so the array length is unchanged
                // and an unreadable entity arrives as an empty shell. Indexing
                // one would put a nameless item in the catalogue.
                //
                // The GUID set additionally stops a server that ignores the
                // offset from turning this loop into a spin on page one.
                if (entity.Guid.Length > 0 &&
                    !string.Equals(entity.Guid, "-1", StringComparison.Ordinal) &&
                    entity.IsActive &&
                    seen.Add(entity.Guid))
                {
                    found.Add(entity);
                }
            }

            int returned = entities.GetArrayLength();

            if (returned < limit || found.Count == before)
            {
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

    /// <summary>Reads one hop of lineage either side of an entity.</summary>
    /// <param name="entity">The entity to describe. Its Upstream and Downstream are filled in.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>A task for the operation.</returns>
    public async Task AddLineageAsync(AtlasEntity entity, CancellationToken cancellationToken)
    {
        using JsonDocument? document = await this.TryGetJsonAsync(
            $"/api/atlas/v2/lineage/{Uri.EscapeDataString(entity.Guid)}?direction=BOTH&depth=1",
            cancellationToken);

        if (document is null ||
            !document.RootElement.TryGetProperty("guidEntityMap", out JsonElement map) ||
            !document.RootElement.TryGetProperty("relations", out JsonElement relations) ||
            relations.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (JsonElement relation in relations.EnumerateArray())
        {
            string from = Text(relation, "fromEntityId");
            string to = Text(relation, "toEntityId");

            // A relation pointing AT this entity is something that feeds it;
            // one pointing away is something it feeds. Anything not touching it
            // is a second hop the depth of 1 let through and is not described.
            if (string.Equals(to, entity.Guid, StringComparison.Ordinal))
            {
                Add(entity.Upstream, DisplayName(map, from));
            }
            else if (string.Equals(from, entity.Guid, StringComparison.Ordinal))
            {
                Add(entity.Downstream, DisplayName(map, to));
            }
        }
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

    private static string DisplayName(JsonElement map, string guid)
    {
        if (guid.Length == 0 || !map.TryGetProperty(guid, out JsonElement entity))
        {
            return string.Empty;
        }

        string name = Text(entity, "displayText");

        if (name.Length > 0)
        {
            return name;
        }

        return entity.TryGetProperty("attributes", out JsonElement attributes)
            ? Text(attributes, "qualifiedName")
            : string.Empty;
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

    private async Task<JsonDocument> GetJsonAsync(string path, CancellationToken cancellationToken)
    {
        using HttpResponseMessage response =
            await this.SendAsync(HttpMethod.Get, path, content: null, cancellationToken);

        await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken);

        return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
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

            // A 404 stays an HttpRequestException so TryGetJsonAsync can treat
            // one entity vanishing between the search and the read as a skip.
            // No body in either message: an Atlas error echoes the request and a
            // Java stack trace, and neither belongs in a log a wider group can
            // read than can read the catalogue.
            if (status == HttpStatusCode.NotFound)
            {
                throw new HttpRequestException(
                    $"Atlas returned 404 for {path}.", inner: null, statusCode: status);
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
