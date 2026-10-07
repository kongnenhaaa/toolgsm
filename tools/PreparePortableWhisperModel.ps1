param(
    [Parameter(Mandatory = $true)]
    [string]$PythonExe,

    [Parameter(Mandatory = $true)]
    [string]$ModelDir
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

$pythonPath = [System.IO.Path]::GetFullPath($PythonExe)
$modelPath = [System.IO.Path]::GetFullPath($ModelDir)
$requiredFiles = @{
    'config.json' = 100
    'model.bin' = 400000000
    'tokenizer.json' = 100000
    'vocabulary.txt' = 1000
}

function Test-WhisperModel([string]$Path) {
    foreach ($entry in $requiredFiles.GetEnumerator()) {
        $candidate = Join-Path $Path $entry.Key
        if (-not (Test-Path -LiteralPath $candidate -PathType Leaf)) {
            return $false
        }
        if ((Get-Item -LiteralPath $candidate).Length -lt $entry.Value) {
            return $false
        }
    }
    return $true
}

if (Test-WhisperModel $modelPath) {
    Write-Host "Portable Whisper model is ready: $modelPath"
    exit 0
}
if (-not (Test-Path -LiteralPath $pythonPath -PathType Leaf)) {
    throw "Portable Python not found: $pythonPath"
}

$modelParent = Split-Path -Parent $modelPath
New-Item -ItemType Directory -Path $modelParent -Force | Out-Null
$stagingPath = Join-Path $modelParent ('.faster-whisper-small-staging-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $stagingPath -Force | Out-Null

try {
    $downloadCode = "from huggingface_hub import snapshot_download; print(snapshot_download(repo_id='Systran/faster-whisper-small'))"
    $downloadOutput = & $pythonPath -c $downloadCode
    if ($LASTEXITCODE -ne 0) {
        throw "Whisper model preparation failed (exit code $LASTEXITCODE)"
    }
    $snapshotPath = ($downloadOutput | Where-Object { $_ -and $_.Trim() } | Select-Object -Last 1).Trim()
    if (-not (Test-Path -LiteralPath $snapshotPath -PathType Container)) {
        throw "Whisper snapshot was not found: $snapshotPath"
    }

    foreach ($fileName in $requiredFiles.Keys) {
        $source = Join-Path $snapshotPath $fileName
        $destination = Join-Path $stagingPath $fileName
        if (-not (Test-Path -LiteralPath $source -PathType Leaf)) {
            throw "Whisper snapshot is missing $fileName"
        }
        [System.IO.File]::Copy($source, $destination, $true)
    }
    if (-not (Test-WhisperModel $stagingPath)) {
        throw 'Prepared Whisper model did not pass validation'
    }

    if (Test-Path -LiteralPath $modelPath) {
        [System.IO.Directory]::Delete($modelPath, $true)
    }
    [System.IO.Directory]::Move($stagingPath, $modelPath)
    Write-Host "Portable Whisper model prepared: $modelPath"
}
finally {
    if (Test-Path -LiteralPath $stagingPath) {
        [System.IO.Directory]::Delete($stagingPath, $true)
    }
}
