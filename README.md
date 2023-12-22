This repository aims to provide access to the propriertary PNRF Reader Toolkit (https://www.hbm.com/de/7557/anbindung-von-genesis-highspeed-und-oder-perception/) so that [Nexus](https://github.com/malstroem-labs/nexus) is able to read HBM Perception `.pnrf` files.

# Prepare the Docker container

```bash
sudo docker build -t nexus-hbm-perception-pnrf-container .

# allow docker user on local machine to connect to X windows display 
# (don't know why it works as the docker user does not exists)
xhost +local:docker

sudo docker run --name pnrf-tmp --network host -e DISPLAY=$DISPLAY -d nexus-hbm-perception-pnrf-container
sudo docker exec -it pnrf-tmp bash -c "./winetricks -q msxml6; wine PNRF\ Reader\ 08.30.22203.exe; exit"

sudo docker commit pnrf-tmp docker.io/apollo3zehn/nexus-hbm-perception-pnrf-container:latest
sudo docker push docker.io/apollo3zehn/nexus-hbm-perception-pnrf-container:latest
```

# Prepare the application

The docker container does not contain the actual code to read PNRF data to speed up the development cycle. The code itself may be mounted into the container and compiled there (.NET 8 SDK is availabe within the container) like this:

```bash
cd <source folder>
dotnet publish --self-contained --runtime win-x64 --output /app
```

The executable is then available by running `wine /app/HbmPnrf.exe`. This executable expects specific parameters as it tries to connect to a Nexus instance and is only usable in a meaningful way from via Nexus.

In case you want to start from scratch (e.g. if PNRF Reader got an update), it is necessary to create `Interop.RecordingInterface.dll` and `Interop.RecordingLoaders.dll` first by doing the following:

1. Install `PNRF Reader 6.30.22203.exe` on a Windows machine

2. Put the following xml in a file named `HbmPnrf-Interop.csproj` and compile that project:

<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net8.0</TargetFramework>
    <PlatformTarget>x64</PlatformTarget>
  </PropertyGroup>
  <ItemGroup>
    <COMReference Include="RecordingInterface">
      <Guid>{8098371E-98AD-0070-BEF3-21B9A51D6B3E}</Guid>
      <VersionMajor>1</VersionMajor>
      <VersionMinor>0</VersionMinor>
      <Lcid>0</Lcid>
      <WrapperTool>tlbimp</WrapperTool>
      <Isolated>False</Isolated>
      <Private>False</Private>
    </COMReference>
    <COMReference Include="RecordingLoaders">
      <Guid>{8098371E-98AD-0062-BEF3-21B9A51D6B3E}</Guid>
      <VersionMajor>1</VersionMajor>
      <VersionMinor>0</VersionMinor>
      <Lcid>0</Lcid>
      <WrapperTool>tlbimp</WrapperTool>
      <Isolated>False</Isolated>
      <Private>False</Private>
    </COMReference>
  </ItemGroup>
</Project>

3. Copy `Interop.RecordingInterface.dll` and `Interop.RecordingLoaders.dll` from the resulting `obj/` directory into the `src/lib/` directory

4. Compile `HbmPnrf.csproj`