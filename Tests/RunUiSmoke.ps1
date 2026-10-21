# Run after building Tests/TuningRegression in Release. Each invocation creates
# a separate portable runtime, so neither the user's INI nor shared build files
# are modified. The generated fixture and screenshots contain no game assets.
param(
    [Parameter(Mandatory = $true)][string]$OutputDirectory,
    [string]$InputProject = (Join-Path $PSScriptRoot 'TuningRegression/Fixtures/format1003.fms'),
    [string]$DotnetPath = 'dotnet',
    [ValidateSet('', '--mmc5-pcm-ui')][string]$Mode = '',
    [string[]]$Cases = @('zho:100', 'eng:100', 'deu:100', 'spa:100', 'por:100', 'rus:100', 'kor:100', 'zho:150')
)
$ErrorActionPreference = 'Stop'
$source = Join-Path $PSScriptRoot 'TuningRegression/bin/Release/net8.0'
if (-not (Test-Path -LiteralPath (Join-Path $source 'TuningRegression.dll'))) {
    throw 'Build Tests/TuningRegression/TuningRegression.csproj -c Release before running UI tests.'
}
$destination = [IO.Path]::GetFullPath($OutputDirectory)
$inputFile = (Resolve-Path -LiteralPath $InputProject).Path
New-Item -ItemType Directory -Path $destination -Force | Out-Null
$taskRuntime = Join-Path $destination ('portable-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $taskRuntime | Out-Null
Get-ChildItem -LiteralPath $source | ForEach-Object {
    # Do not inherit a mistakenly-created shared configuration.
    if ($_.Name -notin @('FamiStudio.ini', 'portable.txt', 'AutoSaves')) {
        Copy-Item -LiteralPath $_.FullName -Destination $taskRuntime -Recurse
    }
}
Set-Content -LiteralPath (Join-Path $taskRuntime 'portable.txt') -Value 'Isolated UI regression runtime.'
$results = @()
Push-Location (Split-Path -Parent $PSScriptRoot)
try {
    foreach ($case in $Cases) {
        if ($case -notmatch '^(eng|zho|deu|spa|por|rus|kor):(100|125|150|175|200)$') {
            throw "Invalid UI case: $case"
        }
        $language = $Matches[1]
        $scale = [int]$Matches[2]
        $name = "$language-$scale"
        $captures = Join-Path $destination $name
        $log = Join-Path $destination "$name.log"
        & $DotnetPath (Join-Path $taskRuntime 'TuningRegression.dll') --ui $language $captures $inputFile $scale $Mode *> $log
        $code = $LASTEXITCODE
        $lines = Get-Content -LiteralPath $log
        $expected = if ($Mode -eq '--mmc5-pcm-ui') { "PASS PCM UI $language" } else { "PASS UI $language scale=$scale" }
        $passed = $code -eq 0 -and $lines[-1] -eq $expected
        $results += [pscustomobject]@{
            language = $language; scale = $scale; exit_code = $code; passed = $passed
            pass_lines = @($lines | Where-Object { $_ -match '^PASS ' }).Count
            log = $log; capture_directory = $captures
        }
        Write-Output "$name passed=$passed"
    }
}
finally {
    Pop-Location
    $results | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $destination 'ui-results.json') -Encoding utf8
}
if (@($results | Where-Object { -not $_.passed }).Count -gt 0) {
    throw 'One or more UI cases failed; inspect ui-results.json and the preserved logs.'
}
