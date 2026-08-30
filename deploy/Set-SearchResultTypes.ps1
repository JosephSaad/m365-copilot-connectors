<#
.SYNOPSIS
    Registers the connection-level search result templates (display templates)
    for a Copilot connector connection, and can put them back the way they were.

.DESCRIPTION
    A connection with no display templates is not broken — it renders with the
    platform default layout, which is a title, a snippet and nothing else. That
    is the whole reason this script exists: the connector can be perfectly
    correct and the result still looks like a stub, and the user's conclusion is
    "the connector does not work". Layout is adoption, not plumbing.

    WHAT THIS DOES, PRECISELY. It PATCHes ONE property of ONE connection:

        PATCH /external/connections/{id}
        { "searchSettings": { "searchResultTemplates": [ ... ] } }

    searchResultTemplates is a collection of displayTemplate objects — an id, a
    priority, an optional rules collection, and a layout that is an Adaptive
    Card. Microsoft caps it at TWO templates per connection
    (graph/api/resources/externalconnectors-searchsettings), which is the single
    constraint that shapes everything below: this source has THREE item types
    and only two templates to spend on them.

    The split chosen here, and why. Time entries are the odd one out — a person,
    a date, a number of hours — while customers and engagements are both
    container-shaped things with a manager, a status and a roll-up. So:

        priority 1, rules: itemType equals TimeEntry   -> the time-entry card
        priority 2, no rules                            -> everything else

    A template with no rules matches every item in the connection, so the second
    is a genuine fallback rather than a third case waiting to be written. Lower
    priority numbers are evaluated first
    (graph/api/resources/externalconnectors-displaytemplate), so the specific
    rule wins and the fallback catches customers, engagements, and any item type
    a future source adds. That last point matters more than it looks: without a
    catch-all, a new item type would silently fall back to the platform default
    and nobody would notice for months.

    WHAT THIS DOES NOT DO. It does not create a search vertical, and it does not
    create an admin-centre result type. Those are different objects, they live in
    the Microsoft 365 admin centre, and Microsoft publishes NO Graph API for
    either — see docs/COPILOT-SURFACING.md section 4. Nothing this script can be
    given will make them appear. Nor does it touch Copilot's own rendering:
    Copilot Search does not use Adaptive Card layouts at all, it renders from
    semantic labels. Run Get-SearchSurfacing.ps1 for the label side.

    SAFETY. The default is a dry run: it validates, prints the exact payload, and
    writes nothing. -Apply is required to PATCH, and when it does it first reads
    the connection's current searchSettings and saves them to a JSON file, so the
    revert is the state that was actually there rather than an assumption about
    it. -Revert restores from that file (or clears the templates when there is no
    file and none were there to begin with).

.PARAMETER ConfigPath
    appsettings.json for the push tool that OWNS this connection. Auth:TenantId
    and Auth:ClientId are read from it, and the credential is resolved the same
    way the tool itself resolves it — certificate from the configured store, or
    the client secret from the Windows Credential Manager entry named in
    Auth:ClientSecretCredentialTarget. There is no prompt: a missing credential
    is an error, because a prompt would test a secret somebody typed rather than
    the one the deployment holds.

.PARAMETER ConnectionId
    The external connection to configure. Default: consultingwork.

.PARAMETER IconUrl
    The square logo shown at the left of every result. Microsoft's guidance is to
    give every result layout an icon, because without one the connector's results
    break the eye's scanning pattern down the results page and read as noise
    (microsoftsearch/customize-results-layout, "Things to consider"). Minimum
    32x32, and it has to be legible on a dark background.

    The default is Microsoft's own generic connector icon from the search CDN —
    the same URL their Confluence Cloud result layout ships with. It is a
    placeholder and it is meant to be replaced: point this at a customer-hosted
    square PNG before anyone but a tester sees the results.

.PARAMETER LayoutPath
    Optional. A directory holding timeentry.json and fallback.json — the two
    Adaptive Card layouts — used instead of the ones embedded below. For the
    case where a designer has iterated in the admin centre's layout designer and
    you want to promote the result rather than retype it.

.PARAMETER BackupPath
    Where -Apply saves the pre-change searchSettings, and where -Revert reads
    them from. Default: .\searchSettings-backup-{connectionId}.json beside this
    script.

.PARAMETER Apply
    Perform the PATCH. Without it, nothing is written to the tenant.

.PARAMETER Revert
    Restore searchSettings from the backup file. When the backup records that
    searchSettings was null — a connection that never had templates — this sends
    an explicit null, which is the only way back to that state.

.PARAMETER OutFile
    Optional. Write the request body to this path as well as printing it. Useful
    for a change record, and for handing the payload to somebody who will run it
    through Graph Explorer instead.

.EXAMPLE
    .\Set-SearchResultTypes.ps1 -ConfigPath C:\Connectors\SqlHierarchyPush\appsettings.json

    Dry run. Validates the layouts against the registered schema, prints the
    payload, changes nothing.

.EXAMPLE
    .\Set-SearchResultTypes.ps1 -ConfigPath .\appsettings.json -Apply -IconUrl https://portal.consultco.com/static/icon32.png

.EXAMPLE
    .\Set-SearchResultTypes.ps1 -ConfigPath .\appsettings.json -Revert

.NOTES
    Permissions. The PATCH needs ExternalConnection.ReadWrite.OwnedBy, which is
    the documented least-privileged permission for Update externalConnection
    (graph/api/externalconnectors-externalconnection-update) and is already held
    by the push app registration. OwnedBy is enough only because the SAME app
    created the connection; run this as any other identity and every call 404s.
    Nothing here needs a Search Administrator, and nothing here can be done by a
    Search Administrator either — see the note above about verticals.

    Exit codes: 0 success (or a clean dry run), 1 the tenant refused or the
    verification read disagreed, 2 configuration or credential problem.
#>

[CmdletBinding()]
param(
    [string]$ConfigPath = '.\appsettings.json',
    [string]$ConnectionId = 'consultingwork',
    [string]$IconUrl = 'https://searchuxcdn.blob.core.windows.net/designerapp/images/DefaultMRTIcon.png',
    [string]$LayoutPath = '',
    [string]$BackupPath = '',
    [switch]$Apply,
    [switch]$Revert,
    [string]$OutFile = ''
)

$ErrorActionPreference = 'Stop'
$script:failures = 0

# $PSScriptRoot is empty inside a param() default under Windows PowerShell 5.1,
# so every path that depends on it is resolved here instead. This has bitten
# this repository before; the rule is that param defaults are literals.
. (Join-Path $PSScriptRoot 'GraphPushAuth.ps1')

if (-not $BackupPath) {
    $BackupPath = Join-Path $PSScriptRoot "searchSettings-backup-$ConnectionId.json"
}

# Windows PowerShell 5.1 inherits the .NET Framework default protocol list. On a
# patched Windows 11 that already includes TLS 1.2, but the failure mode when it
# does not is a bare "underlying connection was closed", so it is named here
# rather than debugged later. PowerShell 7 negotiates TLS itself and the
# ServicePointManager setting is inert there, hence the guard.
if ($PSVersionTable.PSVersion.Major -lt 6) {
    [Net.ServicePointManager]::SecurityProtocol =
        [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
}

function Step([string]$name) { Write-Host "`n== $name ==" -ForegroundColor Cyan }
function Pass([string]$msg) { Write-Host "  PASS  $msg" -ForegroundColor Green }
function Fail([string]$msg) { Write-Host "  FAIL  $msg" -ForegroundColor Red; $script:failures++ }
function Warn([string]$msg) { Write-Host "  WARN  $msg" -ForegroundColor Yellow }
function Note([string]$msg) { Write-Host "  note  $msg" -ForegroundColor DarkGray }

# ---------------------------------------------------------------------------
# The two layouts.
#
# Held as literal Adaptive Card JSON rather than built from hashtables, because
# this is the artefact a designer reviews and the admin centre's layout designer
# emits. It has to be readable as JSON, and diffable against what the designer
# produced, without anybody mentally executing PowerShell.
#
# Binding. ${name} binds to the schema property of that name. The names below
# are exactly the ones SqlHierarchyPush registers, and every one of them is
# marked isRetrievable — a property that is not retrievable is not returned in
# the result set and the template renders the placeholder text instead
# (graph/connecting-external-content-manage-schema, "Retrievable"). The script
# checks this against the live schema before it writes anything.
#
# ${ResultSnippet} is not a schema property. It is the system placeholder for
# the dynamic snippet generated from the item's content, with the query terms
# highlighted, and it is documented on microsoftsearch/customize-results-layout.
# Markdown is deliberately NOT used on that line: the same page says markdown in
# the snippet breaks the query-term highlighting.
#
# The rules Microsoft publishes for these layouts, all obeyed below:
#   * only a subset of Adaptive Card elements renders — TextBlock, RichTextBlock,
#     Image, ColumnSet, ImageSet, FactSet, Container
#   * no px values in element properties
#   * $when guards every element whose property can be absent, and compares like
#     with like: this source omits null properties entirely rather than sending
#     them, so a customer genuinely has no engagementName
#   * wrap and maxLines on anything holding source text, so one long title
#     cannot push the card past the height at which the results page crops it
# ---------------------------------------------------------------------------

$timeEntryLayout = @'
{
    "type": "AdaptiveCard",
    "version": "1.3",
    "body": [
        {
            "type": "ColumnSet",
            "columns": [
                {
                    "type": "Column",
                    "width": "auto",
                    "horizontalAlignment": "center",
                    "items": [
                        {
                            "type": "Image",
                            "url": "__ICON_URL__",
                            "altText": "Logged time",
                            "horizontalAlignment": "center",
                            "size": "small"
                        }
                    ]
                },
                {
                    "type": "Column",
                    "width": "stretch",
                    "spacing": "medium",
                    "items": [
                        {
                            "type": "TextBlock",
                            "text": "[${title}](${url})",
                            "weight": "bolder",
                            "color": "accent",
                            "size": "medium",
                            "maxLines": 2,
                            "wrap": true
                        },
                        {
                            "type": "TextBlock",
                            "text": "${hours} hours on {{DATE(${workDate})}}",
                            "spacing": "small",
                            "wrap": true,
                            "$when": "${workDate != ''}"
                        },
                        {
                            "type": "TextBlock",
                            "text": "Logged by **${consultantName}** against ${containerName}",
                            "spacing": "small",
                            "wrap": true,
                            "maxLines": 2,
                            "$when": "${consultantName != ''}"
                        },
                        {
                            "type": "TextBlock",
                            "text": "${customerName} - ${workType}",
                            "spacing": "small",
                            "wrap": true,
                            "$when": "${workType != ''}"
                        },
                        {
                            "type": "TextBlock",
                            "text": "${ResultSnippet}",
                            "spacing": "small",
                            "wrap": true,
                            "maxLines": 2
                        }
                    ]
                }
            ]
        }
    ],
    "$schema": "http://adaptivecards.io/schemas/adaptive-card.json"
}
'@

$fallbackLayout = @'
{
    "type": "AdaptiveCard",
    "version": "1.3",
    "body": [
        {
            "type": "ColumnSet",
            "columns": [
                {
                    "type": "Column",
                    "width": "auto",
                    "horizontalAlignment": "center",
                    "items": [
                        {
                            "type": "Image",
                            "url": "__ICON_URL__",
                            "altText": "Consulting work",
                            "horizontalAlignment": "center",
                            "size": "small"
                        }
                    ]
                },
                {
                    "type": "Column",
                    "width": "stretch",
                    "spacing": "medium",
                    "items": [
                        {
                            "type": "TextBlock",
                            "text": "[${title}](${url})",
                            "weight": "bolder",
                            "color": "accent",
                            "size": "medium",
                            "maxLines": 2,
                            "wrap": true
                        },
                        {
                            "type": "TextBlock",
                            "text": "${itemType} in ${containerName}",
                            "spacing": "small",
                            "wrap": true,
                            "$when": "${containerName != ''}"
                        },
                        {
                            "type": "TextBlock",
                            "text": "**${engagementName}** - ${practice}, ${status}",
                            "spacing": "small",
                            "wrap": true,
                            "maxLines": 2,
                            "$when": "${engagementName != ''}"
                        },
                        {
                            "type": "TextBlock",
                            "text": "Account manager ${accountManager}",
                            "spacing": "small",
                            "wrap": true,
                            "$when": "${accountManager != '' && engagementName == ''}"
                        },
                        {
                            "type": "TextBlock",
                            "text": "${ResultSnippet}",
                            "spacing": "small",
                            "wrap": true,
                            "maxLines": 2
                        }
                    ]
                }
            ]
        }
    ],
    "$schema": "http://adaptivecards.io/schemas/adaptive-card.json"
}
'@

# ---------------------------------------------------------------------------

Step '0. Configuration and credential'

if (-not (Test-Path $ConfigPath)) {
    Fail "no appsettings.json at $ConfigPath. Point -ConfigPath at the config of the push tool that owns '$ConnectionId'."
    exit 2
}

$config = Get-PushConfig -Path $ConfigPath
Pass "read $ConfigPath"

if ($ConnectionId.Length -lt 3 -or $ConnectionId.Length -gt 32 -or $ConnectionId -notmatch '^[a-zA-Z0-9]+$') {
    Fail "-ConnectionId '$ConnectionId' must be 3 to 32 alphanumeric characters"
    exit 2
}

# Deliberately NOT Get-PushCredential: that function falls back to Read-Host,
# and this script has to be runnable from a scheduled task and from a change
# window with nobody at the keyboard. A missing credential is a hard error here.
$certificate = $null
$secret = $null

if ($config.Auth.Mode -eq 'ClientSecret') {
    $target = $config.Auth.ClientSecretCredentialTarget
    if (-not $target) {
        Fail 'Auth:Mode is ClientSecret but Auth:ClientSecretCredentialTarget is empty.'
        exit 2
    }
    $secret = Get-StoredClientSecret -Target $target
    if (-not $secret) {
        Fail "no Windows Credential Manager entry named '$target' is readable by $env:USERNAME."
        Note 'Credential Manager is per account. Run this as the account that runs the push tool.'
        exit 2
    }
    Pass "client secret read from Credential Manager entry '$target'"
}
else {
    $certificate = Get-PushCertificate -Config $config
    if (-not $certificate) {
        Fail 'no configured certificate with a usable private key was found in the configured store.'
        exit 2
    }
    Pass "certificate $($certificate.Thumbprint), expires $($certificate.NotAfter.ToString('yyyy-MM-dd'))"
}

$auth = Get-PushToken -Config $config -Certificate $certificate -ClientSecret $secret
if (-not $auth.Token) {
    Fail "token request failed: $($auth.Error)"
    if ($auth.Detail) { Note $auth.Detail }
    if ($auth.Advice) { Note $auth.Advice }
    exit 2
}

Pass "token acquired; roles: $($auth.Roles -join ', ')"

# The roles claim is the only client-side evidence of what was actually
# consented. A permission listed in the portal but never granted simply is not
# in the token, and the PATCH would come back as an unexplained 403.
if ($auth.Roles -notcontains 'ExternalConnection.ReadWrite.OwnedBy' -and
    $auth.Roles -notcontains 'ExternalConnection.ReadWrite.All') {
    Fail 'neither ExternalConnection.ReadWrite.OwnedBy nor .All is in the token.'
    Note 'Update externalConnection needs one of them. Grant and admin-consent it in Entra.'
    exit 2
}

$headers = @{
    Authorization = "Bearer $($auth.Token)"
    # searchSettings and activitySettings are not new, but the labels enum on the
    # schema is evolvable, and asking for unknown members costs nothing and makes
    # every enum in every response here read back as its real value.
    Prefer        = 'include-unknown-enum-members'
}

$base = "https://graph.microsoft.com/v1.0/external/connections/$ConnectionId"

function Invoke-Graph {
    param(
        [Parameter(Mandatory)][string]$Method,
        [Parameter(Mandatory)][string]$Uri,
        [string]$Body
    )

    $arguments = @{ Method = $Method; Uri = $Uri; Headers = $headers }

    if ($Body) {
        # UTF-8 bytes rather than a string. Windows PowerShell 5.1 encodes a
        # string body using the charset of the Content-Type header and defaults
        # to ISO-8859-1 when there is none, so a layout carrying anything above
        # ASCII arrives mangled. Bytes plus an explicit charset behaves
        # identically on both hosts.
        $arguments['Body'] = [Text.Encoding]::UTF8.GetBytes($Body)
        $arguments['ContentType'] = 'application/json; charset=utf-8'
    }

    Invoke-RestMethod @arguments
}

function Show-GraphError {
    param($ErrorRecord)

    $message = $ErrorRecord.Exception.Message
    $detail = "$($ErrorRecord.ErrorDetails.Message)"
    Fail $message
    if ($detail) { Note $detail }

    # These four are the whole distribution of failures for this call, and each
    # one is a different person's problem.
    if ($message -match '404') {
        Note "404: either '$ConnectionId' does not exist, or this app did not create it. OwnedBy means owned by THIS app registration; another app's connection is invisible, not forbidden."
    }
    elseif ($message -match '403') {
        Note '403 with the role present in the token usually means the connection is owned by a different app.'
    }
    elseif ($message -match '400') {
        Note 'A 400 here is the payload. Check that every ${property} in the layouts exists in the registered schema and is retrievable, and that no template id exceeds 16 alphanumeric characters.'
    }
    elseif ($message -match '503') {
        Note 'Microsoft documents a 503 as the response to a BROKEN Adaptive Card in a result layout (graph/known-issues). Validate the layout in the Adaptive Cards designer before retrying.'
    }
}

Step "1. Connection '$ConnectionId'"

$connection = $null
try {
    $connection = Invoke-Graph -Method GET -Uri "$base`?`$select=id,name,description,state,contentCategory,searchSettings"
    Pass "'$($connection.name)' — state: $($connection.state)"
}
catch {
    Show-GraphError -ErrorRecord $_
    exit 1
}

if ($connection.state -ne 'ready') {
    Warn "state is '$($connection.state)', not 'ready'. Display templates on a draft connection render nothing, because there is nothing indexed to render."
}

$currentTemplates = @()
if ($connection.searchSettings -and $connection.searchSettings.searchResultTemplates) {
    $currentTemplates = @($connection.searchSettings.searchResultTemplates)
}

if ($currentTemplates.Count -eq 0) {
    Note 'searchSettings is currently empty: results render with the platform default layout — title, snippet, nothing else.'
}
else {
    Note "currently $($currentTemplates.Count) template(s): $(($currentTemplates | ForEach-Object { "$($_.id) (priority $($_.priority))" }) -join ', ')"
}

# ---------------------------------------------------------------------------

if ($Revert) {
    Step '2. Revert'

    if (-not (Test-Path $BackupPath)) {
        Fail "no backup at $BackupPath. Nothing to restore from."
        Note 'A backup is written by -Apply. Without one, this script will not guess what the previous state was.'
        exit 2
    }

    $backup = Get-Content $BackupPath -Raw | ConvertFrom-Json
    if ($backup.connectionId -ne $ConnectionId) {
        Fail "backup at $BackupPath is for connection '$($backup.connectionId)', not '$ConnectionId'."
        exit 2
    }

    # $null and an empty collection are different states, and only the first is
    # what a connection that never had templates looks like. Restoring the wrong
    # one leaves a connection that reads differently from the one that was there
    # before, which is the thing a revert exists to avoid.
    if ($null -eq $backup.searchSettings) {
        $revertBody = '{"searchSettings":null}'
        Note "backup records searchSettings as null (no templates were ever set); sending an explicit null."
    }
    else {
        $revertBody = [pscustomobject]@{ searchSettings = $backup.searchSettings } | ConvertTo-Json -Depth 64 -Compress
        Note "backup records $(@($backup.searchSettings.searchResultTemplates).Count) template(s); restoring them."
    }

    try {
        Invoke-Graph -Method PATCH -Uri $base -Body $revertBody | Out-Null
        Pass 'PATCH accepted'
    }
    catch {
        Show-GraphError -ErrorRecord $_
        exit 1
    }

    $after = Invoke-Graph -Method GET -Uri "$base`?`$select=id,searchSettings"
    $afterCount = if ($after.searchSettings) { @($after.searchSettings.searchResultTemplates).Count } else { 0 }
    Pass "verified by read-back: searchSettings now holds $afterCount template(s)"
    exit 0
}

# ---------------------------------------------------------------------------

Step '2. Schema check'

# Every ${name} in a layout has to be a property that exists and is retrievable,
# or the card renders the literal placeholder. Catching that here is the
# difference between a failed script and a results page full of "${hours}".
$schema = $null
try {
    $schema = Invoke-Graph -Method GET -Uri "$base/schema"
}
catch {
    Show-GraphError -ErrorRecord $_
    exit 1
}

$properties = @{}
foreach ($property in @($schema.properties)) {
    $properties[$property.name] = $property
}
Pass "$($properties.Count) properties registered"

if ($LayoutPath) {
    foreach ($pair in @(@('timeentry.json', 'timeEntryLayout'), @('fallback.json', 'fallbackLayout'))) {
        $file = Join-Path $LayoutPath $pair[0]
        if (-not (Test-Path $file)) {
            Fail "-LayoutPath was given but $file does not exist."
            exit 2
        }
        Set-Variable -Name $pair[1] -Value (Get-Content $file -Raw)
        Pass "layout loaded from $file"
    }
}

$timeEntryLayout = $timeEntryLayout.Replace('__ICON_URL__', $IconUrl)
$fallbackLayout = $fallbackLayout.Replace('__ICON_URL__', $IconUrl)

# ResultSnippet is a system placeholder rather than a schema property, so it is
# excluded from the existence check by name.
$systemPlaceholders = @('ResultSnippet')
$bound = New-Object System.Collections.Generic.HashSet[string]

foreach ($layout in @($timeEntryLayout, $fallbackLayout)) {
    foreach ($match in [regex]::Matches($layout, '\$\{\s*([A-Za-z0-9_]+)')) {
        [void]$bound.Add($match.Groups[1].Value)
    }
}

foreach ($name in ($bound | Sort-Object)) {
    if ($systemPlaceholders -contains $name) {
        Note "$name is a system placeholder (the dynamic content snippet), not a schema property"
        continue
    }
    if (-not $properties.ContainsKey($name)) {
        Fail "the layouts bind `${$name} but the registered schema has no property called '$name'."
        continue
    }
    if (-not $properties[$name].isRetrievable) {
        Fail "'$name' is in the schema but is NOT retrievable, so it is never returned in a result and the card would render the placeholder."
    }
}

if ($script:failures -eq 0) {
    Pass "every bound property exists and is retrievable: $(($bound | Sort-Object) -join ', ')"
}

# The rule property has the same requirement, for the same reason.
$ruleProperty = 'itemType'
if (-not $properties.ContainsKey($ruleProperty)) {
    Fail "the display rule matches on '$ruleProperty', which is not in the schema."
}
elseif (-not $properties[$ruleProperty].isQueryable) {
    Warn "'$ruleProperty' is not queryable. Display rules are evaluated against the indexed item; a non-queryable property is the least reliable thing to key a rule on."
}
else {
    Pass "rule property '$ruleProperty' is queryable and retrievable"
}

if ($script:failures -gt 0) {
    Write-Host ''
    Write-Host "$script:failures problem(s) with the layouts. Nothing was sent." -ForegroundColor Red
    exit 1
}

# ---------------------------------------------------------------------------

Step '3. Payload'

$templates = @(
    [pscustomobject][ordered]@{
        id       = 'timeentry'
        priority = 1
        rules    = @(
            [pscustomobject][ordered]@{
                property       = 'itemType'
                operation      = 'equals'
                valuesJoinedBy = 'or'
                values         = @('TimeEntry')
            }
        )
        layout   = ($timeEntryLayout | ConvertFrom-Json)
    },
    [pscustomobject][ordered]@{
        id       = 'consulting'
        priority = 2
        layout   = ($fallbackLayout | ConvertFrom-Json)
    }
)

# The cap is two, published, per connection. Checked rather than trusted because
# the failure is a 400 fifteen seconds into a change window.
if ($templates.Count -gt 2) {
    Fail "$($templates.Count) templates: Microsoft caps searchResultTemplates at 2 per connection."
    exit 2
}

foreach ($template in $templates) {
    if ($template.id.Length -gt 16 -or $template.id -notmatch '^[a-zA-Z0-9]+$') {
        Fail "template id '$($template.id)' must be at most 16 alphanumeric characters."
        exit 2
    }
    if ($template.priority -lt 1) {
        Fail "template '$($template.id)' has priority $($template.priority); it must be positive."
        exit 2
    }
}

$payload = [pscustomobject]@{
    searchSettings = [pscustomobject]@{ searchResultTemplates = $templates }
}

# Two renderings of one document, deliberately. Windows PowerShell 5.1 indents
# ConvertTo-Json output to the width of the longest key on the line, which turns
# this 5.7 KB payload into 21 KB of whitespace and makes the two hosts produce
# artefacts that will not diff against each other. So the WIRE body is
# compressed — identical in meaning on both hosts and a quarter of the size —
# and the readable rendering is what is printed and saved.
#
# One remaining host difference, and it is harmless: Windows PowerShell escapes
# apostrophes to their ' form, and the layouts are full of them because
# every $when expression compares against ''. That is valid JSON and unescapes
# to the same string.
$body = $payload | ConvertTo-Json -Depth 64 -Compress
$readable = $payload | ConvertTo-Json -Depth 64

Pass "$($templates.Count) templates, $($body.Length) bytes on the wire"
Note "priority 1 'timeentry' when itemType equals TimeEntry; priority 2 'consulting' with no rules, matching everything else"

Write-Host ''
Write-Host $readable

if ($OutFile) {
    # UTF-8 without a BOM: this is a JSON document that may be posted by another
    # tool, and a BOM at the head of a JSON body is rejected by strict parsers.
    [IO.File]::WriteAllText($OutFile, $readable, (New-Object Text.UTF8Encoding $false))
    Pass "written to $OutFile"
}

if (-not $Apply) {
    Write-Host ''
    Write-Host 'Dry run. Nothing was sent. Re-run with -Apply to PATCH the connection.' -ForegroundColor Yellow
    exit 0
}

# ---------------------------------------------------------------------------

Step '4. Backup'

# Taken from the connection that is about to change, not from a previous run, so
# a revert restores what was actually there a second ago.
$backupRecord = [pscustomobject]@{
    takenUtc       = [DateTime]::UtcNow.ToString('o')
    connectionId   = $ConnectionId
    connectionName = $connection.name
    searchSettings = $connection.searchSettings
}

[IO.File]::WriteAllText(
    $BackupPath,
    ($backupRecord | ConvertTo-Json -Depth 64),
    (New-Object Text.UTF8Encoding $false))

Pass "previous searchSettings saved to $BackupPath"
Note "revert with: .\Set-SearchResultTypes.ps1 -ConfigPath '$ConfigPath' -ConnectionId $ConnectionId -Revert"

Step '5. Apply'

try {
    Invoke-Graph -Method PATCH -Uri $base -Body $body | Out-Null
    Pass 'PATCH accepted (204)'
}
catch {
    Show-GraphError -ErrorRecord $_
    Note "The connection was not changed. The backup at $BackupPath is still valid."
    exit 1
}

Step '6. Verify by reading it back'

# A 204 says the request was accepted, not that the templates are there. The
# read-back is the only proof, and it costs one call.
$after = Invoke-Graph -Method GET -Uri "$base`?`$select=id,searchSettings"
$afterTemplates = @()
if ($after.searchSettings -and $after.searchSettings.searchResultTemplates) {
    $afterTemplates = @($after.searchSettings.searchResultTemplates)
}

if ($afterTemplates.Count -ne $templates.Count) {
    Fail "sent $($templates.Count) templates, read back $($afterTemplates.Count)."
}
else {
    Pass "$($afterTemplates.Count) templates stored: $(($afterTemplates | ForEach-Object { "$($_.id) (priority $($_.priority))" }) -join ', ')"
}

Write-Host ''
if ($script:failures -eq 0) {
    Write-Host 'Result templates registered.' -ForegroundColor Green
    Write-Host ''
    Write-Host 'What this did NOT do, and what is still outstanding:' -ForegroundColor Yellow
    Write-Host '  * Copilot Search does not use Adaptive Card layouts. It renders from semantic'
    Write-Host '    labels. Run Get-SearchSurfacing.ps1 to see which labels this schema is missing.'
    Write-Host '  * Search verticals and admin-centre result types have no Graph API. A search'
    Write-Host '    administrator creates them at'
    Write-Host '    https://admin.microsoft.com/Adminportal/Home#/MicrosoftSearch/verticals'
    Write-Host '  * Changes are cached: allow a few minutes for a result type, and up to a few'
    Write-Host '    hours for a vertical. Append cacheClear=true to the SharePoint or Office search'
    Write-Host '    URL to see them sooner.'
    exit 0
}

Write-Host "$script:failures check(s) failed." -ForegroundColor Red
exit 1
