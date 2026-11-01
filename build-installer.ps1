$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$project = Join-Path $root 'src\ISLA\ISLA.csproj'
$installerProject = Join-Path $root 'installer\Installer.csproj'
$publish = Join-Path $root 'src\ISLA\bin\Release\net10.0-windows10.0.22621.0\win-x64\publish'
$installer = Join-Path $root 'installer'
$payload = Join-Path $installer 'ISLA-payload.zip'
$identityPackage = Join-Path $installer 'ISLA.Identity.msix'
$identityCertificate = Join-Path $installer 'ISLA.Identity.cer'
$identityDirectory = Join-Path $root 'artifacts\identity'
$release = Join-Path $root 'release'
$setup = Join-Path $release 'ISLA-Setup.exe'

New-Item $release -ItemType Directory -Force | Out-Null
Remove-Item $identityDirectory -Recurse -Force -ErrorAction SilentlyContinue
New-Item $identityDirectory -ItemType Directory -Force | Out-Null
Remove-Item $payload, $identityPackage, $identityCertificate, $setup -Force -ErrorAction SilentlyContinue
dotnet publish $project -c Release -r win-x64 --self-contained false
if ($LASTEXITCODE -ne 0) { throw 'ISLA publish failed.' }
Compress-Archive -Path (Join-Path $publish '*') -DestinationPath $payload -CompressionLevel Optimal

$makeAppx = Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\bin" -Filter MakeAppx.exe -Recurse |
    Where-Object { $_.FullName -match '\\x64\\' } |
    Sort-Object LastWriteTime |
    Select-Object -Last 1
$signtool = Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\bin" -Filter SignTool.exe -Recurse |
    Where-Object { $_.FullName -match '\\x64\\' } |
    Sort-Object LastWriteTime |
    Select-Object -Last 1
if ($null -eq $makeAppx -or $null -eq $signtool) {
    throw 'Windows SDK MakeAppx.exe and SignTool.exe are required to build the notification identity package.'
}

$manifest = @'
<?xml version="1.0" encoding="utf-8"?>
<Package IgnorableNamespaces="uap uap3 uap10 rescap"
  xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10"
  xmlns:uap="http://schemas.microsoft.com/appx/manifest/uap/windows10"
  xmlns:uap3="http://schemas.microsoft.com/appx/manifest/uap/windows/10/3"
  xmlns:uap10="http://schemas.microsoft.com/appx/manifest/uap/windows10/10"
  xmlns:rescap="http://schemas.microsoft.com/appx/manifest/foundation/windows10/restrictedcapabilities">
  <Identity Name="ISLA.Identity" Publisher="CN=ISLA Development" Version="1.0.0.0" ProcessorArchitecture="neutral" />
  <Properties>
    <DisplayName>ISLA</DisplayName>
    <PublisherDisplayName>ISLA Development</PublisherDisplayName>
    <Logo>Assets\ISLA.png</Logo>
    <uap10:AllowExternalContent>true</uap10:AllowExternalContent>
  </Properties>
  <Resources>
    <Resource Language="en-us" />
  </Resources>
  <Dependencies>
    <TargetDeviceFamily Name="Windows.Desktop" MinVersion="10.0.19041.0" MaxVersionTested="10.0.26100.0" />
  </Dependencies>
  <Capabilities>
    <rescap:Capability Name="runFullTrust" />
    <rescap:Capability Name="unvirtualizedResources" />
    <uap3:Capability Name="userNotificationListener" />
  </Capabilities>
  <Applications>
    <Application Id="ISLA" Executable="ISLA.exe" uap10:TrustLevel="mediumIL" uap10:RuntimeBehavior="win32App">
      <uap:VisualElements AppListEntry="none" DisplayName="ISLA" Description="ISLA Dynamic Island" BackgroundColor="transparent" Square150x150Logo="Assets\ISLA.png" Square44x44Logo="Assets\ISLA.png" />
    </Application>
  </Applications>
</Package>
'@
$manifestPath = Join-Path $identityDirectory 'AppxManifest.xml'
Set-Content -Path $manifestPath -Value $manifest -Encoding UTF8
& $makeAppx.FullName pack /o /nv /d $identityDirectory /p $identityPackage
if ($LASTEXITCODE -ne 0) { throw 'Identity package creation failed.' }

$certificate = New-SelfSignedCertificate `
    -Type Custom `
    -Subject 'CN=ISLA Development' `
    -FriendlyName 'ISLA Development Package Signing' `
    -KeyUsage DigitalSignature `
    -CertStoreLocation 'Cert:\CurrentUser\My'
$signingPassword = 'ISLA-' + [Guid]::NewGuid().ToString('N')
$password = ConvertTo-SecureString $signingPassword -AsPlainText -Force
$pfxPath = Join-Path $identityDirectory 'ISLA-signing.pfx'
Export-PfxCertificate -Cert $certificate -FilePath $pfxPath -Password $password | Out-Null
Export-Certificate -Cert $certificate -FilePath $identityCertificate | Out-Null
& $signtool.FullName sign /fd SHA256 /f $pfxPath /p $signingPassword $identityPackage
if ($LASTEXITCODE -ne 0) { throw 'Identity package signing failed.' }
Remove-Item "Cert:\CurrentUser\My\$($certificate.Thumbprint)" -Force -ErrorAction SilentlyContinue
Remove-Item $pfxPath -Force

dotnet publish $installerProject -c Release -r win-x64 --self-contained false
if ($LASTEXITCODE -ne 0) { throw 'Installer publish failed.' }
Copy-Item (Join-Path $installer 'bin\Release\net10.0-windows\win-x64\publish\ISLA-Installer.exe') $setup -Force
Write-Host "Installer: $setup"
