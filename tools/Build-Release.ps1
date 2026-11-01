# Portable output layout for the application and bundled Bluetooth receiver.
[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$OutputDirectory)
$ErrorActionPreference='Stop'
$project=Split-Path -Parent $PSScriptRoot
$destination=[IO.Path]::GetFullPath($OutputDirectory)
if(Test-Path -LiteralPath $destination){throw "输出目录已存在，拒绝覆盖：$destination"}
New-Item -ItemType Directory -Path $destination | Out-Null
$app=Join-Path $destination 'app'
$bluetooth=Join-Path $app 'bluetooth'
& dotnet publish (Join-Path $project 'app\NetCenter.csproj') -c Release -r win-x64 --self-contained true -p:DebugType=None -p:DebugSymbols=false -o $app
if($LASTEXITCODE -ne 0){throw 'NetCenter 发布失败。'}
& dotnet publish (Join-Path $project 'vendor\BluetoothAudioReceiver\BluetoothAudioReceiver.csproj') -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:DebugType=None -p:DebugSymbols=false -o $bluetooth
if($LASTEXITCODE -ne 0){throw '蓝牙音频组件发布失败。'}
foreach($name in @('support\campus-auth','support\hotspot','support\tools','assets','tools','third-party-licenses','vendor\BluetoothAudioReceiver')){
 New-Item -ItemType Directory -Path (Join-Path $destination $name) -Force | Out-Null
}
$files=@(
 'support\campus-auth\campus_auth.py','support\campus-auth\config.example.json',
 'support\hotspot\Keep-HotspotOn.ps1','support\hotspot\Set-Hotspot.ps1','support\hotspot\config.example.json',
 'support\tools\clear-stale-cache.ps1','tools\Prepare-Dependencies.ps1',
 'third-party-licenses\CommunityToolkit.Mvvm-LICENSE.md','third-party-licenses\CommunityToolkit.Mvvm-ThirdPartyNotices.txt',
 'third-party-licenses\NAudio-LICENSE.txt','vendor\BluetoothAudioReceiver\LICENSE'
)
foreach($name in $files){Copy-Item -LiteralPath (Join-Path $project $name) -Destination (Join-Path $destination $name)}
Get-ChildItem -LiteralPath (Join-Path $project 'assets') -Filter 'icon-tray-*.ico' -File | Copy-Item -Destination (Join-Path $destination 'assets')
New-Item -ItemType Directory -Path (Join-Path $destination 'docs\images') -Force | Out-Null
Get-ChildItem -LiteralPath (Join-Path $project 'docs\images') -Filter '*.png' -File | Copy-Item -Destination (Join-Path $destination 'docs\images')
foreach($name in @('README.md','CONTRIBUTING.md','SECURITY.md','THIRD_PARTY_NOTICES.md','requirements.txt')){Copy-Item -LiteralPath (Join-Path $project $name) -Destination $destination}
Copy-Item -LiteralPath (Join-Path $project 'vendor\BluetoothAudioReceiver\LICENSE') -Destination (Join-Path $bluetooth 'LICENSE-BluetoothAudioReceiver.txt')
Write-Output "发行文件已生成：$destination"
