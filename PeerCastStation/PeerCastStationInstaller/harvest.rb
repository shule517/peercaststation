
HEAT = ENV['WIX'] + 'bin\\heat.exe'

Projects = %W(
PeerCastStation.Core
PeerCastStation.App
PeerCastStation.ASF
PeerCastStation.FLV
PeerCastStation.MKV
PeerCastStation.TS
PeerCastStation.CustomFilter
PeerCastStation.HTTP
PeerCastStation.PCP
PeerCastStation.UI
PeerCastStation.Updater
PeerCastStation.UI.HTTP
PeerCastStation.WPF
)

Projects.each do |proj|
  args = %W(
    #{HEAT}
    project
    #{File.join('..', proj, proj + '.csproj')}
    -configuration Release
    -platform AnyCPU
    -ag
    -cg #{proj}
    -pog Binaries
    -pog Satellites
    -pog Content
    -out #{proj}.wxs
    -directoryid INSTALLFOLDER
    -nologo
  )
  p system(*args)
end

