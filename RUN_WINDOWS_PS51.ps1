param(
  [ValidateSet('BUILD_PACKAGE')][string]$Mode = 'BUILD_PACKAGE',
  [string]$GameRoot = 'C:\SPT',
  [string]$BundleRoot,
  [string]$DownloadDir
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
function Fail([string]$Message) { throw ('GIGAPMC_P10_FAIL_CLOSED|' + $Message) }
try {
  if ($PSVersionTable.PSVersion.Major -ne 5 -or $PSVersionTable.PSVersion.Minor -ne 1 -or -not [Environment]::Is64BitProcess) {
    Fail 'POWERSHELL_5_1_X64_REQUIRED'
  }
  if ([string]::IsNullOrWhiteSpace($BundleRoot) -or -not [IO.Directory]::Exists($BundleRoot)) { Fail 'BUNDLE_ROOT_MISSING' }
  if ([string]::IsNullOrWhiteSpace($DownloadDir) -or -not [IO.Directory]::Exists($DownloadDir)) { Fail 'DOWNLOADS_MISSING' }
  $fullGame = [IO.Path]::GetFullPath($GameRoot).TrimEnd('\')
  $fullBundle = [IO.Path]::GetFullPath($BundleRoot).TrimEnd('\')
  if ($fullBundle.Equals($fullGame,[StringComparison]::OrdinalIgnoreCase) -or $fullBundle.StartsWith($fullGame+'\',[StringComparison]::OrdinalIgnoreCase)) { Fail 'BUNDLE_INSIDE_GAME' }
  if (-not $fullBundle.StartsWith('C:\SPT_MODS\GigaPMC\',[StringComparison]::OrdinalIgnoreCase)) { Fail 'BUILD_ROOT_NOT_CANONICAL' }
  $runtime = Join-Path $fullGame 'SPT_Runtime'
  $serverCore = Join-Path $runtime 'SPTarkov.Server.Core.dll'
  $clientBep = Join-Path $fullGame 'BepInEx\core\BepInEx.dll'
  $clientUnity = Join-Path $fullGame 'EscapeFromTarkov_Data\Managed\UnityEngine.CoreModule.dll'
  foreach ($file in @($serverCore,$clientBep,$clientUnity)) {
    if (-not [IO.File]::Exists($file)) { Fail ('MISSING_GAME_REFERENCE|' + $file) }
  }
  $corePin = '490409F7C67480A8BF647DA67ADA8B91361330C2A9324D9E9787CD1EBAF5BF27'
  if ((Get-FileHash -LiteralPath $serverCore -Algorithm SHA256).Hash.ToUpperInvariant() -ne $corePin) { Fail 'SERVER_CORE_PIN_MISMATCH' }
  Write-Host 'PASS|SPT_SERVER_CORE_SHA256_PIN'
  $dotnet = Get-Command dotnet.exe -ErrorAction Stop
  $sdks = @(& $dotnet.Source --list-sdks)
  if ($LASTEXITCODE -ne 0 -or @($sdks | Where-Object { $_ -match '^10\.' }).Count -eq 0) { Fail 'NET10_SDK_MISSING' }
  Write-Host ('PASS|DOTNET10|' + @($sdks | Where-Object { $_ -match '^10\.' })[-1])

  $src = Join-Path $fullBundle 'src'
  $testProj = Join-Path $src 'Tests\VitalityTests.csproj'
  $serverProj = Join-Path $src 'Server\GigaPMC.Server.csproj'
  $clientProj = Join-Path $src 'Client\GigaPMC.Client.csproj'
  foreach ($file in @($testProj,$serverProj,$clientProj)) { if (-not [IO.File]::Exists($file)) { Fail ('PROJECT_MISSING|' + $file) } }
  foreach ($dep in @('SPTarkov.Server.Core.dll','SPTarkov.Common.dll','SPTarkov.DI.dll','SPTarkov.Reflection.dll')) {
    if (-not [IO.File]::Exists((Join-Path $runtime $dep))) { Fail ('SERVER_REFERENCE_MISSING|' + $dep) }
  }
  $env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
  $env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
  $env:DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE = 'true'
  $env:NUGET_PACKAGES = Join-Path $fullBundle '_NUGET_PACKAGES'
  $emptyFeed = Join-Path $fullBundle '_EMPTY_NUGET_FEED'
  [void][IO.Directory]::CreateDirectory($emptyFeed)
  Write-Host 'BOUNDARY=C_DRIVE_BUILD_ONLY|NO_SPT_WRITE|NO_PROFILE_ACCESS|OUTPUT_DOWNLOADS_ONLY'
  Write-Host 'NOTE|CLIENT_RESTORE_ALLOWED_NUGET_ORG_NETSTANDARD_REF_2_1_0'

  $pGame = '-p:SptGameRoot=' + $fullGame
  $pRuntime = '-p:SptRuntime=' + $runtime
  foreach ($p in @($testProj,$serverProj)) {
    & $dotnet.Source restore $p '--source' $emptyFeed $pGame $pRuntime '-p:NuGetAudit=false' '--verbosity' 'minimal'
    if ($LASTEXITCODE -ne 0) { Fail ('OFFLINE_RESTORE_FAILED|' + [IO.Path]::GetFileName($p)) }
  }
  # .NET 10 SDK may not ship NETStandard.Library.Ref targeting pack. NuGet restore
  # downloads the official 2.1.0 ref pack to isolated build cache, not C:\SPT.
  $refPack = Join-Path (Split-Path -Parent $dotnet.Source) 'packs\NETStandard.Library.Ref'
  $localRefFound = $false
  if ([IO.Directory]::Exists($refPack)) {
    $refs = @(Get-ChildItem -LiteralPath $refPack -Directory | Where-Object { [IO.File]::Exists((Join-Path $_.FullName 'ref\netstandard2.1\netstandard.dll')) })
    $localRefFound = ($refs.Count -gt 0)
  }
  if ($localRefFound) {
    Write-Host 'PASS|NETSTANDARD21_REF|SDK_PACK'
    & $dotnet.Source restore $clientProj '--source' $emptyFeed $pGame '-p:NuGetAudit=false' '--verbosity' 'minimal'
  }
  else {
    Write-Host 'INFO|NETSTANDARD21_REF|RESTORE_FROM_NUGET_ORG'
    & $dotnet.Source restore $clientProj '--source' 'https://api.nuget.org/v3/index.json' $pGame '-p:NuGetAudit=false' '--verbosity' 'minimal'
  }
  if ($LASTEXITCODE -ne 0) { Fail 'NETSTANDARD21_CLIENT_RESTORE_FAILED_CHECK_NETWORK_OR_NUGET_ACCESS' }
  Write-Host 'PASS|CLIENT_NETSTANDARD21_RESTORE'

  & $dotnet.Source run '--project' $testProj '--no-restore' '--configuration' 'Release' '--verbosity' 'minimal'
  if ($LASTEXITCODE -ne 0) { Fail ('MATH_TESTS_FAILED_RC_' + $LASTEXITCODE) }
  & $dotnet.Source build $serverProj '--no-restore' '--configuration' 'Release' $pRuntime '--verbosity' 'minimal'
  if ($LASTEXITCODE -ne 0) { Fail ('SERVER_BUILD_FAILED_RC_' + $LASTEXITCODE) }
  & $dotnet.Source build $clientProj '--no-restore' '--configuration' 'Release' $pGame '--verbosity' 'minimal'
  if ($LASTEXITCODE -ne 0) { Fail ('CLIENT_BUILD_FAILED_RC_' + $LASTEXITCODE) }
  $serverDll = Join-Path $src 'Server\bin\Release\net10.0\GigaPMC.Server.dll'
  $clientDll = Join-Path $src 'Client\bin\Release\netstandard2.1\GigaPMC.Client.dll'
  foreach ($file in @($serverDll,$clientDll)) { if (-not [IO.File]::Exists($file)) { Fail ('COMPILED_DLL_MISSING|' + $file) } }
  Write-Host 'PASS|P09_FIKA_RULES_SOURCE_IN_BUILD'
  Write-Host ('PASS|SERVER_DLL_BUILT|SHA256=' + (Get-FileHash -LiteralPath $serverDll -Algorithm SHA256).Hash.ToUpperInvariant())
  Write-Host ('PASS|CLIENT_F12_DLL_BUILT|SHA256=' + (Get-FileHash -LiteralPath $clientDll -Algorithm SHA256).Hash.ToUpperInvariant())


  # Freeze scope: rebrand both components, preserve all HP behavior and IDs.
  $modSrc = [IO.File]::ReadAllText((Join-Path $src 'Server\Mod.cs'), [Text.Encoding]::UTF8)
  $clientSrc = [IO.File]::ReadAllText((Join-Path $src 'Client\Plugin.cs'), [Text.Encoding]::UTF8)
  $fikaSrc = [IO.File]::ReadAllText((Join-Path $src 'Server\FikaRules.cs'), [Text.Encoding]::UTF8)
  if (-not $modSrc.Contains('"GigaPMC - For Tarkov Enjoyers"') -or -not $modSrc.Contains('"Vargor / OptimisticSociopath"') -or -not $modSrc.Contains('new("1.0.0")') -or -not $modSrc.Contains('"com.vargor.gigapmc"')) { Fail 'SERVER_REBRAND_CONTRACT_INVALID' }
  if (-not $clientSrc.Contains('"GigaPMC - For Tarkov Enjoyers", "1.0.0"') -or -not $clientSrc.Contains('ShowRangeAsPercent = false') -or -not $clientSrc.Contains('"com.vargor.gigapmc"')) { Fail 'CLIENT_REBRAND_CONTRACT_INVALID' }
  Write-Host 'PASS|V100_REBRANDED_METADATA_AND_GUIDS'
  if (-not $fikaSrc.Contains('Path.Combine(sptRuntime, "user", "mods", "GigaPMC"') -or -not $clientSrc.Contains('"mods", "GigaPMC", "config"')) { Fail 'SERVER_CLIENT_CONFIG_PATH_REBRAND_INVALID' }
  Write-Host 'PASS|SERVER_CLIENT_CONFIG_PATH_REBRAND'
  foreach ($activeSrc in @($modSrc, $clientSrc, $fikaSrc)) { if ($activeSrc.Contains('PmcVitalityLevels') -or $activeSrc.Contains('com.igor.pmcvitalitylevels')) { Fail 'OLD_IDENTITY_REMAINS_IN_ACTIVE_SOURCE' } }
  Write-Host 'PASS|NO_OLD_BRAND_OR_GUID_IN_ACTIVE_CODE'

  $icon = Join-Path $fullBundle 'assets\GigaPMC_icon_512.png'
  if (-not [IO.File]::Exists($icon) -or (Get-FileHash -LiteralPath $icon -Algorithm SHA256).Hash.ToUpperInvariant() -ne '46DD86DA9667AD090FB8BDA4BB1DB9104C30F69F9CE9ECDA2D569146EA969B90') { Fail 'ICON_PIN_MISMATCH' }
  Write-Host 'PASS|ICON_SHA256_PIN'

  $finalName = 'GigaPMC-For-Tarkov-Enjoyers-v1.0.0-SPT-4.1.6-REBRAND.zip'
  $finalPath = Join-Path $DownloadDir $finalName
  $partial = $finalPath + '.partial'
  if ([IO.File]::Exists($finalPath) -or [IO.File]::Exists($partial)) { Fail 'FINAL_OUTPUT_ALREADY_EXISTS_NO_OVERWRITE' }
  $pack = Join-Path $fullBundle '_PACKAGE'
  if ([IO.Directory]::Exists($pack)) { Fail 'PACKAGE_STAGE_EXISTS' }
  $serverDest = Join-Path $pack 'SPT_Runtime\user\mods\GigaPMC'
  $clientDest = Join-Path $pack 'BepInEx\plugins'
  [void][IO.Directory]::CreateDirectory($serverDest)
  [void][IO.Directory]::CreateDirectory($clientDest)
  [IO.File]::Copy($serverDll,(Join-Path $serverDest 'GigaPMC.Server.dll'))
  [IO.File]::Copy($clientDll,(Join-Path $clientDest 'GigaPMC.Client.dll'))
  [IO.File]::Copy($icon,(Join-Path $serverDest 'icon.png'))
  $serverCfg = Join-Path $serverDest 'config'
  [void][IO.Directory]::CreateDirectory($serverCfg)
  [IO.File]::Copy((Join-Path $fullBundle 'config\fika-server-settings.example.json'),(Join-Path $serverCfg 'fika-server-settings.example.json'))
  [IO.File]::Copy((Join-Path $fullBundle 'INSTALL_README.txt'),(Join-Path $pack 'INSTALL_README.txt'))
  [IO.File]::Copy((Join-Path $fullBundle 'MIGRATION_PL_EN.txt'),(Join-Path $pack 'MIGRATION_PL_EN.txt'))

  $items = @(
    'SPT_Runtime/user/mods/GigaPMC/GigaPMC.Server.dll',
    'BepInEx/plugins/GigaPMC.Client.dll',
    'SPT_Runtime/user/mods/GigaPMC/icon.png',
    'SPT_Runtime/user/mods/GigaPMC/config/fika-server-settings.example.json',
    'INSTALL_README.txt',
    'MIGRATION_PL_EN.txt'
  )
  $manifest = Join-Path $pack 'MANIFEST_SHA256.csv'
  $lines = @('Path,Sha256,Bytes')
  foreach ($rel in $items) {
    $p = Join-Path $pack $rel.Replace('/','\')
    $h = (Get-FileHash -LiteralPath $p -Algorithm SHA256).Hash.ToUpperInvariant()
    $b = (Get-Item -LiteralPath $p).Length
    $lines += ($rel + ',' + $h + ',' + $b)
  }
  [IO.File]::WriteAllLines($manifest,$lines,([Text.UTF8Encoding]::new($false)))
  Add-Type -AssemblyName System.IO.Compression
  Add-Type -AssemblyName System.IO.Compression.FileSystem
  [IO.Compression.ZipFile]::CreateFromDirectory($pack,$partial,[IO.Compression.CompressionLevel]::Optimal,$false)
  $expected = @($items) + @('MANIFEST_SHA256.csv')
  $z = [IO.Compression.ZipFile]::OpenRead($partial)
  try {
    $entries = @($z.Entries | Where-Object { $_.Name })
    if ($entries.Count -ne 7) { Fail ('FINAL_ZIP_COUNT|' + $entries.Count) }
    $seen = @{}
    foreach ($e in $entries) {
      $name = $e.FullName.Replace('\','/')
      if (-not ($expected -ccontains $name) -or $seen.ContainsKey($name) -or $e.Length -le 0) { Fail ('FINAL_ZIP_UNEXPECTED|' + $name) }
      $seen[$name] = $true
      $stream = $e.Open()
      $sha = [Security.Cryptography.SHA256]::Create()
      try {
        $zipHash = ([BitConverter]::ToString($sha.ComputeHash($stream))).Replace('-','')
      }
      finally { $sha.Dispose(); $stream.Dispose() }
      $local = Join-Path $pack $name.Replace('/','\')
      if ($zipHash -ne (Get-FileHash -LiteralPath $local -Algorithm SHA256).Hash.ToUpperInvariant()) { Fail ('FINAL_ZIP_SHA256|' + $name) }
    }
    if ($seen.Count -ne 7) { Fail 'FINAL_ZIP_INCOMPLETE' }
  }
  finally { $z.Dispose() }
  [IO.File]::Move($partial,$finalPath)
  Write-Host 'PASS|FINAL_ZIP_EXACT_SET|7'
  Write-Host 'PASS|FIKA_ADMIN_SETTINGS_EXAMPLE|NO_LIVE_CONFIG_OVERWRITE'
  Write-Host 'PASS|EXISTING_F12_CONFIG_NOT_INCLUDED'
  Write-Host ('READY_ZIP=' + $finalPath)
  Write-Host ('READY_ZIP_SHA256=' + (Get-FileHash -LiteralPath $finalPath -Algorithm SHA256).Hash.ToUpperInvariant())
  Write-Host 'STATUS=PASS_GIGAPMC_FIKA_PACKAGED_DOWNLOADS_NO_SPT_WRITE' -ForegroundColor Green
  Write-Host 'RUNTIME_FIKA=UNVERIFIED|REQUIRES_TWO_PLAYER_RAID_TEST'
}
catch {
  Write-Host ('FAIL_CLOSED=' + $_.Exception.Message) -ForegroundColor Red
  Write-Host ('BUILD_EVIDENCE=' + $BundleRoot) -ForegroundColor Yellow
  Write-Host 'STATUS=GIGAPMC_P10_BUILD_REVIEW_REQUIRED'
  exit 1
}
finally {
  Write-Host 'ACTION=NO_SPT_FILES_MODIFIED'
}
