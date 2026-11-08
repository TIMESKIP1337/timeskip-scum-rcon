@echo off
rem Starts the RCON server from its own folder so config\ and logs\ are found.
rem --console adds a command prompt (rcon>) to this same window: type ListPlayers, say global Hello, ...
rem Type exit to stop the server.
cd /d "%~dp0"
title ScumRcon
.\ScumRcon.Server.exe --console
echo.
echo The RCON server has stopped.
pause
