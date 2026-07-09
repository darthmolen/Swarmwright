@echo off
setlocal enabledelayedexpansion

rem Start the Swarmwright local-dev stack (postgres [+ vLLM with --gpu]).
rem
rem Usage:
rem   scripts\start.cmd          postgres only
rem   scripts\start.cmd --gpu    also start the vLLM model server (gpu profile)
rem
rem Requires Docker Desktop. Published container ports are reachable from Windows
rem localhost (postgres 5432, vLLM 8000).

cd /d "%~dp0.."

set "GPU=false"
if /i "%~1"=="--gpu" set "GPU=true"

if not exist ".env" (
    copy /y ".env.example" ".env" >nul
    echo No .env found; created one from .env.example. Review it before going further.
)

rem Defaults; overridden by matching keys in .env below.
set "POSTGRES_USER=swarmwright"
set "POSTGRES_PORT=5432"
set "VLLM_PORT=8000"
set "VLLM_MODEL=?"

for /f "usebackq eol=# tokens=1,* delims==" %%A in (".env") do (
    if /i "%%A"=="POSTGRES_USER" set "POSTGRES_USER=%%B"
    if /i "%%A"=="POSTGRES_PORT" set "POSTGRES_PORT=%%B"
    if /i "%%A"=="VLLM_PORT" set "VLLM_PORT=%%B"
    if /i "%%A"=="VLLM_MODEL" set "VLLM_MODEL=%%B"
)

echo Starting containers...
if "%GPU%"=="true" (
    docker compose --profile gpu up -d
) else (
    docker compose up -d
)
if errorlevel 1 goto :fail

call :wait_postgres
if errorlevel 1 goto :fail

if "%GPU%"=="true" (
    echo Note: the first run downloads and loads the model; this can take several minutes.
    call :wait_vllm
    if errorlevel 1 goto :fail
)

echo.
echo Stack is up. Endpoints:
echo   Postgres : localhost:%POSTGRES_PORT%
if "%GPU%"=="true" (
    echo   vLLM     : http://localhost:%VLLM_PORT%/v1  ^(model: %VLLM_MODEL%^)
) else (
    echo   vLLM     : not started ^(run "scripts\start.cmd --gpu" to enable^)
)
exit /b 0

:fail
echo.
echo ERROR: stack failed to start. Check logs with: docker compose logs
exit /b 1

:wait_postgres
set /a elapsed=0
<nul set /p "=Waiting for postgres"
:wp_loop
docker compose exec -T postgres pg_isready -U "%POSTGRES_USER%" >nul 2>&1
if not errorlevel 1 ( echo  ready.& exit /b 0 )
if %elapsed% geq 120 ( echo.& echo ERROR: timed out after 120s waiting for postgres.& exit /b 1 )
<nul set /p "=."
timeout /t 5 /nobreak >nul
set /a elapsed+=5
goto :wp_loop

:wait_vllm
set /a elapsed=0
<nul set /p "=Waiting for vLLM model server"
:wv_loop
curl -fs "http://localhost:%VLLM_PORT%/v1/models" >nul 2>&1
if not errorlevel 1 ( echo  ready.& exit /b 0 )
if %elapsed% geq 600 ( echo.& echo ERROR: timed out after 600s waiting for vLLM model server.& exit /b 1 )
<nul set /p "=."
timeout /t 5 /nobreak >nul
set /a elapsed+=5
goto :wv_loop
