[CmdletBinding()]
param(
    [string]$GameRoot='C:\SPT',
    [string]$OfflineFeed=''
)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$root=$PSScriptRoot
$oldPackages=$env:NUGET_PACKAGES
$oldTiered=$env:DOTNET_TieredCompilation
$result=0
try {
    foreach($entry in Import-Csv -LiteralPath (Join-Path $root 'GAME_REFERENCES_SHA256.csv')) {
        $reference=Join-Path $GameRoot $entry.Path
        if(-not (Test-Path -LiteralPath $reference -PathType Leaf)) { throw ('GAME_REFERENCE_MISSING|'+$entry.Path) }
        if((Get-FileHash -LiteralPath $reference -Algorithm SHA256).Hash -ne $entry.SHA256) {
            throw ('GAME_REFERENCE_CHANGED|'+$entry.Path)
        }
    }
    $env:NUGET_PACKAGES=Join-Path $root '.packages'
    $env:DOTNET_TieredCompilation='0'
    $dotnet=(Get-Command dotnet.exe -ErrorAction Stop).Source
    Push-Location -LiteralPath $root
    try {
        if((& $dotnet --version).Trim() -ne '10.0.403') { throw 'EXPECTED_DOTNET_SDK_10_0_403' }
        $run=Join-Path $root ('artifacts\build-'+[Guid]::NewGuid().ToString('N'))
        New-Item -ItemType Directory -Path $run -Force | Out-Null
        Write-Host ('RELEASE_BUILD_RUN='+$run)

        function Invoke-Checked([string]$name,[string[]]$arguments) {
            $logFile=Join-Path $run ($name+'.log')
            # Windows PowerShell 5.1: native stderr should not be promoted
            # into a terminating PowerShell exception before checking exit code.
            $previous=$ErrorActionPreference
            $ErrorActionPreference='Continue'
            try {
                & $dotnet @arguments *> $logFile
                $code=$LASTEXITCODE
            } finally { $ErrorActionPreference=$previous }
            if($code -ne 0) { throw ('FAILED|'+$name+'|EXIT='+$code+'|LOG='+$logFile) }
            Write-Host ('PASS|'+$name)
        }
        function Has-Utf16String([byte[]]$bytes,[string]$value) {
            $a=[Text.Encoding]::Unicode.GetString($bytes)
            if($a.Contains($value)) { return $true }
            if($bytes.Length -gt 1) {
                $b=[Text.Encoding]::Unicode.GetString($bytes,1,$bytes.Length-1)
                if($b.Contains($value)) { return $true }
            }
            return $false
        }

        # Release defaults and host-only write boundary. Do not mutate existing user config.
        $pluginSource=[IO.File]::ReadAllText((Join-Path $root 'src\Client\Plugin.cs'))
        if(-not $pluginSource.Contains('"02 Edit shared HP on this computer (SPT server host)", true,')) {
            throw 'RELEASE_HOST_HP_DEFAULT_NOT_ENABLED'
        }
        if(-not $pluginSource.Contains('"01 DEV VISION - Enable", false,') -and
           -not $pluginSource.Contains('"01 DEV VISION - Enable",false,')) {
            throw 'RELEASE_DEV_VISION_DEFAULT_NOT_DISABLED'
        }
        if(-not $pluginSource.Contains('if (!verifiedLocalFikaBackend) fikaHostHpEditor.Value=false;')) {
            throw 'RELEASE_REMOTE_FIKA_HP_WRITE_GUARD_MISSING'
        }
        Write-Host 'PASS|RELEASE_F12_DEFAULTS_AND_HOST_GUARD'

        $client=Join-Path $root 'src\Client\GigaPMC.Client.csproj'
        $server=Join-Path $root 'src\Server\GigaPMC.Server.csproj'
        $movement=Join-Path $root 'src\MovementTests\MovementTests.csproj'
        $settings=Join-Path $root 'src\SettingsTests\SettingsTests.csproj'
        $audit=Join-Path $root 'tools\GameAbiAudit\GameAbiAudit.csproj'
        $all=@($client,$server,$movement,$settings,$audit)
        $gameArg='-p:SptGameRoot='+$GameRoot
        $serverArg='-p:SptRuntime='+(Join-Path $GameRoot 'SPT_Runtime')
        foreach($project in $all) {
            if(-not (Test-Path -LiteralPath $project -PathType Leaf)) { throw ('PROJECT_MISSING|'+$project) }
            $args=@('restore',$project,'-p:NuGetAudit=false',$gameArg,$serverArg,'--verbosity','minimal')
            if($OfflineFeed) { $args+=@('--source',$OfflineFeed) }
            Invoke-Checked ('restore-'+[IO.Path]::GetFileNameWithoutExtension($project)) $args
        }
        # P08: never trust ZIP timestamps or incremental outputs. A source
        # archive may contain files older than an already-built P06 DLL.
        foreach($project in $all) {
            Invoke-Checked ('clean-'+[IO.Path]::GetFileNameWithoutExtension($project)) @(
                'clean',$project,'-c','Release',$gameArg,$serverArg,'--verbosity','minimal')
        }
        $clientDll=Join-Path $root 'src\Client\bin\Release\netstandard2.1\GigaPMC.Client.dll'
        $serverDll=Join-Path $root 'src\Server\bin\Release\net10.0\GigaPMC.Server.dll'
        if(Test-Path -LiteralPath $clientDll -PathType Leaf) { throw 'STALE_CLIENT_DLL_SURVIVED_DOTNET_CLEAN' }
        if(Test-Path -LiteralPath $serverDll -PathType Leaf) { throw 'STALE_SERVER_DLL_SURVIVED_DOTNET_CLEAN' }
        Write-Host 'PASS|RELEASE_STALE_OUTPUTS_REMOVED'

        foreach($project in $all) {
            $name=[IO.Path]::GetFileNameWithoutExtension($project)
            Invoke-Checked ('rebuild-'+$name) @('build',$project,'-c','Release','--no-restore',
                '--no-incremental',$gameArg,$serverArg,'--verbosity','minimal')
        }
        Invoke-Checked 'movement-tests' @('run','--project',$movement,'-c','Release','--no-restore','--no-build')
        Invoke-Checked 'settings-tests' @('run','--project',$settings,'-c','Release','--no-restore','--no-build')
        Invoke-Checked 'game-abi-audit' @('run','--project',$audit,'-c','Release','--no-restore','--no-build',
            '--',$GameRoot,$clientDll,$run)

        # Fail closed unless the newly COMPILED DLL and newly executed test
        # logs prove P07's movement guard plus P08's unambiguous build stamp.
        if(-not (Test-Path -LiteralPath $clientDll -PathType Leaf)) { throw 'CLIENT_DLL_MISSING' }
        if(-not (Test-Path -LiteralPath $serverDll -PathType Leaf)) { throw 'SERVER_DLL_MISSING' }
        $clientBytes=[IO.File]::ReadAllBytes($clientDll)
        foreach($tag in @('2.0.1-REL','MOVEMENT_DIRECT_BIND_VERIFIED',
            'DIRECT_APPLY_IMPLEMENTATION_DIFFERENT',
            'P09_FIKA_HP_F12_HOST_EDITOR')) {
            if(-not (Has-Utf16String $clientBytes $tag)) { throw ('RELEASE_COMPILED_DLL_MISSING|'+$tag) }
        }
        if(Has-Utf16String $clientBytes 'DIRECT_APPLY_OVERRIDE_UNAUDITED') {
            throw 'P08_STALE_P06_MOVEMENT_GUARD_STILL_IN_DLL'
        }
        $clientHash=(Get-FileHash -LiteralPath $clientDll -Algorithm SHA256).Hash
        if($clientHash -eq 'EF1399F05A67EA0A284D7B59CC759EE1A775E604976201A837DBA321A81E2E64') {
            throw 'P08_OLD_CLIENT_HASH_REPEATED'
        }
        $movementText=Get-Content -LiteralPath (Join-Path $run 'movement-tests.log') -Raw
        $auditText=Get-Content -LiteralPath (Join-Path $run 'game-abi-audit.log') -Raw
        foreach($tag in @('P07_FIKA_INHERITED_DIRECT_APPLY_ACCEPTED_BY_METADATA_IDENTITY',
            'P07_GENUINE_DIRECT_APPLY_OVERRIDE_REMAINS_BLOCKED','MOVEMENT_OFFLINE_PASS|CHECKS=69')) {
            if(-not $movementText.Contains($tag)) { throw ('P08_MOVEMENT_TEST_MISSING|'+$tag) }
        }
        foreach($tag in @('P07_RUNTIME_DIRECT_APPLY_METADATA_IDENTITY_GUARD',
            'P07_REAL_OVERRIDE_FAIL_CLOSED_WITH_DIAGNOSTIC','GAME_ABI_AUDIT_PASS|CHECKS=51')) {
            if(-not $auditText.Contains($tag)) { throw ('P08_ABI_TEST_MISSING|'+$tag) }
        }
        Write-Host ('PASS|RELEASE_FRESH_CLIENT_PROVEN|SHA256='+$clientHash)
        $settingsText=Get-Content -LiteralPath (Join-Path $run 'settings-tests.log') -Raw
        foreach($tag in @('P09_FIKA_HP_HOST_JSON_TESTS_PASS','SETTINGS_TESTS_PASS|CHECKS=89')) {
            if(-not $settingsText.Contains($tag)) { throw ('P09_SETTINGS_TEST_MISSING|'+$tag) }
        }
        Write-Host 'PASS|RELEASE_TEST_GATES|MOVEMENT_69|SETTINGS_89|ABI_51'

        $package=Join-Path $run 'package'
        $plugins=Join-Path $package 'BepInEx\plugins'
        $backend=Join-Path $package 'SPT_Runtime\user\mods\GigaPMC'
        New-Item -ItemType Directory -Path $plugins,(Join-Path $backend 'config') -Force | Out-Null
        Copy-Item -LiteralPath $clientDll -Destination $plugins
        Copy-Item -LiteralPath $serverDll -Destination $backend
        Copy-Item -LiteralPath (Join-Path $root 'assets\GigaPMC_icon_512.png') -Destination (Join-Path $backend 'icon.png')
        Copy-Item -LiteralPath (Join-Path $root 'config\fika-server-settings.example.json') -Destination (Join-Path $backend 'config')
        foreach($name in @('README.md','INSTALL_README.txt','LICENSE','CHANGELOG.md')) {
            Copy-Item -LiteralPath (Join-Path $root $name) -Destination $package
        }
        $serverHash=(Get-FileHash -LiteralPath $serverDll -Algorithm SHA256).Hash
        [IO.File]::WriteAllText((Join-Path $package 'BUILD_INFO.txt'),
            ('GigaPMC 2.0.1 full release'+"`r`n"+
             'Client SHA256: '+$clientHash+"`r`n"+
             'Server SHA256: '+$serverHash+"`r`n"+
             'Movement offline: 69 PASS'+"`r`n"+
             'Settings offline: 89 PASS'+"`r`n"+
             'Game ABI: 51 PASS'+"`r`n"+
             'Fika shared HP editor: included'+"`r`n"),
            (New-Object System.Text.UTF8Encoding($false)))
        $manifest=@(Get-ChildItem -LiteralPath $package -File -Recurse | ForEach-Object {
            [pscustomobject]@{
                Path=$_.FullName.Substring($package.Length+1).Replace('\','/')
                SHA256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash
            }
        })
        $manifestPath=Join-Path $package 'MANIFEST_SHA256.csv'
        $manifest | Export-Csv -LiteralPath $manifestPath -NoTypeInformation -Encoding UTF8
        Add-Type -AssemblyName System.IO.Compression,System.IO.Compression.FileSystem
        # Explicit ZIP forward slashes are required by SS-PTMM on Windows.
        $zip=Join-Path $run 'GigaPMC-v2.0.1-FULL.zip'
        $writer=[IO.Compression.ZipFile]::Open($zip,[IO.Compression.ZipArchiveMode]::Create)
        try {
            foreach($file in @(Get-ChildItem -LiteralPath $package -File -Recurse | Sort-Object FullName)) {
                $relative=$file.FullName.Substring($package.Length+1).Replace('\','/')
                [void][IO.Compression.ZipFileExtensions]::CreateEntryFromFile(
                    $writer,$file.FullName,$relative,[IO.Compression.CompressionLevel]::Optimal)
            }
        } finally { $writer.Dispose() }

        # Read the archive back and compare every entry against the prepared
        # package. A PASS must not mean only that ZipFile.Create succeeded.
        $z=[IO.Compression.ZipFile]::OpenRead($zip)
        try {
            $expected=@(Get-ChildItem -LiteralPath $package -File -Recurse)
            # PS 5.1 / .NET Framework can store directory separators as \ in
            # ZipArchiveEntry.FullName on Windows. Normalize both forms before
            # readback; never weaken exact-set or per-entry SHA256 checks.
            $archiveFiles=@{}
            foreach($entry in $z.Entries) {
                $originalPath=[string]$entry.FullName
                if($originalPath.EndsWith('/') -or $originalPath.EndsWith('\')) { continue }
                if($originalPath.Contains('\')) { throw ('P09_ZIP_BACKSLASH_ENTRY|'+$originalPath) }
                $archivePath=$originalPath
                if([string]::IsNullOrWhiteSpace($archivePath) -or
                   $archivePath.StartsWith('/') -or
                   $archivePath.Contains(':') -or
                   $archivePath.Contains('//') -or
                   $archivePath -match '(^|/)\.\.(/|$)' -or
                   $archivePath -match '(^|/)\.(/|$)') {
                    throw ('P08_RELEASE_UNSAFE_ENTRY|'+$originalPath)
                }
                if($archiveFiles.ContainsKey($archivePath)) {
                    throw ('P08_RELEASE_DUPLICATE_ENTRY|'+$archivePath)
                }
                $archiveFiles[$archivePath]=$entry
            }
            if($archiveFiles.Count -ne $expected.Count) { throw 'P08_RELEASE_FILE_COUNT_MISMATCH' }
            foreach($file in $expected) {
                $relative=$file.FullName.Substring($package.Length+1).Replace('\','/')
                if(-not $archiveFiles.ContainsKey($relative)) {
                    throw ('P08_RELEASE_ENTRY_MISSING|'+$relative)
                }
                $e=$archiveFiles[$relative]
                $s=$e.Open()
                $sha=[Security.Cryptography.SHA256]::Create()
                try { $hash=[BitConverter]::ToString($sha.ComputeHash($s)).Replace('-','') }
                finally { $s.Dispose(); $sha.Dispose() }
                if($hash -ne (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash) {
                    throw ('P08_RELEASE_READBACK_MISMATCH|'+$relative)
                }
            }
            Write-Host 'PASS|P09_ARCHIVE_PATH_NORMALIZATION'
        } finally { $z.Dispose() }
        Write-Host ('PASS|P09_RELEASE_READBACK|FILES='+$expected.Count)

        $downloads=Join-Path $env:USERPROFILE 'Downloads'
        if(-not (Test-Path -LiteralPath $downloads -PathType Container)) {
            throw ('DOWNLOADS_NOT_FOUND|'+$downloads)
        }
        $release=Join-Path $downloads ('GigaPMC-v2.0.1-FULL-'+
            [IO.Path]::GetFileName($run).Substring(6,8)+'.zip')
        if(Test-Path -LiteralPath $release) { throw ('DOWNLOADS_RELEASE_ALREADY_EXISTS|'+$release) }
        Copy-Item -LiteralPath $zip -Destination $release
        $zipHash=(Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash
        if((Get-FileHash -LiteralPath $release -Algorithm SHA256).Hash -ne $zipHash) {
            throw 'DOWNLOADS_RELEASE_COPY_HASH_MISMATCH'
        }
        Get-FileHash -LiteralPath $clientDll,$serverDll,$zip,$release -Algorithm SHA256 |
            Export-Csv -LiteralPath (Join-Path $run 'OUTPUT_SHA256.csv') -NoTypeInformation
        Write-Host 'BUILD_TEST_AND_PACKAGE_PASS|GIGAPMC_2_0_1_FULL'
        Write-Host ('READY_ZIP='+$release)
        Write-Host ('READY_SHA256='+$zipHash)
        Write-Host ('BUILD_RUN='+$run)
        Write-Host 'GAME_INSTALLATION_NOT_PERFORMED'
    } finally { Pop-Location }
} catch {
    Write-Host ('BUILD_FAILED|'+$_.Exception.Message) -ForegroundColor Red
    $result=1
} finally {
    $env:NUGET_PACKAGES=$oldPackages
    $env:DOTNET_TieredCompilation=$oldTiered
}
exit $result
