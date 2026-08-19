# Entra app registrations — exact specification

Audience: whoever creates the app registrations, and the security reviewer who
has to approve them. Every permission is listed with why it is needed and what
happens without it. Both credential types are specified: certificate and client
secret.

**There are three identities in this deployment, and they are not
interchangeable.** The most common failure in setting this up is granting one
identity's permissions to another.

| # | Identity | Talks to | Graph permissions |
|---|---|---|---|
| 1 | **Graph connector agent** | Microsoft Graph, on your behalf | Yes — three, below |
| 2 | **SqlTicketsConnector** (this service) | Azure Key Vault only | **None. Ever.** |
| 3 | **SqlGraphPush** (optional operator tool) | Microsoft Graph directly | Two, below |

Identity 2 never calls Microsoft Graph. If you find yourself adding
`ExternalItem.ReadWrite.OwnedBy` to it, stop: the agent does the ingestion, and
granting Graph access to the connector widens the blast radius of a service that
runs unattended on a domain-joined server for no functional gain.

Identity 3 is only needed if you use the direct push tool. Skip section 3
otherwise.

---

## Roles you need before starting

| Task | Role |
|---|---|
| Install the agent on-premises | AI Administrator, or Copilot admin |
| Create the app registrations | Application Administrator, or Cloud Application Administrator |
| Grant admin consent | Privileged Role Administrator, or Global Administrator |
| Assign Key Vault RBAC | Owner or User Access Administrator on the vault |
| Create the SQL login and service account | Your DBA and AD team |

Application Administrator can create the registration but **cannot consent** to
application permissions. Plan for two people, or one with both roles.

---

## 1. The Graph connector agent registration

This is the registration the agent's configuration app asks for. It is
Microsoft's requirement, not this project's.

### 1.1 Create it

1. **Entra admin center** → **Identity** → **Applications** → **App
   registrations** → **New registration**.
2. **Name**: something that survives a leaver — `Copilot-GraphConnectorAgent-Prod`.
3. **Supported account types**: **Accounts in this organizational directory
   only (single tenant)**. Anything wider is a finding.
4. **Redirect URI**: **leave empty**. The agent uses client credentials; a
   redirect URI on an app-only registration is an unnecessary attack surface.
5. **Register**, then record the **Application (client) ID** and **Directory
   (tenant) ID**. Neither is a secret.

### 1.2 API permissions, one by one

**API permissions** → **Add a permission** → **Microsoft Graph** → **Application
permissions**. Delegated permissions are not supported here and cause
registration to fail.

| Permission | Type | Required | Why | Without it |
|---|---|---|---|---|
| `ExternalItem.ReadWrite.OwnedBy` | Application | **Always** | Create, update and delete the items this connection owns | The agent cannot write items; crawls run and index nothing |
| `ExternalConnection.ReadWrite.OwnedBy` | Application | **Always** | Create and manage the external connection and its schema | Connection creation fails in the admin centre |
| `Directory.Read.All` | Application | **For SQL-backed connectors** | Resolve directory objects when applying ACLs to items | ACL application can fail, or items index with no permissions and are invisible |

**On `OwnedBy` versus `All`:** Microsoft accepts
`ExternalItem.ReadWrite.All` in place of `ExternalItem.ReadWrite.OwnedBy`.
**Use `OwnedBy`.** It scopes the app to connections it created; `All` grants
read-write over every external connection in the tenant, including ones other
teams own. There is no functional gain for this deployment.

**On `Directory.Read.All`:** this is the one to think about rather than copy.
Microsoft lists it as required for the File share, MS SQL and Oracle SQL
connectors, because those resolve identities when applying ACLs. This connector
grants a **single Entra group by object ID** — no email-to-object-ID lookup — so
it may not be exercised in your tenant. It is also the broadest permission on
the list: read access to all directory data.

Decide deliberately, and record which way you went:

- **Start without it.** Run a full crawl. If items index and are visible to the
  group, you never needed it.
- **Add it if** ACL application fails, or items index but nobody can see them.

3. **Grant admin consent for &lt;tenant&gt;** → **Yes**.
4. Confirm every row shows **Granted for &lt;tenant&gt;**. A permission that is
   added but not consented does nothing, and the agent's failure will not
   mention consent.

### 1.3 Credentials — certificate (preferred)

Certificate is preferred over a client secret: the private key can be held by
the machine, it is not copy-pasteable out of a config screen, and expiry is
visible on the machine rather than only in Entra.

1. **Obtain a certificate.** Your PKI, or self-signed if policy allows.
   Minimum specification:

   | Property | Value |
   |---|---|
   | Key algorithm | RSA |
   | Key length | 2048 minimum; 3072 or 4096 preferred |
   | Signature hash | SHA-256 |
   | Key usage | Digital signature |
   | Subject | A name that identifies the agent, e.g. `CN=gca.contoso.local` |
   | Validity | 12 months. Longer trades rotation practice for convenience |
   | Store | `LocalMachine\My` on the agent server |

   Microsoft's sample script uses `-KeyExportPolicy Exportable`, which it must,
   because the certificate is installed from a `.pfx`. If your PKI can issue
   directly into the machine store, prefer a **non-exportable** key.

2. **Upload the public key**: app registration → **Certificates & secrets** →
   **Certificates** → **Upload certificate** → the `.cer`. Never the `.pfx`.
   Uploading a `.pfx` would put the private key in Entra.

3. **Install on the agent server** into `LocalMachine\My`.

4. **Grant the agent's service account read access to the private key** — the
   step that is missed most often:

   - `certlm.msc` → **Personal** → **Certificates** → right-click the
     certificate → **All Tasks** → **Manage Private Keys** → **Add**
   - **Locations** → select the local computer
   - Type `NT Service\GcaHostService` and select **OK**. **Do not select Check
     Names** — it will not resolve, and that is expected.
   - Grant **Read**. Not Full Control.

5. Record the **thumbprint** and enter it in the agent's configuration app.

### 1.4 Credentials — client secret

Use when your tenant will not issue a certificate for this.

1. App registration → **Certificates & secrets** → **Client secrets** → **New
   client secret**.
2. **Description**: include the rotation due date — `agent-prod-expires-2027-02`.
3. **Expires**: **6 months** where the process supports it, 12 at most. Entra
   allows 24; that is a long time for a credential that grants write access to
   your Copilot index.
4. **Copy the Value immediately.** It is shown once. The **Secret ID** is not
   the secret.
5. Paste it into the agent's configuration app. It is stored by the agent; it
   does not belong in a file, a ticket, or a chat message.
6. **Diary the expiry.** Nothing warns you. The failure mode is every crawl
   failing on a date months from now.

### 1.5 Network — required before any of this works

The agent's health check tests these, and this is what the "Health check
failed" screen means. All outbound HTTPS on 443, from the agent server:

| Endpoint | Purpose |
|---|---|
| `https://login.microsoftonline.com` | Token acquisition |
| `https://graph.microsoft.com/` | Ingestion |
| `https://gcs.office.com/` | Copilot connector service |
| `*.office.com` | Agent service and updates |
| `*.events.data.microsoft.com` | Telemetry |
| `<namespace>.servicebus.windows.net` | Agent reachability from the service |

Government clouds substitute different hosts — GCC uses `gcsgcc.office.com`,
GCCH `gcs.office365.us` and `login.microsoftonline.us`.

**Proxy authentication is not supported.** If your proxy requires
authentication, the agent must be allowed to bypass it. An authenticating proxy
is not a configuration you can work around here.

Verify from the agent server before blaming credentials:

```powershell
tnc gcs.office.com -Port 443            # expect TcpTestSucceeded: True
wget https://gcs.office.com/v1.0/admin/AdminDataSetCrawl/healthcheck   # expect StatusCode: 200
```

A credential type change fixes nothing while these fail.

---

## 2. SqlTicketsConnector — this service

**No Graph permissions. No API permissions of any kind.** This identity exists
to read one secret from Key Vault. If `DataSource:SqlAuthMode` is
`WindowsIntegrated` and no Key Vault is configured, it needs no Entra identity
at all.

### 2.1 Create it

Only needed when `Auth:Mode` is `Certificate` or `ClientSecret`. With
`ManagedIdentity` there is no registration — but managed identity is not
available on a domain-joined on-premises server, which is why it is not the
default here.

1. **App registrations** → **New registration**.
2. **Name**: `SqlTicketsConnector-KeyVault-Prod`. The name should say what it
   reaches, so nobody later assumes it can write to Graph.
3. **Single tenant.** No redirect URI.
4. Record the client ID and tenant ID; both go into `appsettings.json`, which is
   correct — neither is sensitive.

### 2.2 API permissions

**Add none.** Leave the API permissions blade empty apart from the
`User.Read` delegated permission Entra adds by default, which you should
**remove**: this app never signs a user in.

Key Vault access is **not** an API permission. It is a data-plane role
assignment:

1. Key Vault → **Access control (IAM)** → **Add role assignment**
2. Role: **Key Vault Secrets User** — read secret *values*, nothing else. Not
   Secrets Officer, which can write; not Contributor, which can change the
   vault's access model.
3. Assign to the app registration's service principal.
4. **Scope: this vault only.** Not the resource group, not the subscription.

If the vault still uses access policies rather than RBAC, grant **Get** on
Secrets, and nothing else. Prefer migrating to RBAC.

### 2.3 Credentials — certificate

Same specification as §1.3, with these differences:

| Property | Value |
|---|---|
| Subject | `CN=sqltickets.contoso.local` — matches `Auth:CertificateSubject` |
| Store | `LocalMachine\My` on the connector host |
| Private key ACL | **Read** for the connector's service account, e.g. `CONTOSO\svc_gca_reader$` |

Upload the `.cer` to **Certificates & secrets** → **Certificates**, then put the
thumbprint in `Auth:CertificateThumbprints`. That key takes a **list**, in
order: during rotation, put the new thumbprint first and leave the old one
second, so the service proves the new certificate before you remove the old.
`docs/RUNBOOK.md` §1 has the full sequence, which needs no outage.

`Install-Connector.ps1` verifies the certificate is present, in date, and
readable by the service account, and fails the install if not — so this is
checked at install time rather than at first crawl.

### 2.4 Credentials — client secret

Supported from v1.2.8. Read the trade-offs in `docs/SECURITY.md` deviation 7
before choosing it: DPAPI rather than a non-exportable key, a restart to rotate,
and no expiry warning.

1. Create the secret exactly as in §1.4 — 6 months, dated description, copy the
   Value once.
2. **Store it in Windows Credential Manager, as the service account**:

   ```cmd
   cmdkey /generic:SqlTicketsConnector/EntraClientSecret /user:<client-id> /pass:<secret>
   ```

   Credential Manager is per account. An entry stored by an administrator is
   invisible to the service account. For an account that cannot log on
   interactively — a gMSA — use the PsExec or scheduled-task route in
   `docs/RUNBOOK.md` §2a.

3. Configure **only the name**:

   ```json
   "Auth": {
     "Mode": "ClientSecret",
     "TenantId": "…",
     "ClientId": "…",
     "ClientSecretCredentialTarget": "SqlTicketsConnector/EntraClientSecret"
   }
   ```

   Startup rejects a value at that key which looks like a secret rather than a
   name, so a paste fails loudly rather than sitting in a file on a server.

---

## 3. SqlGraphPush — the direct push tool (optional)

Only if you use the operator-run push path. It calls Microsoft Graph directly
and needs no agent.

### 3.1 Permissions

**Microsoft Graph** → **Application permissions**:

| Permission | Type | Why | Why not the wider one |
|---|---|---|---|
| `ExternalConnection.ReadWrite.OwnedBy` | Application | Create the connection and register its schema | `.All` would let this tool rewrite connections owned by other teams |
| `ExternalItem.ReadWrite.OwnedBy` | Application | Write and delete items in its own connection | Same |

Grant admin consent. Nothing else — no `Directory.Read.All` here: this tool
sends group object IDs it was configured with and resolves nothing.

### 3.2 Credentials

Certificate, per §1.3, with the public key uploaded to this registration and the
private key in `CurrentUser\My` on the operator's workstation or jump box —
`CurrentUser`, not `LocalMachine`, because it runs as a person, not a service.

Client secret is supported the same way as §2.4 if certificates are not
available.

---

## 4. Hardening that applies to all three

Do these once per registration. They are cheap and they are what a reviewer
looks for.

1. **Single tenant.** `signInAudience` = `AzureADMyOrg`.
2. **No redirect URIs, no implicit grant, no public client flows.** These are
   app-only identities.
3. **Require assignment** on the enterprise application: Entra → **Enterprise
   applications** → the app → **Properties** → **Assignment required** = **Yes**.
4. **Named owners** — at least two, and people who are still employed. An app
   registration with no owner is nobody's job to rotate.
5. **App instance property lock** (default on for new registrations) prevents
   another admin adding credentials to the app. Leave it on.
6. **Conditional Access for workload identities**, if licensed: restrict each
   service principal to the IP ranges of the servers that legitimately use it.
   A leaked client secret is then unusable from anywhere else. This is the
   single strongest control available for the client secret path.
7. **Credential inventory**: record for each registration — owner, credential
   type, expiry date, where the private key or secret lives, and which server
   uses it. Everything else here is checkable in the portal; this is not.
8. **Monitor sign-ins**: Entra → **Sign-in logs** → **Service principal
   sign-ins**. Filter on each app ID. Sign-ins from an unexpected IP are the
   first sign a credential has been copied.

---

## 5. Verification

Work through these in order. Each one fails differently, and in this order the
failures do not mask each other.

| # | Check | How | Expected |
|---|---|---|---|
| 1 | Network | `tnc gcs.office.com -Port 443` on the agent server | `TcpTestSucceeded: True` |
| 2 | Agent registration | Agent config app → **Health check** | All endpoints reachable, registration succeeds |
| 3 | Consent | Entra → app → API permissions | Every row **Granted** |
| 4 | Connector certificate or secret | `.\Install-Connector.ps1 …` | Certificate found and readable, or the Credential Manager warning is absent |
| 5 | Connector startup | Start the service, read the log | `Auth:Mode is …` then `resolved` — no placeholder errors |
| 6 | Key Vault | Startup log | Secrets resolve; a 403 here means the role assignment or its scope is wrong |
| 7 | End to end | Full crawl from the admin centre | Items appear in Copilot for a member of the granted group |

A useful property of this order: steps 1 to 3 are the tenant and network, 4 to 6
are this server, and 7 is the whole path. If 7 fails but 1 to 6 pass, the
problem is the connection configuration in the admin centre, not identity.

---

## 6. What each identity must never have

Worth stating explicitly, because these are the mistakes that get through
review:

- **SqlTicketsConnector must never hold a Graph application permission.** It
  does not call Graph. `docs/SECURITY.md` §1 makes the same point, and the
  connector project has no Graph SDK reference to make it possible.
- **No identity here needs `ExternalItem.Read.All`,
  `Directory.ReadWrite.All`, `Application.ReadWrite.All`, or any `.All` write
  permission** beyond the `Directory.Read.All` decision in §1.2.
- **No delegated permissions anywhere.** All three are app-only. A delegated
  permission on these registrations means someone has misunderstood the flow.
- **No certificate private key in Entra.** Upload `.cer`, never `.pfx`.
- **No client secret in `appsettings.json`, a deployment script, an environment
  variable, or a ticket.** The build fails on the first, and `docs/SECURITY.md`
  SEC-1 is the control being kept.
