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
// The parsing follows from the same rule. A field dropped here is not a field
// missing from a report - it is a policy read as something the cluster never
// said. Each resource is therefore read with its isExcludes and isRecursive
// flags rather than its values alone: an exclusion read as an inclusion turns
// "every finance table except salaries" into "salaries", which is the exact
// inverse, and indexes the one table the policy exists to withhold.
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

    /// <summary>
    /// How many policies to ask Ranger for at a time.
    ///
    /// Ranger clamps this to its own ranger.db.maxrows.default, 200 out of the
    /// box, so asking for more is a request rather than an expectation - which
    /// is exactly why the loop advances by what a page held rather than by what
    /// it asked for.
    /// </summary>
    private const int PolicyPageSize = 1000;

    /// <summary>
    /// A ceiling on the whole policy set, past which the run stops.
    ///
    /// Not a real policy count - it is a backstop against a pager that will not
    /// terminate. A policy set this connector cannot finish reading is an
    /// unreadable Ranger, and the rule for that is already written at the top of
    /// this file: stop, rather than decide what may be indexed from half of it.
    /// </summary>
    private const int MaxPolicies = 100_000;

    /// <summary>Reads every policy defined on one service.</summary>
    /// <param name="serviceName">The Ranger service, for example cm_hdfs or cm_hive.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The enabled and disabled policies, as Ranger returned them.</returns>
    /// <exception cref="PushSourceAuthenticationException">Ranger refused this identity.</exception>
    /// <exception cref="InvalidOperationException">Ranger could not be read for any other reason.</exception>
    public async Task<IReadOnlyList<RangerPolicy>> PoliciesAsync(
        string serviceName, CancellationToken cancellationToken)
    {
        var policies = new List<RangerPolicy>();
        var seen = new HashSet<long>();
        int startIndex = 0;
        int pages = 0;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            List<RangerPolicy> page = await this.PageAsync(serviceName, startIndex, cancellationToken);
            pages++;

            if (page.Count == 0)
            {
                break;
            }

            int added = 0;

            foreach (RangerPolicy policy in page)
            {
                if (seen.Add(policy.Id))
                {
                    policies.Add(policy);
                    added++;
                }
            }

            // Advance by what the page ACTUALLY held, never by what was asked
            // for. Ranger clamps pageSize to its own ranger.db.maxrows.default,
            // so a request for a thousand can come back with two hundred - and
            // stepping the index by a thousand would then skip the eight
            // hundred in between and never know it.
            startIndex += page.Count;

            // A full page that contributed nothing new is a server ignoring
            // startIndex, which would otherwise spin on page one for ever.
            if (added == 0)
            {
                break;
            }

            if (policies.Count >= MaxPolicies)
            {
                throw new InvalidOperationException(
                    $"Ranger Admin returned more than {MaxPolicies} policies for service '{serviceName}', " +
                    "which is past anything this connector should be asked to reason about. The run stops " +
                    "rather than deciding what may be indexed from a policy set it has not finished reading.");
            }
        }

        this.log.Information(
            "Read {Count} Ranger polic(y/ies) from service {Service} over {Pages} page(s).",
            policies.Count,
            serviceName,
            pages);

        return policies;
    }

    /// <summary>Reads one page of the policy list.</summary>
    /// <param name="serviceName">The Ranger service.</param>
    /// <param name="startIndex">Where the page starts.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The policies on that page, in the order Ranger returned them.</returns>
    private async Task<List<RangerPolicy>> PageAsync(
        string serviceName, int startIndex, CancellationToken cancellationToken)
    {
        string url =
            $"{this.baseUrl}/service/public/v2/api/service/{Uri.EscapeDataString(serviceName)}/policy" +
            $"?pageSize={PolicyPageSize}&startIndex={startIndex}";

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

            return Parse(document.RootElement);
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

                    // The values alone are not the resource. isExcludes says
                    // the values are what the policy does NOT cover, so a
                    // policy read without it says the opposite of what it says;
                    // isRecursive says whether a path reaches under itself.
                    // Ranger's own default for each, when the document omits
                    // it, is false, and that is what an absent flag means here.
                    policy.SetResource(
                        resource.Name,
                        values,
                        isExcludes: Flag(resource.Value, "isExcludes"),
                        isRecursive: Flag(resource.Value, "isRecursive"));
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

    /// <summary>Reads one resource flag, absent meaning Ranger's own default of false.</summary>
    /// <param name="resource">The resource object.</param>
    /// <param name="name">The flag, isExcludes or isRecursive.</param>
    /// <returns>What the flag says, or false.</returns>
    private static bool Flag(JsonElement resource, string name)
    {
        if (!resource.TryGetProperty(name, out JsonElement flag))
        {
            return false;
        }

        // Ranger writes a JSON boolean. A quoted "true" is accepted as well
        // because the alternative is reading an exclusion as an inclusion,
        // which indexes exactly the table the policy was written to withhold;
        // anything else unreadable stays at the default.
        return flag.ValueKind == JsonValueKind.True ||
               (flag.ValueKind == JsonValueKind.String &&
                bool.TryParse(flag.GetString(), out bool parsed) &&
                parsed);
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
