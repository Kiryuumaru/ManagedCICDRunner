$repo = '{{$repo}}'
$appname = '{{$appname}}'
$appexec = '{{$appexec}}'
$rootextract = '{{$rootextract}}'

$root = (Get-Item .).FullName
$tempPath = "$root\temp"

$ErrorActionPreference = 'SilentlyContinue'
New-Item -ItemType Directory -Path "$tempPath" -ErrorAction SilentlyContinue | Out-Null
$ErrorActionPreference = 'Stop'

$appZipName = "$appname.zip"
$appPath = "$tempPath\$appname"
$appZipPath = "$tempPath\$appZipName"
$appExecPath = "$appPath\$appexec"

if (Test-Path $appZipPath) {
    Remove-Item -Path $appZipPath -Force
}

$appUri = "https://github.com/$repo/releases/latest/download/$appZipName"
Invoke-WebRequest -Uri $appUri -OutFile $appZipPath

Expand-Archive -LiteralPath $appZipPath -DestinationPath $tempPath -Force

Copy-Item -Path "$tempPath\$rootextract\*" -Destination "$root" -Recurse -Force

& $appExecPath --install-service
