$ErrorActionPreference = "Stop"

dotnet restore

dotnet publish .\OneDriveLivePhotoViewer.csproj `
  -c Release `
  -r win-x64 `
  --self-contained true `
  /p:PublishSingleFile=true `
  /p:IncludeNativeLibrariesForSelfExtract=true `
  /p:EnableCompressionInSingleFile=true `
  -o .\publish\win-x64

Write-Host "Published to .\publish\win-x64" -ForegroundColor Green
