# HSaiHelper — установка плагина в Hearthstone Deck Tracker (без исходников и без dotnet).

# Проще всего: двойной щелчок по Install.cmd. Или: powershell -ExecutionPolicy Bypass -File Install.ps1

# Потом: перезапустить HDT → Options → Tracker → Plugins → включить «HSAI Recorder» и «HSAI Lobby».



$ErrorActionPreference = 'Stop'

$here = $PSScriptRoot

$hdt = Join-Path $env:APPDATA 'HearthstoneDeckTracker'

$target = Join-Path $hdt 'Plugins\HSaiHelper'

if (-not (Test-Path $hdt)) {

    Write-Host 'Не найден Hearthstone Deck Tracker. Установите его: https://hsdecktracker.net — и запустите Install.cmd ещё раз.'

    Write-Host 'Hearthstone Deck Tracker not found. Install it from https://hsdecktracker.net and run Install.cmd again.'

    exit 1

}

# Файлы из интернета Windows помечает «заблокировано» — тогда HDT может не загрузить плагин. Снимаем пометку.

Get-ChildItem $here -File | Unblock-File

New-Item -ItemType Directory -Force $target | Out-Null

Copy-Item (Join-Path $here 'HSaiHelper.Hdt.dll'), (Join-Path $here 'core_rules_en.md') $target -Force

foreach ($optional in 'HSaiHelper.Hdt.pdb', 'seed_boards.jsonl.gz', 'key_cards.json') {
    if (Test-Path (Join-Path $here $optional)) { Copy-Item (Join-Path $here $optional) $target -Force }
}

Get-ChildItem $target -File | Unblock-File

Write-Host ''

Write-Host "Готово: плагин установлен в $target"

Write-Host "Done: the plugin is installed to $target"

Write-Host ''

if (Get-Process HearthstoneDeckTracker -ErrorAction SilentlyContinue) {

    Write-Host 'HDT запущен — перезапустите его. / HDT is running — restart it.'

}

Write-Host 'Дальше: HDT > Options > Tracker > Plugins > включите HSAI Recorder и HSAI Lobby.'

Write-Host 'Next: HDT > Options > Tracker > Plugins > enable HSAI Recorder and HSAI Lobby.'

Write-Host 'ИИ-помощник (по желанию, бесплатно): Setup-AI.cmd. / AI assistant (optional, free): Setup-AI.cmd.'

