$appname = '{{$appname}}'
$appexec = '{{$appexec}}'

$root = (Get-Item .).FullName

$appExecPath = "$root\$appexec"

& $appExecPath --uninstall-service
