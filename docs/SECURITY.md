# SqlTicketsConnector — security control mapping

Audience: the security architect reviewing this connector before it is allowed
into the regulated environment. Every control is listed with where it is
implemented and what evidence exists that it works.

Nothing in this document asks you to take a claim on trust: each control names a
file, and where a test proves it, the test name.

---

## 1. What this connector is, and what it is not

| | |
|---|---|
| **Deployment** | On-premises Windows Server, alongside the Microsoft Graph connector agent (GCA). |
| **Data flow** | `dbo.Tickets` → this process (gRPC over loopback) → GCA → Microsoft Graph → Copilot semantic index. |
| **Two optional side paths** | `SqlGraphPush` (`dbo.Tickets`) and `SqlHierarchyPush` (`Customers`/`Engagements`/`TimeEntries`) call Microsoft Graph directly, bypassing the agent. Neither is required, and neither changes anything above. |
| **Tenant relationship** | Held by the GCA. **This process never calls Microsoft Graph** and holds no Graph permission. |
| **Certificate use in `SqlTicketsConnector`** | Azure Key Vault access and the loopback TLS listener only. |
| **Certificate use in `SqlGraphPush`** | Microsoft Graph. That tool is a separate, operator-run utility. |
| **Certificate use in `SqlHierarchyPush`** | Microsoft Graph. A second operator-run utility, for the three level test case; same permissions, its own connection. |
| **Data at rest in this process** | None. Rows are streamed, never spooled to disk. |
| **Where the side paths' code lives** | `PushCore`, one engine both run on. A push tool is a schema, a query and a row mapping; credentials, SQL, ACLs, truncation and throttling are the engine's. Adding a third source changes no file in it — see `docs/ADDING-A-PUSH-CONNECTOR.md`. |

**`PushCore` is where the Graph SDK is allowed to be, and that is deliberate.**
It sits between the push tools and `Connector.Security`, so the shared
credential, vault and SQL code can be shared with the agent-hosted connector
without the Graph SDK reaching it. `Connector.Security` references
neither the Graph SDK nor the gRPC contracts, and that is what keeps the
boundary below honest rather than merely stated.

The connector project has no reference to the Microsoft Graph SDK. A reference
appearing there in a future change is a review failure, not a refactor:
`src/SqlTicketsConnector/SqlTicketsConnector.csproj` should list only
`Google.Protobuf`, `Grpc.Core`, `Grpc.Tools`, `Microsoft.Data.SqlClient`,
`Serilog` and its sinks, plus the `Connector.Security` project.

---

## 2. Control mapping

### Secret handling

| ID | Control | Implementation | Evidence |
|---|---|---|---|
| SEC-1 | No secret, password, PFX or credential-bearing connection string in source, configuration, environment or logs | `src/SqlTicketsConnector/appsettings.json`, `src/SqlGraphPush/appsettings.json` and `src/SqlHierarchyPush/appsettings.json` hold vault URI, secret *names*, the Credential Manager target *name*, tenant ID, client ID, thumbprints, server and database only | `build/SecretHygiene.targets` fails the build on a credential-shaped key with a value; `.gitleaks.toml` scans history and staged changes |
| SEC-2 | Secrets resolved at runtime, held in memory only | `Security/Secrets/KeyVaultSecretProvider.cs`, `Security/Sql/SqlConnectionFactory.cs` (the password enters a `SqlConnectionStringBuilder` and is never logged or persisted) | `ConfigurationTests.SqlLogin_requires_a_resolved_password_and_keeps_it_out_of_configuration` |
| SEC-3 | `ISecretProvider` with a Key Vault production implementation | `Security/Secrets/ISecretProvider.cs`, `KeyVaultSecretProvider.cs` | — |
| SEC-4 | Environment provider is development-only and refuses to run in Production | `Security/Secrets/EnvironmentSecretProvider.cs` — constructor throws when `Environment` is `Production`, and logs a prominent warning otherwise | `ConfigurationTests.The_environment_secret_provider_refuses_to_run_in_production` |
| SEC-5 | In-memory cache with configurable TTL, default 60 minutes, never to disk | `Security/Secrets/CachingSecretProvider.cs` | `SecretCacheTests.Cached_value_is_reused_inside_the_time_to_live`, `.Value_is_resolved_again_once_the_time_to_live_expires` |
| SEC-6 | Authentication failure invalidates the cached secret and retries **exactly once** | `Security/Secrets/SecretRefreshRetryPolicy.cs`, applied in `Security/Sql/SqlConnectionFactory.OpenAsync` | `SecretCacheTests.Authentication_failure_invalidates_the_secret_and_retries_exactly_once`, `.A_second_authentication_failure_is_surfaced_rather_than_retried_again` |
| SEC-7 | No file-based secret provider exists. One DPAPI-backed provider does, added deliberately — see deviation 7 | `Security/Secrets/` holds four providers: Key Vault, environment (development only), a cache, and Windows Credential Manager. None reads a secret from a file, and none writes one anywhere | Directory listing; `WindowsCredentialStore` contains `CredRead` and no `CredWrite` |
| SEC-8 | The client secret mode keeps the secret out of source, configuration, environment and deployment scripts | `Auth:ClientSecretCredentialTarget` holds the *name* of a Credential Manager entry; `Security/Secrets/WindowsCredentialStore.cs` reads the value at startup | `ClientSecretAuthTests.A_secret_stored_in_credential_manager_is_read_back_unchanged`, `.A_secret_pasted_into_the_target_name_is_rejected_at_startup` |

**On `SecureString`:** not used, deliberately. On .NET 10 `SecureString` is not
encrypted at rest in memory on any platform the runtime supports for this
workload, and the value must be marshalled back to a managed `string` to build a
connection string or an HTTP header. Using it would imply a protection that does
not exist. The real controls are the TTL, invalidation on failure, and never
writing the value anywhere. This reasoning is repeated in the XML documentation
on `ISecretProvider` so it is visible at the point of use.

### Certificate handling

| ID | Control | Implementation | Evidence |
|---|---|---|---|
| CERT-1 | Certificates load from the Windows certificate store only; no PFX loader exists | `Security/Certificates/StoreCertificateResolver.cs` | Absence of any `X509Certificate2(string path…)` call in the solution |
| CERT-2 | Store location configurable (`LocalMachine` for the service, `CurrentUser` for development) | `Auth:CertificateStoreLocation` → `AuthOptions.ParsedStoreLocation` | `ConfigurationTests.Every_invalid_field_is_reported_in_one_pass` (rejects an invalid location) |
| CERT-3 | Locate by thumbprint **list**, tried in order, plus locate-by-subject for rotation | `Security/Certificates/CertificateSelector.cs` | `CertificateResolutionTests.First_valid_thumbprint_in_the_configured_order_is_selected`, `.Subject_matches_are_used_after_thumbprints_newest_first` |
| CERT-4 | Validation on load: not expired, not inside the warning window, private key present **and usable by this process** | `CertificateSelector.TryUsePrivateKey` signs a byte with the key rather than trusting `HasPrivateKey` | `CertificateResolutionTests.A_certificate_whose_private_key_is_unusable_is_reported_clearly` |
| CERT-5 | Clear startup failure naming the process identity | `StoreCertificateResolver.DescribeFailure` includes `ProcessIdentity.Current()` and the `certlm.msc` remedy | Same test asserts the identity and remedy appear in the message |
| CERT-6 | Warning daily inside the expiry window, Error once expired | `StoreCertificateResolver.ReportExpiryState`, driven by a 24 hour timer in `Server/ConnectorServer.cs` | `CertificateResolutionTests.A_certificate_inside_the_warning_window_is_flagged_but_still_usable` |
| CERT-7 | Rotation without an outage: install new, restart, confirm from the log, remove old | `Security/Credentials/RotatingCertificateCredential.cs` tries each candidate until a token is issued and logs the winning thumbprint at `Information` on first use | `docs/RUNBOOK.md` §1 |
| CERT-8 | `SendCertificateChain = true` (subject name and issuer authentication) | `RotatingCertificateCredential` constructor | — |
| CERT-9 | No `DefaultAzureCredential` in production paths | `Security/Credentials/TokenCredentialFactory.cs` returns `ManagedIdentityCredential`, `RotatingCertificateCredential` or `ClientSecretCredential` according to `Auth:Mode`, and throws otherwise | Grep: `DefaultAzureCredential` appears nowhere in the solution |
| CERT-10 | Private key material never written to disk, including for TLS | `ConnectorServer.BuildServerCredentials` exports PEM in memory from the store certificate | — |

### SQL access

| ID | Control | Implementation | Evidence |
|---|---|---|---|
| SQL-1 | Authentication preference order: Entra ID token, Windows integrated, SQL login from Key Vault | `Security/Sql/SqlConnectionStringFactory.Build`, `SqlConnectionFactory.OpenCoreAsync`. **Shipped configuration uses `WindowsIntegrated`** | `ConfigurationTests.Windows_integrated_connections_carry_no_credential_and_force_encryption` |
| SQL-2 | `Encrypt=true` on every path | `SqlConnectionStringFactory.Build` sets it unconditionally | Same test |
| SQL-3 | `TrustServerCertificate=true` rejected in Production, wherever it is configured | `SqlConnectionStringFactory.InspectExtraOptions` (startup and per-call), applied to the wizard-supplied data source URL in `Connector/AgentRequestInspector.cs` | `ConfigurationTests.TrustServerCertificate_is_rejected_in_production` |
| SQL-4 | No credential in any operator-editable connection text | `InspectExtraOptions` rejects `Password` and `User ID` | `ConfigurationTests.Credentials_in_operator_supplied_connection_text_are_rejected` |
| SQL-5 | Least privilege: `SELECT` on `dbo.Tickets`, nothing else | `sql/01-least-privilege.sql`, including explicit `DENY` and a verification query | Run the verification query at the end of the script |
| SQL-7 | Three level source: the push identity reads **views only** and cannot read the base tables | `sql/13-timesheet-least-privilege.sql` grants `SELECT` on the four views and `DENY`s every verb on `dbo.Customers`, `dbo.Engagements` and `dbo.TimeEntries`. Ownership chaining makes the grant sufficient; the `DENY` exists so a future role membership cannot widen it | Run the verification query at the end of the script — expect four rows, all `SELECT`, all on views |
| SQL-8 | The soft-delete filter cannot be bypassed by editing the tool | It lives inside the views in `sql/12-timesheet-views.sql`, not in a `WHERE` clause in C#. `SqlHierarchyPush` selects from one view and adds no predicate | Code review: `HierarchyPushConnector.BuildQuery` builds no `WHERE`, and `PushEngine` adds none |
| SQL-9 | The view name is not an injection surface | `Source:ItemView` is concatenated into a query, so it is validated as a `[schema.]name` identifier — letters, digits and underscores only, at most one dot — and rejected otherwise | `PushConfigurationTests.A_view_name_that_is_not_a_plain_identifier_is_rejected` puts a battery of hostile values through it - injection suffixes, bracket quoting, leading digits, a trailing `; DROP TABLE`. A non-identifier value fails startup with exit code 2 |
| SQL-6 | Transient faults retried, not surfaced as crawl failures | `ConnectRetryCount`/`ConnectRetryInterval` in the connection string; `Security/Sql/SqlErrorClassifier.cs` classifies by error number; transient failures return `RetryDetails` with `ExponentialBackOff` | `ConnectorCrawlerServiceImpl.BuildFailureStatus` |

### Access control on indexed items

| ID | Control | Implementation | Evidence |
|---|---|---|---|
| ACL-1 | Entra group principals, never "everyone" | `Connector/AclBuilder.cs` builds `PrincipalType.Group` + `IdentityType.AadId` entries from `Acl:GrantGroupObjectIds` | `ContentAndSchemaTests.A_built_item_carries_truncated_content_and_the_configured_acl` |
| ACL-2 | Startup fails when no ACL is configured, rather than defaulting to everyone | `AclOptions.Validate`, `AclBuilder.Build` throws on an empty list | `ContentAndSchemaTests.An_empty_acl_configuration_fails_loudly_instead_of_granting_everyone`, `ConfigurationTests.An_empty_acl_section_fails_validation` |
| ACL-3 | Both direct push tools apply the same principals | `src/SqlGraphPush/Program.cs` and `src/SqlHierarchyPush/Program.cs` build `AclType.Group` entries from the same configuration section | Code review |
| ACL-4 | Every level of the hierarchy carries the same ACL | `SqlHierarchyPush` stamps one ACL list onto customer, engagement and time entry items alike. A time entry narrative is at least as sensitive as the engagement it belongs to, so there is no case for trimming them differently | Code review: one `acl` list, built once, applied to every item |

### Logging and redaction

| ID | Control | Implementation | Evidence |
|---|---|---|---|
| LOG-1 | Structured Serilog throughout; rolling file 10 MB × 30, under the install directory | `Logging/LoggingSetup.cs`, `Logging:Directory` | — |
| LOG-2 | Windows event log, `Warning` and above, custom source, **source created by the installer** not at runtime | `LoggingSetup.Create` passes `manageEventSource: false`; `deploy/Install-Connector.ps1` step 3 creates it | — |
| LOG-3 | **Item content and property values are never logged at any level** | The crawl logs item ID, counts and byte sizes only. `Logging/RedactionDestructuringPolicy.cs` collapses rows, items and any protobuf message to a summary; `Security/Logging/ScrubbingEnricher.cs` applies the same policy to plain `{Value}` holes, which Serilog would otherwise stringify at capture time | **`RedactionCanaryTests.Crawl_does_not_leak_row_content_into_logs`** — runs a real crawl over a fake source containing a canary and asserts the canary appears in no emitted event |
| LOG-4 | Connection strings never logged; server and database logged from the parsed builder instead | `RedactionDestructuringPolicy`, `Security/Logging/LogScrubber.cs` | `RedactionCanaryTests.A_connection_string_never_reaches_a_sink_in_either_form` |
| LOG-5 | No secret, token or private key material logged; thumbprint and subject are logged and are not secrets | `LogScrubber` removes JWTs, bearer headers, PEM private key blocks and credential keywords | `RedactionCanaryTests.Tokens_and_private_keys_are_scrubbed` |
| LOG-6 | `SqlException` messages logged, scrubbed of anything resembling a connection string | `Security/Logging/RedactedException.cs` wraps the exception, preserving type name and stack trace | `RedactionCanaryTests.Exception_text_is_scrubbed_before_it_is_written` |
| LOG-7 | Crawl correlation: a GUID per crawl pushed into `LogContext`, plus connector ID, machine name and process ID on every event | `ConnectorCrawlerServiceImpl` (`LogContext.PushProperty("CrawlId", …)`), `LoggingSetup.Create` | Any log line |
| LOG-8 | Incremental watermark logged on entry and exit at `Information` | `ConnectorCrawlerServiceImpl.GetIncrementalCrawlStream` | — |
| LOG-9 | `HealthCheck` never logged at `Information` | `ConnectorInfoServiceImpl.HealthCheck` logs nothing; `Logging/CallLoggingInterceptor.cs` logs it at `Verbose` | Code review of both files |
| LOG-10 | One consistent place for call telemetry | `CallLoggingInterceptor`, attached with `.Intercept(...)` on all four service bindings in `ConnectorServer.Start` | — |
| LOG-11 | Metrics: items, deletes, skips, truncations, content bytes, SQL round trips, duration, errors by category | `Logging/CrawlMetrics.cs`, one summary line per crawl at `Information` | — |
| LOG-12 | The control evidence tests cannot be silently deleted | `ControlEvidenceTests.Every_control_evidence_test_is_still_present` fails the suite if a named test is renamed or removed | That test |

### Build and change management

| ID | Control | Implementation | Evidence |
|---|---|---|---|
| BLD-1 | Build fails on a credential-shaped key with a value in `appsettings.json` | `build/SecretHygiene.targets` (inline MSBuild task, no external dependency), imported by every project; `build/SecretHygiene.proj` runs it repository-wide | Add `"Password": "x"` to any appsettings file and build |
| BLD-2 | Pre-commit secret scanning | `.pre-commit-config.yaml` (gitleaks, private key detection, a hook rejecting committed certificate files, and the appsettings scan) | `pre-commit run --all-files` |
| BLD-3 | Repository secret scanning configuration | `.gitleaks.toml`, extending the default rule set with SQL connection string, `TrustServerCertificate`, key material and Entra secret rules | `gitleaks detect --config .gitleaks.toml --redact` |
| BLD-4 | Release packages cannot be produced from a failing tree | `Build.ps1` runs the secret scan and the full test suite before publishing, and refuses to package `.pfx`, `.p12`, `.pem` or `.key` files | Run `Build.ps1` |
| BLD-5 | The declared dependency set matches the one the build actually resolves | `build/Get-OfflinePackages.ps1` lists every package required, and `build/Test-OfflinePackageList.ps1` compares that list with `project.assets.json` in all three configurations — base, OTLP, and the self-contained publish's runtime packs. CI fails on drift | `pwsh build/Test-OfflinePackageList.ps1 -Configuration Base` after a restore |
| BLD-6 | An external schema mistake cannot reach the tenant | A registered Graph schema is append-only: no property's type, annotation or label can be changed afterwards, so a mistake is corrected only by deleting the connection and every item in it. `ExternalSchemaRules` enforces the two irrecoverable rules — 32 alphanumeric characters, and searchable and refinable being mutually exclusive — and `PushSchema.Prop` is the only way a connector builds a property, so a connector added later cannot opt out. It throws before the first Graph call rather than failing server side fifteen minutes into registration | `PushSchemaTests`, in particular `A_searchable_and_refinable_property_is_rejected_before_any_graph_call` and `A_property_name_the_platform_would_reject_is_caught_before_any_graph_call`. Both are in the `ControlEvidenceTests` tripwire |
| BLD-7 | Removing a control's evidence fails the build | `ControlEvidenceTests` names thirty-one tests that exist as evidence for the rows in this document and asserts each is still present and still a `[Fact]`. Deleting or renaming one is a build failure that names it, rather than a quiet reduction in coverage | `dotnet test --filter ControlEvidenceTests` |
| CDP-1 | A table whose rows or columns Ranger transforms per user is never indexed | A Ranger row filter or column mask shows different data to different people when a query runs; an index holds one copy and cannot reproduce that, so indexing would either leak the unfiltered rows to everyone granted the item or store the masked version and lie to the people entitled to the real one. `Ranger/RoutingEvaluator.EvaluateTable` routes such a table to a live query and returns no grants, and the source yields nothing for it | `CdpConnectorTests.A_table_ranger_filters_or_masks_is_routed_to_a_live_query`, in the `ControlEvidenceTests` tripwire. `deploy/Test-RangerRouting.ps1` prints the verdict per table before deployment |
| CDP-2 | A Ranger deny is obeyed by refusing to index, never by mirroring it | Graph supports deny ACEs, but a mirrored deny only protects while the translation is right every time and a drifted translation fails open. `PushAclEntry` has no deny at all — the type cannot express one — and `RoutingEvaluator` routes any denied table or path to a live query | `CdpConnectorTests.A_table_with_a_deny_policy_is_routed_rather_than_mirrored`, and `A_deny_on_a_path_stops_its_subtree_being_indexed` |
| CDP-3 | Item permissions come from the cluster, and fail closed | Grants are derived per file from the owning group's read bit, named `group:` ACL entries and Ranger path policies; group principals only, never users, never everyone. A cluster group that does not resolve to an Entra group is **dropped**, and an item left with no grants is **skipped and named in the log** — there is deliberately no fallback to the connection-wide ACL, which would widen the audience of exactly the item whose permissions could not be established | `CdpConnectorTests.An_unresolved_group_is_dropped_rather_than_guessed`; `CdpSourceTests.A_file_nobody_can_be_granted_is_skipped_before_its_content_is_read` asserts the file is never even fetched. Both are in the tripwire |
| CDP-4 | Permission changes at the source reach the index within a bounded time | A permission change does not alter a file's modification time, so an incremental crawl never revisits the file and its indexed ACL would stay stale indefinitely. `Settings:FullRecrawlEveryRuns` makes every Nth run ignore the watermark and re-derive every grant; that number of runs is therefore the **upper bound on ACL staleness** and belongs in the deployment's risk register. Setting it to zero is accepted and reported at startup | `CdpSourceTests.The_periodic_full_recrawl_ignores_the_marker`, in the tripwire. `CdpConnectorTests.Turning_off_the_full_recrawl_is_reported_because_it_is_the_acl_staleness_bound` proves the startup report |
| CDP-5 | Nothing is indexed while the access policies are unreadable | Ranger is what says which tables and paths may be indexed at all. `RangerPolicyClient` treats an unreachable or refusing Ranger as fatal — the run stops before the first listing rather than defaulting to indexing a source whose rules it cannot read | `CdpSourceTests.An_unreadable_ranger_stops_the_run_rather_than_indexing_anyway`, in the tripwire |
| CDP-6 | The cluster is reached with no credential in the process | Hive and Impala authenticate through the ODBC driver's SSPI plugin (`AuthMech=1;UseOnlySSPI=1`) and HDFS through HTTP Negotiate, both from the logon session of the service account — a gMSA, whose password Active Directory owns and rotates. The connection string is composed from typed settings rather than pasted, so there is no configuration key to put a credential in, and `HiveConnectionStringFactory.Inspect` rejects a credential keyword or a TLS downgrade smuggled through `Settings:HiveExtraOptions` | `CdpConnectorTests.The_composed_odbc_string_authenticates_with_kerberos_and_carries_no_credential` and `A_credential_or_a_downgrade_in_the_extra_options_is_refused`, the latter in the tripwire |
| CDP-7 | A failed crawl cannot advance a watermark | Enforced by the engine rather than by connector discipline: `PushEngine` calls `IPushSource.OnItemCommittedAsync` only after the write returned, `OnCrawlCompletedAsync` only when the enumeration ended with no failed write, and neither during a dry run. A source cannot checkpoint something that was merely read | `PushSourceTests.A_write_that_dies_leaves_the_watermark_on_the_last_item_that_landed`, `A_dry_run_writes_nothing_and_commits_nothing`, and `CdpSourceTests.The_watermark_moves_only_over_items_the_engine_confirmed`. All in the tripwire |

**Allowlisted configuration paths.** The build scan permits exactly two paths
whose names match the credential pattern but whose values are not credentials:
`KeyVault:Secrets:*` (the *name* of a vault secret) and
`KeyVault:SecretCacheTtlMinutes` (a number). Both are declared in
`AppSettingsSecretScanAllowedPaths` in `build/SecretHygiene.targets`.

---

## 3. Dependency notes for the scan

Package versions for the push path live in `src/PushCore/PushCore.csproj`
alone. The executables reference the engine and declare no package of their own,
so the Kiota advisory pin cannot be applied to one push tool and forgotten on
another, and a connector added later inherits it without being told.

The test project references the engine, both push tools and the connector, so its
dependency graph includes `Microsoft.Graph` and Kiota. That is a reference to
projects already in this solution rather than a new dependency: the package set
the repository resolves is unchanged, which `build/Test-OfflinePackageList.ps1`
confirms. Nothing internal is exposed — the types the tests reach are public,
and no `InternalsVisibleTo` was added.

All four projects target **net10.0**, the current LTS. `Grpc.Core` 2.40.0 and its
generated contract code are `netstandard2.0`, so the retarget does not disturb
them; the CI build on windows-latest is the evidence.

The target framework is a single property, `ConnectorTargetFramework` in
`Directory.Build.props`, and there is a second release line built with it set to
`net9.0` for Visual Studio 2022, which has no .NET 10 support. Check which one
you are reviewing: the framework is in the package name of a local build, in the
`-net9` suffix of a release tag, and in `Directory.Build.props` of the source
tree inside either package. The code is identical; the dependency set is not.
The `net9.0` graph is twelve packages larger, because `System.Text.Json`,
`System.IO.Pipelines` and their neighbours are NuGet packages there and part of
the shared framework on `net10.0`. Those extra packages are Microsoft's, at the
9.0.x servicing level, and they appear in that line's own offline package list.

The release package is published `--self-contained`, so the .NET runtime ships
inside the zip and the target server needs no runtime install. That widens the
artefact's surface — the runtime is now part of what you are accepting — in
exchange for removing a download from a locked-down server. The scan should
therefore treat the release asset, not only the source tree, as the unit under
review.

A build machine with no route to `api.nuget.org` is served by
`build/Get-OfflinePackages.ps1`, which stages the 77 packages a restore needs
(68 for a plain build, 4 runtime packs for the self-contained publish, 5 for the
optional OTLP configuration). Those packages come from the same flat container
endpoint a NuGet client would use, so provenance is unchanged; what the folder
removes is the network hop, not a control. The list is data that can rot, so
`build/Test-OfflinePackageList.ps1` checks it against what NuGet actually
resolved and CI fails on any difference (BLD-5).


| Package | Version | Note |
|---|---|---|
| `Grpc.Core` | 2.40.0 | **Past end of support (May 2021).** Flagged deliberately. The Microsoft connector contracts in `Contracts/` generate against it, and migrating to `Grpc.AspNetCore` means regenerating the server stubs and rewriting the host, which is out of scope for this change. It listens on loopback only, is not exposed to the network, and the agent is the only client. Track it as an accepted risk with a review date. |
| `Google.Protobuf` | 3.18.0 | Pinned to match the `Grpc.Tools` code generator. |
| `Grpc.Tools` | 2.40.0 | Build-time only (`PrivateAssets=all`); not shipped. |
| `Microsoft.Data.SqlClient` | 5.2.2 | Current 5.x servicing line. |
| `Azure.Identity` | 1.21.0 | Raised from 1.13.2, which NuGet reports as deprecated. |
| `Azure.Security.KeyVault.Secrets` | 4.11.0 | |
| `Serilog` | 4.4.0 | Raised from 3.1.1: `Serilog.Sinks.EventLog` 4.0.0 requires Serilog 4.x, and the event log sink is control LOG-2. Dependabot has since moved it within 4.x. |
| `Serilog.Sinks.OpenTelemetry` | 4.1.1, **not referenced by default** | The OTLP exporter pulls in `Grpc.Net.Client` and requires `Google.Protobuf` 3.26.1 or later, doubling the gRPC surface in the dependency scan for a feature that ships disabled. It is behind an MSBuild switch: `dotnet build -p:EnableOtlpExporter=true`, or `Build.ps1 -EnableOtlpExporter`. Enabling it also raises `Google.Protobuf` to 3.35.1, because the pinned 3.18.0 cannot satisfy the sink; the code generator is unchanged, so the contract types are identical either way. CI builds the solution in **both** configurations, so the optional path cannot rot into an unbuildable state. `Logging:Otlp:Enabled` controls it at runtime; if the flag is set but the build excluded the package, startup says so on stderr. |
| `Microsoft.Graph` | 5.105.0 | **`SqlGraphPush` and `SqlHierarchyPush` only.** Not referenced by the connector or the Security project. |
| `Microsoft.Kiota.Abstractions` | 1.22.2, **pinned deliberately** | Neither push tool uses it directly. The reference exists only to raise a transitive dependency past GHSA-7j59-v9qr-6fq9 / CVE-2026-44503 (High): the Kiota `RedirectHandler` leaks `Cookie` and `Proxy-Authorization` headers on a cross-host redirect, fixed in 1.22.0. `Microsoft.Graph` 5.105.0 still asks for 1.21.1. Both push tools carry the same pin; remove them together once the Graph SDK's own dependency reaches 1.22.0. |

Graph application permissions (the two push tools only, admin consent, public
certificate uploaded to the app registration):
`ExternalConnection.ReadWrite.OwnedBy`, `ExternalItem.ReadWrite.OwnedBy`.
Both tools need the same pair and can share one registration, which then owns
both connections — `OwnedBy` scopes to what the calling app created.
The agent-hosted connector holds **no** Graph permission.

[`docs/APP-REGISTRATION.md`](APP-REGISTRATION.md) specifies every identity
in this deployment — the connector agent's own registration, this connector's
Key Vault identity, and the push tools — permission by permission, with both
credential types, the hardening settings to apply to each, and what each
identity must never be granted.

---

## 4. Deviations and accepted risks

1. **`Authentication=ActiveDirectoryDefault` is not used.** The brief asked for
   that keyword together with `SqlConnection.AccessToken`; SqlClient throws when
   both are supplied. The token path is implemented (`SqlConnectionFactory`
   acquires `https://database.windows.net/.default` with the certificate
   credential and sets `AccessToken`), because `ActiveDirectoryDefault` carries
   the same non-deterministic fallback chain that rules out
   `DefaultAzureCredential` elsewhere in this document.
2. **`Connector:UseTls` defaults to `true`.** gRPC Core needs PEM key material,
   which is exported in memory from the store certificate, so the private key
   must be marked exportable at import time. If it is not, startup fails with a
   message naming the fix. Turning TLS off leaves the traffic on loopback only,
   readable by a local process; that is a conscious downgrade, not a default.
3. **The `Security` project also contains SQL connection construction, log
   scrubbing, shared option binding and content truncation.** The brief scoped it
   to secrets, certificates and the credential factory. Both consuming projects
   need those pieces, and duplicating them would be worse than widening the
   project. It still references neither the Graph SDK nor the gRPC contracts.
4. **Wizard-supplied credentials are ignored.** Configuration on the host is
   authoritative for SQL access. Anything typed into the credential fields of the
   connection wizard is reported at `Warning` (type only, never a value) and
   discarded; `deploy/Manifest.json` therefore advertises `Windows` only.
5. **`Grpc.Core` 2.40.0 is out of support** — see the dependency table.
6. **The deployment package contains a copy of the source tree** under
   `source/`, at the customer's explicit direction, so that one download serves
   both deployment and rebuild. The consequence is that application source code
   is present on the connector host, which is a wider footprint than a binary
   deployment and is worth a decision from you rather than a silent acceptance.
   It contains no build output, no repository history and no credentials; CI
   fails if `bin/`, `obj/` or `.git/` appear inside it. To reverse this, remove
   the source staging block in `Build.ps1` and publish the source archive as a
   separate release asset instead — GitHub already attaches one to every tag.

7. **A DPAPI-backed secret store was added, against the original control set.**
   The brief excluded file-based and DPAPI secret providers. `Auth:Mode` now
   accepts `ClientSecret`, which reads the secret from Windows Credential
   Manager, and Credential Manager is DPAPI backed. This was a customer decision
   for tenants that will not issue a client certificate to this application.

   What is kept: the secret is absent from source, `appsettings.json`,
   environment variables and deployment scripts. Configuration holds only the
   entry's name. The value is read at startup, held in memory, and never logged
   — the redaction canary test covers it like any other secret.

   What is given up, and should be weighed before choosing this mode:

   - **The protection is DPAPI under one account on one machine.** Anything
     running as the service account can read the secret, including a process
     that is not this connector. A certificate's private key can be marked
     non-exportable; a secret cannot.
   - **No rotation without a restart.** The credential is read once at startup,
     so replacing it takes a service restart. Certificate mode rotates without
     one, because `RotatingCertificateCredential` tries each candidate in turn.
   - **Expiry is invisible here.** Certificate mode warns daily for 30 days
     before expiry (CERT-6). A client secret's expiry is known only to Entra, so
     it has to be tracked outside this service.
   - **The credential is per account.** An entry stored by an administrator is
     unreadable by the service account, and a gMSA cannot log on to store one
     interactively. `docs/RUNBOOK.md` has the two routes that work.

   Certificate remains the default and the recommendation. This mode exists so
   that a tenant policy against issuing certificates does not become a reason to
   put a secret in a configuration file, which is the outcome it is competing
   with.

---

## 5. Running the evidence

```powershell
dotnet test SqlTicketsConnector.sln                                  # 179 tests, no live dependencies
dotnet build build\SecretHygiene.proj -t:ScanAppSettingsForSecrets    # configuration hygiene
gitleaks detect --config .gitleaks.toml --redact                      # repository history
pre-commit run --all-files                                            # the same checks a developer gets

# Dependency audit. Both must come back clean before a release; the only
# expected result is the xunit 2.9.3 "Legacy" deprecation in the test project,
# which is a supersession by xunit.v3, not a vulnerability.
dotnet list SqlTicketsConnector.sln package --vulnerable --include-transitive
dotnet list SqlTicketsConnector.sln package --deprecated
```

The test suite needs no tenant, no vault, no SQL instance and no network:
certificates are generated in memory, Key Vault and SQL are fakes.
