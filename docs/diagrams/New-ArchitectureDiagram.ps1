# Emits docs/architecture.svg and docs/architecture.png.
#
# WHY THIS EXISTS. The diagrams in docs/ had no generator. They were drawn once
# and thereafter hand-edited, which is why the architecture picture still showed
# the shape of the repository as it was several releases ago: no crawl state
# database, no run lock, no telemetry. A diagram nobody can regenerate is a
# diagram that stops being true quietly, and the SVG and the PNG stop agreeing
# with each other first.
#
# ONE LAYOUT, TWO RENDERERS. The boxes and arrows are declared once, below, and
# drawn twice. Two hand-maintained copies of one picture diverge on the first
# edit and the divergence is invisible until somebody puts them side by side.
# The SVG is the better artefact - it scales and it can be diffed; the PNG
# exists because it embeds reliably everywhere, including in the decks.
#
# The layout is CHECKED rather than trusted: every node must sit inside its
# zone, no two nodes may overlap, every edge must name real nodes, and an edge
# whose straight line would cross a third box is routed round it through the
# gutter. Each of those checks exists because the first version of this drawing
# broke it.
#
#     pwsh docs/diagrams/New-ArchitectureDiagram.ps1

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

# Output beside the docs that reference it, resolved from this script so it
# follows the repository rather than naming one person's checkout.
$base = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$W = 1900; $H = 1180

# Palette.
$NAVY  = '#0b2f52'; $AZURE = '#1f7ac0'; $STEEL = '#5c8ca8'
$GRID  = '#d6e0e9'; $INK   = '#1a1a1a'; $MUTED = '#5b6a76'
$WASH  = '#f3f7fa'; $AMBER = '#c07a1a'; $WHITE = '#ffffff'

# --- the model -------------------------------------------------------------
# Zones are the background groupings; nodes sit inside them.
$zones = @(
    @{ Id='src'; X=50;   Y=150; W=380; H=520; Label='SOURCES';                 Note='On premises, never exposed to the cloud' }
    @{ Id='plat';X=520;  Y=150; W=520; H=520; Label='THE PUSH TOOLS';          Note='Console apps. One line of Main each' }
    @{ Id='m365';X=1130; Y=150; W=430; H=520; Label='MICROSOFT 365 CLOUD';     Note='' }
    # Operations sits directly under the tools, and the agent path directly under
    # Microsoft 365, so that every arrow between bands is short and vertical. The
    # first layout put both side by side along the bottom, which made the
    # telemetry arrows cross three boxes each.
    @{ Id='ops'; X=520;  Y=720; W=520; H=390; Label='OPERATIONS';              Note='' }
    @{ Id='dr';  X=1130; Y=720; W=430; H=390; Label='THE OTHER PATH';          Note='Agent hosted, no Graph dependency' }
)

$nodes = @(
    # Sources
    @{ Id='sqlt'; X=80;   Y=225; W=320; H=76;  T='SQL Server, tickets';          S='SELECT only, least privilege'; C=$STEEL }
    @{ Id='sqlh'; X=80;   Y=325; W=320; H=76;  T='SQL Server, hierarchy';        S='Customers, engagements, time'; C=$STEEL }
    @{ Id='cdp';  X=80;   Y=425; W=320; H=96;  T='Cloudera CDP';                 S='HDFS, Hive, Atlas catalogue'; C=$STEEL; Dashed=$true }
    @{ Id='rang'; X=80;   Y=545; W=320; H=96;  T='Apache Ranger';                S='Read before anything else.'; S2='A masked table is never indexed'; C=$STEEL; Dashed=$true }

    # The push tools
    @{ Id='hostA';X=550;  Y=225; W=460; H=92;  T='PushCore engine';              S='Read, map, truncate, ACL, hash, batch, write'; C=$AZURE }
    @{ Id='hostB';X=550;  Y=337; W=460; H=76;  T='Second instance, refused';     S='One live crawl per connection, by lease'; C=$AZURE; Dashed=$true }
    @{ Id='state';X=550;  Y=433; W=460; H=110; T='Crawl state database';         S='What was seen, what changed, what to delete,'; S2='the run lock, and the principal cache'; C=$NAVY }
    @{ Id='build';X=550;  Y=563; W=460; H=76;  T='Release package';              S='Authenticode, file catalog, SBOM'; C=$AZURE }

    # Microsoft 365
    @{ Id='graph';X=1160; Y=225; W=370; H=92;  T='Copilot connectors API';       S='Connections, schema, items, $batch'; C=$NAVY }
    @{ Id='srch'; X=1160; Y=337; W=370; H=76;  T='Microsoft Search';             S='Result types and display templates'; C=$NAVY }
    @{ Id='cop';  X=1160; Y=433; W=370; H=76;  T='Microsoft 365 Copilot';        S='Grounded on connector content'; C=$NAVY }
    @{ Id='entra';X=1160; Y=529; W=370; H=97;  T='Microsoft Entra ID';           S='Groups the access control lists'; S2='are expressed in. No user ACEs'; C=$NAVY }

    # Operations, stacked so the arrows down from the tools are vertical
    @{ Id='dash'; X=550;  Y=790; W=460; H=90;  T='Operations dashboard';         S='Runs, items, health. Read only, by role'; C=$AZURE }
    @{ Id='watch';X=550;  Y=900; W=460; H=90;  T='Health watchdog';              S='Freshness, and the paging matrix behind it'; C=$AZURE }
    @{ Id='apm';  X=550;  Y=1010;W=460; H=76;  T='OTLP collector';               S='Spans and metrics per run. Optional'; C=$AZURE; Dashed=$true }

    # The agent-hosted path
    @{ Id='dr1';  X=1160; Y=790; W=370; H=100; T='Graph connector agent';        S='Holds the tenant relationship.'; S2='Polls the connector over gRPC'; C=$NAVY }
    @{ Id='dr2';  X=1160; Y=910; W=370; H=100; T='SqlTicketsConnector';          S='Loopback gRPC service.'; S2='Never calls Microsoft Graph'; C=$AZURE; Dashed=$true }
)

# from, to, label, style: solid | dashed | thick
$edges = @(
    @{ F='sqlt';  T='hostA'; L='SELECT';                  S='solid' }
    @{ F='sqlh';  T='hostA'; L='';                        S='solid' }
    @{ F='cdp';   T='hostA'; L='read';                    S='dashed' }
    @{ F='rang';  T='hostA'; L='';                        S='dashed' }
    @{ F='hostA'; T='graph'; L='$batch, 20 per request';  S='thick' }
    @{ F='graph'; T='srch';  L='';                        S='solid' }
    @{ F='srch';  T='cop';   L='';                        S='solid' }
    @{ F='hostA'; T='state'; L='change detection, run lock'; S='solid' }
    @{ F='hostB'; T='state'; L='lease refused, exit 5';   S='dashed' }
    @{ F='state'; T='dash';  L='read only, by role';      S='solid' }
    @{ F='dash';  T='watch'; L='health endpoint';         S='solid' }
    @{ F='watch'; T='apm';   L='alerts';                  S='solid' }
    @{ F='hostA'; T='apm';   L='';                        S='dashed' }
    @{ F='dr2';   T='dr1';   L='gRPC on loopback';        S='thick' }
)

$byId = @{}
foreach ($n in $nodes) {
    foreach ($k in 'S2','Dashed') { if (-not $n.ContainsKey($k)) { $n[$k] = $null } }
    $n.CX = $n.X + $n.W / 2
    $n.CY = $n.Y + $n.H / 2
    $byId[$n.Id] = $n
}

# --- checks ---------------------------------------------------------------
$fail = 0
foreach ($e in $edges) {
    foreach ($end in 'F','T') { if (-not $byId.ContainsKey($e[$end])) { "FAIL: edge names unknown node $($e[$end])"; $fail++ } }
}
foreach ($n in $nodes) {
    $inside = $false
    foreach ($z in $zones) {
        if ($n.X -ge $z.X -and $n.Y -ge $z.Y -and ($n.X + $n.W) -le ($z.X + $z.W) -and ($n.Y + $n.H) -le ($z.Y + $z.H)) { $inside = $true }
    }
    if (-not $inside) { "FAIL: node $($n.Id) is not inside any zone"; $fail++ }
}
# Boxes must not overlap: an overlap is invisible in code and obvious on the page.
for ($i = 0; $i -lt $nodes.Count; $i++) {
    for ($j = $i + 1; $j -lt $nodes.Count; $j++) {
        $a = $nodes[$i]; $b = $nodes[$j]
        if ($a.X -lt ($b.X + $b.W) -and ($a.X + $a.W) -gt $b.X -and $a.Y -lt ($b.Y + $b.H) -and ($a.Y + $a.H) -gt $b.Y) {
            "FAIL: $($a.Id) overlaps $($b.Id)"; $fail++
        }
    }
}
if ($fail) { "FAILED CHECKS: $fail"; exit 1 }
"layout: $($zones.Count) zones, $($nodes.Count) nodes, $($edges.Count) edges, no overlaps"

# --- edge routing ----------------------------------------------------------
# Anchors on the box face nearest the other end, then checks whether the
# straight run would pass through a THIRD box. In a stacked column it usually
# would: host A to the crawl state database goes straight through host B, and
# the state database to the dashboard goes straight through the release package.
# A diagram whose arrows pass under boxes reads as though those boxes are on the
# path, which is the opposite of what it is trying to say.
#
# Where the straight line is blocked, the edge is routed as an elbow through the
# gutter to the right of the column. Three segments, orthogonal, and it never
# needs to be cleverer than that because every column here is a single stack.

function Anchor($from, $to) {
    $dx = $to.CX - $from.CX; $dy = $to.CY - $from.CY
    if ([Math]::Abs($dx) * $from.H -ge [Math]::Abs($dy) * $from.W) {
        $x = if ($dx -gt 0) { $from.X + $from.W } else { $from.X }
        return @{ X = $x; Y = $from.CY }
    }
    $y = if ($dy -gt 0) { $from.Y + $from.H } else { $from.Y }
    return @{ X = $from.CX; Y = $y }
}

# Segment against axis-aligned rectangle, by sampling. Sampling rather than a
# proper intersection test because the segments here are short, the boxes are
# large, and a false negative costs a crossed arrow rather than a wrong answer.
function CrossesAny($x1, $y1, $x2, $y2, $skipA, $skipB, $all) {
    $steps = 60
    for ($i = 1; $i -lt $steps; $i++) {
        $t = $i / [double]$steps
        $px = $x1 + ($x2 - $x1) * $t
        $py = $y1 + ($y2 - $y1) * $t
        foreach ($n in $all) {
            if ($n.Id -eq $skipA -or $n.Id -eq $skipB) { continue }
            if ($px -ge $n.X -and $px -le ($n.X + $n.W) -and $py -ge $n.Y -and $py -le ($n.Y + $n.H)) {
                return $n.Id
            }
        }
    }
    return $null
}

$routed = @()
$detours = 0
foreach ($e in $edges) {
    $a = $byId[$e.F]; $b = $byId[$e.T]
    $p1 = Anchor $a $b
    $p2 = Anchor $b $a

    $blocker = CrossesAny $p1.X $p1.Y $p2.X $p2.Y $a.Id $b.Id $nodes

    if ($null -eq $blocker) {
        $pts = @(@($p1.X, $p1.Y), @($p2.X, $p2.Y))
    }
    else {
        # WHERE THE DETOUR RUNS IS NOT ARBITRARY, and the first two attempts got
        # it wrong in opposite ways. Always going right sent right-to-left edges
        # the long way round; then routing outside everything sent the state
        # store to its replica out past the right edge of the page and back
        # through the Copilot box, because the two are diagonal rather than
        # stacked.
        #
        # So: when the boxes are in DIFFERENT columns the elbow runs down the
        # gutter BETWEEN them, which is empty by construction. Only when the
        # columns overlap, which is the stacked case, does it go outside, and
        # then it goes right because that is the side with room.
        $lane = $detours % 3   # keeps two stacked detours off the same line

        if ($b.X -gt ($a.X + $a.W)) {
            $gutter = (($a.X + $a.W) + $b.X) / 2
            $pts = @(@(($a.X + $a.W), $a.CY), @($gutter, $a.CY), @($gutter, $b.CY), @($b.X, $b.CY))
        }
        elseif (($b.X + $b.W) -lt $a.X) {
            $gutter = (($b.X + $b.W) + $a.X) / 2
            $pts = @(@($a.X, $a.CY), @($gutter, $a.CY), @($gutter, $b.CY), @(($b.X + $b.W), $b.CY))
        }
        else {
            $gutter = [Math]::Max(($a.X + $a.W), ($b.X + $b.W)) + 26 + ($lane * 16)
            $pts = @(@(($a.X + $a.W), $a.CY), @($gutter, $a.CY), @($gutter, $b.CY), @(($b.X + $b.W), $b.CY))
        }

        $detours++
    }

    # Where along its longest segment the label sits. Two elbows running down
    # adjacent lanes have their long legs at the same height, so a fixed
    # midpoint stacks their labels and the lower one disappears under the upper:
    # the state-store-to-dashboard label was entirely hidden by the replication
    # label. Cycling the fraction separates them without moving the lines.
    $frac = @(0.5, 0.34, 0.66)[$routed.Count % 3]

    $routed += [pscustomobject]@{ Pts = $pts; L = $e.L; S = $e.S; Blocked = $blocker; Frac = $frac }
}
"routing: $detours of $($edges.Count) edges detoured around a box"
foreach ($i in 0..($edges.Count-1)) { if ($routed[$i].Blocked) { "  detour: {0} -> {1} (blocked by {2})" -f $edges[$i].F, $edges[$i].T, $routed[$i].Blocked } }

# --- SVG -------------------------------------------------------------------
function Esc($s) { [System.Net.WebUtility]::HtmlEncode([string]$s) }

# The label goes on the LONGEST segment of the polyline, not at the midpoint of
# the whole run. On an elbow the midpoint often falls on the short leg, where
# the label sticks out past the corner.
function LongestMid($pts, $frac = 0.5) {
    $best = -1.0; $mx = 0.0; $my = 0.0
    for ($i = 0; $i -lt ($pts.Count - 1); $i++) {
        $dx = $pts[$i + 1][0] - $pts[$i][0]
        $dy = $pts[$i + 1][1] - $pts[$i][1]
        $len = [Math]::Sqrt($dx * $dx + $dy * $dy)
        if ($len -gt $best) {
            $best = $len
            $mx = $pts[$i][0] + $dx * $frac
            $my = $pts[$i][1] + $dy * $frac
        }
    }
    return @{ X = $mx; Y = $my }
}

$sb = [System.Text.StringBuilder]::new()
[void]$sb.AppendLine("<svg xmlns=""http://www.w3.org/2000/svg"" viewBox=""0 0 $W $H"" width=""$W"" height=""$H"" font-family=""Segoe UI, Calibri, sans-serif"">")
[void]$sb.AppendLine('  <defs>')
[void]$sb.AppendLine("    <marker id=""a"" viewBox=""0 0 10 10"" refX=""9"" refY=""5"" markerWidth=""7"" markerHeight=""7"" orient=""auto-start-reverse""><path d=""M 0 0 L 10 5 L 0 10 z"" fill=""$MUTED""/></marker>")
[void]$sb.AppendLine("    <marker id=""at"" viewBox=""0 0 10 10"" refX=""9"" refY=""5"" markerWidth=""6"" markerHeight=""6"" orient=""auto-start-reverse""><path d=""M 0 0 L 10 5 L 0 10 z"" fill=""$AZURE""/></marker>")
[void]$sb.AppendLine('  </defs>')
[void]$sb.AppendLine("  <rect width=""$W"" height=""$H"" fill=""$WHITE""/>")
[void]$sb.AppendLine("  <text x=""50"" y=""62"" font-size=""34"" font-weight=""600"" fill=""$NAVY"">Microsoft 365 Copilot connector platform</text>")
[void]$sb.AppendLine("  <text x=""50"" y=""98"" font-size=""20"" fill=""$MUTED"">High level architecture as of v1.7.1. Dashed elements are optional, out of scope, or the path not taken.</text>")

foreach ($z in $zones) {
    [void]$sb.AppendLine("  <rect x=""$($z.X)"" y=""$($z.Y)"" width=""$($z.W)"" height=""$($z.H)"" rx=""14"" fill=""$WASH"" stroke=""$GRID"" stroke-width=""2""/>")
    [void]$sb.AppendLine("  <text x=""$($z.X + 22)"" y=""$($z.Y + 34)"" font-size=""17"" font-weight=""600"" letter-spacing=""1.5"" fill=""$NAVY"">$(Esc $z.Label)</text>")
    if ($z.Note) { [void]$sb.AppendLine("  <text x=""$($z.X + 22)"" y=""$($z.Y + 58)"" font-size=""15"" fill=""$MUTED"">$(Esc $z.Note)</text>") }
}

foreach ($r in $routed) {
    $stroke = if ($r.S -eq 'thick') { $AZURE } else { $MUTED }
    $wdt = if ($r.S -eq 'thick') { 3.5 } else { 2 }
    $dash = if ($r.S -eq 'dashed') { ' stroke-dasharray="7,6"' } else { '' }
    $mk = if ($r.S -eq 'thick') { 'at' } else { 'a' }
    $d = ($r.Pts | ForEach-Object { "$([Math]::Round($_[0],1)),$([Math]::Round($_[1],1))" }) -join ' '
    [void]$sb.AppendLine("  <polyline points=""$d"" fill=""none"" stroke=""$stroke"" stroke-width=""$wdt""$dash stroke-linejoin=""round"" marker-end=""url(#$mk)""/>")
}

foreach ($n in $nodes) {
    $dash = if ($n.Dashed) { ' stroke-dasharray="8,6"' } else { '' }
    $op = if ($n.Dashed) { '0.58' } else { '1' }
    [void]$sb.AppendLine("  <rect x=""$($n.X)"" y=""$($n.Y)"" width=""$($n.W)"" height=""$($n.H)"" rx=""10"" fill=""$($n.C)"" stroke=""$($n.C)"" stroke-width=""2""$dash opacity=""$op""/>")
    [void]$sb.AppendLine("  <text x=""$($n.X + 18)"" y=""$($n.Y + 32)"" font-size=""19"" font-weight=""600"" fill=""$WHITE"">$(Esc $n.T)</text>")
    if ($n.S)  { [void]$sb.AppendLine("  <text x=""$($n.X + 18)"" y=""$($n.Y + 56)"" font-size=""15"" fill=""#dbe7f0"">$(Esc $n.S)</text>") }
    if ($n.S2) { [void]$sb.AppendLine("  <text x=""$($n.X + 18)"" y=""$($n.Y + 76)"" font-size=""15"" fill=""#dbe7f0"">$(Esc $n.S2)</text>") }
}

# Edge labels last, for the reason given above the PNG pass below.
foreach ($r in $routed) {
    if (-not $r.L) { continue }
    $m = LongestMid $r.Pts $r.Frac
    $tw = $r.L.Length * 7.4 + 16
    [void]$sb.AppendLine("  <rect x=""$([Math]::Round($m.X - $tw/2,1))"" y=""$([Math]::Round($m.Y - 12,1))"" width=""$([Math]::Round($tw,1))"" height=""23"" rx=""5"" fill=""$WHITE"" opacity=""0.95""/>")
    [void]$sb.AppendLine("  <text x=""$([Math]::Round($m.X,1))"" y=""$([Math]::Round($m.Y + 4,1))"" font-size=""14"" fill=""$MUTED"" text-anchor=""middle"">$(Esc $r.L)</text>")
}

[void]$sb.AppendLine("  <text x=""50"" y=""$($H - 26)"" font-size=""15"" fill=""$MUTED"">Every item written to Microsoft Graph carries an access control list derived from its source, so Copilot returns to each user only what the source system would.</text>")
[void]$sb.AppendLine('</svg>')

$svgPath = Join-Path $base 'architecture.svg'
[System.IO.File]::WriteAllText($svgPath, $sb.ToString(), (New-Object System.Text.UTF8Encoding($false)))
"svg written: $svgPath"

# --- PNG -------------------------------------------------------------------
$bmp = New-Object System.Drawing.Bitmap $W, $H
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
$g.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::ClearTypeGridFit
$g.Clear([System.Drawing.Color]::White)

function Col([string]$hex) { [System.Drawing.ColorTranslator]::FromHtml($hex) }
function Fnt([single]$size, [string]$style = 'Regular') {
    New-Object System.Drawing.Font('Segoe UI', $size, [System.Drawing.FontStyle]::$style, [System.Drawing.GraphicsUnit]::Pixel)
}
function RoundRect($x, $y, $w, $h, $r) {
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $p.AddArc([single]$x, [single]$y, [single]($r*2), [single]($r*2), 180, 90)
    $p.AddArc([single]($x+$w-$r*2), [single]$y, [single]($r*2), [single]($r*2), 270, 90)
    $p.AddArc([single]($x+$w-$r*2), [single]($y+$h-$r*2), [single]($r*2), [single]($r*2), 0, 90)
    $p.AddArc([single]$x, [single]($y+$h-$r*2), [single]($r*2), [single]($r*2), 90, 90)
    $p.CloseFigure()
    return $p
}

$bMuted = New-Object System.Drawing.SolidBrush (Col $MUTED)
$bNavy  = New-Object System.Drawing.SolidBrush (Col $NAVY)
$bWhite = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::White)
$bSub   = New-Object System.Drawing.SolidBrush (Col '#dbe7f0')

$g.DrawString('Microsoft 365 Copilot connector platform', (Fnt 34 'Bold'), $bNavy, 46, 32)
$g.DrawString('High level architecture as of v1.7.1. Dashed elements are optional, out of scope, or the path not taken.', (Fnt 20), $bMuted, 48, 78)

foreach ($z in $zones) {
    $p = RoundRect $z.X $z.Y $z.W $z.H 14
    $g.FillPath((New-Object System.Drawing.SolidBrush (Col $WASH)), $p)
    $g.DrawPath((New-Object System.Drawing.Pen((Col $GRID), 2)), $p)
    $p.Dispose()
    $g.DrawString($z.Label, (Fnt 17 'Bold'), $bNavy, [single]($z.X + 20), [single]($z.Y + 18))
    if ($z.Note) { $g.DrawString($z.Note, (Fnt 15), $bMuted, [single]($z.X + 20), [single]($z.Y + 42)) }
}

foreach ($r in $routed) {
    $pen = New-Object System.Drawing.Pen((Col $(if ($r.S -eq 'thick') { $AZURE } else { $MUTED })), $(if ($r.S -eq 'thick') { 3.5 } else { 2 }))
    if ($r.S -eq 'dashed') { $pen.DashPattern = @(3.5, 3.0) }
    $pen.LineJoin = [System.Drawing.Drawing2D.LineJoin]::Round
    $pen.CustomEndCap = New-Object System.Drawing.Drawing2D.AdjustableArrowCap 5, 5
    [System.Drawing.PointF[]]$pts = @($r.Pts | ForEach-Object { New-Object System.Drawing.PointF([single]$_[0], [single]$_[1]) })
    $g.DrawLines($pen, $pts)
    $pen.Dispose()
}

foreach ($n in $nodes) {
    $p = RoundRect $n.X $n.Y $n.W $n.H 10
    $c = Col $n.C
    if ($n.Dashed) { $c = [System.Drawing.Color]::FromArgb(148, $c.R, $c.G, $c.B) }
    $g.FillPath((New-Object System.Drawing.SolidBrush $c), $p)
    $p.Dispose()
    $g.DrawString($n.T, (Fnt 19 'Bold'), $bWhite, [single]($n.X + 16), [single]($n.Y + 14))
    if ($n.S)  { $g.DrawString($n.S,  (Fnt 15), $bSub, [single]($n.X + 16), [single]($n.Y + 42)) }
    if ($n.S2) { $g.DrawString($n.S2, (Fnt 15), $bSub, [single]($n.X + 16), [single]($n.Y + 62)) }
}

# Edge labels last, over the boxes. Z-ORDER WAS THE WHOLE BUG: both renderers
# drew zones, then edges with their labels, then nodes, so any label whose
# midpoint fell in a gutter beside a box had its left half painted over on the
# next pass. The page read "nge detection, run lock" and "P from every run".
# The lines still belong under the boxes; only the labels move on top.
foreach ($r in $routed) {
    if (-not $r.L) { continue }
    $m = LongestMid $r.Pts $r.Frac
    $f = Fnt 14
    $sz = $g.MeasureString($r.L, $f)
    $g.FillRectangle($bWhite, [single]($m.X - $sz.Width/2 - 6), [single]($m.Y - $sz.Height/2 - 1), [single]($sz.Width + 12), [single]($sz.Height + 2))
    $g.DrawString($r.L, $f, $bMuted, [single]($m.X - $sz.Width/2), [single]($m.Y - $sz.Height/2))
}

$g.DrawString('Every item written to Microsoft Graph carries an access control list derived from its source, so Copilot returns to each user only what the source system would.',
    (Fnt 15), $bMuted, 48, [single]($H - 40))

$pngPath = Join-Path $base 'architecture.png'
$bmp.Save($pngPath, [System.Drawing.Imaging.ImageFormat]::Png)
$g.Dispose(); $bmp.Dispose()
"png written: $pngPath"
'ALL CHECKS PASSED'
