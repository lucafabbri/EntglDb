@echo off
setlocal enabledelayedexpansion
echo Starting Stress Test with 40 Nodes...
echo Errors will be logged to stress_errors.log

rem Clear previous log
if exist stress_errors.log del stress_errors.log

for /L %%i in (1,1,80) do (
   set /a port=23200+%%i
   echo Starting Node-%%i on Port !port!
   start "Node-%%i" dotnet run --project samples/EntglDb.Sample.Console/EntglDb.Sample.Console.csproj -- node-%%i !port! --localhost --error-log stress_errors_node-%%i.log
   timeout /t 1 >nul
)

echo All 40 nodes started.
pause
