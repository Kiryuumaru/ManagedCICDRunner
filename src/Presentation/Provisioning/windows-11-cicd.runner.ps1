# Github CICD runner provision script fo windows 11

$ProgressPreference = "SilentlyContinue"

# Install git
$GIT_VERSION = "2.45.2"
$GIT_HOME = "C:\\Program Files\\MinGit"
Invoke-WebRequest "https://github.com/git-for-windows/git/releases/download/v${GIT_VERSION}.windows.1/MinGit-${GIT_VERSION}-64-bit.zip" -OutFile "${env:TEMP}\\mingit.zip" -UseBasicParsing;
$GIT_SHA256 = '7ed2a3ce5bbbf8eea976488de5416894ca3e6a0347cee195a7d768ac146d5290';
if ((Get-FileHash "${env:TEMP}\\mingit.zip" -Algorithm sha256).Hash -ne $GIT_SHA256) {
    Write-Host 'GIT_SHA256 CHECKSUM VERIFICATION FAILED!';
    exit 1;
};
Expand-Archive "${env:TEMP}\\mingit.zip" -DestinationPath "$GIT_HOME" -Force;
$env:PATH = $env:PATH + ";$GIT_HOME\\cmd\\;$GIT_HOME\\cmd";
& "$GIT_HOME\\cmd\\git.exe" config --global core.packedGitLimit 512m
& "$GIT_HOME\\cmd\\git.exe" config --global core.packedGitWindowSize 512m
& "$GIT_HOME\\cmd\\git.exe" config --global pack.deltaCacheSize 2047m
& "$GIT_HOME\\cmd\\git.exe" config --global pack.packSizeLimit 2047m
& "$GIT_HOME\\cmd\\git.exe" config --global pack.windowMemory 2047m
Set-ItemProperty -Path 'HKLM:\\SYSTEM\\CurrentControlSet\\Control\\Session Manager\\Environment\\' -Name Path -Value $env:PATH

# Install gh-cli
$GH_VERSION = "2.54.0"
$GH_HOME = "C:\\Program Files\\Gh"
Invoke-WebRequest "https://github.com/cli/cli/releases/download/v${GH_VERSION}/gh_${GH_VERSION}_windows_amd64.zip" -OutFile "${env:TEMP}\\gh-cli.zip" -UseBasicParsing;
$GH_SHA256 = 'd3400b7e5c5a7f3912e8da606a2daf3376981d49f0ba0dea0337725eae25351d';
if ((Get-FileHash "${env:TEMP}\\gh-cli.zip" -Algorithm sha256).Hash -ne $GH_SHA256) {
    Write-Host 'GH_SHA256 CHECKSUM VERIFICATION FAILED!';
    exit 1;
};
Expand-Archive "${env:TEMP}\\gh-cli.zip" -DestinationPath "$GH_HOME" -Force;
$env:PATH = $env:PATH + ";$GH_HOME\\bin\\;$GH_HOME\\bin";
Set-ItemProperty -Path 'HKLM:\\SYSTEM\\CurrentControlSet\\Control\\Session Manager\\Environment\\' -Name Path -Value $env:PATH

# Install docker
$DOCKER_VERSION = "27.1.1"
$DOCKER_HOME = "C:\\Program Files\\Docker"
Enable-WindowsOptionalFeature -Online -FeatureName $("Microsoft-Hyper-V", "Containers", "VirtualMachinePlatform") -All -NoRestart
Invoke-WebRequest "https://download.docker.com/win/static/stable/x86_64/docker-${DOCKER_VERSION}.zip" -OutFile "${env:TEMP}\\docker-static.zip" -UseBasicParsing;
$DOCKER_SHA256 = '978d3ba0f8440173469580f9c17f44fe28fcef3a59ab87c32d42a2d9a2853c26';
if ((Get-FileHash "${env:TEMP}\\docker-static.zip" -Algorithm sha256).Hash -ne $DOCKER_SHA256) {
    Write-Host 'DOCKER_SHA256 CHECKSUM VERIFICATION FAILED!';
    exit 1;
};
Expand-Archive "${env:TEMP}\\docker-static.zip" -DestinationPath "${env:TEMP}\\docker-static" -Force;
xcopy "${env:TEMP}\\docker-static\\docker" "$DOCKER_HOME" /h /i /c /k /e /r /y
$DOCKER_DATA_PATH = "$($env:ProgramData)\\docker"
$DOCKER_CONFIG_PATH = Join-Path $DOCKER_DATA_PATH "config"
[System.IO.Directory]::CreateDirectory($DOCKER_CONFIG_PATH)
$DOCKER_DAEMON_SETTINGS = New-Object PSObject
$DOCKER_DAEMON_SETTINGS | Add-Member NoteProperty hosts @("npipe://")
$DOCKER_DAEMON_SETTINGS | ConvertTo-Json | Out-File -FilePath (Join-Path $DOCKER_CONFIG_PATH "daemon.json") -Encoding ASCII
& "$DOCKER_HOME\\dockerd.exe" --register-service --service-name docker
$env:PATH = $env:PATH + ";$DOCKER_HOME\\;$DOCKER_HOME";
Set-ItemProperty -Path 'HKLM:\\SYSTEM\\CurrentControlSet\\Control\\Session Manager\\Environment\\' -Name Path -Value $env:PATH

# Install az-cli
$AZCLI_VERSION = 2.51.0
Invoke-WebRequest "https://azcliprod.blob.core.windows.net/msi/azure-cli-${AZCLI_VERSION}-x64.msi" -OutFile "${env:TEMP}\\az-cli.msi" -UseBasicParsing;
$AZCLI_SHA256 = 'ded0e2fbbae52016239c899e7334785726be0e48671c018ee918fca8f62c1ea5';
if ((Get-FileHash "${env:TEMP}\\az-cli.msi" -Algorithm sha256).Hash -ne $AZCLI_SHA256) {
    Write-Host 'AZCLI_SHA256 CHECKSUM VERIFICATION FAILED!';
    exit 1;
};
Start-Process msiexec.exe -Wait -ArgumentList '/I', "${env:TEMP}\\az-cli.msi", '/quiet', '/norestart'
$env:PATH = $env:PATH + ";C:\\Program Files\\Microsoft SDKs\\Azure\\CLI2\\wbin\\;C:\\Program Files\\Microsoft SDKs\\Azure\\CLI2\\wbin";
Set-ItemProperty -Path 'HKLM:\\SYSTEM\\CurrentControlSet\\Control\\Session Manager\\Environment\\' -Name Path -Value $env:PATH

# Install Visual Studio
Invoke-WebRequest "https://aka.ms/vs/17/release/vs_community.exe" -OutFile "${env:TEMP}\\vs_community.exe" -UseBasicParsing;
& "${env:TEMP}\\vs_community.exe" --quiet --wait --norestart --noUpdateInstaller --includeRecommended `
    --add Microsoft.VisualStudio.Workload.Azure `
    --add Microsoft.VisualStudio.Workload.Data `
    --add Microsoft.VisualStudio.Workload.DataScience `
    --add Microsoft.VisualStudio.Workload.ManagedDesktop `
    --add Microsoft.VisualStudio.Workload.NativeCrossPlat `
    --add Microsoft.VisualStudio.Workload.NativeDesktop `
    --add Microsoft.VisualStudio.Workload.NativeMobile `
    --add Microsoft.VisualStudio.Workload.NetCrossPlat `
    --add Microsoft.VisualStudio.Workload.NetWeb `
    --add Microsoft.VisualStudio.Workload.Node `
    --add Microsoft.VisualStudio.Workload.Python `
    --add Microsoft.VisualStudio.Workload.Universal `
    --add Microsoft.VisualStudio.Workload.VisualStudioExtension `
    --add Microsoft.VisualStudio.Component.VC.14.40.17.10.x86.x64 `
    --add Microsoft.VisualStudio.Component.VC.14.40.17.10.ARM64 `
    --add Microsoft.VisualStudio.Component.VC.14.40.17.10.CLI.Support `
    | Out-Default

# Install 7zip
$_7ZIP_VERSION = "2408"
$_7ZIP_DIR = "C:\\Program Files\\7-Zip"
Invoke-WebRequest "https://7-zip.org/a/7z${_7ZIP_VERSION}-x64.exe" -OutFile "${env:TEMP}\\7z-x64.exe" -UseBasicParsing;
$_7ZIP_SHA256 = '67cb9d3452c9dd974b04f4a5fd842dbcba8184f2344ff72e3662d7cdb68b099b';
if ((Get-FileHash "${env:TEMP}\\7z-x64.exe" -Algorithm sha256).Hash -ne $_7ZIP_SHA256) {
    Write-Host '_7ZIP_SHA256 CHECKSUM VERIFICATION FAILED!';
    exit 1;
};
Start-Process "$env:TEMP\\7z-x64.exe" -Wait -ArgumentList @('/S', "/D=`"$_7ZIP_DIR`"")
$env:PATH = $env:PATH + ";$_7ZIP_DIR\\;$_7ZIP_DIR";
Set-ItemProperty -Path 'HKLM:\\SYSTEM\\CurrentControlSet\\Control\\Session Manager\\Environment\\' -Name Path -Value $env:PATH

# Install Miniconda
$MINICONDA_RELEASE = "py312_24.5.0-0"
$MINICONDA_DIR = "C:\\Program Files\\miniconda3"
Invoke-WebRequest "https://repo.anaconda.com/miniconda/Miniconda3-${MINICONDA_RELEASE}-Windows-x86_64.exe" -OutFile "${env:TEMP}\\miniconda.exe" -UseBasicParsing;
$MINICONDA_SHA256 = 'b1ce11a339c8246010e898065f6fa6feb1940a55fefd550b57a8039c7d4b6200';
if ((Get-FileHash "${env:TEMP}\\miniconda.exe" -Algorithm sha256).Hash -ne $MINICONDA_SHA256) {
    Write-Host 'MINICONDA_SHA256 CHECKSUM VERIFICATION FAILED!';
    exit 1;
};
mkdir -p "$MINICONDA_DIR"
Start-Process "$env:TEMP\\miniconda.exe" -Wait -ArgumentList @('/S', '/InstallationType=AllUsers', '/AddToPath=1', "/D=$MINICONDA_DIR")
$env:PATH = $env:PATH + ";$MINICONDA_DIR\\condabin\\;$MINICONDA_DIR\\condabin";
Set-ItemProperty -Path 'HKLM:\\SYSTEM\\CurrentControlSet\\Control\\Session Manager\\Environment\\' -Name Path -Value $env:PATH
& "$MINICONDA_DIR\\condabin\\conda.bat" init --system

# Install NVM and node
$NVM_VERSION = "1.1.11"
$env:NVM_HOME = "C:\\Program Files\\nvm"
$env:NVM_SYMLINK = "C:\\Program Files\\nodejs"
$NVM_VERSIONS_DIR = "$env:NVM_HOME\\versions"
mkdir -p "$env:NVM_HOME"
mkdir -p "$NVM_VERSIONS_DIR"
Invoke-WebRequest "https://github.com/coreybutler/nvm-windows/releases/download/${NVM_VERSION}/nvm-noinstall.zip" -OutFile "${env:TEMP}\\nvm-noinstall.zip" -UseBasicParsing;
$NVM_SHA256 = '12e9c418a6b0d9d06736b7ccbf67225fe081f64e81f9ab831bc399b4255d8aad';
if ((Get-FileHash "${env:TEMP}\\nvm-noinstall.zip" -Algorithm sha256).Hash -ne $NVM_SHA256) {
    Write-Host 'NVM_SHA256 CHECKSUM VERIFICATION FAILED!';
    exit 1;
};
Expand-Archive "${env:TEMP}\\nvm-noinstall.zip" -DestinationPath "${env:TEMP}\\nvm-noinstall" -Force;
xcopy "${env:TEMP}\\nvm-noinstall" "$env:NVM_HOME" /h /i /c /k /e /r /y
$env:PATH = $env:PATH + ";$env:NVM_HOME\\;$env:NVM_HOME;$env:NVM_SYMLINK\\;$env:NVM_SYMLINK";
Set-ItemProperty -Path 'HKLM:\\SYSTEM\\CurrentControlSet\\Control\\Session Manager\\Environment\\' -Name Path -Value $env:PATH
[System.Environment]::SetEnvironmentVariable('NVM_HOME', $env:NVM_HOME, 'Machine')
[System.Environment]::SetEnvironmentVariable('NVM_SYMLINK', $env:NVM_SYMLINK, 'Machine')
$NVM_SETTINGS = @("root: $NVM_VERSIONS_DIR", "arch: 64", "proxy: none")
$Utf8Encoding = New-Object System.Text.UTF8Encoding $False
[System.IO.File]::WriteAllLines("$env:NVM_HOME\\settings.txt", $NVM_SETTINGS, $Utf8Encoding)
& "$env:NVM_HOME\\nvm.exe" install 12
& "$env:NVM_HOME\\nvm.exe" install 13
& "$env:NVM_HOME\\nvm.exe" install 14
& "$env:NVM_HOME\\nvm.exe" install 15
& "$env:NVM_HOME\\nvm.exe" install 16
& "$env:NVM_HOME\\nvm.exe" install 17
& "$env:NVM_HOME\\nvm.exe" install 18
& "$env:NVM_HOME\\nvm.exe" install 19
& "$env:NVM_HOME\\nvm.exe" install 20
& "$env:NVM_HOME\\nvm.exe" install 21
& "$env:NVM_HOME\\nvm.exe" install 22
& "$env:NVM_HOME\\nvm.exe" use 22
