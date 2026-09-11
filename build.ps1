$ErrorActionPreference = "Stop"
dotnet restore
dotnet publish .\src\iPhoneUsbShare.csproj -c Release -r win-x64 --self-contained true
Write-Host ""
Write-Host "Built:" -ForegroundColor Green
Write-Host ".\src\bin\Release\net8.0-windows\win-x64\publish\iPhoneUsbShare.exe"
