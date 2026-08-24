# Fills launch.json.template's __TOKEN__ placeholders from the repo-root .env and writes the result to
# launch.json, which is gitignored because it bakes in a machine-specific game install path. Runs
# automatically on folder open (see tasks.json's runOn) and is also available as a manual task, for after
# an .env edit.

$root = Split-Path -Parent $PSScriptRoot
$envPath = Join-Path $root ".env"
$templatePath = Join-Path $PSScriptRoot "launch.json.template"
$outPath = Join-Path $PSScriptRoot "launch.json"

if (-not (Test-Path $envPath)) {
    Write-Warning "No .env found at $envPath. Copy .env.example to .env and fill in your paths, then rerun this task."
    exit 0
}

$envValues = @{}
foreach ($line in Get-Content $envPath) {
    if ($line -match '^\s*([A-Z_]+)\s*=\s*(.+?)\s*$') {
        $envValues[$Matches[1]] = $Matches[2]
    }
}

function Require-Value([string]$key) {
    if (-not $envValues.ContainsKey($key) -or [string]::IsNullOrWhiteSpace($envValues[$key])) {
        Write-Warning "$key is not set in .env, so launch.json was not regenerated. See .env.example."
        exit 0
    }
    return $envValues[$key]
}

$vintageStoryPath = Require-Value "VINTAGE_STORY_PATH"
$debugDataPath = Require-Value "DEBUG_DATA_PATH"

# Plain string replacement, not -replace: these are Windows paths and backslash has no special meaning
# to .Replace(), which is exactly what's wanted once the path is pre-doubled for JSON below.
$vintageStoryPathJson = $vintageStoryPath.Replace('\', '\\')
$debugDataPathJson = $debugDataPath.Replace('\', '\\')

$content = Get-Content $templatePath -Raw
$content = $content.Replace('__VINTAGE_STORY_PATH__', $vintageStoryPathJson)
$content = $content.Replace('__DEBUG_DATA_PATH__', $debugDataPathJson)
Set-Content -Path $outPath -NoNewline -Value $content

Write-Host "Regenerated .vscode/launch.json from .env."
