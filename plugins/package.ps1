# Сборка релизного архива HSaiHelper: plugins\package.ps1 → dist\HSaiHelper-<версия>.zip
# Внутри: плагин (dll), правила для ИИ, Install.ps1 (без dotnet), Setup-AI.ps1 (бесплатный локальный ИИ), README.md.
$ErrorActionPreference = 'Stop'
$project = Join-Path $PSScriptRoot 'HSaiHelper.Hdt'
dotnet build $project -c Release -nologo -v q
if ($LASTEXITCODE -ne 0) { throw 'Сборка не удалась' }
$bin = Join-Path $project 'bin\Release\net472'
$version = (Get-Item (Join-Path $bin 'HSaiHelper.Hdt.dll')).VersionInfo.FileVersion -replace '\.0$', ''
$dist = Join-Path $PSScriptRoot "..\dist\HSaiHelper-$version"
if (Test-Path $dist) { Remove-Item $dist -Recurse -Force }
New-Item -ItemType Directory -Force $dist | Out-Null
Copy-Item (Join-Path $bin 'HSaiHelper.Hdt.dll'), (Join-Path $bin 'HSaiHelper.Hdt.pdb') $dist
Copy-Item (Join-Path $PSScriptRoot '..\data\reference\core_rules_en.md') $dist
# Стартовый набор столов (tools/build_seed_boards.py) — шансы до боя и сила стола с первой партии.
$seed = Join-Path $PSScriptRoot '..\data\reference\seed_boards.jsonl.gz'
if (Test-Path $seed) { Copy-Item $seed $dist }
# Заметки о ключевых картах (tools/key_cards.py build).
$keys = Join-Path $PSScriptRoot '..\data\reference\key_cards.json'
if (Test-Path $keys) { Copy-Item $keys $dist }
Copy-Item (Join-Path $PSScriptRoot 'release\*') $dist
$zip = "$dist.zip"
if (Test-Path $zip) { Remove-Item $zip }
Compress-Archive -Path (Join-Path $dist '*') -DestinationPath $zip
Write-Host "Готово: $zip"
