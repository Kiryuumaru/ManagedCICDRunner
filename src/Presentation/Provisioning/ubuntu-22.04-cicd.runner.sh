# Github CICD runner provision script fo ubuntu 22.04

echo "deb http://security.ubuntu.com/ubuntu jammy-security main" | tee /etc/apt/sources.list.d/jammy-security.list

apt-get update
DEBIAN_FRONTEND=noninteractive apt-get install -y \
    apt-transport-https \
    sudo \
    ca-certificates \
    gnupg \
    lsb-release \
    zip \
    unzip \
    tar \
    bzip2 \
    p7zip-full \
    curl \
    gpg \
    apt-utils \
    software-properties-common \
    uidmap \
    dbus \
    libc6 \
    libgcc1 \
    libgssapi-krb5-2 \
    libstdc++6 \
    zlib1g \
    ninja-build \
    build-essential \
    python3-pip \
    nasm \
    gperf \
    bison \
    libtool \
    autoconf \
    gcc \
    g++
    
# Install OS version specific
echo "deb http://security.ubuntu.com/ubuntu focal-security main" | tee /etc/apt/sources.list.d/focal-security.list
apt-get update
DEBIAN_FRONTEND=noninteractive apt-get install -y \
    libssl1.1 \
    libssl3
rm /etc/apt/sources.list.d/focal-security.list

pip install jinja2

# Install git and GH
curl -fsSL https://cli.github.com/packages/githubcli-archive-keyring.gpg | gpg --dearmor -o /usr/share/keyrings/githubcli-archive-keyring.gpg > /dev/null
echo "deb [arch=$(dpkg --print-architecture) signed-by=/usr/share/keyrings/githubcli-archive-keyring.gpg] https://cli.github.com/packages stable main" | tee /etc/apt/sources.list.d/github-cli.list > /dev/null
apt-get update
DEBIAN_FRONTEND=noninteractive apt-get install -y git gh
git config --global core.packedGitLimit 512m
git config --global core.packedGitWindowSize 512m
git config --global pack.deltaCacheSize 2047m
git config --global pack.packSizeLimit 2047m
git config --global pack.windowMemory 2047m

# Install Docker
DOCKER_VERSION=5:27.1.1
curl -fsSL https://download.docker.com/linux/ubuntu/gpg | gpg --dearmor -o /usr/share/keyrings/docker-archive-keyring.gpg > /dev/null
echo "deb [arch=$(dpkg --print-architecture) signed-by=/usr/share/keyrings/docker-archive-keyring.gpg] https://download.docker.com/linux/ubuntu $(lsb_release -cs) stable" | tee /etc/apt/sources.list.d/docker.list > /dev/null
apt-get update
apt-get install -y docker-ce=$DOCKER_VERSION-1~$(lsb_release -is).$(lsb_release -rs)~$(lsb_release -cs)

# Install Cmake
curl -fsSL https://apt.kitware.com/keys/kitware-archive-latest.asc | gpg --dearmor -o /usr/share/keyrings/kitware-archive-keyring.gpg > /dev/null
echo "deb [arch=$(dpkg --print-architecture) signed-by=/usr/share/keyrings/kitware-archive-keyring.gpg] https://apt.kitware.com/ubuntu/ focal main" | tee /etc/apt/sources.list.d/kitware.list > /dev/null
apt-get update
DEBIAN_FRONTEND=noninteractive apt-get install -y cmake

# Install Helm
HELM_VERSION=3.16.2
curl -fSL --output /tmp/helm.tar.gz https://get.helm.sh/helm-v${HELM_VERSION}-linux-amd64.tar.gz
helm_sha512='0513b6cf7b311fee26a3be1aa7e4863cad6ff00bf61a15aee2c7cdb39ff4329ef9edadcbac3f2103d117c26bb9fd1714e51e04071af7e4f605141438bbab4671'
echo "$helm_sha512 /tmp/helm.tar.gz" | sha512sum -c -
mkdir -p /usr/share/helm
tar -zxf /tmp/helm.tar.gz -C /usr/share/helm
rm /tmp/helm.tar.gz
ln -s /usr/share/helm/linux-amd64/helm /usr/bin/helm

# Install AZ CLI
AZCLI_VERSION=2.51.0
mkdir -p /etc/apt/keyrings
curl -sLS https://packages.microsoft.com/keys/microsoft.asc | gpg --dearmor | tee /etc/apt/keyrings/microsoft.gpg > /dev/null
chmod go+r /etc/apt/keyrings/microsoft.gpg
echo "Types: deb\nURIs: https://packages.microsoft.com/repos/azure-cli/\nSuites: $(lsb_release -cs)\nComponents: main\nArchitectures: $(dpkg --print-architecture)\nSigned-by: /etc/apt/keyrings/microsoft.gpg" | tee /etc/apt/sources.list.d/azure-cli.sources
apt-get update
DEBIAN_FRONTEND=noninteractive apt-get install -y azure-cli=$AZCLI_VERSION-1~$(lsb_release -cs)

# Install .NET
DOTNET_VERSION=8.0.403
curl -fSL --output /tmp/dotnet.tar.gz https://dotnetcli.azureedge.net/dotnet/Sdk/$DOTNET_VERSION/dotnet-sdk-$DOTNET_VERSION-linux-x64.tar.gz
dotnet_sha512='7aa03678228b174f51c4535f18348cdf7a5d35e243b1f8cb28a4a30e402e47567d06df63c8f6da4bdc3c7e898f54f4acc08d9952bfa49d3f220d0353253ac3e9'
echo "$dotnet_sha512 /tmp/dotnet.tar.gz" | sha512sum -c -
mkdir -p /usr/share/dotnet
tar -zxf /tmp/dotnet.tar.gz -C /usr/share/dotnet
rm /tmp/dotnet.tar.gz
ln -s /usr/share/dotnet/dotnet /usr/bin/dotnet

# Install Miniconda
export MINICONDA3_DIR="/usr/local/miniconda3"
mkdir -p $MINICONDA3_DIR
wget https://repo.anaconda.com/miniconda/Miniconda3-latest-Linux-x86_64.sh -O $MINICONDA3_DIR/miniconda.sh
bash $MINICONDA3_DIR/miniconda.sh -b -u -p $MINICONDA3_DIR
rm -rf $MINICONDA3_DIR/miniconda.sh
echo ". $MINICONDA3_DIR/etc/profile.d/conda.sh" | tee -a /etc/profile.d/conda.sh /root/.bashrc
echo 'export PATH=/usr/local/miniconda3/bin:$PATH' | tee -a /etc/profile.d/conda.sh /root/.bashrc
/usr/local/miniconda3/bin/conda init --system

# Install NVM and node
NVM_VERSION=0.40.0
export NVM_DIR="/usr/local/nvm"
mkdir -p $NVM_DIR/versions
curl https://raw.githubusercontent.com/creationix/nvm/v$NVM_VERSION/install.sh | bash
. $NVM_DIR/nvm.sh
nvm install 12
nvm install 13
nvm install 14
nvm install 15
nvm install 16
nvm install 17
nvm install 18
nvm install 19
nvm install 20
nvm install 21
nvm install 22
echo "export NVM_DIR=$NVM_DIR" | tee -a /etc/profile.d/nvm.sh /root/.bashrc
echo '. $NVM_DIR/nvm.sh' | tee -a  /etc/profile.d/nvm.sh /root/.bashrc
echo 'nvm use 22' | tee -a  /etc/profile.d/nvm.sh /root/.bashrc

rm -rf /var/lib/apt/lists/*