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
        if((Get-FileHash -LiteralPath (Join-Path $GameRoot $entry.Path)).Hash -ne $entry.SHA256){throw ('GAME_REFERENCE_CHANGED|'+$entry.Path)}
    }
    $env:NUGET_PACKAGES=Join-Path $root '.packages'
    $env:DOTNET_TieredCompilation='0'
    $dotnet=(Get-Command dotnet.exe -ErrorAction Stop).Source
    Push-Location -LiteralPath $root
    try {
        if((& $dotnet --version).Trim() -ne '10.0.403'){throw 'EXPECTED_DOTNET_SDK_10_0_403'}
        $run=Join-Path $root ('artifacts\build-'+[Guid]::NewGuid().ToString('N'))
        New-Item -ItemType Directory -Path $run -Force | Out-Null
        function Invoke-Checked([string]$name,[string[]]$arguments) {
            & $dotnet @arguments *> (Join-Path $run ($name+'.log'))
            if($LASTEXITCODE -ne 0){throw ('FAILED|'+$name+'|LOGS='+$run)}
            Write-Host ('PASS|'+$name)
        }
        $client=Join-Path $root 'src\Client\GigaPMC.Client.csproj'
        $server=Join-Path $root 'src\Server\GigaPMC.Server.csproj'
        $movement=Join-Path $root 'src\MovementTests\MovementTests.csproj'
        $settings=Join-Path $root 'src\SettingsTests\SettingsTests.csproj'
        $audit=Join-Path $root 'tools\GameAbiAudit\GameAbiAudit.csproj'
        $gameArg='-p:SptGameRoot='+$GameRoot
        $serverArg='-p:SptRuntime='+(Join-Path $GameRoot 'SPT_Runtime')
        foreach($project in @($client,$server,$movement,$settings,$audit)) {
            $arguments=@('restore',$project,'-p:NuGetAudit=false',$gameArg,$serverArg,'--verbosity','minimal')
            if($OfflineFeed){$arguments+=@('--source',$OfflineFeed)}
            Invoke-Checked ('restore-'+[IO.Path]::GetFileNameWithoutExtension($project)) $arguments
        }
        Invoke-Checked 'client-build' @('build',$client,'-c','Release','--no-restore',$gameArg,'--verbosity','minimal')
        Invoke-Checked 'server-build' @('build',$server,'-c','Release','--no-restore',$serverArg,'--verbosity','minimal')
        Invoke-Checked 'movement-tests' @('run','--project',$movement,'-c','Release','--no-restore')
        Invoke-Checked 'settings-tests' @('run','--project',$settings,'-c','Release','--no-restore')
        $clientDll=Join-Path $root 'src\Client\bin\Release\netstandard2.1\GigaPMC.Client.dll'
        $serverDll=Join-Path $root 'src\Server\bin\Release\net10.0\GigaPMC.Server.dll'
        Invoke-Checked 'game-abi-audit' @('run','--project',$audit,'-c','Release','--no-restore',$gameArg,'--',$GameRoot,$clientDll,$run)
        $package=Join-Path $run 'package'
        $plugins=Join-Path $package 'BepInEx\plugins'
        $backend=Join-Path $package 'SPT_Runtime\user\mods\GigaPMC'
        New-Item -ItemType Directory -Path $plugins,(Join-Path $backend 'config') -Force | Out-Null
        Copy-Item -LiteralPath $clientDll -Destination $plugins
        Copy-Item -LiteralPath $serverDll -Destination $backend
        Copy-Item -LiteralPath (Join-Path $root 'assets\GigaPMC_icon_512.png') -Destination (Join-Path $backend 'icon.png')
        Copy-Item -LiteralPath (Join-Path $root 'config\fika-server-settings.example.json') -Destination (Join-Path $backend 'config')
        foreach($name in @('README.md','INSTALL_README.txt','LICENSE','CHANGELOG.md')){Copy-Item -LiteralPath (Join-Path $root $name) -Destination $package}
        $manifest=@(Get-ChildItem -LiteralPath $package -File -Recurse | ForEach-Object {
            [pscustomobject]@{Path=$_.FullName.Substring($package.Length+1).Replace('\','/');SHA256=(Get-FileHash -LiteralPath $_.FullName).Hash}
        })
        $manifest | Export-Csv -LiteralPath (Join-Path $package 'MANIFEST_SHA256.csv') -NoTypeInformation
        Add-Type -AssemblyName System.IO.Compression,System.IO.Compression.FileSystem
        $zip=Join-Path $run 'GigaPMC-v2.0.0-FULL-SSPTMM.zip'
        $archive=[IO.Compression.ZipFile]::Open($zip,[IO.Compression.ZipArchiveMode]::Create)
        try {
            foreach($file in Get-ChildItem -LiteralPath $package -Recurse -File) {
                $relative=$file.FullName.Substring($package.Length+1).Replace('\','/')
                [void][IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive,$file.FullName,$relative,[IO.Compression.CompressionLevel]::Optimal)
            }
        } finally {$archive.Dispose()}
        Get-FileHash -LiteralPath $clientDll,$serverDll,$zip | Export-Csv -LiteralPath (Join-Path $run 'OUTPUT_SHA256.csv') -NoTypeInformation
        Write-Host ('BUILD_TEST_AND_PACKAGE_PASS|'+$run)
        Write-Host 'LIVE_GAMEPLAY_NOT_EXECUTED|NO_GAME_INSTALLATION_PERFORMED'
    } finally {Pop-Location}
} catch {
    Write-Error ('BUILD_FAILED|'+$_.Exception.Message) -ErrorAction Continue
    $result=1
} finally {
    $env:NUGET_PACKAGES=$oldPackages
    $env:DOTNET_TieredCompilation=$oldTiered
}
exit $result
