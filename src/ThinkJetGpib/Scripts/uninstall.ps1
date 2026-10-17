<#
  uninstall.ps1 - run by ThinkJetGpib.exe --uninstall (and before an upgrade).
  Removes what install.ps1 created, as recorded in install-state.json: the service, printer, port,
  driver (including its driver-store package) and the local signing certificate.
  Best effort: each step reports a failure and the rest still run.
#>
param(
    [Parameter(Mandatory)] [string] $StateFile
)

$ErrorActionPreference = 'Continue'
$ProgressPreference    = 'SilentlyContinue'
function Step([string] $message) { Write-Host "==> $message" }
function Detail([string] $message) { Write-Host "    $message" }

$state = if (Test-Path $StateFile) { Get-Content $StateFile -Raw | ConvertFrom-Json } else { $null }
$printerName = if ($state) { $state.PrinterName } else { 'HP 2225 ThinkJet' }
$driverName  = if ($state) { $state.DriverName }  else { 'HP 2225 ThinkJet (GPIB)' }
$portName    = if ($state) { $state.PortName }    else { 'ThinkJetGpib_127.0.0.1_9100' }
$serviceName = if ($state) { $state.ServiceName } else { 'ThinkJetGpib' }

Step "Removing service '$serviceName'"
if (Get-Service -Name $serviceName -ErrorAction SilentlyContinue) {
    Stop-Service -Name $serviceName -Force -ErrorAction SilentlyContinue
    & sc.exe delete $serviceName | Out-Null
} else { Detail 'not installed' }

Step "Removing printer '$printerName'"
if (Get-Printer -Name $printerName -ErrorAction SilentlyContinue) { Remove-Printer -Name $printerName } else { Detail 'not installed' }

Step "Removing port $portName"
if (Get-PrinterPort -Name $portName -ErrorAction SilentlyContinue) { Remove-PrinterPort -Name $portName } else { Detail 'not installed' }

Step "Removing driver '$driverName'"
function Remove-Driver {
    foreach ($attempt in 1..5) {
        try { Remove-PrinterDriver -Name $driverName -RemoveFromDriverStore -ErrorAction Stop; return $true }
        catch { Start-Sleep -Seconds 2 }
    }
    return $false
}
if (Get-PrinterDriver -Name $driverName -ErrorAction SilentlyContinue) {
    if (-not (Remove-Driver)) {
        Detail 'The spooler is still holding the driver; restarting the Print Spooler service'
        Restart-Service -Name Spooler -Force
        Start-Sleep -Seconds 3
        if (-not (Remove-Driver)) { Write-Warning "Could not remove the printer driver; remove it in Print Management (printmanagement.msc)." }
    }
} else { Detail 'not installed' }

# Remove every staged copy of this package, including ones from failed installs that never became
# a printer driver. pnputil /enum-drivers lists "Published Name" then "Original Name" per package.
Step 'Removing driver packages from the driver store'
$pnputil = Join-Path $env:windir 'System32\pnputil.exe'
$packages = New-Object System.Collections.Generic.List[string]
if ($state -and $state.PublishedInf) { $packages.Add($state.PublishedInf) }
$published = $null; $isOurs = $false; $ourProvider = $false
foreach ($line in (& $pnputil /enum-drivers)) {
    if ($line -match '(oem\d+\.inf)\s*$') { $published = $Matches[1]; $isOurs = $false; $ourProvider = $false; continue }
    if ($line -match ':\s*tj2225\.inf\s*$') { $isOurs = $true }
    if ($line -match ':\s*ThinkJetGpib\s*$') { $ourProvider = $true }
    if ($published -and $isOurs -and $ourProvider) { $packages.Add($published); $published = $null }
}
$packages = $packages | Sort-Object -Unique
if (-not $packages) { Detail 'none found' }
foreach ($package in $packages) {
    & $pnputil /delete-driver $package /force | Out-Null
    if ($LASTEXITCODE -eq 0) { Detail "deleted $package" } else { Detail "$package not deleted (exit code $LASTEXITCODE)" }
}

# Last: the certificate only stops being trusted once nothing it signed is left installed.
if ($state -and $state.CertThumbprint) {
    Step "Removing signing certificate $($state.CertThumbprint)"
    foreach ($store in 'Root', 'TrustedPublisher', 'My') {
        $path = "Cert:\LocalMachine\$store\$($state.CertThumbprint)"
        if (Test-Path $path) { Remove-Item $path -DeleteKey -ErrorAction SilentlyContinue; Remove-Item $path -ErrorAction SilentlyContinue; Detail "removed from $store" }
    }
}

Remove-Item $StateFile -ErrorAction SilentlyContinue
Step 'Done'
