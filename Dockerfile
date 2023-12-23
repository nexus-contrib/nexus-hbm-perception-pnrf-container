FROM mcr.microsoft.com/dotnet/sdk:8.0-bookworm-slim
WORKDIR /tmp

ENV WINEARCH=win64

# Display support:
# https://stackoverflow.com/a/59733566

RUN dpkg --add-architecture i386 &&\
    apt update &&\
    apt install wine32:i386 wine64 curl cabextract openssh-server -y &&\
    # start openssh-server
    sed -i 's/#PermitRootLogin prohibit-password/PermitRootLogin yes/g' '/etc/ssh/sshd_config' &&\
    # download winetricks (and fix missing win64 executable: https://bugs.debian.org/cgi-bin/bugreport.cgi?bug=1031649)
    ln -s /usr/bin/wine /usr/bin/wine64 &&\
    curl -o winetricks https://raw.githubusercontent.com/Winetricks/winetricks/master/src/winetricks &&\
    chmod +x winetricks &&\
    # download PNRF Reader (COM libraries)
    curl -o "PNRF Reader 08.30.22203.exe" https://www.hbm.com/fileadmin/mediapool/support/download/genesishighspeed/perception/software/8-30/PNRF%20Reader%208.30.22203.exe

CMD ["sleep", "infinity"]
EXPOSE 22