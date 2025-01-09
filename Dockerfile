FROM docker.io/debian:bookworm
WORKDIR /tmp

ENV WINEARCH=win64

# Display support:
# https://stackoverflow.com/a/59733566

RUN dpkg --add-architecture i386 &&\
    apt update &&\
    apt install wine32:i386 wine64 curl cabextract git -y
    
    # download winetricks
RUN curl -o winetricks https://raw.githubusercontent.com/Winetricks/winetricks/master/src/winetricks &&\
    chmod +x winetricks &&\
    # download PNRF Reader (COM libraries)
    curl -o "PNRF_Reader.exe" https://www.hbm.com/fileadmin/mediapool/support/download/genesishighspeed/perception/software/8-54/PNRF%20Reader%208.54.23248.exe &&\
    # download .NET 9 installer
    curl -o "dotnet-9-sdk.exe" https://download.visualstudio.microsoft.com/download/pr/38e45a81-a6a4-4a37-a986-bc46be78db16/33e64c0966ebdf0088d1a2b6597f62e5/dotnet-sdk-9.0.101-win-x64.exe

CMD ["sleep", "infinity"]
EXPOSE 22