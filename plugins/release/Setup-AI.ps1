# HSaiHelper — установка локального ИИ (бесплатно, работает только на этом компьютере, без интернета после установки).
# Скачивает llama.cpp (github.com/ggml-org/llama.cpp) и открытую модель Gemma 4 (huggingface.co/unsloth) в %LOCALAPPDATA%\HSaiHelper\llm.
# Проще всего: двойной щелчок по Setup-AI.cmd. Или: powershell -ExecutionPolicy Bypass -File Setup-AI.ps1 [-Model auto|game|smart|small]
#   auto  — по объёму памяти: 32 ГБ и больше → game, 16–32 → smart, меньше → small
#   game  — Gemma-4-26B-A4B (16,9 ГБ, быстрая)   smart — Gemma-4-12B (7,1 ГБ, лучше рассуждает)   small — Gemma-4-E4B (5 ГБ)
# Плагин сам запускает модель при входе в Battlegrounds (тумблер — в окне «ИИ»).

param([ValidateSet('auto', 'game', 'smart', 'small')] [string]$Model = 'auto')
$ErrorActionPreference = 'Stop'
$llm = Join-Path $env:LOCALAPPDATA 'HSaiHelper\llm'
$bin = Join-Path $llm 'llama.cpp'
$models = Join-Path $llm 'models'
New-Item -ItemType Directory -Force $bin, $models | Out-Null

$ramGb = [math]::Round((Get-CimInstance Win32_ComputerSystem).TotalPhysicalMemory / 1GB)
if ($Model -eq 'auto') { $Model = if ($ramGb -ge 30) { 'game' } elseif ($ramGb -ge 15) { 'smart' } else { 'small' } }
$files = @{
    game  = @('unsloth/gemma-4-26B-A4B-it-GGUF', 'gemma-4-26B-A4B-it-UD-Q4_K_M.gguf')
    smart = @('unsloth/gemma-4-12b-it-GGUF', 'gemma-4-12b-it-Q4_K_M.gguf')
    small = @('unsloth/gemma-4-E4B-it-GGUF', 'gemma-4-E4B-it-Q4_K_M.gguf')
}
Write-Host "Память: $ramGb ГБ > модель: $($files[$Model][1])"

# 1) llama.cpp: NVIDIA → CUDA, иначе Vulkan (AMD/Intel), без видеокарты — процессор.
if (-not (Test-Path (Join-Path $bin 'llama-server.exe'))) {
    $gpu = (Get-CimInstance Win32_VideoController | ForEach-Object Name) -join ' '
    $kind = if ($gpu -match 'NVIDIA') { 'cuda-13.4-x64' } elseif ($gpu -match 'AMD|Radeon|Intel|Arc') { 'vulkan-x64' } else { 'cpu-x64' }
    # CUDA 13 требует драйвер NVIDIA 580+; со старым драйвером — сборка CUDA 12.4.
    if ($kind -like 'cuda*') {
        $driver = (& nvidia-smi --query-gpu=driver_version --format=csv,noheader 2>$null | Select-Object -First 1)
        if ($driver -and [version]($driver.Trim()) -lt [version]'580.0') { $kind = 'cuda-12.4-x64' }
    }
    Write-Host "Видеокарта: $gpu > сборка llama.cpp: $kind"
    # «latest» у llama.cpp бывает выпуском без сборок для Windows (2026-10-23: v0.7.0) — берём первый свежий выпуск, где сборка есть.
    $releases = Invoke-RestMethod 'https://api.github.com/repos/ggml-org/llama.cpp/releases?per_page=20' -Headers @{ 'User-Agent' = 'HSaiHelper' }
    $release = $releases | Where-Object { $_.assets | Where-Object { $_.name -like "llama-*-bin-win-$kind.zip" } } | Select-Object -First 1
    if (-not $release) { throw "Среди свежих выпусков llama.cpp нет сборки win-$kind / no win-$kind build in recent llama.cpp releases" }
    Write-Host "llama.cpp $($release.tag_name)"
    $assets = @($release.assets | Where-Object { $_.name -like "llama-*-bin-win-$kind.zip" })
    if ($kind -like 'cuda*') { $assets += $release.assets | Where-Object { $_.name -eq "cudart-llama-bin-win-$kind.zip" } }
    foreach ($a in $assets) {
        $zip = Join-Path $env:TEMP $a.name
        Write-Host "Качаю $($a.name) ($([math]::Round($a.size / 1MB)) МБ)..."
        Start-BitsTransfer -Source $a.browser_download_url -Destination $zip
        Expand-Archive $zip $bin -Force
        Remove-Item $zip
    }
}

# 2) Модель — с докачкой (BITS), ~5–17 ГБ.
$repo, $name = $files[$Model]
$path = Join-Path $models $name
if (-not (Test-Path $path)) {
    Write-Host "Качаю модель $name — это несколько гигабайт, может занять время..."
    Start-BitsTransfer -Source "https://huggingface.co/$repo/resolve/main/$name" -Destination $path -DisplayName "HSaiHelper: $name"
}

# 3) Сказать плагину, где ИИ (файл llm_dir.txt рядом с плагином) и какую модель брать.
$plugin = Join-Path $env:APPDATA 'HearthstoneDeckTracker\Plugins\HSaiHelper'
New-Item -ItemType Directory -Force $plugin | Out-Null
Set-Content -Path (Join-Path $plugin 'llm_dir.txt') -Value $llm -Encoding UTF8
$settingsFile = Join-Path $env:APPDATA 'HSaiHelper\settings.json'
if (Test-Path $settingsFile) {
    $s = Get-Content $settingsFile -Raw | ConvertFrom-Json
    $s | Add-Member -NotePropertyName AiModelFile -NotePropertyValue $name -Force
    $s | ConvertTo-Json -Depth 5 | Set-Content $settingsFile -Encoding UTF8
}
Write-Host "Готово. ИИ: $llm. Перезапустите HDT — модель запустится сама при входе в Battlegrounds."
