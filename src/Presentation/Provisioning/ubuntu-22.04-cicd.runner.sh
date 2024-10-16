# Github CICD runner provision script fo ubuntu 22.04

apt-get update
DEBIAN_FRONTEND=noninteractive apt-get install -y \
    apt-transport-https \
    sudo \
    ca-certificates \
    libssl3 \
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
    cmake \
    ninja-build \
    build-essential \
    pip \
    nasm \
    gperf \
    bison \
    libtool \
    autoconf \
    gcc \
    g++
rm -rf /var/lib/apt/lists/*

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
rm -rf /var/lib/apt/lists/*

# Install Docker
DOCKER_VERSION=5:27.1.1
curl -fsSL https://download.docker.com/linux/ubuntu/gpg | gpg --dearmor -o /usr/share/keyrings/docker-archive-keyring.gpg > /dev/null
echo "deb [arch=$(dpkg --print-architecture) signed-by=/usr/share/keyrings/docker-archive-keyring.gpg] https://download.docker.com/linux/ubuntu $(lsb_release -cs) stable" | tee /etc/apt/sources.list.d/docker.list > /dev/null
apt-get update
apt-get install -y docker-ce=$DOCKER_VERSION-1~$(lsb_release -is).$(lsb_release -rs)~$(lsb_release -cs)
rm -rf /var/lib/apt/lists/*

# Install AZ CLI
AZCLI_VERSION=2.51.0
mkdir -p /etc/apt/keyrings
curl -sLS https://packages.microsoft.com/keys/microsoft.asc | gpg --dearmor | tee /etc/apt/keyrings/microsoft.gpg > /dev/null
chmod go+r /etc/apt/keyrings/microsoft.gpg
echo "Types: deb\nURIs: https://packages.microsoft.com/repos/azure-cli/\nSuites: $(lsb_release -cs)\nComponents: main\nArchitectures: $(dpkg --print-architecture)\nSigned-by: /etc/apt/keyrings/microsoft.gpg" | tee /etc/apt/sources.list.d/azure-cli.sources
apt-get update
DEBIAN_FRONTEND=noninteractive apt-get install -y azure-cli=$AZCLI_VERSION-1~$(lsb_release -cs)
rm -rf /var/lib/apt/lists/*

# Install .NET
DOTNET_VERSION=8.0.402
curl -fSL --output /tmp/dotnet.tar.gz https://dotnetcli.azureedge.net/dotnet/Sdk/$DOTNET_VERSION/dotnet-sdk-$DOTNET_VERSION-linux-x64.tar.gz
dotnet_sha512='6ba966801ad3869275469b0f7ee7af0b88b659d018a37b241962335bd95ef6e55cb6741ab77d96a93c68174d30d0c270b48b3cda21b493270b0d6038ee3fe79e'
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
