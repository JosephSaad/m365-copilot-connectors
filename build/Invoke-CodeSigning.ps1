<#
.SYNOPSIS
    Authenticode signs a built deployment package and produces a signed catalog
    covering everything in it.

.DESCRIPTION
    Three things happen here, and they cover three different kinds of file:

      1. The assemblies THIS REPOSITORY BUILDS are Authenticode signed.
      2. The PowerShell that ships in the package is Authenticode signed.
      3. A Windows file catalog is built over the whole package and signed, so
         that the files no signature can be embedded in - SQL scripts, JSON,
         Markdown, the staged source tree - are covered too.

    Together those answer the change advisory board's question, which is not
    "did you sign the exe" but "is what I unpacked what you built".

    WHY THIRD PARTY ASSEMBLIES ARE LEFT ALONE. A published package is signed by
    its publisher, and that signature is a stronger provenance statement than
    ours: it says Microsoft or Serilog built this, which we cannot say. Re-
    signing replaces it with a weaker claim and destroys the ability to verify
    the original. So only assemblies built from this repository are signed, and
    they are identified by name from the project list rather than by "everything
    in publish\", because the latter is how a third party assembly gets re-signed
    by accident the first time a dependency is added.

    WHY THE REPOSITORY WORKING TREE IS NEVER SIGNED. Set-AuthenticodeSignature on
    a .ps1 APPENDS a signature block to the file. Run against deploy\*.ps1 in the
    repository it would modify tracked source, the modification would be
    committed, and the next edit to that script would invalidate the signature
    while leaving the block in place - a file that looks signed and is not. This
    script therefore refuses to operate on a path inside the repository unless
    that path is the artifacts output, and the check is by resolved path rather
    than by name.

    WHY TIMESTAMPING IS NOT OPTIONAL. An Authenticode signature without a
    countersigned timestamp becomes invalid when the signing certificate
    expires, typically in one to three years, and the package on the customer's
    file share stops verifying long after anybody remembers why. With a
    timestamp the signature remains valid for the life of the timestamp
    authority's own certificate. A release signing run that cannot reach a
    timestamp server fails rather than producing a package with a shelf life.

    THE TEST CERTIFICATE. -CreateTestCertificate generates a self-signed code
    signing certificate so that the mechanism can be exercised without the real
    one, which is the only way this script gets tested before a customer needs
    it. Packages signed that way are marked, and -RequireTrustedCertificate
    refuses them, so a test signature cannot be mistaken for a release.

.PARAMETER Path
    The built package root, normally artifacts\.

.PARAMETER CertificateThumbprint
    Thumbprint of a code signing certificate in Cert:\CurrentUser\My or
    Cert:\LocalMachine\My.

.PARAMETER TimestampServer
    RFC 3161 timestamp authority. Defaults to DigiCert's.

.PARAMETER CreateTestCertificate
    Generate and use a self-signed certificate. For verifying this script only.

.PARAMETER RequireTrustedCertificate
    Fail if the signing certificate does not chain to a trusted root. Set this
    for anything that will be released.

.PARAMETER AllowUntrustedCertificate
    Accept a signature whose certificate does not chain to a trusted root, which
    Windows reports as UnknownError rather than as a failure. For a build agent
    holding the signing key but not the enterprise root, and for rehearsing with
    a self-signed certificate supplied by thumbprint.

.PARAMETER SkipTimestamp
    Sign without a timestamp. Refused unless -CreateTestCertificate is also set.

.EXAMPLE
    .\Invoke-CodeSigning.ps1 -Path .\artifacts -CertificateThumbprint A1B2... -RequireTrustedCertificate

.EXAMPLE
    .\Invoke-CodeSigning.ps1 -Path .\artifacts -CreateTestCertificate -SkipTimestamp

.NOTES
    Windows only: Authenticode, New-FileCatalog and Test-FileCatalog have no
    cross-platform equivalent. Works on Windows PowerShell 5.1 and PowerShell 7.
#>

[CmdletBinding(DefaultParameterSetName = 'Thumbprint')]
param(
    [Parameter(Mandatory)]
    [string]$Path,

    [Parameter(ParameterSetName = 'Thumbprint')]
    [string]$CertificateThumbprint,

    [Parameter(ParameterSetName = 'TestCertificate')]
    [switch]$CreateTestCertificate,

    [string]$TimestampServer = 'http://timestamp.digicert.com',

    [switch]$RequireTrustedCertificate,

    # Accept a signature whose certificate does not chain to a trusted root.
    # Needed when rehearsing the signing step with a self-signed certificate
    # supplied by thumbprint rather than created here: Windows reports such a
    # signature as UnknownError, which means "present, well formed, untrusted
    # chain" rather than "failed to sign", and without this switch that would be
    # treated as a signing failure.
    #
    # It exists as a separate switch from -CreateTestCertificate because the two
    # are different claims. -CreateTestCertificate says "I made a throwaway
    # certificate"; this says "the certificate is real to me but not to this
    # machine's trust store", which is the ordinary situation on a build agent
    # that has the signing key but not the enterprise root.
    [switch]$AllowUntrustedCertificate,

    [switch]$SkipTimestamp
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if (-not $IsWindows -and $PSVersionTable.PSVersion.Major -ge 6) {
    throw 'Code signing requires Windows. Authenticode and file catalogs have no cross-platform equivalent.'
}

# ---------------------------------------------------------------------------
# Normalise every path to its long form before anything compares two of them.
#
# THIS IS NOT TIDINESS, IT IS THE BUG THAT DEFEATED BOTH GUARDS BELOW ON THEIR
# FIRST RUN. Resolve-Path preserves an 8.3 short name: given
# C:\Users\JOSEPH~1\... it returns C:\Users\JOSEPH~1\..., while Get-ChildItem
# returns C:\Users\JosephSaad\... for the very same file. Every StartsWith
# comparison between the two is then false, and a guard that answers "no" to
# "is this file inside the directory I must not touch" is not a guard.
#
# Measured, because it is easy to disbelieve:
#   Resolve-Path        C:\Users\JOSEPH~1\AppData\Local\Temp\signtest
#   Get-Item .FullName  C:\Users\JosephSaad\AppData\Local\Temp\signtest
#   StartsWith between them: False
#
# The first run of this script signed a file under source\ that the exclusion
# was written to protect. The same defect in the repository guard would have
# appended signature blocks to tracked source, which is the failure this script
# exists to prevent.
#
# GetFullPath rather than Get-Item, because artifactsRoot below need not exist
# yet and Get-Item throws on a path that does not.
# ---------------------------------------------------------------------------

$repoRoot = [System.IO.Path]::GetFullPath((Split-Path $PSScriptRoot -Parent))
$Path = [System.IO.Path]::GetFullPath((Resolve-Path $Path).Path)

# ---------------------------------------------------------------------------
# Refuse to sign the repository itself.
#
# Resolved paths, not name matching. "artifacts" appearing somewhere in a
# string is not evidence that the path IS the artifacts directory, and the
# failure this guard prevents - a signature block appended to tracked source -
# is not one you notice until the file will not run.
# ---------------------------------------------------------------------------

$artifactsRoot = [System.IO.Path]::GetFullPath((Join-Path $repoRoot 'artifacts'))
$isUnderRepo = $Path.StartsWith($repoRoot, [StringComparison]::OrdinalIgnoreCase)
$isArtifacts = $Path.StartsWith($artifactsRoot, [StringComparison]::OrdinalIgnoreCase)

if ($isUnderRepo -and -not $isArtifacts) {
    throw "Refusing to sign '$Path': it is inside the repository but is not the artifacts output. Signing a .ps1 appends a signature block to it, which would modify tracked source. Sign a built package instead."
}

# ---------------------------------------------------------------------------
# Acquire the certificate.
# ---------------------------------------------------------------------------

$usingTestCertificate = $false

if ($CreateTestCertificate) {
    Write-Host '== Creating a self-signed code signing certificate ==' -ForegroundColor Yellow
    Write-Warning 'This certificate is for verifying the signing mechanism. A package signed with it must never be released.'

    $cert = New-SelfSignedCertificate `
        -Subject 'CN=Connector Build Test Signing, O=DO NOT TRUST' `
        -Type CodeSigningCert `
        -KeyUsage DigitalSignature `
        -KeyAlgorithm RSA `
        -KeyLength 3072 `
        -CertStoreLocation 'Cert:\CurrentUser\My' `
        -NotAfter (Get-Date).AddDays(2)

    $usingTestCertificate = $true
    Write-Host "  thumbprint $($cert.Thumbprint), expires $($cert.NotAfter.ToString('yyyy-MM-dd'))"
}
else {
    if (-not $CertificateThumbprint) {
        throw 'Supply -CertificateThumbprint, or -CreateTestCertificate to exercise the mechanism.'
    }

    $cert = Get-ChildItem -Path 'Cert:\CurrentUser\My', 'Cert:\LocalMachine\My' -ErrorAction SilentlyContinue |
        Where-Object { $_.Thumbprint -eq $CertificateThumbprint } |
        Select-Object -First 1

    if (-not $cert) {
        throw "No certificate with thumbprint $CertificateThumbprint in Cert:\CurrentUser\My or Cert:\LocalMachine\My."
    }

    if (-not $cert.HasPrivateKey) {
        throw "Certificate $CertificateThumbprint has no private key in this store, so it cannot sign."
    }

    # A code signing certificate carries Enhanced Key Usage 1.3.6.1.5.5.7.3.3.
    # Signing with one that does not will appear to succeed and will fail
    # validation on the customer's machine, which is the wrong place to find out.
    $eku = $cert.Extensions | Where-Object { $_.Oid.Value -eq '2.5.29.37' }
    if ($eku) {
        $ekuText = $eku.Format($false)
        if ($ekuText -notmatch 'Code Signing|1\.3\.6\.1\.5\.5\.7\.3\.3') {
            throw "Certificate $CertificateThumbprint is not a code signing certificate. Its enhanced key usage is: $ekuText"
        }
    }

    if ($cert.NotAfter -lt (Get-Date)) {
        throw "Certificate $CertificateThumbprint expired on $($cert.NotAfter.ToString('yyyy-MM-dd'))."
    }
    if ($cert.NotAfter -lt (Get-Date).AddDays(30)) {
        Write-Warning "Certificate $CertificateThumbprint expires on $($cert.NotAfter.ToString('yyyy-MM-dd')). Timestamped signatures survive expiry; new signings will not."
    }
}

if ($RequireTrustedCertificate) {
    $chain = New-Object System.Security.Cryptography.X509Certificates.X509Chain
    $chainOk = $chain.Build($cert)
    if (-not $chainOk) {
        $reasons = ($chain.ChainStatus | ForEach-Object { $_.StatusInformation.Trim() }) -join '; '
        throw "-RequireTrustedCertificate is set and the certificate does not chain to a trusted root: $reasons"
    }
    if ($usingTestCertificate) {
        throw '-RequireTrustedCertificate and -CreateTestCertificate are mutually exclusive: a self-signed build certificate is by definition not the release one.'
    }
}

# One flag for "an untrusted chain is expected here", however it arose. The three
# verification points below all ask the same question, and if they answered it
# differently a package would pass one check and fail the next for no reason a
# reader could see.
$tolerateUntrusted = $usingTestCertificate -or $AllowUntrustedCertificate

if ($SkipTimestamp -and -not $usingTestCertificate) {
    throw '-SkipTimestamp is only permitted with -CreateTestCertificate. A release signature without a timestamp stops verifying when the certificate expires.'
}

# ---------------------------------------------------------------------------
# Work out what to sign.
#
# Our own assemblies by name. The list comes from the project directories rather
# than from a hand maintained array, so a new project is covered the day it is
# added instead of the day somebody notices it was not.
# ---------------------------------------------------------------------------

$ourAssemblyNames = @(
    Get-ChildItem -Path (Join-Path $repoRoot 'src') -Directory |
        ForEach-Object { $_.Name }
) | Sort-Object -Unique

Write-Verbose "Own assemblies: $($ourAssemblyNames -join ', ')"

$peFiles = @(
    Get-ChildItem -Path $Path -Recurse -File -Include *.exe, *.dll |
        Where-Object {
            $baseName = [System.IO.Path]::GetFileNameWithoutExtension($_.Name)
            $ourAssemblyNames -contains $baseName
        }
)

# The staged source tree under source\ is a copy of the repository, and signing
# a .ps1 there would append a block to a file whose whole purpose is to be
# rebuilt. It is covered by the catalog instead.
$sourceStage = [System.IO.Path]::GetFullPath((Join-Path $Path 'source'))
$scriptFiles = @(
    Get-ChildItem -Path $Path -Recurse -File -Include *.ps1, *.psm1, *.psd1 |
        Where-Object { -not ([System.IO.Path]::GetFullPath($_.FullName)).StartsWith($sourceStage, [StringComparison]::OrdinalIgnoreCase) }
)

# The exclusion has to actually exclude. If a staged source tree is present and
# every script in it still came through, the path comparison has failed and the
# next thing that happens is signature blocks appended to files meant to be
# rebuilt. Checked rather than trusted, because this is precisely what went
# wrong the first time.
if (Test-Path $sourceStage) {
    $leaked = @($scriptFiles | Where-Object { $_.FullName -like (Join-Path $sourceStage '*') })
    if ($leaked.Count -gt 0) {
        throw "The staged source exclusion failed: $($leaked.Count) file(s) under source\ were selected for signing. This is a path normalisation defect, not a configuration one."
    }
}

Write-Host "== Signing ==" -ForegroundColor Cyan
Write-Host "  $($peFiles.Count) assembly file(s) built here"
Write-Host "  $($scriptFiles.Count) PowerShell file(s) outside the staged source tree"

if ($peFiles.Count -eq 0 -and $scriptFiles.Count -eq 0) {
    throw "Nothing to sign under $Path. Check that the package was built before signing it."
}

# ---------------------------------------------------------------------------
# Sign.
# ---------------------------------------------------------------------------

$signArgs = @{
    Certificate   = $cert
    HashAlgorithm = 'SHA256'
    ErrorAction   = 'Stop'
}
if (-not $SkipTimestamp) {
    $signArgs['TimestampServer'] = $TimestampServer
}

$signed = 0
$failures = New-Object System.Collections.ArrayList

foreach ($file in @($peFiles) + @($scriptFiles)) {
    try {
        $result = Set-AuthenticodeSignature -FilePath $file.FullName @signArgs
        if ($result.Status -ne 'Valid') {
            # UnknownError with a self-signed certificate means the chain is not
            # trusted, which is expected under -CreateTestCertificate and is not
            # a signing failure: the signature is present and well formed.
            if ($tolerateUntrusted -and $result.Status -eq 'UnknownError') {
                $signed++
            }
            else {
                [void]$failures.Add("$($file.Name): $($result.Status) $($result.StatusMessage)")
            }
        }
        else {
            $signed++
        }
    }
    catch {
        [void]$failures.Add("$($file.Name): $($_.Exception.Message)")
    }
}

if ($failures.Count -gt 0) {
    $failures | ForEach-Object { Write-Error $_ }
    throw "Signing failed for $($failures.Count) file(s)."
}

Write-Host "  signed $signed file(s)" -ForegroundColor Green

# ---------------------------------------------------------------------------
# Catalog.
#
# Everything else in the package: SQL scripts, JSON, Markdown, the staged
# source. None of them can carry an embedded signature, and all of them change
# what the deployment does. sql\43 alters the run lock; appsettings.json decides
# which tenant is written to. A package where the exe is signed and the SQL is
# not is a package where the interesting half is unprotected.
#
# Catalog version 2 uses SHA-256. Version 1 is SHA-1 and is not acceptable.
# ---------------------------------------------------------------------------

# ---------------------------------------------------------------------------
# Record what was signed and with what, BEFORE the catalog is built.
#
# The order is load-bearing and got this wrong on its first run. New-FileCatalog
# hashes the tree as it stands, and Test-FileCatalog validates in both
# directions: a file present on disk that the catalog does not list fails
# validation exactly as a modified file does. Writing the manifest after
# cataloguing therefore produced a package that failed its own verification the
# moment anybody ran it, while the run that produced it reported Valid because
# it tested before writing. Writing it first also means the manifest is itself
# covered by the catalog, which is what you want of the file that states which
# certificate to trust.
#
# The customer's change advisory board is asked to accept a package on the
# strength of a signature. This file tells them which certificate to expect, so
# that "it is signed" can be checked against "it is signed by the right party"
# rather than taken on trust.
# ---------------------------------------------------------------------------

$manifest = [ordered]@{
    signedUtc          = (Get-Date).ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ')
    subject            = $cert.Subject
    issuer             = $cert.Issuer
    thumbprint         = $cert.Thumbprint
    notAfter           = $cert.NotAfter.ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ')
    hashAlgorithm      = 'SHA256'
    timestamped        = (-not $SkipTimestamp)
    timestampServer    = if ($SkipTimestamp) { $null } else { $TimestampServer }
    testCertificate    = $usingTestCertificate
    signedFileCount    = $signed
    catalogFile        = 'package.cat'
    catalogVersion     = 2
    verifyWith         = 'Test-FileCatalog -Path . -CatalogFilePath .\package.cat -FilesToSkip package.cat -Detailed'
}

$manifestPath = Join-Path $Path 'signing-manifest.json'
$utf8NoBom = New-Object System.Text.UTF8Encoding($false)
[System.IO.File]::WriteAllText($manifestPath, ($manifest | ConvertTo-Json -Depth 5), $utf8NoBom)

Write-Host "  manifest written: $manifestPath"

Write-Host '== Cataloguing ==' -ForegroundColor Cyan

$catalogPath = Join-Path $Path 'package.cat'
if (Test-Path $catalogPath) { Remove-Item $catalogPath -Force }

$catalog = New-FileCatalog -Path $Path -CatalogFilePath $catalogPath -CatalogVersion 2
$catalogResult = Set-AuthenticodeSignature -FilePath $catalog.FullName @signArgs

if ($catalogResult.Status -ne 'Valid' -and -not ($tolerateUntrusted -and $catalogResult.Status -eq 'UnknownError')) {
    throw "Could not sign the catalog: $($catalogResult.Status) $($catalogResult.StatusMessage)"
}

Write-Host "  catalog written: $catalogPath" -ForegroundColor Green

# ---------------------------------------------------------------------------
# Verify what was just done, rather than assume it.
# ---------------------------------------------------------------------------

Write-Host '== Verifying ==' -ForegroundColor Cyan

$unsigned = New-Object System.Collections.ArrayList
foreach ($file in @($peFiles) + @($scriptFiles)) {
    $sig = Get-AuthenticodeSignature -FilePath $file.FullName
    if ($sig.SignerCertificate -eq $null) {
        [void]$unsigned.Add($file.FullName)
    }
    elseif ($sig.SignerCertificate.Thumbprint -ne $cert.Thumbprint) {
        [void]$unsigned.Add("$($file.FullName) (signed by a different certificate)")
    }
}

if ($unsigned.Count -gt 0) {
    $unsigned | ForEach-Object { Write-Error "Not signed: $_" }
    throw "$($unsigned.Count) file(s) are not carrying the expected signature."
}

Write-Host "  all $signed file(s) carry the expected signature" -ForegroundColor Green

# Test-FileCatalog compares the catalog against the tree. The catalog file and
# its own signature are excluded, because they did not exist when the hashes
# were taken and cataloguing a catalog is circular.
$catalogTest = Test-FileCatalog -Path $Path -CatalogFilePath $catalogPath -FilesToSkip 'package.cat' -Detailed
Write-Host "  catalog status: $($catalogTest.Status)"

if ($catalogTest.Status -ne 'Valid') {
    if ($tolerateUntrusted -and $catalogTest.Status -eq 'ValidationFailed' -and $catalogTest.Signature.Status -eq 'UnknownError') {
        Write-Host '  (the catalog hashes match; the signature is untrusted because the certificate is self-signed)' -ForegroundColor Yellow
    }
    else {
        throw "Catalog verification failed: $($catalogTest.Status)"
    }
}

Write-Host ''
if ($usingTestCertificate) {
    Write-Warning 'Signed with a self-signed test certificate. This package must not be released.'
}
Write-Host "Signing complete. Manifest: $manifestPath" -ForegroundColor Green

return [pscustomobject]@{
    SignedFiles     = $signed
    CatalogPath     = $catalogPath
    ManifestPath    = $manifestPath
    Thumbprint      = $cert.Thumbprint
    TestCertificate = $usingTestCertificate
}
