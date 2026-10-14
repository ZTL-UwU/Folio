<#
.SYNOPSIS
  Builds this checkout's Folio, registers it as a PDF app and starts it.

.DESCRIPTION
  Every checkout (the main clone or any git worktree) is registered under its own name, "Folio
  Dev (<folder>)", so builds from several worktrees can be installed side by side without
  replacing each other, and each runs as its own instance. Test one by passing PDFs to this
  script, or from File Explorer: right-click a PDF > Open with > Folio Dev (<folder>). The About
  dialog shows which checkout and commit a running build came from.

  Registration is per user (HKCU) and needs no admin rights. Registrations of checkouts that no
  longer exist are removed on every run.

  Release (the default) publishes with Native AOT, as CI ships. Debug is a plain JIT build, which
  compiles faster.

  A build from this checkout that's still running is closed first, as it locks the output files.

  The File Explorer thumbnail handler (src/Folio.Thumbnails) is built too, with Native AOT in either
  configuration, and registered for the file type of every registered build. As with the Store
  build, Explorer uses it only while one of them is the default PDF app (-SetDefault). Its COM class
  ID is fixed, so it's shared by every checkout: thumbnails come from the build registered most
  recently.

.EXAMPLE
  tools\dev.ps1                         # build, register and start
  tools\dev.ps1 a.pdf b.pdf             # ...and open files
  tools\dev.ps1 -NoLaunch               # build and register only
  tools\dev.ps1 -Configuration Debug    # faster JIT build
  tools\dev.ps1 -SetDefault             # also open Settings to make this build the default PDF app
  tools\dev.ps1 -List                   # show every registered build
  tools\dev.ps1 -Unregister             # remove this checkout's registration (-All: every one)
#>
[CmdletBinding(DefaultParameterSetName = "Build", PositionalBinding = $false)]
param(
    [Parameter(ParameterSetName = "Build", ValueFromRemainingArguments)] [string[]]$Files = @(),
    [Parameter(ParameterSetName = "Build")] [ValidateSet("Debug", "Release")] [string]$Configuration = "Release",
    [Parameter(ParameterSetName = "Build")] [switch]$NoLaunch,
    [Parameter(ParameterSetName = "Build")] [switch]$SetDefault,
    [Parameter(ParameterSetName = "List")] [switch]$List,
    [Parameter(ParameterSetName = "Unregister")] [switch]$Unregister,
    [Parameter(ParameterSetName = "Unregister")] [switch]$All
)

$ErrorActionPreference = "Stop"

$checkout = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$label = Split-Path $checkout -Leaf
$sha = [Security.Cryptography.SHA256]::Create()
$id = -join ($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($checkout.ToUpperInvariant()))[0..3] | ForEach-Object { $_.ToString("x2") })
$displayName = "Folio Dev ($label)"

# Per build: a ProgID for .pdf, and Capabilities under Software\FolioDev\<id> so that Settings >
# Default apps lists it. The key also records the checkout, to find registrations that are stale.
$classes = "HKCU:\Software\Classes"
$builds = "HKCU:\Software\FolioDev"
$registeredApps = "HKCU:\Software\RegisteredApplications"
# Must match ThumbnailProvider.Clsid in src/Folio.Thumbnails.
$thumbnailClsid = "{54e57302-4f85-47eb-8be3-76ee7e671857}"
$thumbnailServer = "$classes\CLSID\$thumbnailClsid"

function Get-Registrations {
    Get-ChildItem $builds -ErrorAction SilentlyContinue | ForEach-Object {
        $properties = Get-ItemProperty $_.PSPath
        [pscustomobject]@{ Id = $_.PSChildName; Name = $properties.Name; Checkout = $properties.Checkout; Exe = $properties.Exe }
    }
}

function Remove-Registration([string]$Id) {
    # The thumbnail handler is shared; remove it only if it's this build's.
    $exe = (Get-ItemProperty "$builds\$Id" -ErrorAction SilentlyContinue).Exe
    $dll = (Get-ItemProperty "$thumbnailServer\InprocServer32" -ErrorAction SilentlyContinue).'(default)'
    if ($exe -and $dll -eq (Join-Path (Split-Path $exe) "Folio.Thumbnails.dll")) {
        Remove-Item $thumbnailServer -Recurse -Force -ErrorAction SilentlyContinue
    }
    Remove-Item "$classes\FolioDev.$Id" -Recurse -Force -ErrorAction SilentlyContinue
    Remove-ItemProperty "$classes\.pdf\OpenWithProgids" -Name "FolioDev.$Id" -ErrorAction SilentlyContinue
    Remove-ItemProperty $registeredApps -Name "FolioDev.$Id" -ErrorAction SilentlyContinue
    Remove-Item "$builds\$Id" -Recurse -Force -ErrorAction SilentlyContinue
}

function Send-AssociationsChanged {
    if (-not ("FolioDev.Shell" -as [type])) {
        Add-Type -Namespace FolioDev -Name Shell -MemberDefinition @"
[System.Runtime.InteropServices.DllImport("shell32.dll")]
public static extern void SHChangeNotify(int eventId, uint flags, System.IntPtr item1, System.IntPtr item2);
"@
    }
    [FolioDev.Shell]::SHChangeNotify(0x08000000, 0, [IntPtr]::Zero, [IntPtr]::Zero) # SHCNE_ASSOCCHANGED
}

function Set-Value([string]$Key, [string]$Name, [string]$Value) {
    if (-not (Test-Path $Key)) { New-Item $Key -Force | Out-Null }
    Set-ItemProperty $Key -Name $Name -Value $Value
}

if ($List) {
    $registrations = @(Get-Registrations)
    if (-not $registrations) { Write-Host "No builds are registered."; return }
    $registrations | ForEach-Object {
        $state = if (-not (Test-Path $_.Exe)) { "not built" } elseif (Get-Process Folio -ErrorAction SilentlyContinue | Where-Object Path -EQ $_.Exe) { "running" } else { "" }
        [pscustomobject]@{ Name = $_.Name; State = $state; Exe = $_.Exe }
    } | Format-Table -AutoSize
    return
}

if ($Unregister) {
    $targets = if ($All) { @(Get-Registrations).Id } else { @($id) }
    foreach ($target in $targets) { Remove-Registration $target }
    Send-AssociationsChanged
    Write-Host $(if ($All) { "Unregistered every Folio Dev build." } else { "Unregistered $displayName." })
    Write-Host "If one was your default PDF app, pick another in Settings > Apps > Default apps."
    return
}

# ------------------------------------------------------------------ build

$project = Join-Path $checkout "src\Folio\Folio.csproj"
$platform = if ([Runtime.InteropServices.RuntimeInformation]::OSArchitecture -eq "Arm64") { @("-p:Platform=ARM64") } else { @() }
# Release publishes to out\dev. Debug stays in bin: built with -o, it would leave out the compiled XAML.
$output = if ($Configuration -eq "Release") { Join-Path $checkout "out\dev\Release" }
    else { (dotnet msbuild $project -getProperty:TargetDir -p:Configuration=Debug @platform).Trim().TrimEnd("\") }
$exe = Join-Path $output "Folio.exe"

# A running build from this folder locks its files.
$running = Get-Process Folio -ErrorAction SilentlyContinue |
    Where-Object { $_.Path -and $_.Path.StartsWith("$output\", [StringComparison]::OrdinalIgnoreCase) }
if ($running) {
    Write-Host "Closing the running build..."
    $running | Stop-Process -Force
    $running | Wait-Process -Timeout 10 -ErrorAction SilentlyContinue
}
# So does the COM surrogate (dllhost.exe) Explorer loads the thumbnail handler into, which keeps it and
# pdfium.dll loaded. Explorer starts a new one when it next needs a thumbnail.
$surrogates = Get-Process dllhost -ErrorAction SilentlyContinue | Where-Object {
    try { $_.Modules | Where-Object { $_.FileName.StartsWith("$output\", [StringComparison]::OrdinalIgnoreCase) } } catch { $false }
}
if ($surrogates) {
    Write-Host "Closing the thumbnail handler's COM surrogate..."
    $surrogates | Stop-Process -Force
    $surrogates | Wait-Process -Timeout 10 -ErrorAction SilentlyContinue
}

# Native AOT finds the linker through vcvarsall.bat, which fails unless vswhere.exe is on PATH.
$installer = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer"
if (-not (Get-Command vswhere.exe -ErrorAction SilentlyContinue) -and (Test-Path $installer)) { $env:PATH += ";$installer" }

if ($Configuration -eq "Release") {
    # Publishing is what compiles with Native AOT; a Release build would still run on the JIT.
    dotnet publish $project -c Release -o $output --nologo @platform
} else {
    dotnet build $project -c Debug --nologo @platform
}
if ($LASTEXITCODE -ne 0) { throw "Build failed." }

# The thumbnail handler is a native COM DLL, so it's always published with Native AOT. It goes next to
# Folio.exe, as it loads the pdfium.dll beside it.
$thumbnails = Join-Path $checkout "out\dev\thumbnails-$Configuration"
$rid = if ($platform) { "win-arm64" } else { "win-x64" }
dotnet publish (Join-Path $checkout "src\Folio.Thumbnails\Folio.Thumbnails.csproj") -c $Configuration -r $rid -o $thumbnails --nologo
if ($LASTEXITCODE -ne 0) { throw "Building the thumbnail handler failed." }
$thumbnailDll = Join-Path $output "Folio.Thumbnails.dll"
Copy-Item (Join-Path $thumbnails "Folio.Thumbnails.dll") $thumbnailDll -Force

# ------------------------------------------------------------------ register

foreach ($stale in @(Get-Registrations | Where-Object { -not (Test-Path (Join-Path "$($_.Checkout)" "Folio.sln")) })) {
    Remove-Registration $stale.Id
    Write-Host "Removed the registration of $($stale.Checkout), which no longer exists."
}

$progId = "$classes\FolioDev.$id"
$command = "`"$exe`" `"%1`""
$icon = "`"$exe`",0"
Set-Value $progId "(default)" "PDF Document"
Set-Value $progId "FriendlyTypeName" "PDF Document"
Set-Value "$progId\DefaultIcon" "(default)" $icon
Set-Value "$progId\shell\open" "FriendlyAppName" $displayName
Set-Value "$progId\shell\open\command" "(default)" $command
Set-Value "$progId\Application" "ApplicationName" $displayName
Set-Value "$progId\Application" "ApplicationIcon" $icon
# The badge Explorer draws in the corner of the thumbnails, and the thumbnail handler, which this build's
# DLL now provides to every registered build.
Set-Value $progId "TypeOverlay" "$exe,0"
foreach ($other in @($id) + @(Get-Registrations | ForEach-Object Id) | Select-Object -Unique) {
    Set-Value "$classes\FolioDev.$other\ShellEx\{e357fccd-a995-4576-b01f-234630154e96}" "(default)" $thumbnailClsid
}
Set-Value $thumbnailServer "(default)" "Folio PDF Thumbnails"
Set-Value "$thumbnailServer\InprocServer32" "(default)" $thumbnailDll
Set-Value "$thumbnailServer\InprocServer32" "ThreadingModel" "Both"
# Offered under Open with for .pdf.
if (-not (Test-Path "$classes\.pdf\OpenWithProgids")) { New-Item "$classes\.pdf\OpenWithProgids" -Force | Out-Null }
New-ItemProperty "$classes\.pdf\OpenWithProgids" -Name "FolioDev.$id" -PropertyType None -Value ([byte[]]@()) -Force | Out-Null

$build = "$builds\$id"
Set-Value $build "Name" $displayName
Set-Value $build "Checkout" $checkout
Set-Value $build "Exe" $exe
Set-Value "$build\Capabilities" "ApplicationName" $displayName
Set-Value "$build\Capabilities" "ApplicationDescription" "Development build of Folio from $checkout"
Set-Value "$build\Capabilities" "ApplicationIcon" $icon
Set-Value "$build\Capabilities\FileAssociations" ".pdf" "FolioDev.$id"
Set-Value $registeredApps "FolioDev.$id" "Software\FolioDev\$id\Capabilities"
Send-AssociationsChanged
Write-Host "Registered $displayName -> $exe"

if ($SetDefault) {
    Write-Host "Opening Settings: choose $displayName for .pdf."
    Start-Process "ms-settings:defaultapps?registeredAppUser=FolioDev.$id"
}

if (-not $NoLaunch) {
    $paths = @($Files | ForEach-Object { "`"$((Resolve-Path $_).Path)`"" })
    if ($paths) { Start-Process $exe -ArgumentList $paths } else { Start-Process $exe }
    Write-Host "Started $displayName."
}
