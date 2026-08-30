<#
.SYNOPSIS
    Reports how a Copilot connector connection is configured to SURFACE — labels,
    display templates, activity settings, content category — and names the things
    Microsoft Graph cannot tell you, so the gap is visible rather than assumed.

.DESCRIPTION
    Read-only. It acquires an app-only token and issues GETs. It creates,
    changes and deletes nothing, and it can be run in a production tenant during
    business hours without a change record.

    This exists because "the connector works" and "a user sees something useful"
    are different claims with different evidence, and the second one is the one
    that decides whether anybody adopts it. Verify-GraphConnection.ps1 proves the
    first: the connection is there, the schema is registered, the item was
    ingested. This proves the second, as far as an API can:

      1. Which semantic labels the registered schema carries, and which of the
         ones Microsoft names as REQUIRED for Copilot are missing. Microsoft's
         wording is exact and worth quoting: "The iconUrl, title, and url labels
         must be applied for content to surface in Copilot"
         (graph/connecting-external-content-experiences).

      2. Whether every labelled property is retrievable. Labels on a
         non-retrievable property are inert — "Properties must be marked as
         retrievable before they can be mapped to labels"
         (graph/connecting-external-content-manage-schema).

      3. Whether the connection has display templates (searchSettings), a
         urlToItemResolver (activitySettings), and a contentCategory other than
         uncategorized.

      4. What no Graph call can answer: whether a search vertical exists, whether
         an admin-centre result type exists, and whether the connection is ticked
         for inline results in the All vertical. Microsoft publishes no API for
         any of the three. They are Microsoft 365 admin centre objects and a
         Search Administrator owns them. This script prints the URLs and what to
         look at, because the alternative is a reader concluding from silence
         that there is nothing to check.

    THE ENUM TRAP. GET /schema without the Prefer: include-unknown-enum-members
    header returns labels the running API version does not enumerate as the
    literal string "unknownFutureValue". On this connection that made
    containerName and containerUrl look like they had been registered with a
    garbage label, when both are stored correctly. The header costs nothing and
    is always sent here. If you see unknownFutureValue in some other tool's
    output, suspect the header before you suspect the schema.

.PARAMETER ConfigPath
    appsettings.json for the push tool that owns the connection. The credential
    is resolved exactly as the tool resolves it — certificate from the configured
    store, or the client secret from the named Credential Manager entry. Never a
    prompt.

.PARAMETER ConnectionId
    The connection to report on. Default: consultingwork. Pass 'sqltickets' for
    the tickets connector.

.PARAMETER All
    Report on every connection this app owns, rather than one.

.EXAMPLE
    .\Get-SearchSurfacing.ps1 -ConfigPath C:\Connectors\SqlHierarchyPush\appsettings.json

.EXAMPLE
    .\Get-SearchSurfacing.ps1 -ConfigPath .\appsettings.json -All

.NOTES
    Permissions. Everything here is satisfied by ExternalConnection.ReadWrite.OwnedBy,
    which the push app already holds — Get externalConnection and Get schema both
    name it as the least-privileged permission. There is no read-only OwnedBy
    variant; ExternalConnection.Read.All exists but is tenant-wide and needs
    admin consent, so OwnedBy on the owning app is the smaller grant.

    OwnedBy means owned by THIS app registration. Run this with a different
    app's credentials and the connection is not forbidden, it is absent — a 404,
    or an empty list. An empty list is not evidence that a connection does not
    exist.

    Exit codes: 0 nothing blocking, 1 a documented Copilot requirement is unmet,
    2 configuration or credential problem.
#>

[CmdletBinding()]
param(
    [string]$ConfigPath = '.\appsettings.json',
    [string]$ConnectionId = 'consultingwork',
    [switch]$All
)

$ErrorActionPreference = 'Stop'
$script:blocking = 0

# Resolved here, not in a param() default: $PSScriptRoot is empty inside a
# param() default under Windows PowerShell 5.1.
. (Join-Path $PSScriptRoot 'GraphPushAuth.ps1')

if ($PSVersionTable.PSVersion.Major -lt 6) {
    [Net.ServicePointManager]::SecurityProtocol =
        [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
}

function Step([string]$name) { Write-Host "`n== $name ==" -ForegroundColor Cyan }
function Good([string]$msg) { Write-Host "  ok    $msg" -ForegroundColor Green }
function Gap([string]$msg) { Write-Host "  GAP   $msg" -ForegroundColor Red; $script:blocking++ }
function Soft([string]$msg) { Write-Host "  miss  $msg" -ForegroundColor Yellow }
function Note([string]$msg) { Write-Host "  note  $msg" -ForegroundColor DarkGray }

# Microsoft names these three as required for content to surface in Copilot at
# all. They are treated as blocking; everything else is advice.
$requiredForCopilot = @('title', 'url', 'iconUrl')

# In Microsoft's own descending order of impact on discovery, from
# graph/connecting-external-content-manage-schema, "Relevance".
$recommended = @(
    'lastModifiedDateTime',
    'lastModifiedBy',
    'fileName',
    'fileExtension',
    'createdBy',
    'createdDateTime',
    'containerName',
    'containerUrl'
)

# ---------------------------------------------------------------------------

Step '0. Credential'

if (-not (Test-Path $ConfigPath)) {
    Write-Host "  no appsettings.json at $ConfigPath" -ForegroundColor Red
    exit 2
}

$config = Get-PushConfig -Path $ConfigPath

$certificate = $null
$secret = $null

if ($config.Auth.Mode -eq 'ClientSecret') {
    $target = $config.Auth.ClientSecretCredentialTarget
    if (-not $target) {
        Write-Host '  Auth:Mode is ClientSecret but Auth:ClientSecretCredentialTarget is empty.' -ForegroundColor Red
        exit 2
    }
    $secret = Get-StoredClientSecret -Target $target
    if (-not $secret) {
        Write-Host "  no Credential Manager entry '$target' readable by $env:USERNAME." -ForegroundColor Red
        Note 'Credential Manager is per account. Run as the account that runs the push tool.'
        exit 2
    }
    Good "client secret from Credential Manager entry '$target'"
}
else {
    $certificate = Get-PushCertificate -Config $config
    if (-not $certificate) {
        Write-Host '  no configured certificate with a usable private key.' -ForegroundColor Red
        exit 2
    }
    Good "certificate $($certificate.Thumbprint)"
}

$auth = Get-PushToken -Config $config -Certificate $certificate -ClientSecret $secret
if (-not $auth.Token) {
    Write-Host "  token request failed: $($auth.Error)" -ForegroundColor Red
    if ($auth.Detail) { Note $auth.Detail }
    if ($auth.Advice) { Note $auth.Advice }
    exit 2
}

Good "token acquired for app $($config.Auth.ClientId)"
Note "roles in the token: $($auth.Roles -join ', ')"

$headers = @{
    Authorization = "Bearer $($auth.Token)"
    Prefer        = 'include-unknown-enum-members'
}

function Get-Graph {
    param([Parameter(Mandatory)][string]$Uri)
    Invoke-RestMethod -Method GET -Uri $Uri -Headers $headers
}

# ---------------------------------------------------------------------------

Step '1. Connections this app owns'

$targets = @()
try {
    $list = Get-Graph 'https://graph.microsoft.com/v1.0/external/connections'
    foreach ($item in @($list.value)) {
        Note "$($item.id) — state $($item.state) — $($item.name)"
    }
    if ($All) {
        $targets = @($list.value | ForEach-Object { $_.id })
    }
    else {
        $targets = @($ConnectionId)
    }
}
catch {
    Write-Host "  GET /external/connections failed: $($_.Exception.Message)" -ForegroundColor Red
    Note 'An empty or failed list under OwnedBy is not proof of absence: it means this app did not create them.'
    exit 2
}

foreach ($id in $targets) {

    $base = "https://graph.microsoft.com/v1.0/external/connections/$id"

    Step "2. $id — connection settings"

    $connection = $null
    try {
        $connection = Get-Graph ($base + '?$select=id,name,description,state,contentCategory,searchSettings,activitySettings,connectorId')
    }
    catch {
        Write-Host "  GET $base failed: $($_.Exception.Message)" -ForegroundColor Red
        Note "404 here means this app did not create '$id'. OwnedBy hides other apps' connections rather than forbidding them."
        $script:blocking++
        continue
    }

    Good "name: $($connection.name)"
    Good "state: $($connection.state)"

    # The description is not decoration. Microsoft: "Rich descriptions improve
    # the likelihood of content displayed in Copilot", and the recommendation is
    # that it answers what the content is, what users call it, and when in their
    # workflow they reach for it.
    if (-not $connection.description) {
        Soft 'description is empty. Copilot uses it to decide whether this connection is relevant to a prompt.'
    }
    elseif ($connection.description.Length -lt 40) {
        Soft "description is $($connection.description.Length) characters: '$($connection.description)'. Microsoft recommends it say what the content is, what users call it, and when they reach for it."
    }
    else {
        Good "description: $($connection.description)"
    }

    # contentCategory is the cheapest Copilot-side improvement available: one
    # PATCH, no reingestion. Microsoft: it "helps Microsoft Graph optimize
    # relevance, ranking, and semantic understanding".
    if (-not $connection.contentCategory -or $connection.contentCategory -eq 'uncategorized') {
        Soft "contentCategory is 'uncategorized'. Setting it signals the nature of the content to the ranker; crm, taskManagement and knowledgeBase are the plausible values for this source."
    }
    else {
        Good "contentCategory: $($connection.contentCategory)"
    }

    # ingestedItemsCount is beta-only. It is worth one extra call because "the
    # templates are perfect and the index is empty" is a real state.
    try {
        $beta = Get-Graph "https://graph.microsoft.com/beta/external/connections/$id"
        Good "ingestedItemsCount: $($beta.ingestedItemsCount) (beta only; refreshed every 15 minutes)"
        Note "enabledContentExperiences: $($beta.enabledContentExperiences -join ', ') — 'search' is the only value Microsoft publishes for this property; it is NOT a Copilot on/off switch."
    }
    catch {
        Note "beta read for the item count failed: $($_.Exception.Message)"
    }

    Step "3. $id — display templates (searchSettings)"

    $templates = @()
    if ($connection.searchSettings -and $connection.searchSettings.searchResultTemplates) {
        $templates = @($connection.searchSettings.searchResultTemplates)
    }

    if ($templates.Count -eq 0) {
        Soft 'no display templates. Microsoft Search renders these results with the platform default layout: title, snippet, nothing else.'
        Note 'Register them with Set-SearchResultTypes.ps1. Maximum of two per connection.'
    }
    else {
        foreach ($template in ($templates | Sort-Object priority)) {
            $rules = if ($template.rules) {
                ($template.rules | ForEach-Object { "$($_.property) $($_.operation) [$($_.values -join ', ')]" }) -join " $($template.rules[0].valuesJoinedBy) "
            }
            else { 'no rules (catch-all)' }
            Good "priority $($template.priority): '$($template.id)' — $rules"
        }
        if (-not ($templates | Where-Object { -not $_.rules })) {
            Soft 'every template has rules. An item matching none of them falls back to the platform default layout, silently.'
        }
    }

    Step "4. $id — activity settings"

    $resolvers = @()
    if ($connection.activitySettings -and $connection.activitySettings.urlToItemResolvers) {
        $resolvers = @($connection.activitySettings.urlToItemResolvers)
    }

    if ($resolvers.Count -eq 0) {
        Soft 'no urlToItemResolvers. When somebody pastes a source URL into Teams or Outlook, Microsoft 365 cannot tell that it names an indexed item, so the share is not counted as a signal.'
        Note 'This is the ONLY activity signal this connector can realistically produce. See docs/COPILOT-SURFACING.md section 7.'
    }
    else {
        foreach ($resolver in ($resolvers | Sort-Object priority)) {
            Good "priority $($resolver.priority): $($resolver.urlMatchInfo.baseUrls -join ', ') pattern $($resolver.urlMatchInfo.urlPattern) -> $($resolver.itemId)"
        }
    }

    Step "5. $id — semantic labels"

    $schema = $null
    try {
        $schema = Get-Graph "$base/schema"
    }
    catch {
        Write-Host "  GET $base/schema failed: $($_.Exception.Message)" -ForegroundColor Red
        $script:blocking++
        continue
    }

    $labelled = @{}
    $retrievable = 0
    $searchable = 0

    foreach ($property in @($schema.properties)) {
        if ($property.isRetrievable) { $retrievable++ }
        if ($property.isSearchable) { $searchable++ }
        foreach ($label in @($property.labels)) {
            if (-not $label) { continue }
            if ($label -eq 'unknownFutureValue') {
                Note "'$($property.name)' reports label 'unknownFutureValue' EVEN WITH the Prefer header — that is a label this API version genuinely does not know, not a header problem."
                continue
            }
            if (-not $labelled.ContainsKey($label)) { $labelled[$label] = @() }
            $labelled[$label] += $property.name
        }
    }

    Note "$($schema.properties.Count) properties; $retrievable retrievable, $searchable searchable"

    foreach ($label in $requiredForCopilot) {
        if ($labelled.ContainsKey($label)) {
            Good "$label -> $($labelled[$label] -join ', ')  (required for Copilot)"
        }
        else {
            Gap "no property carries the '$label' label. Microsoft: the iconUrl, title and url labels MUST be applied for content to surface in Copilot."
        }
    }

    foreach ($label in $recommended) {
        if ($labelled.ContainsKey($label)) {
            Good "$label -> $($labelled[$label] -join ', ')"
        }
        else {
            Soft "no property carries the '$label' label."
        }
    }

    # A label on a property that is not retrievable does nothing at all, and
    # nothing in the API refuses it, so it is the kind of mistake that survives
    # a review.
    foreach ($property in @($schema.properties)) {
        $realLabels = @($property.labels | Where-Object { $_ -and $_ -ne 'unknownFutureValue' })
        if ($realLabels.Count -gt 0 -and -not $property.isRetrievable) {
            Gap "'$($property.name)' carries label(s) $($realLabels -join ', ') but is NOT retrievable. The label is inert."
        }
    }

    # Duplicated labels are the other silent one: Microsoft's validation
    # checklist says each label should map to exactly one property.
    foreach ($label in $labelled.Keys) {
        if (@($labelled[$label]).Count -gt 1) {
            Soft "label '$label' is on $(@($labelled[$label]).Count) properties ($($labelled[$label] -join ', ')). Microsoft's checklist says one label, one property."
        }
    }

    Step "6. $id — what Graph cannot answer"

    Note 'Microsoft publishes no Graph API for any of the following. A Search Administrator has to look.'
    Note ''
    Note 'a) Is this connection ticked for INLINE RESULTS in the All vertical?'
    Note '   Admin centre > Search & intelligence > Customizations > Verticals > All >'
    Note '   Manage connection results. On by default, but the connectors FAQ lists it'
    Note '   first among the reasons custom-connector results do not appear.'
    Note '   https://admin.microsoft.com/Adminportal/Home#/MicrosoftSearch/verticals'
    Note ''
    Note 'b) Does a dedicated VERTICAL exist for this connection?'
    Note '   Optional for the All tab, required for a tab of its own. A vertical WITHOUT'
    Note '   an admin-centre result type renders an empty page rather than a default'
    Note '   layout, which is the single most common way this is got wrong.'
    Note ''
    Note 'c) Does an admin-centre RESULT TYPE exist for this connection?'
    Note '   https://admin.microsoft.com/Adminportal/Home#/MicrosoftSearch/resulttypes'
    Note '   Distinct from the connection-level display templates reported in section 3:'
    Note '   same idea, different object, different owner, no API.'
    Note ''
    Note 'd) Does a delegated search actually return these items?'
    Note '   POST /search/query has no app-only support, so this script cannot ask.'
    Note "   Use: .\Verify-GraphConnection.ps1 -ConnectionId $id -SearchFor `"<term>`""
}

Write-Host ''
if ($script:blocking -eq 0) {
    Write-Host 'No blocking gaps found.' -ForegroundColor Green
    Write-Host 'Items marked "miss" are recommendations, not requirements. Read docs/COPILOT-SURFACING.md for what each one buys.'
    exit 0
}

Write-Host "$script:blocking blocking gap(s). See docs/COPILOT-SURFACING.md." -ForegroundColor Red
exit 1
