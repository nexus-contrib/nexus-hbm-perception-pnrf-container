# How to prepare Docker container

```bash
sudo docker build -t nexus-hbm-perception-pnrf-container .

# allow docker user on local machine to connect to X windows display 
# (don't know why it works as the docker user does not exists)
xhost +local:docker

sudo docker run --name pnrf-tmp --network host -e DISPLAY=$DISPLAY -d nexus-hbm-perception-pnrf-container
sudo docker exec -it pnrf-tmp bash -c "./winetricks -q msxml6; wine PNRF\ Reader\ 08.30.22203.exe; exit"

sudo docker commit pnrf-tmp docker.io/apollo3zehn/nexus-hbm-perception-pnrf-container:latest
```

# TODO

```bash
WINEARCH="win64" WINEPREFIX="/home/vincent/Downloads/pnrf_prefix" DOTNET_ROOT="C:/Program Files/dotnet/" wine dotnet run --project "Z:/home/vincent/Downloads/PNRF Reader/pnrf.csproj"

#     wine dotnet publish Z:/src/pnrf.csproj --self-contained --runtime win-x64 --output .
```

1. Install PNRF Reader 6.30.22203.exe
2. Compile the following project without any code:

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

3. Copy Interop.RecordingInterface.dll and Interop.RecordingLoaders.dll from the resulting obj/ directory into a new lib/ directory

4. Run and code pnrf.csproj from this folder