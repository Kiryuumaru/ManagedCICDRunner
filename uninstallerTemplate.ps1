$appname = '{{$appname}}'
$appexec = '{{$appexec}}'

$root = (Get-Item .).FullName
$tempPath = "$root\temp"

$appPath = "$tempPath\$appname"
$appExecPath = "$appPath\$appexec"

& $appExecPath --uninstall-service
