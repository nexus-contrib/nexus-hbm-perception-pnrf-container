FROM mcr.microsoft.com/dotnet/sdk:8.0-bookworm-slim
WORKDIR /tmp

ENV WINEARCH=win64

# Display support:
# https://stackoverflow.com/a/59733566

RUN dpkg --add-architecture i386 &&\
    apt update &&\
    apt install wine32:i386 wine64 curl cabextract openssh-server -y xz-utils &&\
    # start openssh-server
    sed -i 's/#PermitRootLogin prohibit-password/PermitRootLogin yes/g' '/etc/ssh/sshd_config' &&\
    # download winetricks (and fix missing win64 executable: https://bugs.debian.org/cgi-bin/bugreport.cgi?bug=1031649)
    ln -s /usr/bin/wine /usr/bin/wine64 &&\
    curl -o winetricks https://raw.githubusercontent.com/Winetricks/winetricks/master/src/winetricks &&\
    chmod +x winetricks &&\
    # download & extract newest 7zip
    curl -o 7z.tar.xz https://www.7-zip.org/a/7z2301-linux-x64.tar.xz &&\
    tar xf 7z.tar.xz &&\
    # download & extract PNRF Reader (COM libraries)
    curl -o "PNRF_Reader.exe" https://www.hbm.com/fileadmin/mediapool/support/download/genesishighspeed/perception/software/8-54/PNRF%20Reader%208.54.23248.exe &&\
    ./7zz x PNRF_Reader.exe

CMD ["sleep", "infinity"]
EXPOSE 22