@echo off
echo Packing EntglDb NuGet Packages...
mkdir nupkgs 2>nul

dotnet pack src/EntglDb.Core/EntglDb.Core.csproj -c Release -o nupkgs
dotnet pack src/EntglDb.Network/EntglDb.Network.csproj -c Release -o nupkgs
dotnet pack src/EntglDb.Persistence.Sqlite/EntglDb.Persistence.Sqlite.csproj -c Release -o nupkgs

echo.
echo Packages created in 'nupkgs' directory.
dir nupkgs
pause
