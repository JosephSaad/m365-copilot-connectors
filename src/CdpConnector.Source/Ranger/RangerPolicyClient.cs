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

    // Checked once per client, not once per service. The tag plane is a
    // property of the cluster, and two resource services read through one
    // client would otherwise fetch it twice.
    private bool tagPlaneChecked;

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
    /// <summary>
    /// The Ranger TAG service to refuse on, or empty to skip that check.
    ///
    /// Set from Settings:RangerTagService. Empty is correct for a cluster with
    /// no tag service, and wrong for one that simply did not configure it -
    /// which is why the deployment guide names it rather than leaving it to a
    /// default nobody reads.
    /// </summary>
    public string TagService { get; init; } = string.Empty;

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

        RefuseSecurityZones(serviceName, policies);
        this.RefuseUnreadableConstructs(serviceName, policies);

        // The tag plane, once. Inside the read rather than beside it at the
        // three call sites that construct this client, so a fourth cannot
        // forget it - the same reason RefuseSecurityZones lives here.
        if (!this.tagPlaneChecked && this.TagService.Length > 0)
        {
            this.tagPlaneChecked = true;
            await this.RefuseTagPoliciesAsync(this.TagService, cancellationToken);
        }

        this.log.Information(
            "Read {Count} Ranger polic(y/ies) from service {Service} over {Pages} page(s).",
            policies.Count,
            serviceName,
            pages);

        return policies;
    }

    /// <summary>
    /// Stops the run when the TAG service holds a policy that could deny or
    /// mask.
    ///
    /// Item 0, step 3. This connector reads resource services only, so a tag
    /// policy is invisible to it and a tag DENY is read as absent - the same
    /// over-grant CDP-18 refuses on the resource plane, arriving by a route
    /// CDP-18 cannot see because it never fetches the service.
    ///
    /// Only DENY and MASK policies stop the run. A tag policy that merely GRANTS
    /// is ignorable: not reading it under-grants, which costs content rather
    /// than exposing it, and refusing on it would block a crawl over a policy
    /// that can only ever have made this connector too cautious. That is the
    /// same direction rule the resource-plane guard applies.
    ///
    /// Call it with the tag service configured. An empty service name skips the
    /// check, which is right for a cluster that has no tag service and wrong for
    /// one that simply did not configure it - so the caller names it explicitly.
    /// </summary>
    /// <param name="tagService">The Ranger tag service, or empty to skip.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>A task that completes when the tag plane is cleared.</returns>
    public async Task RefuseTagPoliciesAsync(string tagService, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(tagService))
        {
            return;
        }

        IReadOnlyList<RangerPolicy> policies;

        try
        {
            policies = await this.PoliciesAsync(tagService, cancellationToken);
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("404", StringComparison.Ordinal))
        {
            // No tag service on this cluster. Absent is not the same as
            // unreadable, and only absence is safe to read as "no tag policies".
            this.log.Information(
                "Ranger has no tag service {Service}, so there are no tag policies to honour.", tagService);
            return;
        }

        List<RangerPolicy> dangerous = policies
            .Where(policy => policy.Enabled)
            .Where(policy =>
                policy.Deny.Count > 0 ||
                policy.PolicyType is RangerPolicyType.Masking or RangerPolicyType.RowFilter)
            .ToList();

        if (dangerous.Count == 0)
        {
            this.log.Information(
                "Ranger tag service {Service} holds {Count} polic(y/ies), none of which denies or masks.",
                tagService,
                policies.Count);
            return;
        }

        string ids = string.Join(", ", dangerous.Select(policy => policy.Id).Take(10));

        if (dangerous.Count > 10)
        {
            ids += $" and {dangerous.Count - 10} more";
        }

        throw new InvalidOperationException(
            $"Ranger tag service '{tagService}' holds {dangerous.Count} polic(y/ies) that deny or mask, and " +
            "this connector evaluates the tag plane not at all - it reads resource services only. A tag deny " +
            "is therefore read as absent, so an indexed item would be granted to people the cluster refuses, " +
            "and a tag policy carrying a condition diverges silently the moment that condition changes. " +
            "The run stops rather than writing an access-control list computed from half the policy model. " +
            "Either scope the crawl to resources no tag policy reaches, or clear Settings:RangerTagService " +
            $"once that is established and re-run. Policy ids: {ids}. There is no setting that weakens this " +
            "beyond naming a different service.");
    }

    /// <summary>
    /// Stops the run when a policy carries a construct this evaluator cannot
    /// honour AND ignoring it would over-grant.
    ///
    /// RefuseSecurityZones' sibling, and the same argument. Four constructs are
    /// read as absent by an evaluator that does not implement them, and reading
    /// them as absent makes the policy MORE permissive than the cluster:
    ///
    ///   allowExceptions    principals carved OUT of a grant. Ignoring it hands
    ///                      the item to exactly the people the policy excludes.
    ///   conditions         the answer depends on something not visible here -
    ///                      an expiry date, an IP, a time. Ignoring one on a
    ///                      deny drops the deny; on an allow it grants
    ///                      unconditionally.
    ///   validitySchedules  a start or end date on the policy. Worse than a
    ///                      condition, because a Graph permission is a static
    ///                      snapshot with no clock: even a fully-honouring
    ///                      evaluator could not mirror it.
    ///   isDenyAllElse      denies everything not explicitly allowed, so the
    ///                      policy is more restrictive than its items read.
    ///
    /// Two more are read as absent and fail the OTHER way, so they are logged
    /// rather than refused - they cost content instead of exposing it:
    /// denyExceptions (principals exempted from a deny) and grants to named
    /// users, which RoutingEvaluator drops because it reads item.Groups only.
    /// A Graph ACL carries Entra GROUP object IDs, so an individual grant may
    /// be unrepresentable rather than merely unimplemented - see
    /// docs/What-is-Next.md item 0, step 4.
    ///
    /// Like the zone guard there is no setting that disables this, and for the
    /// same reason: a warning is read once and then not again, and there is no
    /// partial evaluation to fall back to that would not be a guess.
    /// </summary>
    /// <param name="serviceName">The Ranger service the policies came from.</param>
    /// <param name="policies">Everything read for it.</param>
    private void RefuseUnreadableConstructs(string serviceName, IReadOnlyList<RangerPolicy> policies)
    {
        // A disabled policy decides nothing, so it cannot over-grant either.
        List<RangerPolicy> live = policies.Where(policy => policy.Enabled).ToList();

        List<RangerPolicy> underGranting = live
            .Where(policy => policy.HasDenyExceptions || policy.NamesUsers)
            .ToList();

        if (underGranting.Count > 0)
        {
            this.log.Warning(
                "{Count} polic(y/ies) on Ranger service {Service} carry a denyExceptions block or a grant to " +
                "a named user. Neither is evaluated here, and both fail CLOSED - content the cluster would " +
                "show is left out of the index rather than content it hides being put in. The run continues. " +
                "Policy ids: {Ids}.",
                underGranting.Count,
                serviceName,
                string.Join(", ", underGranting.Select(policy => policy.Id).Take(20)));
        }

        var reasons = new List<string>();

        Count("an allowExceptions block", policy => policy.HasAllowExceptions);
        Count("a condition", policy => policy.HasConditions);
        Count("a validity schedule", policy => policy.HasValiditySchedules);
        Count("isDenyAllElse", policy => policy.DeniesAllElse);

        if (reasons.Count == 0)
        {
            return;
        }

        List<long> ids = live
            .Where(policy => policy.HasAllowExceptions || policy.HasConditions ||
                             policy.HasValiditySchedules || policy.DeniesAllElse)
            .Select(policy => policy.Id)
            .ToList();

        string named = string.Join(", ", ids.Take(10));

        if (ids.Count > 10)
        {
            named += $" and {ids.Count - 10} more";
        }

        throw new InvalidOperationException(
            $"Ranger service '{serviceName}' has polic(y/ies) carrying {string.Join(", ", reasons)}, and this " +
            "connector evaluates none of them. It reads policyItems and denyPolicyItems only, so each of these " +
            "is read as absent - and every one of them makes the cluster MORE restrictive than this connector " +
            "would compute, which means an indexed item would be granted to people Ranger refuses. The run " +
            "stops rather than writing an access-control list it knows to be too generous. Either remove the " +
            "construct from the policies covering the crawled resources, or scope the crawl to resources no " +
            $"such policy covers. Policy ids: {named}. There is no setting that disables this.");

        void Count(string description, Func<RangerPolicy, bool> predicate)
        {
            int count = live.Count(predicate);

            if (count > 0)
            {
                reasons.Add($"{description} ({count})");
            }
        }
    }

    /// <summary>
    /// Stops the run when the service uses Ranger security zones.
    ///
    /// This connector evaluates every policy it read against every resource.
    /// That is not how Ranger reads a zoned cluster: a resource that falls
    /// inside a zone is evaluated against THAT ZONE's policies only, and a
    /// resource outside every zone against unzoned policies only. Reading them
    /// together applies a legacy unzoned grant to a table the zone protects, and
    /// hands the indexed item to people the cluster refuses.
    ///
    /// Refusing is the answer rather than a warning, and rather than quietly
    /// evaluating the unzoned policies alone. A warning would be read once and
    /// then not again, and dropping the zoned policies would still be a guess
    /// about a zone this code cannot see. The file header's rule already covers
    /// the case: a Ranger this connector cannot read faithfully stops the run,
    /// because "index it anyway" is the one answer that cannot be taken back.
    ///
    /// Honouring zones properly means fetching the zone definitions and
    /// selecting a resource's zone before any policy is filtered. Until that
    /// exists, this is the honest behaviour.
    /// </summary>
    /// <param name="serviceName">The Ranger service the policies came from.</param>
    /// <param name="policies">Everything read for it.</param>
    private static void RefuseSecurityZones(string serviceName, IReadOnlyList<RangerPolicy> policies)
    {
        List<RangerPolicy> zoned = policies.Where(policy => policy.ZoneName.Length > 0).ToList();

        if (zoned.Count == 0)
        {
            return;
        }

        List<string> zones = zoned
            .Select(policy => policy.ZoneName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        string named = string.Join(", ", zones.Take(5));

        if (zones.Count > 5)
        {
            named += $" and {zones.Count - 5} more";
        }

        throw new InvalidOperationException(
            $"Ranger service '{serviceName}' has {zoned.Count} polic(y/ies) in security zone(s) {named}, " +
            "and this connector cannot evaluate zones. It applies every policy to every resource, whereas " +
            "Ranger evaluates a resource inside a zone against that zone's policies only and a resource " +
            "outside every zone against the unzoned ones only - so reading them together would grant an " +
            "indexed item to people the cluster refuses. The run stops rather than deciding what may be " +
            "indexed from a policy set it cannot read faithfully. Point Settings:RangerSqlService and " +
            "Settings:RangerHdfsService at a service without zones, or wait for zone support; there is " +
            "deliberately no setting that disables this check.");
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

                // Absent on an unzoned policy, and empty on one from some Ranger
                // builds. Both mean the same thing and both must read as unzoned,
                // or every cluster would trip the guard below.
                ZoneName = element.TryGetProperty("zoneName", out JsonElement zone) &&
                           zone.ValueKind == JsonValueKind.String
                    ? zone.GetString()?.Trim() ?? string.Empty
                    : string.Empty,
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

            // The constructs this evaluator does not honour. They are read only
            // so the guard below can refuse them: none of them is acted on, and
            // reading one as absent is exactly the failure being guarded.
            policy.HasAllowExceptions = HasEntries(element, "allowExceptions");
            policy.HasDenyExceptions = HasEntries(element, "denyExceptions");
            policy.HasValiditySchedules = HasEntries(element, "validitySchedules");

            policy.DeniesAllElse = element.TryGetProperty("isDenyAllElse", out JsonElement denyAll) &&
                                   denyAll.ValueKind == JsonValueKind.True;

            // A condition can sit on the policy or on any individual item, and
            // both change the answer, so both have to be looked for.
            policy.HasConditions =
                HasEntries(element, "conditions") ||
                AnyEntryHasConditions(element, "policyItems") ||
                AnyEntryHasConditions(element, "denyPolicyItems") ||
                AnyEntryHasConditions(element, "allowExceptions") ||
                AnyEntryHasConditions(element, "denyExceptions");

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

    /// <summary>True when the named property is an array with something in it.</summary>
    private static bool HasEntries(JsonElement element, string name)
    {
        return element.TryGetProperty(name, out JsonElement array) &&
               array.ValueKind == JsonValueKind.Array &&
               array.GetArrayLength() > 0;
    }

    /// <summary>True when any entry of the named item array carries a condition.</summary>
    private static bool AnyEntryHasConditions(JsonElement policy, string name)
    {
        if (!policy.TryGetProperty(name, out JsonElement items) || items.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        foreach (JsonElement item in items.EnumerateArray())
        {
            if (HasEntries(item, "conditions"))
            {
                return true;
            }
        }

        return false;
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
