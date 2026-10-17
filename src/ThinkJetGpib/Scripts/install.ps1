<#
  install.ps1 - run by ThinkJetGpibSetup.exe (elevated, 64-bit Windows PowerShell).

  1. Builds the driver catalog and signs it with a certificate made on this PC. Only the public
     certificate is trusted (Root + TrustedPublisher); the private key is deleted right after
     signing, so it can never sign anything else.
  2. Stages the driver with pnputil and installs it.
  3. Creates a Standard TCP/IP RAW port to 127.0.0.1:<Port> and the printer on it.
  4. Installs and starts the bridge service, which forwards the port's bytes to the printer.

  Writes install-state.json so uninstall.ps1 knows what to remove.
#>
param(
    [Parameter(Mandatory)] [string] $InstallDir,
    [Parameter(Mandatory)] [string] $ExePath,
    [Parameter(Mandatory)] [string] $StateFile,
    [int]    $Port        = 9100,
    [string] $PrinterName = 'HP 2225 ThinkJet',
    [string] $DriverName  = 'HP 2225 ThinkJet (GPIB)',
    [string] $ServiceName = 'ThinkJetGpib',
    [switch] $TestPage
)

$ErrorActionPreference = 'Stop'
$ProgressPreference    = 'SilentlyContinue'
function Step([string] $message) { Write-Host "==> $message" }
function Detail([string] $message) { Write-Host "    $message" }

$driverDir = Join-Path $InstallDir 'driver'
$inf       = Join-Path $driverDir 'TJ2225.inf'
$gpd       = Join-Path $driverDir 'TJ2225.gpd'
$cat       = Join-Path $driverDir 'tj2225.cat'
$portName  = "ThinkJetGpib_127.0.0.1_$Port"
$state     = [ordered]@{
    PrinterName = $PrinterName; DriverName = $DriverName; PortName = $portName
    ServiceName = $ServiceName; CertThumbprint = $null; PublishedInf = $null; InstalledUtc = (Get-Date).ToUniversalTime().ToString('o')
}
function Save-State { $state | ConvertTo-Json | Set-Content -Path $StateFile -Encoding UTF8 }

# ---- 1. Catalog + signature ------------------------------------------------------------------
# Stamp DriverVer with the install time so each install's package is unique and newest (and never
# dated in the future, which Inf2Cat rejects).
$now = Get-Date
$driverVer = 'DriverVer={0},1.0.{1}.{2}' -f $now.ToString('MM/dd/yyyy', [Globalization.CultureInfo]::InvariantCulture),
    $now.ToString('yyMM'), $now.ToString('ddHH')
(Get-Content $inf) -replace '^DriverVer=.*$', $driverVer | Set-Content $inf -Encoding ASCII
Detail $driverVer

Step 'Creating the driver catalog'
Remove-Item $cat -ErrorAction SilentlyContinue
$inf2cat = Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\bin" -Recurse -Filter Inf2Cat.exe -ErrorAction SilentlyContinue |
    Where-Object FullName -match '\\x86\\' | Sort-Object FullName | Select-Object -Last 1
if ($inf2cat) {
    Detail "Using $($inf2cat.FullName)"
    & $inf2cat.FullName "/driver:$driverDir" '/os:10_X64' '/uselocaltime' | ForEach-Object { Detail $_ }
    if ($LASTEXITCODE -ne 0) { throw "Inf2Cat failed (exit code $LASTEXITCODE)." }
} else {
    Detail 'Inf2Cat (WDK) not found; using New-FileCatalog'
    New-FileCatalog -Path @($inf, $gpd) -CatalogFilePath $cat -CatalogVersion 2.0 | Out-Null
}

Step 'Signing the catalog with a certificate created on this PC'
$cert = New-SelfSignedCertificate -Type CodeSigningCert `
    -Subject 'CN=ThinkJetGpib Local Driver Signing' -FriendlyName 'ThinkJetGpib local driver signing' `
    -CertStoreLocation Cert:\LocalMachine\My -KeyExportPolicy NonExportable `
    -KeyAlgorithm RSA -KeyLength 3072 -HashAlgorithm SHA256 -NotAfter (Get-Date).AddYears(30)
$state.CertThumbprint = $cert.Thumbprint
Save-State
Detail "Certificate $($cert.Thumbprint)"

$publicCer = Join-Path $env:TEMP "ThinkJetGpib-$($cert.Thumbprint).cer"
try {
    Export-Certificate -Cert $cert -FilePath $publicCer | Out-Null
    Import-Certificate -FilePath $publicCer -CertStoreLocation Cert:\LocalMachine\Root | Out-Null
    Import-Certificate -FilePath $publicCer -CertStoreLocation Cert:\LocalMachine\TrustedPublisher | Out-Null
    $signature = Set-AuthenticodeSignature -FilePath $cat -Certificate $cert -HashAlgorithm SHA256
} finally {
    Remove-Item $publicCer -ErrorAction SilentlyContinue
    # Whatever happened above, the private key must not outlive this step.
    Remove-Item "Cert:\LocalMachine\My\$($cert.Thumbprint)" -DeleteKey
}
Detail 'Private key deleted; only the public certificate remains (trusted for this driver)'
if ($signature.Status -ne 'Valid') { throw "Catalog signature is '$($signature.Status)': $($signature.StatusMessage)" }

# ---- 2. Driver ----------------------------------------------------------------------------------
Step 'Adding the driver package to the driver store'
$pnputil = Join-Path $env:windir 'System32\pnputil.exe'
$pnpOutput = & $pnputil /add-driver $inf
$pnpExit = $LASTEXITCODE
$pnpOutput | ForEach-Object { Detail $_ }
if ($pnpExit -notin 0, 3010) { throw "pnputil failed (exit code $pnpExit)." }
$published = $pnpOutput | Select-String -Pattern '(oem\d+\.inf)' | Select-Object -First 1
if ($published) {
    $state.PublishedInf = $published.Matches[0].Groups[1].Value
    Save-State
}

Step "Installing printer driver '$DriverName'"
try { Add-PrinterDriver -Name $DriverName }
catch {
    $hint = ''
    $wppm = Get-ItemProperty 'HKLM:\SOFTWARE\Policies\Microsoft\Windows NT\Printers\WPP' -ErrorAction SilentlyContinue
    if ($wppm -and $wppm.WindowsProtectedPrintGroupPolicyState -eq 1) {
        $hint = ' Windows protected print mode is on, and it blocks all third-party drivers.'
    }
    throw "Add-PrinterDriver failed: $($_.Exception.Message)$hint"
}

# ---- 3. Port + printer --------------------------------------------------------------------------
Step "Creating printer port $portName (RAW, 127.0.0.1:$Port)"
if (Get-Printer -Name $PrinterName -ErrorAction SilentlyContinue) { Remove-Printer -Name $PrinterName }
if (Get-PrinterPort -Name $portName -ErrorAction SilentlyContinue) { Remove-PrinterPort -Name $portName }
Add-PrinterPort -Name $portName -PrinterHostAddress '127.0.0.1' -PortNumber $Port

Step "Creating printer '$PrinterName'"
Add-Printer -Name $PrinterName -DriverName $DriverName -PortName $portName

# ---- 4. Bridge service -------------------------------------------------------------------------
Step "Installing service '$ServiceName'"
if (Get-Service -Name $ServiceName -ErrorAction SilentlyContinue) {
    Stop-Service -Name $ServiceName -Force -ErrorAction SilentlyContinue
    & sc.exe delete $ServiceName | Out-Null
}
# A deleted service lingers ("marked for deletion") while anything, such as the Services console,
# holds it open.
foreach ($i in 1..30) {
    if (-not (Get-Service -Name $ServiceName -ErrorAction SilentlyContinue)) { break }
    if ($i -eq 30) { throw "Service '$ServiceName' is still marked for deletion. Close the Services console (services.msc) and run setup again." }
    Start-Sleep -Milliseconds 500
}
New-Service -Name $ServiceName -BinaryPathName "`"$ExePath`" --service" `
    -DisplayName 'ThinkJet GPIB Print Bridge' -StartupType Automatic `
    -Description "Forwards print jobs from 127.0.0.1:$Port to the HP 2225 ThinkJet over NI-VISA." | Out-Null
& sc.exe failure $ServiceName reset= 86400 actions= restart/5000/restart/5000/restart/30000 | Out-Null
Start-Service -Name $ServiceName

$listening = $false
foreach ($i in 1..20) {
    $client = New-Object System.Net.Sockets.TcpClient
    try { $client.Connect('127.0.0.1', $Port); $listening = $true; break }
    catch { Start-Sleep -Milliseconds 500 }
    finally { $client.Dispose() }
}
if (-not $listening) { throw "The bridge service started but is not listening on 127.0.0.1:$Port. See $env:ProgramData\ThinkJetGpib\bridge.log" }
Detail "Bridge is listening on 127.0.0.1:$Port"
Save-State

if ($TestPage) {
    Step 'Sending a Windows test page'
    $printer = Get-CimInstance Win32_Printer | Where-Object Name -eq $PrinterName
    $result = Invoke-CimMethod -InputObject $printer -MethodName PrintTestPage
    if ($result.ReturnValue -ne 0) { Write-Warning "PrintTestPage returned $($result.ReturnValue)" }
}

Step 'Done'
