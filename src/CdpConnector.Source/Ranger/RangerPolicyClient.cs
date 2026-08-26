// ---------------------------------------------------------------------------
// RangerPolicyClient.cs
// Reads a Ranger service's policies, over SPNEGO, as this service's identity.
//
// One rule governs everything here: if the policies cannot be read, nothing is
// indexed. Ranger is the component that says which tables carry row filters and
// column masks, and a connector that indexed a lake while unable to see that
// would be copying exactly the data whose access rules it could not evaluate.
// So an unreachable Ranger is a failure, never a default, and never a warning
// followed by a crawl.
//
// A note for whoever deploys this: many Ranger installations front the REST API
// with basic authentication against local users rather than SPNEGO. This client
// does Kerberos only, on purpose - a password here would be a secret in
// configuration, which this repository does not do. If the target cluster's
// Ranger is basic-auth only, that is a cluster-side change (enable Kerberos
// authentication on the Ranger Admin API), not a change to this file.
// ---------------------------------------------------------------------------

namespace CdpConnector.Source.Ranger;

using System.Net;
using System.Text.Json;
using PushCore;
using Serilog;

/// <summary>Reads policies from Ranger Admin.</summary>
public sealed class RangerPolicyClient : IDisposable
{
    private readonly HttpClient http;
    private readonly string baseUrl;
    private readonly ILogger log;
    private readonly bool ownsClient;

    /// <summary>Initializes a new instance of the <see cref="RangerPolicyClient"/> class.</summary>
    /// <param name="baseUrl">The Ranger Admin base URL.</param>
    /// <param name="log">Where to report progress.</param>
    public RangerPolicyClient(string baseUrl, ILogger log)
        : this(baseUrl, Hdfs.WebHdfsClient.CreateNegotiatingClient(), log, ownsClient: true)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="RangerPolicyClient"/> class with a supplied client.</summary>
    /// <param name="baseUrl">The Ranger Admin base URL.</param>
    /// <param name="http">The client to use. A test supplies one over a fake handler.</param>
    /// <param name="log">Where to report progress.</param>
    /// <param name="ownsClient">True when disposing this should dispose the client.</param>
    public RangerPolicyClient(string baseUrl, HttpClient http, ILogger log, bool ownsClient = false)
    {
        this.baseUrl = baseUrl.TrimEnd('/');
        this.http = http;
        this.log = log;
        this.ownsClient = ownsClient;
    }

    /// <summary>Reads every policy defined on one service.</summary>
    /// <param name="serviceName">The Ranger service, for example cm_hdfs or cm_hive.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The enabled and disabled policies, as Ranger returned them.</returns>
    /// <exception cref="PushSourceAuthenticationException">Ranger refused this identity.</exception>
    /// <exception cref="InvalidOperationException">Ranger could not be read for any other reason.</exception>
    public async Task<IReadOnlyList<RangerPolicy>> PoliciesAsync(
        string serviceName, CancellationToken cancellationToken)
    {
        string url = $"{this.baseUrl}/service/public/v2/api/service/{Uri.EscapeDataString(serviceName)}/policy";

        HttpResponseMessage response;

        try
        {
            response = await this.http.GetAsync(url, cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException &&
                                   !cancellationToken.IsCancellationRequested)
        {
            // Deliberately fatal. See the file header: an unreadable Ranger
            // cannot become "index it anyway".
            throw new InvalidOperationException(
                $"Ranger Admin at {this.baseUrl} could not be reached, so which tables and paths may be indexed " +
                "is unknown. The run stops rather than indexing a source whose access policies it cannot read.",
                ex);
        }

        using (response)
        {
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                throw new PushSourceAuthenticationException(
                    $"Ranger Admin refused this identity with {(int)response.StatusCode}. The service account " +
                    "needs read access to the policy API, and Ranger Admin must accept Kerberos - this connector " +
                    "holds no password to offer it.");
            }

            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException(
                    $"Ranger Admin returned {(int)response.StatusCode} for service '{serviceName}'. " +
                    "Check the service name against Ranger's own list; it is the CM service name, " +
                    "for example cm_hdfs or cm_hive.");
            }

            await using Stream body = await response.Content.ReadAsStreamAsync(cancellationToken);
            using JsonDocument document = await JsonDocument.ParseAsync(body, cancellationToken: cancellationToken);

            List<RangerPolicy> policies = Parse(document.RootElement);

            this.log.Information(
                "Read {Count} Ranger polic(y/ies) from service {Service}.", policies.Count, serviceName);

            return policies;
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

    /// <summary>Parses Ranger's policy list.</summary>
    /// <param name="root">The array Ranger returned.</param>
    /// <returns>The policies.</returns>
    public static List<RangerPolicy> Parse(JsonElement root)
    {
        var policies = new List<RangerPolicy>();

        if (root.ValueKind != JsonValueKind.Array)
        {
            return policies;
        }

        foreach (JsonElement element in root.EnumerateArray())
        {
            var policy = new RangerPolicy
            {
                Id = element.TryGetProperty("id", out JsonElement id) ? id.GetInt64() : 0,
                Name = element.TryGetProperty("name", out JsonElement name) ? name.GetString() ?? string.Empty : string.Empty,
                Enabled = !element.TryGetProperty("isEnabled", out JsonElement enabled) || enabled.GetBoolean(),
                PolicyType = element.TryGetProperty("policyType", out JsonElement type)
                    ? (RangerPolicyType)type.GetInt32()
                    : RangerPolicyType.Access,
            };

            if (element.TryGetProperty("resources", out JsonElement resources) &&
                resources.ValueKind == JsonValueKind.Object)
            {
                foreach (JsonProperty resource in resources.EnumerateObject())
                {
                    var values = new List<string>();

                    if (resource.Value.TryGetProperty("values", out JsonElement list) &&
                        list.ValueKind == JsonValueKind.Array)
                    {
                        foreach (JsonElement value in list.EnumerateArray())
                        {
                            string? text = value.GetString();

                            if (!string.IsNullOrEmpty(text))
                            {
                                values.Add(text);
                            }
                        }
                    }

                    policy.Resources[resource.Name] = values;
                }
            }

            ReadItems(element, "policyItems", policy.Allow);
            ReadItems(element, "denyPolicyItems", policy.Deny);

            // A masking or row-filter policy carries its items under its own
            // name. They are read into Allow because what this connector needs
            // from them is only "these exist", which the policy type already
            // says; keeping the principals lets the routing report name who the
            // filter was written for.
            ReadItems(element, "dataMaskPolicyItems", policy.Allow);
            ReadItems(element, "rowFilterPolicyItems", policy.Allow);

            policies.Add(policy);
        }

        return policies;
    }

    private static void ReadItems(JsonElement policy, string name, IList<RangerPolicyItem> into)
    {
        if (!policy.TryGetProperty(name, out JsonElement items) || items.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (JsonElement element in items.EnumerateArray())
        {
            var item = new RangerPolicyItem();

            AddStrings(element, "groups", item.Groups);
            AddStrings(element, "users", item.Users);

            if (element.TryGetProperty("accesses", out JsonElement accesses) &&
                accesses.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement access in accesses.EnumerateArray())
                {
                    bool allowed = !access.TryGetProperty("isAllowed", out JsonElement flag) || flag.GetBoolean();

                    if (allowed && access.TryGetProperty("type", out JsonElement accessType))
                    {
                        string? text = accessType.GetString();

                        if (!string.IsNullOrEmpty(text))
                        {
                            item.Accesses.Add(text);
                        }
                    }
                }
            }

            into.Add(item);
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
            string? text = value.GetString();

            if (!string.IsNullOrEmpty(text))
            {
                into.Add(text);
            }
        }
    }
}
