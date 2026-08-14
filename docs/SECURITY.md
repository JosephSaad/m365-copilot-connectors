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
| **Tenant relationship** | Held by the GCA. **This process never calls Microsoft Graph** and holds no Graph permission. |
| **Certificate use in `SqlTicketsConnector`** | Azure Key Vault access and the loopback TLS listener only. |
| **Certificate use in `SqlGraphPush`** | Microsoft Graph. That tool is a separate, operator-run utility. |
| **Data at rest in this process** | None. Rows are streamed, never spooled to disk. |

The connector project has no reference to the Microsoft Graph SDK. A reference
appearing there in a future change is a review failure, not a refactor:
`src/SqlTicketsConnector/SqlTicketsConnector.csproj` should list only
`Google.Protobuf`, `Grpc.Core`, `Grpc.Tools`, `Microsoft.Data.SqlClient`,
`Serilog` and its sinks, plus the `SqlTicketsConnector.Security` project.

---

## 2. Control mapping

### Secret handling

| ID | Control | Implementation | Evidence |
|---|---|---|---|
| SEC-1 | No secret, password, PFX or credential-bearing connection string in source, configuration, environment or logs | `src/SqlTicketsConnector/appsettings.json` and `src/SqlGraphPush/appsettings.json` hold vault URI, secret *names*, tenant ID, client ID, thumbprints, server and database only | `build/SecretHygiene.targets` fails the build on a credential-shaped key with a value; `.gitleaks.toml` scans history and staged changes |
| SEC-2 | Secrets resolved at runtime, held in memory only | `Security/Secrets/KeyVaultSecretProvider.cs`, `Security/Sql/SqlConnectionFactory.cs` (the password enters a `SqlConnectionStringBuilder` and is never logged or persisted) | `ConfigurationTests.SqlLogin_requires_a_resolved_password_and_keeps_it_out_of_configuration` |
| SEC-3 | `ISecretProvider` with a Key Vault production implementation | `Security/Secrets/ISecretProvider.cs`, `KeyVaultSecretProvider.cs` | — |
| SEC-4 | Environment provider is development-only and refuses to run in Production | `Security/Secrets/EnvironmentSecretProvider.cs` — constructor throws when `Environment` is `Production`, and logs a prominent warning otherwise | `ConfigurationTests.The_environment_secret_provider_refuses_to_run_in_production` |
| SEC-5 | In-memory cache with configurable TTL, default 60 minutes, never to disk | `Security/Secrets/CachingSecretProvider.cs` | `SecretCacheTests.Cached_value_is_reused_inside_the_time_to_live`, `.Value_is_resolved_again_once_the_time_to_live_expires` |
| SEC-6 | Authentication failure invalidates the cached secret and retries **exactly once** | `Security/Secrets/SecretRefreshRetryPolicy.cs`, applied in `Security/Sql/SqlConnectionFactory.OpenAsync` | `SecretCacheTests.Authentication_failure_invalidates_the_secret_and_retries_exactly_once`, `.A_second_authentication_failure_is_surfaced_rather_than_retried_again` |
| SEC-7 | No file-based or DPAPI secret provider exists | Only two implementations exist in `Security/Secrets/` | Directory listing |

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
| CERT-9 | No `DefaultAzureCredential` in production paths | `Security/Credentials/TokenCredentialFactory.cs` returns `ManagedIdentityCredential` or `RotatingCertificateCredential`, and throws otherwise | Grep: `DefaultAzureCredential` appears nowhere in the solution |
| CERT-10 | Private key material never written to disk, including for TLS | `ConnectorServer.BuildServerCredentials` exports PEM in memory from the store certificate | — |

### SQL access

| ID | Control | Implementation | Evidence |
|---|---|---|---|
| SQL-1 | Authentication preference order: Entra ID token, Windows integrated, SQL login from Key Vault | `Security/Sql/SqlConnectionStringFactory.Build`, `SqlConnectionFactory.OpenCoreAsync`. **Shipped configuration uses `WindowsIntegrated`** | `ConfigurationTests.Windows_integrated_connections_carry_no_credential_and_force_encryption` |
| SQL-2 | `Encrypt=true` on every path | `SqlConnectionStringFactory.Build` sets it unconditionally | Same test |
| SQL-3 | `TrustServerCertificate=true` rejected in Production, wherever it is configured | `SqlConnectionStringFactory.InspectExtraOptions` (startup and per-call), applied to the wizard-supplied data source URL in `Connector/AgentRequestInspector.cs` | `ConfigurationTests.TrustServerCertificate_is_rejected_in_production` |
| SQL-4 | No credential in any operator-editable connection text | `InspectExtraOptions` rejects `Password` and `User ID` | `ConfigurationTests.Credentials_in_operator_supplied_connection_text_are_rejected` |
| SQL-5 | Least privilege: `SELECT` on `dbo.Tickets`, nothing else | `sql/01-least-privilege.sql`, including explicit `DENY` and a verification query | Run the verification query at the end of the script |
| SQL-6 | Transient faults retried, not surfaced as crawl failures | `ConnectRetryCount`/`ConnectRetryInterval` in the connection string; `Security/Sql/SqlErrorClassifier.cs` classifies by error number; transient failures return `RetryDetails` with `ExponentialBackOff` | `ConnectorCrawlerServiceImpl.BuildFailureStatus` |

### Access control on indexed items

| ID | Control | Implementation | Evidence |
|---|---|---|---|
| ACL-1 | Entra group principals, never "everyone" | `Connector/AclBuilder.cs` builds `PrincipalType.Group` + `IdentityType.AadId` entries from `Acl:GrantGroupObjectIds` | `ContentAndSchemaTests.A_built_item_carries_truncated_content_and_the_configured_acl` |
| ACL-2 | Startup fails when no ACL is configured, rather than defaulting to everyone | `AclOptions.Validate`, `AclBuilder.Build` throws on an empty list | `ContentAndSchemaTests.An_empty_acl_configuration_fails_loudly_instead_of_granting_everyone`, `ConfigurationTests.An_empty_acl_section_fails_validation` |
| ACL-3 | The direct push tool applies the same principals | `src/SqlGraphPush/Program.cs` builds `AclType.Group` entries from the same configuration section | Code review |

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

**Allowlisted configuration paths.** The build scan permits exactly two paths
whose names match the credential pattern but whose values are not credentials:
`KeyVault:Secrets:*` (the *name* of a vault secret) and
`KeyVault:SecretCacheTtlMinutes` (a number). Both are declared in
`AppSettingsSecretScanAllowedPaths` in `build/SecretHygiene.targets`.

---

## 3. Dependency notes for the scan

All four projects target **net10.0**, the current LTS. `Grpc.Core` 2.40.0 and its
generated contract code are `netstandard2.0`, so the retarget does not disturb
them; the CI build on windows-latest is the evidence.

The release package is published `--self-contained`, so the .NET runtime ships
inside the zip and the target server needs no runtime install. That widens the
artefact's surface — the runtime is now part of what you are accepting — in
exchange for removing a download from a locked-down server. The scan should
therefore treat the release asset, not only the source tree, as the unit under
review.

A build machine with no route to `api.nuget.org` is served by
`build/Get-OfflinePackages.ps1`, which stages the 76 packages a restore needs
(68 for a plain build, 3 runtime packs for the self-contained publish, 5 for the
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
| `Microsoft.Graph` | 5.105.0 | **`SqlGraphPush` only.** Not referenced by the connector or the Security project. |
| `Microsoft.Kiota.Abstractions` | 1.22.2, **pinned deliberately** | `SqlGraphPush` does not use it directly. The reference exists only to raise a transitive dependency past GHSA-7j59-v9qr-6fq9 / CVE-2026-44503 (High): the Kiota `RedirectHandler` leaks `Cookie` and `Proxy-Authorization` headers on a cross-host redirect, fixed in 1.22.0. `Microsoft.Graph` 5.105.0 still asks for 1.21.1. Remove the pin once the Graph SDK's own dependency reaches 1.22.0. |

Graph application permissions (`SqlGraphPush` only, admin consent, public
certificate uploaded to the app registration):
`ExternalConnection.ReadWrite.OwnedBy`, `ExternalItem.ReadWrite.OwnedBy`.
The agent-hosted connector holds **no** Graph permission.

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

---

## 5. Running the evidence

```powershell
dotnet test SqlTicketsConnector.sln                                  # 40 tests, no live dependencies
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
