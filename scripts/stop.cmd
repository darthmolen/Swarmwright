@echo off
rem Stop the Swarmwright local-dev stack.
rem
rem Usage:
rem   scripts\stop.cmd             stop containers, keep data volumes
rem   scripts\stop.cmd --volumes   stop containers AND remove named volumes
rem
rem The gpu profile is included so a running vLLM container is also stopped.

cd /d "%~dp0.."

if /i "%~1"=="--volumes" (
    echo Stopping stack and removing named volumes ^(pgdata, redisdata, hfcache^)...
    docker compose --profile gpu down --volumes
) else (
    echo Stopping stack ^(volumes preserved^)...
    docker compose --profile gpu down
)
echo Done.
