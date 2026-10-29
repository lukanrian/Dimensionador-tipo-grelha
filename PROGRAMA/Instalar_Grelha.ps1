$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$version = '0.35'
$publishRoot = Join-Path $root "dist\win-x64\$version"
$installDir = Join-Path $env:LOCALAPPDATA "Programs\Dimensionador Tipo Grelha\$version"
$uiProject = Join-Path $root 'Grelha.IfcInspector.UI\Grelha.IfcInspector.UI.csproj'
$readerProject = Join-Path $root 'Grelha.IfcInspector\Grelha.IfcInspector.csproj'
$uiPublish = Join-Path $publishRoot 'ui'
$readerPublish = Join-Path $publishRoot 'Leitor'
$shortcutPath = Join-Path ([Environment]::GetFolderPath('Programs')) 'Dimensionador Tipo Grelha.lnk'
$appExe = Join-Path $installDir 'Grelha.IfcInspector.UI.exe'
$sampleIfc = Join-Path $root '..\IFC\786-INC-PRE-0000-MOD-GERL-AC-MODELO_IFC-00.ifc'

New-Item -ItemType Directory -Force -Path $uiPublish, $readerPublish, $installDir | Out-Null
Write-Host "Publicando Dimensionador Tipo Grelha $version..."
dotnet publish $uiProject --configuration Release --runtime win-x64 --self-contained true --output $uiPublish
if ($LASTEXITCODE -ne 0) { throw 'Falha ao publicar a interface.' }
dotnet publish $readerProject --configuration Release --runtime win-x64 --self-contained true --output $readerPublish
if ($LASTEXITCODE -ne 0) { throw 'Falha ao publicar o leitor IFC.' }

Copy-Item -Path (Join-Path $uiPublish '*') -Destination $installDir -Recurse -Force
Copy-Item -Path $readerPublish -Destination (Join-Path $installDir 'Leitor') -Recurse -Force

$shell = New-Object -ComObject WScript.Shell
$shortcut = $shell.CreateShortcut($shortcutPath)
$shortcut.TargetPath = $appExe
$shortcut.WorkingDirectory = $installDir
if (Test-Path $sampleIfc) { $shortcut.Arguments = '"' + $sampleIfc + '"' }
$shortcut.Description = "Dimensionador Tipo Grelha $version"
$shortcut.Save()

Write-Host "Instalado em: $installDir"
Write-Host "Atalho criado em: $shortcutPath"
Start-Process -FilePath $appExe -ArgumentList $(if (Test-Path $sampleIfc) { @('"' + $sampleIfc + '"') } else { @() }) -WorkingDirectory $installDir
