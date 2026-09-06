#requires -Version 5.1
[CmdletBinding()]
param([Parameter(Mandatory)][string]$UpdaterPath, [Parameter(Mandatory)][string]$PublisherPath,
    [Parameter(Mandatory)][string]$AssemblyPath)
$ErrorActionPreference = 'Stop'
. $UpdaterPath
$testParent = Join-Path ([IO.Path]::GetTempPath()) 'CompanyOps.Prebuilt.Tests'
$fixtureRoot = Join-Path $testParent ('package space & (test) ' + [guid]::NewGuid().ToString('N'))
$sourceFixture = Join-Path $fixtureRoot 'source'
$feedFixture = Join-Path $fixtureRoot 'feed'
function Assert-PrebuiltTest([bool]$Condition, [string]$Message) { if (-not $Condition) { throw $Message } }
function Assert-Rejected([scriptblock]$Action, [string]$Name) {
    $failed = $false
    try { & $Action | Out-Null } catch { $failed = $true }
    Assert-PrebuiltTest $failed ("Expected rejection: " + $Name)
}
try {
    [IO.Directory]::CreateDirectory((Join-Path $sourceFixture 'Setup')) | Out-Null
    [IO.Directory]::CreateDirectory((Join-Path $sourceFixture 'Payload\Agent')) | Out-Null
    [IO.File]::Copy($AssemblyPath, (Join-Path $sourceFixture 'Payload\Agent\CompanyOps.Agent.dll'))
    [IO.File]::Copy($AssemblyPath, (Join-Path $sourceFixture 'Setup\CompanyOps-Setup.dll'))
    [IO.File]::WriteAllText((Join-Path $sourceFixture 'Setup\CompanyOps-Setup.exe'), 'INERT: never execute this fixture')
    [IO.File]::WriteAllText((Join-Path $sourceFixture 'Payload.sha256.json'), '{"Version":1,"Files":[]}')
    $publication = & pwsh.exe -NoProfile -File $PublisherPath -PackageRoot $sourceFixture -FeedRoot $feedFixture 2>&1
    Assert-PrebuiltTest ($LASTEXITCODE -eq 0) ("Publisher failed: " + ($publication | Out-String))
    $package = Read-CompanyOpsPrebuiltPackage $feedFixture
    Assert-PrebuiltTest ($package.FileCount -eq 4) 'Transfer did not preserve all fixture files'
    $script:installerCalled = $false
    function Start-CompanyOpsPrebuiltUpgrade { param($SetupPath) $script:installerCalled = $true; throw 'Unexpected GUI launch in a check' }
    Invoke-CompanyOpsPrebuiltUpdate $feedFixture -CheckOnly | Out-Null
    Assert-PrebuiltTest (-not $script:installerCalled) 'Check launched an installer'
    Assert-Rejected { Invoke-CompanyOpsPrebuiltUpdate $feedFixture -Apply } 'apply without unattended mode'
    Assert-Rejected { Invoke-CompanyOpsPrebuiltUpdate $feedFixture -Unattended -Apply -FromRevision 'main' } 'ambiguous source version'

    $pointerPath = Join-Path $feedFixture 'latest.json'
    $recordPath = Join-Path $package.PackageRoot 'package-record.json'
    $originalPointer = [IO.File]::ReadAllText($pointerPath)
    $originalRecord = [IO.File]::ReadAllText($recordPath)
    $payloadFile = Join-Path $package.PackageRoot 'Setup\CompanyOps-Setup.exe'
    $originalPayload = [IO.File]::ReadAllBytes($payloadFile)
    $scenarios = @('tampered-file', 'missing-file', 'extra-file', 'record-hash', 'traversal', 'duplicate', 'missing-entry', 'bad-revision')
    foreach ($scenario in $scenarios) {
        try {
            $record = $originalRecord | ConvertFrom-Json
            $pointer = $originalPointer | ConvertFrom-Json
            switch ($scenario) {
                'tampered-file' { [IO.File]::AppendAllText($payloadFile, 'modified') }
                'missing-file' { [IO.File]::Delete($payloadFile) }
                'extra-file' { [IO.File]::WriteAllText((Join-Path $package.PackageRoot 'unexpected.dll'), 'extra') }
                'record-hash' { $pointer.recordSha256 = '0' * 64 }
                'traversal' { $record.files[0].path = '../outside.dll' }
                'duplicate' { $record.files[1].path = $record.files[0].path }
                'missing-entry' {
                    $record.files = @($record.files | Where-Object { $_.path -ne 'Setup/CompanyOps-Setup.exe' })
                    [IO.File]::Delete($payloadFile)
                }
                'bad-revision' { $pointer.revision = '../source' }
            }
            if ($scenario -in @('traversal', 'duplicate', 'missing-entry')) {
                [IO.File]::WriteAllText($recordPath, ($record | ConvertTo-Json -Depth 5))
                $pointer.recordSha256 = Get-CompanyOpsPackageHash $recordPath
            }
            [IO.File]::WriteAllText($pointerPath, ($pointer | ConvertTo-Json))
            Assert-Rejected { Read-CompanyOpsPrebuiltPackage $feedFixture } $scenario
        } finally {
            [IO.File]::WriteAllText($pointerPath, $originalPointer)
            [IO.File]::WriteAllText($recordPath, $originalRecord)
            [IO.File]::WriteAllBytes($payloadFile, $originalPayload)
            $extra = Join-Path $package.PackageRoot 'unexpected.dll'
            if (Test-Path -LiteralPath $extra) { [IO.File]::Delete($extra) }
        }
    }
    # A repeated publisher must never overwrite an immutable version or change the current selector.
    $savedPreference = $ErrorActionPreference
    try {
        $ErrorActionPreference = 'Continue'
        $publication = & pwsh.exe -NoProfile -File $PublisherPath -PackageRoot $sourceFixture -FeedRoot $feedFixture 2>&1
        $repeatedExitCode = $LASTEXITCODE
    } finally { $ErrorActionPreference = $savedPreference }
    Assert-PrebuiltTest ($repeatedExitCode -ne 0) 'Repeated publication overwrote an existing version'
    Assert-PrebuiltTest ([IO.File]::ReadAllText($pointerPath) -eq $originalPointer) 'Failed publication changed latest.json'
    Assert-PrebuiltTest (@(Get-ChildItem -LiteralPath $feedFixture -Directory -Filter '.upload-*').Count -eq 0) 'Temporary transfer was not cleaned'
    Read-CompanyOpsPrebuiltPackage $feedFixture | Out-Null
    Write-Host 'COMPANYOPS-PREBUILT-TESTS-PASSED'
} finally {
    $resolved = [IO.Path]::GetFullPath($fixtureRoot)
    if (-not $resolved.StartsWith([IO.Path]::GetFullPath($testParent) + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Cleanup escaped fixture parent' }
    if (Test-Path -LiteralPath $resolved) {
        Assert-CompanyOpsOrdinaryPath $resolved
        Remove-Item -LiteralPath $resolved -Recurse -Force
    }
}
