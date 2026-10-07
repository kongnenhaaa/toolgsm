param(
    [Parameter(Mandatory = $true)]
    [string]$RuntimeDir,

    [Parameter(Mandatory = $true)]
    [string]$RequirementsFile
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

$pythonVersion = '3.12.10'
$pythonArchiveUrl = "https://www.python.org/ftp/python/$pythonVersion/python-$pythonVersion-embed-amd64.zip"
$getPipUrl = 'https://bootstrap.pypa.io/get-pip.py'
$requiredImports = @(
    'requests',
    'urllib3',
    'Crypto',
    'cryptography',
    'openpyxl',
    'qrcode',
    'PIL',
    'serial',
    'faster_whisper'
)

$runtimePath = [System.IO.Path]::GetFullPath($RuntimeDir)
$requirementsPath = [System.IO.Path]::GetFullPath($RequirementsFile)
if (-not (Test-Path -LiteralPath $requirementsPath -PathType Leaf)) {
    throw "Requirements file not found: $requirementsPath"
}

$sha256 = [System.Security.Cryptography.SHA256]::Create()
$requirementsStream = [System.IO.File]::OpenRead($requirementsPath)
try {
    $requirementsHash = [System.BitConverter]::ToString(
        $sha256.ComputeHash($requirementsStream)).Replace('-', '')
}
finally {
    $requirementsStream.Dispose()
    $sha256.Dispose()
}
$stampValue = "$pythonVersion|$requirementsHash"
$stampPath = Join-Path $runtimePath '.toolgsm-runtime-version'
$pythonExe = Join-Path $runtimePath 'python.exe'

function Test-PortablePython {
    if (-not (Test-Path -LiteralPath $pythonExe -PathType Leaf)) {
        return $false
    }
    if (-not (Test-Path -LiteralPath $stampPath -PathType Leaf)) {
        return $false
    }
    if ((Get-Content -LiteralPath $stampPath -Raw).Trim() -ne $stampValue) {
        return $false
    }

    $importCode = 'import ' + ($requiredImports -join ',')
    & $pythonExe -I -c $importCode
    return $LASTEXITCODE -eq 0
}

if (Test-PortablePython) {
    Write-Host "Portable Python is ready: $runtimePath"
    exit 0
}

$runtimeParent = Split-Path -Parent $runtimePath
New-Item -ItemType Directory -Path $runtimeParent -Force | Out-Null
$stagingPath = Join-Path $runtimeParent ('.python-runtime-staging-' + [guid]::NewGuid().ToString('N'))
$downloadPath = Join-Path $stagingPath 'python-embed.zip'
$getPipPath = Join-Path $stagingPath 'get-pip.py'
$preparedPath = Join-Path $stagingPath 'runtime'

try {
    New-Item -ItemType Directory -Path $preparedPath -Force | Out-Null
    Write-Host "Downloading portable Python $pythonVersion..."
    Invoke-WebRequest -Uri $pythonArchiveUrl -OutFile $downloadPath -UseBasicParsing
    Expand-Archive -LiteralPath $downloadPath -DestinationPath $preparedPath -Force

    $pthPath = Join-Path $preparedPath 'python312._pth'
    if (-not (Test-Path -LiteralPath $pthPath -PathType Leaf)) {
        throw "Portable Python package is missing python312._pth"
    }
    $pthLines = @(
        'python312.zip',
        '.',
        'Lib\site-packages',
        'import site'
    )
    Set-Content -LiteralPath $pthPath -Value $pthLines -Encoding ASCII
    New-Item -ItemType Directory -Path (Join-Path $preparedPath 'Lib\site-packages') -Force | Out-Null

    Write-Host 'Installing pip into portable Python...'
    Invoke-WebRequest -Uri $getPipUrl -OutFile $getPipPath -UseBasicParsing
    $preparedPython = Join-Path $preparedPath 'python.exe'
    & $preparedPython $getPipPath --disable-pip-version-check --no-warn-script-location
    if ($LASTEXITCODE -ne 0) {
        throw "pip installation failed (exit code $LASTEXITCODE)"
    }

    Write-Host 'Installing ToolGSM Python dependencies...'
    & $preparedPython -m pip install `
        --disable-pip-version-check `
        --no-warn-script-location `
        --no-cache-dir `
        -r $requirementsPath
    if ($LASTEXITCODE -ne 0) {
        throw "Requirements installation failed (exit code $LASTEXITCODE)"
    }

    $importCode = 'import ' + ($requiredImports -join ',')
    & $preparedPython -I -c $importCode
    if ($LASTEXITCODE -ne 0) {
        throw 'Portable Python dependency verification failed'
    }

    Set-Content -LiteralPath (Join-Path $preparedPath '.toolgsm-runtime-version') `
        -Value $stampValue -Encoding ASCII -NoNewline

    if (Test-Path -LiteralPath $runtimePath) {
        $backupPath = "$runtimePath.previous"
        if (Test-Path -LiteralPath $backupPath) {
            Remove-Item -LiteralPath $backupPath -Recurse -Force
        }
        Move-Item -LiteralPath $runtimePath -Destination $backupPath
        Move-Item -LiteralPath $preparedPath -Destination $runtimePath
        Remove-Item -LiteralPath $backupPath -Recurse -Force
    }
    else {
        Move-Item -LiteralPath $preparedPath -Destination $runtimePath
    }

    Write-Host "Portable Python prepared: $runtimePath"
}
finally {
    if (Test-Path -LiteralPath $stagingPath) {
        Remove-Item -LiteralPath $stagingPath -Recurse -Force
    }
}
