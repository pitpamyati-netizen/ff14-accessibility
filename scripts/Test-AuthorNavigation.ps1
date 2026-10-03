[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$taskRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$taskManifest = Get-Content -LiteralPath (Join-Path $taskRoot 'docs/maintenance/AUTHOR_NAVIGATION.json') -Raw | ConvertFrom-Json
foreach ($taskFile in $taskManifest.Files) {
    $taskPath = Join-Path $taskRoot $taskFile.Path
    if ((Get-FileHash -LiteralPath $taskPath -Algorithm SHA256).Hash -ne $taskFile.Sha256) {
        throw "Файл автонавигации отличается от версии автора: $($taskFile.Path)"
    }
}
foreach ($taskMethod in @($taskManifest.Resolver, $taskManifest.HuntSearch)) {
    $taskSource = [IO.File]::ReadAllText((Join-Path $taskRoot $taskMethod.Path)).Replace("`r`n", "`n")
    $taskStart = $taskSource.IndexOf([string]$taskMethod.Signature, [StringComparison]::Ordinal)
    if ($taskStart -lt 0) { throw "Не найден метод автора: $($taskMethod.Signature)" }
    $taskEnd = $taskSource.IndexOf("`n    }", $taskStart, [StringComparison]::Ordinal)
    if ($taskEnd -lt 0) { throw "Не найден конец метода автора: $($taskMethod.Signature)" }
    $taskBytes = [Text.Encoding]::UTF8.GetBytes($taskSource.Substring($taskStart, $taskEnd + 6 - $taskStart))
    $taskSha = [Security.Cryptography.SHA256]::Create()
    try { $taskHash = [BitConverter]::ToString($taskSha.ComputeHash($taskBytes)).Replace('-', '').ToLowerInvariant() }
    finally { $taskSha.Dispose() }
    if ($taskHash -ne $taskMethod.Sha256) { throw "Маршрутизация отличается от версии автора: $($taskMethod.Signature)" }
}
foreach ($taskRemoved in $taskManifest.RemovedFiles) {
    if (Test-Path -LiteralPath (Join-Path $taskRoot $taskRemoved)) {
        throw "Возвращён удалённый дополнительный код навигации: $taskRemoved"
    }
}
Write-Host "Автонавигация сверена с автором: $($taskManifest.Files.Count) файлов, два метода; исходный коммит $($taskManifest.Commit)."
