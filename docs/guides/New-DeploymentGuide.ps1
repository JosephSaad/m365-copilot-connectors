# Builds docs/guides/deployment-and-test-guide.html into a Word document.
#
# WHY THE SOURCE IS HERE AND NOT ONLY THE .docx. A document nobody can
# regenerate stops being true quietly: the next person edits the .docx, the
# edit is invisible to git, and within two releases nobody knows which version
# is current. The HTML is the source, this script is the build, and the .docx
# is output - the same arrangement as docs/diagrams/New-ArchitectureDiagram.ps1.
#
# WORD COM RATHER THAN A CONVERTER, for the same reason the commercial
# documents use it: a real .docx with a live table of contents, styled tables
# and page numbers, rather than an HTML file wearing a .docx extension that
# Word opens in compatibility mode and prints badly.
#
# Requires Word on the machine. It is a documentation build, not part of
# Build.ps1, and nothing in CI depends on it.
#
#     pwsh docs/guides/New-DeploymentGuide.ps1
#     pwsh docs/guides/New-DeploymentGuide.ps1 -OutputDirectory D:\somewhere

[CmdletBinding()]
param(
    # Defaults BESIDE the repository, not inside it: the .docx is build output,
    # and committing a binary that changes wholesale on every rebuild makes
    # every diff useless. Three levels up from docs\guides\ is the directory
    # the clone sits in - the first version of this line had two and quietly
    # wrote into the repository it was trying to stay out of.
    [string]$OutputDirectory = (Join-Path (Split-Path (Split-Path (Split-Path $PSScriptRoot -Parent) -Parent) -Parent) 'docs-out'),
    [string]$Version = '1.8.0'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$src = Join-Path $PSScriptRoot 'deployment-and-test-guide.html'
if (-not (Test-Path $src)) { throw "Source not found: $src" }

$outDir = $OutputDirectory
$out = Join-Path $outDir "Connector-Deployment-And-Test-Guide-v$Version.docx"

New-Item -ItemType Directory -Force -Path $outDir | Out-Null

$word = New-Object -ComObject Word.Application
$word.Visible = $false
$word.DisplayAlerts = 0

try {
    $doc = $word.Documents.Open($src, $false, $false)

    # A4 with room for a binder margin; this is a document people print and tick off.
    $doc.PageSetup.PageWidth  = $word.CentimetersToPoints(21)
    $doc.PageSetup.PageHeight = $word.CentimetersToPoints(29.7)
    foreach ($m in 'TopMargin','BottomMargin') { $doc.PageSetup.$m = $word.CentimetersToPoints(2) }
    $doc.PageSetup.LeftMargin  = $word.CentimetersToPoints(2.2)
    $doc.PageSetup.RightMargin = $word.CentimetersToPoints(1.8)

    # Body and headings.
    $doc.Styles('Normal').Font.Name = 'Segoe UI'
    $doc.Styles('Normal').Font.Size = 10
    $doc.Styles('Normal').ParagraphFormat.SpaceAfter = 6
    $doc.Styles('Normal').ParagraphFormat.LineSpacingRule = 0

    # Sizes as Single explicitly: Word's Font.Size is a float, and a mixed
    # object[] of names and numbers hands it a boxed Double it will not cast.
    $headingSizes = [ordered]@{ 'Heading 1' = 19.0; 'Heading 2' = 14.0; 'Heading 3' = 11.5 }
    foreach ($name in $headingSizes.Keys) {
        $s = $doc.Styles([string]$name)
        $s.Font.Name = 'Segoe UI Semibold'
        $s.Font.Size = [single]$headingSizes[[string]$name]
        $s.Font.Color = 3552822          # dark navy, BGR
        $s.ParagraphFormat.SpaceBefore = [single]14
        $s.ParagraphFormat.SpaceAfter = [single]6
    }

    # Tables: readable, banded, and repeating the header on a page break - these
    # tables run over pages and a header row that vanishes makes column three
    # unreadable.
    foreach ($t in $doc.Tables) {
        $t.Range.Font.Name = 'Segoe UI'
        $t.Range.Font.Size = 9
        $t.Borders.InsideLineStyle = 1
        $t.Borders.OutsideLineStyle = 1
        $t.Borders.InsideColor = 14211288
        $t.Borders.OutsideColor = 14211288
        $t.Rows(1).HeadingFormat = $true
        $t.Rows(1).Range.Font.Bold = $true
        $t.Rows(1).Shading.BackgroundPatternColor = 15789293   # pale blue-grey, BGR
        $t.Rows.AllowBreakAcrossPages = $false
    }

    # A live TOC field rather than a typed list, so it renumbers itself when
    # somebody inserts a section.
    $first = $doc.Paragraphs(1).Range
    $first.InsertParagraphBefore()
    $tocRange = $doc.Paragraphs(1).Range
    $toc = $doc.TablesOfContents.Add($tocRange, $true, 1, 3)
    $toc.Update()
    # No paragraph is inserted after it: once the field exists, Paragraphs(1) is
    # inside the TOC and the range refuses to be edited.

    # Page numbers, because this is printed and worked through.
    $footer = $doc.Sections(1).Footers(1).Range
    $footer.Text = ''
    $footer.Fields.Add($footer, -1, 'PAGE', $true) | Out-Null
    $footer.ParagraphFormat.Alignment = 2
    $footer.Font.Size = 8

    # SaveAs2 with plain arguments. The [ref] form is the documented one for
    # Windows PowerShell and fails under PowerShell 7, which marshals the
    # PSReference as a psobject the COM layer will not accept. 16 is
    # wdFormatDocumentDefault - a real .docx, not HTML wearing the extension.
    $doc.SaveAs2($out, 16)
    $doc.Close(0)
    "written: $out"
}
finally {
    $word.Quit()
    [System.Runtime.InteropServices.Marshal]::ReleaseComObject($word) | Out-Null
}
