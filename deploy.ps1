#Requires -Version 5.1
# Deploy de Reserva2 (backend + frontend) al servidor de produccion.
# Requiere que ya exista acceso SSH sin contraseña a root@104.248.121.138
# (clave en ~/.ssh/id_ed25519, instalada en el servidor con ssh-copy-id).

$ErrorActionPreference = "Stop"

$Server = "root@104.248.121.138"
$RepoRoot = $PSScriptRoot
$BackendDir = Join-Path $RepoRoot "Backend\Reserva2.Api"
$FrontendDir = Join-Path $RepoRoot "Frontend\reserva2-app"
$RemoteBackendPath = "/root/reserva2-api"
$RemoteBackendTemp = "/root/reserva2-api-nuevo"
$RemoteFrontendPath = "/var/www/reserva2"
$RemoteFrontendTemp = "/root/reserva2-frontend-nuevo"

function Invoke-Step {
    param(
        [Parameter(Mandatory)][string]$Nombre,
        [Parameter(Mandatory)][scriptblock]$Accion
    )
    Write-Host ""
    Write-Host "=== $Nombre ===" -ForegroundColor Cyan
    & $Accion
    if ($LASTEXITCODE -ne 0) {
        Write-Host ""
        Write-Host "FALLO en '$Nombre' (codigo de salida $LASTEXITCODE). Deploy detenido." -ForegroundColor Red
        exit 1
    }
    Write-Host "OK: $Nombre" -ForegroundColor Green
}

Write-Host "Iniciando deploy de Reserva2 a $Server" -ForegroundColor Magenta

# 1. Compilar backend
Invoke-Step "Compilar backend (Release)" {
    Push-Location $BackendDir
    try {
        if (Test-Path ".\publicar") { Remove-Item -Recurse -Force ".\publicar" }
        dotnet publish -c Release -o ./publicar
    } finally {
        Pop-Location
    }
}

# 2. Subir backend a una carpeta temporal y despues moverla a su lugar (sin comodines: un
#    "scp -r carpeta\*" no se expande al ser invocado desde PowerShell -> scp.exe, asi que
#    scp.exe recibe el "*" literal y falla con "No such file or directory". Los secretos
#    reales vienen de variables de entorno en el servicio systemd, no de appsettings.json,
#    asi que pisar esa carpeta con la version local no rompe nada).
Invoke-Step "Subir backend al servidor" {
    ssh $Server "rm -rf $RemoteBackendTemp"
    if ($LASTEXITCODE -ne 0) { return }
    scp -r "$BackendDir\publicar" "${Server}:${RemoteBackendTemp}"
    if ($LASTEXITCODE -ne 0) { return }
    ssh $Server "rm -rf $RemoteBackendPath && mv $RemoteBackendTemp $RemoteBackendPath"
}

# 3. Reiniciar el servicio del backend
Invoke-Step "Reiniciar servicio backend (reserva2-api)" {
    ssh $Server "systemctl restart reserva2-api"
}

# 4. Compilar frontend
Invoke-Step "Compilar frontend (production)" {
    Push-Location $FrontendDir
    try {
        ng build --configuration production
    } finally {
        Pop-Location
    }
}

# 5. Subir frontend a una carpeta temporal en el servidor (sin comodines, mismo motivo que el backend)
Invoke-Step "Subir frontend a carpeta temporal en el servidor" {
    ssh $Server "rm -rf $RemoteFrontendTemp"
    if ($LASTEXITCODE -ne 0) { return }
    scp -r "$FrontendDir\dist\reserva2-app\browser" "${Server}:${RemoteFrontendTemp}"
}

# 6. Reemplazar el contenido de /var/www/reserva2 por el nuevo build
Invoke-Step "Reemplazar contenido de $RemoteFrontendPath" {
    ssh $Server "rm -rf $RemoteFrontendPath && mv $RemoteFrontendTemp $RemoteFrontendPath"
}

# 7. Verificar que el sitio responde
Invoke-Step "Verificar que el sitio responde" {
    $script:HttpCode = curl.exe -s -o NUL -w "%{http_code}" https://reservados2.com
    Write-Host "Codigo HTTP recibido: $script:HttpCode"
    if ($script:HttpCode -ne "200") {
        Write-Host "ADVERTENCIA: el sitio no devolvio 200." -ForegroundColor Yellow
    }
}

Write-Host ""
Write-Host "Deploy completo. Sitio respondio con HTTP $script:HttpCode" -ForegroundColor Magenta
