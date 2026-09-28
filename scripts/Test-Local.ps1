[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Local-Common.ps1')
$root = Get-LocalRoot
$logs = Join-Path $root 'artifacts\logs'
New-Item -ItemType Directory -Force -Path $logs | Out-Null
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss-fff'
$projects = @(Get-ChildItem -LiteralPath (Join-Path $root 'tests') -Filter '*.csproj' -File -Recurse)
if (!$projects.Count) { throw 'No test projects found.' }
foreach ($project in $projects) {
    & dotnet test $project.FullName -c Release --logger 'console;verbosity=minimal' 2>&1 |
        Tee-Object -FilePath (Join-Path $logs ("test-$stamp-" + $project.BaseName + '.txt')) | Write-Host
    if ($LASTEXITCODE -ne 0) { throw "Tests failed: $($project.Name), exit $LASTEXITCODE" }
}
Write-Host "Passed test projects: $($projects.Count). This does not verify live game behavior."
