<#
.SYNOPSIS
    Publica el portal Gobierno de Datos en el servidor IIS (SRVMABE602).

.DESCRIPTION
    1. Corre las pruebas (omitible con -SinPruebas).
    2. Publica self-contained win-x64 en una carpeta temporal.
    3. Se conecta a la carpeta compartida del servidor con la credencial indicada (se pide al ejecutar; no se guarda).
    4. Compara contra el servidor y sube solo lo necesario: los archivos propios de la app (GobiernoDatos.Web.*,
       appsettings*, wwwroot) y cualquier archivo nuevo o con tamaño distinto.
    5. Pone app_offline.htm mientras copia (IIS libera la DLL), verifica la DLL principal y lo quita.
    6. Prueba de humo: GET a la URL del portal.

    Nunca sube ni modifica web.config: el del servidor contiene la cadena de conexión y es su fuente de verdad.
    No borra archivos del servidor; los que sobran se reportan.

.EXAMPLE
    .\scripts\deploy.ps1 -Simular        # muestra qué subiría, sin tocar el servidor
.EXAMPLE
    .\scripts\deploy.ps1                 # despliega (pide la contraseña de mabenet\rrsmexti)
#>
[CmdletBinding()]
param(
    # Carpeta de la app en el servidor. Por defecto: \\SRVMABE602\Aplicacion web\GobiernoDatos (con acento).
    [string] $Destino = ('\\SRVMABE602\Aplicaci{0}n web\GobiernoDatos' -f [char]0xF3),
    [string] $Url = 'http://srvmabe602:8010/',
    [string] $Usuario = 'mabenet\rrsmexti',
    [System.Management.Automation.PSCredential] $Credencial,
    [switch] $SinPruebas,
    [switch] $Simular
)

$ErrorActionPreference = 'Stop'
$raiz = Split-Path -Parent $PSScriptRoot
$proyecto = Join-Path $raiz 'src\GobiernoDatos.Web\GobiernoDatos.Web.csproj'
$publicacion = Join-Path ([IO.Path]::GetTempPath()) ('GobiernoDatos_publish_' + (Get-Date -Format 'yyyyMMdd_HHmmss'))
$propios = @('GobiernoDatos.Web.', 'appsettings', 'wwwroot')

function Paso([string] $texto) { Write-Host "`n== $texto" -ForegroundColor Cyan }

# ---- 1. Pruebas ----
if (-not $SinPruebas) {
    Paso 'Pruebas'
    # Sin GD_SQL_TEST las pruebas de SQL se omiten; con ella corren contra el servidor de pruebas indicado (nunca la base compartida).
    & dotnet test (Join-Path $raiz 'GobiernoDatos.sln') --nologo -v q
    if ($LASTEXITCODE -ne 0) { throw 'Las pruebas fallaron: no se despliega.' }
}

# ---- 2. Publicacion ----
Paso "Publicando en $publicacion"
& dotnet publish $proyecto -c Release -r win-x64 --self-contained true -o $publicacion --nologo -v q
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish fallo.' }

# ---- 3. Conexion al servidor ----
Paso "Conectando a $Destino"
if (-not $Credencial) { $Credencial = Get-Credential -UserName $Usuario -Message 'Credencial para la carpeta compartida del servidor' }
$compartido = Split-Path -Parent $Destino
$unidad = 'GDDeploy'
# La unidad solo abre la sesion autenticada; las operaciones usan la ruta UNC (via la unidad, PowerShell duplica la raiz al escribir).

try {
    $remoto = $Destino
    if (-not (Test-Path (Join-Path $remoto 'web.config'))) {
        throw "El servidor no tiene web.config en $Destino. El primer despliegue requiere crearlo con la cadena de conexion (ver docs/05_portal_razor.md)."
    }

    # ---- 4. Comparacion ----
    Paso 'Comparando con el servidor'
    $tamRemoto = @{}
    foreach ($f in Get-ChildItem -LiteralPath $remoto -Recurse -File) { $tamRemoto[$f.FullName.Substring($remoto.TrimEnd('\').Length).TrimStart('\')] = $f.Length }

    $locales = @{}
    $subir = New-Object System.Collections.Generic.List[string]
    foreach ($f in Get-ChildItem $publicacion -Recurse -File) {
        if ($f.Name -eq 'web.config') { continue }
        $rel = $f.FullName.Substring($publicacion.Length).TrimStart('\')
        $locales[$rel] = $true
        $esPropio = @($propios | Where-Object { $rel.StartsWith($_) }).Count -gt 0
        if ($esPropio -or -not $tamRemoto.ContainsKey($rel) -or $tamRemoto[$rel] -ne $f.Length) { $subir.Add($rel) }
    }
    $sobrantes = @($tamRemoto.Keys | Where-Object {
        -not $locales.ContainsKey($_) -and $_ -ne 'web.config' -and -not $_.StartsWith('logs\') -and $_ -ne 'app_offline.htm' })

    Write-Host "Archivos a subir: $($subir.Count)"
    $subir | ForEach-Object { Write-Host "  $_" }
    if ($sobrantes.Count -gt 0) {
        Write-Host "Archivos en el servidor que ya no forman parte de la app (no se borran): $($sobrantes.Count)" -ForegroundColor Yellow
        $sobrantes | ForEach-Object { Write-Host "  $_" }
    }
    if ($Simular) { Write-Host "`nSimulacion: no se copio nada." -ForegroundColor Yellow; Remove-Item -LiteralPath $publicacion -Recurse -Force -ErrorAction SilentlyContinue; return }

    # ---- 5. Copia con la app fuera de linea ----
    Paso 'Copiando (app fuera de linea)'
    $offline = Join-Path $remoto 'app_offline.htm'
    $html = '<!doctype html><html lang="es"><meta charset="utf-8"><title>Actualizando</title>' +
            '<body style="font-family:sans-serif;padding:40px">Gobierno de Datos se est&aacute; actualizando. Vuelve a intentar en un minuto.</body></html>'
    Set-Content -LiteralPath $offline -Value $html -Encoding UTF8
    Start-Sleep -Seconds 8   # IIS detecta app_offline.htm y descarga la app

    # robocopy en modo reanudable (/Z) con reintentos: la red con el servidor a veces se corta a mitad de la copia.
    $copiados = 0
    foreach ($rel in $subir) {
        $carpetaRel = Split-Path -Parent $rel
        $origenDir = if ($carpetaRel) { Join-Path $publicacion $carpetaRel } else { $publicacion }
        $destinoDir = if ($carpetaRel) { Join-Path $remoto $carpetaRel } else { $remoto }
        & robocopy $origenDir $destinoDir (Split-Path -Leaf $rel) /Z /R:20 /W:10 /NP /NJH /NJS /NFL /NDL | Out-Null
        if ($LASTEXITCODE -ge 8) { throw "No se pudo copiar $rel (robocopy $LASTEXITCODE). La app queda fuera de linea (app_offline.htm); vuelve a ejecutar el script." }
        $copiados++
    }
    $hashLocal = (Get-FileHash (Join-Path $publicacion 'GobiernoDatos.Web.dll')).Hash
    $hashRemoto = (Get-FileHash (Join-Path $remoto 'GobiernoDatos.Web.dll')).Hash
    if ($hashLocal -ne $hashRemoto) {
        throw 'La DLL del servidor no coincide con la publicada. La app queda fuera de linea (app_offline.htm) para revisar.'
    }
    Write-Host "Copiados $copiados archivos; DLL verificada."

    # Solo se vuelve a poner en linea si la copia termino completa.
    Remove-Item -LiteralPath $offline -Force
    Write-Host 'App en linea.'
}
finally {
    Remove-PSDrive -Name $unidad -ErrorAction SilentlyContinue
}

# ---- 6. Prueba de humo ----
if (-not $Simular) {
    Paso "Prueba de humo: $Url"
    $ok = $false
    for ($i = 1; $i -le 6 -and -not $ok; $i++) {
        try {
            $r = Invoke-WebRequest -Uri $Url -Credential $Credencial -UseBasicParsing -TimeoutSec 120
            $ok = $r.StatusCode -eq 200
        } catch { Start-Sleep -Seconds 10 }
    }
    if ($ok) { Write-Host "Despliegue terminado: $Url responde 200." -ForegroundColor Green }
    else { Write-Warning "La app no respondio 200 en $Url. Revisa $Destino\logs (stdout) en el servidor." }
}

Remove-Item -LiteralPath $publicacion -Recurse -Force -ErrorAction SilentlyContinue
