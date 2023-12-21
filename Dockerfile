FROM ubuntu:22.04
WORKDIR /tmp

ENV WINEARCH=win64

# Display support:
# https://stackoverflow.com/a/59733566

RUN dpkg --add-architecture i386 &&\
    apt update &&\
    apt install wine32:i386 wine64 curl cabextract openssh-server -y &&\
    # start openssh-server
    sed -i 's/#PermitRootLogin prohibit-password/PermitRootLogin yes/g' '/etc/ssh/sshd_config' &&\
    service ssh start &&\
    # download winetricks
    curl -o winetricks https://raw.githubusercontent.com/Winetricks/winetricks/master/src/winetricks &&\
    chmod +x winetricks &&\
    # download .NET 5 (.NET 6+ did not work due to stack overflow)
    curl -o dotnet-sdk-5.0.408-win-x64.exe https://download.visualstudio.microsoft.com/download/pr/14ccbee3-e812-4068-af47-1631444310d1/3b8da657b99d28f1ae754294c9a8f426/dotnet-sdk-5.0.408-win-x64.exe &&\
    # download PNRF Reader (COM libraries)
    curl -o "PNRF Reader 08.30.22203.exe" https://www.hbm.com/fileadmin/mediapool/support/download/genesishighspeed/perception/software/8-30/PNRF%20Reader%208.30.22203.exe

CMD ["sleep", "infinity"]
EXPOSE 22