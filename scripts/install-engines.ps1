# Install or update the local voice engines into engines/.
#
# What each engine costs on disk (see 根目录 本地音频.md for the runtime budget):
#   tts : ~130MB runtime (Qwen3-TTS-GUI vulkan) + ~700MB of 0.6B GGUF models
#   kws : ~200MB (sherpa-onnx runtime) + ~240MB models + silero VAD
#
# Usage:
#   powershell -ExecutionPolicy Bypass -File scripts/install-engines.ps1              # both
#   powershell -ExecutionPolicy Bypass -File scripts/install-engines.ps1 -Engine tts  # tts only
#   powershell -ExecutionPolicy Bypass -File scripts/install-engines.ps1 -CleanTts    # wipe tts first
#
# The script is intentionally resumable: every download lands as .part and is renamed only when
# complete, so an interrupted run re-downloads at most the broken piece.
param(
    [ValidateSet('both', 'tts', 'kws')]
    [string]$Engine = 'both',

    [switch]$CleanTts
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$engines = Join-Path $root 'engines'
New-Item -ItemType Directory -Force -Path $engines | Out-Null

# Downloads have to survive a flaky link: a truncated body must NOT look like success. Invoke-WebRequest
# silently returned a 670MB file for a 999MiB asset and exit code 0 — the corruption only showed up later
# when tar refused the archive. curl (-C - resumes, --fail surfaces HTTP errors) is the primary path;
# the fallback verifies the body length against the response header before accepting the file.
function Get-Url {
    param([string]$Url, [string]$Destination)

    $tempPath = "$Destination.part"
    Write-Host "download $Url"

    $curl = Get-Command curl.exe -ErrorAction SilentlyContinue
    if ($curl) {
        & $curl.Source -L --fail --retry 5 --retry-delay 5 -C - -o $tempPath $Url
        if ($LASTEXITCODE -ne 0) {
            throw "curl failed with exit code $LASTEXITCODE for $Url (partial file left at $tempPath for a retry)"
        }
    } else {
        $response = Invoke-WebRequest -Uri $Url -OutFile $tempPath -UseBasicParsing -PassThru
        $expected = $response.Headers['Content-Length']
        $actual = (Get-Item $tempPath).Length
        if ($expected -and [long]$expected -ne $actual) {
            throw "truncated download: $actual of $expected bytes for $Url (partial file left at $tempPath)"
        }
    }

    Move-Item -Force $tempPath $Destination
}

# ---- TTS: HaujetZhao/Qwen3-TTS-GGUF pre-built (Vulkan, no CUDA runtime required) ----
if ($Engine -in 'both', 'tts') {
    $tts = Join-Path $engines 'tts'
    if ($CleanTts -and (Test-Path $tts)) {
        Write-Host "removing existing tts engine"
        Remove-Item -Recurse -Force $tts
    }

    New-Item -ItemType Directory -Force -Path $tts | Out-Null

    $repo = 'HaujetZhao/Qwen3-TTS-GGUF'
    if (-not (Test-Path (Join-Path $tts 'qwen3_tts_gguf'))) {
        $zip = Join-Path $tts 'gui.part.zip'
        # The vulkan build: 130MB, works on any DirectML/Vulkan-capable GPU including NVIDIA.
        Get-Url "https://github.com/$repo/releases/download/v0.2/Qwen3-TTS-GUI-vulkan-20260905.zip" $zip
        Write-Host "unpack (this takes a minute)"
        Expand-Archive -Path $zip -DestinationPath $tts -Force
        Remove-Item $zip
        # The zip ships qwen3_tts_gguf/ at its root; verify the layout matches tts-server.py's path setup.
        if (-not (Test-Path (Join-Path $tts 'qwen3_tts_gguf'))) {
            throw "qwen3_tts_gguf not found after unpacking — check the zip layout under $tts"
        }
    }

    # Models: the exporter's OUTPUT_DIR contains all six files tts-server needs. HuggingFace mirror
    # via hf-mirror.com (GitHub raw is unreachable from this machine; modelscope is the alternative).
    $models = Join-Path $tts 'model'
    if (-not (Test-Path (Join-Path $models 'qwen3_tts_predictor.q8_0.gguf'))) {
        throw @"
TTS engine runtime is in place, but the exported model files are not: $models
They come from running the exporter inside the repo (stages 1–3 of its README) or from the
companion export bundle. Place these six files into engines/tts/model/:
    qwen3_tts_talker.q5_k.gguf        (~500MB)
    qwen3_tts_predictor.q8_0.gguf     (~120MB)
    qwen3_tts_decoder.fp16.onnx       (~60MB)
    qwen3_tts_codec_encoder.fp16.onnx (~30MB)
    qwen3_tts_speaker_encoder.fp16.onnx (~10MB)
    tokenizer.json                    (~10MB)
"@
    }

    # FFmpeg must be on PATH for reference-WAV reading (upstream requirement).
    if (-not (Get-Command ffmpeg -ErrorAction SilentlyContinue)) {
        Write-Warning "ffmpeg not on PATH — TTS reference audio (cloning) will fail until it is."
    }

    Write-Host "tts engine ready: engines/tts (qwen3_tts_gguf + models/)"
}

# ---- KWS+ASR: sherpa-onnx runtime + SenseVoice + zipformer KWS ----
if ($Engine -in 'both', 'kws') {
    $kws = Join-Path $engines 'kws-asr'
    $models = Join-Path $kws 'models'
    New-Item -ItemType Directory -Force -Path $kws | Out-Null

    if (-not (Test-Path (Join-Path $kws 'sherpa-onnx'))) {
        throw @"
sherpa-onnx runtime not found at $kws\sherpa-onnx.
Build it from pip once, then copy the whole venv:
    python -m venv engines\kws-asr\sherpa-onnx
    engines\kws-asr\sherpa-onnx\Scripts\pip install sherpa-onnx numpy
(The wheel is Windows CPU; no CUDA needed — ASR stays CPU per the survey.)
"@
    }

    # SenseVoice int8 (~230MB) from the k2-fsa release bundle. The archive is ~999MiB, so a flaky link
    # shows up as a truncated bz2 — curl's --fail plus the size check in Get-Url are what make that loud.
    $sense = Join-Path $models 'sensevoice'
    if (-not (Test-Path (Join-Path $sense 'model.int8.onnx'))) {
        $archive = Join-Path $models 'sensevoice.tar.bz2'
        New-Item -ItemType Directory -Force -Path $models | Out-Null
        Get-Url 'https://github.com/k2-fsa/sherpa-onnx/releases/download/asr-models/sherpa-onnx-sense-voice-zh-en-ja-ko-yue-2024-07-17.tar.bz2' $archive
        tar -xjf $archive -C $models
        $extracted = Join-Path $models 'sherpa-onnx-sense-voice-zh-en-ja-ko-yue-2024-07-17'
        if (-not (Test-Path (Join-Path $extracted 'model.int8.onnx'))) {
            throw "extraction produced no model.int8.onnx under $extracted"
        }
        if (Test-Path $sense) { Remove-Item -Recurse -Force $sense }
        Rename-Item $extracted 'sensevoice'
        Remove-Item $archive
    }

    # KWS zipformer (~32MB archive). The official bundle ships several epoch/chunk variants; the shell
    # picks one (chunk-8 if present, else chunk-16 — this bundle only has chunk-16, ~320ms latency).
    $kwsModels = Join-Path $models 'kws'
    if (-not (Test-Path $kwsModels)) {
        $archive = Join-Path $models 'kws.tar.bz2'
        Get-Url 'https://github.com/k2-fsa/sherpa-onnx/releases/download/kws-models/sherpa-onnx-kws-zipformer-wenetspeech-3.3M-2024-01-01.tar.bz2' $archive
        tar -xjf $archive -C $models
        $extracted = Join-Path $models 'sherpa-onnx-kws-zipformer-wenetspeech-3.3M-2024-01-01'
        if (-not (Test-Path (Join-Path $extracted 'tokens.txt'))) {
            throw "extraction produced no tokens.txt under $extracted"
        }
        if (Test-Path $kwsModels) { Remove-Item -Recurse -Force $kwsModels }
        Rename-Item $extracted 'kws'
        Remove-Item $archive
        Write-Warning @'
KWS models installed. The bundle ships keywords.txt with 8 wake words (小爱同学, 小艺小艺, 小米小米, …).
Edit engines/kws-asr/models/kws/keywords.txt to add your own: one pinyin-token line per word ending in @显示名.
Generate the pinyin with the venv's CLI:
  engines\kws-asr\sherpa-onnx\Scripts\sherpa-onnx-cli.exe text2token `
    --tokens engines\kws-asr\models\kws\tokens.txt --tokens-type ppinyin raw.txt keywords.txt
Keep the file BOM-free (a leading EF BB BF is parsed as garbage).
'@
    }

    # The fp32 companion (model.onnx, ~894MB) is deliberately dropped: the engine runs CPU + int8.
    $full = Join-Path $sense 'model.onnx'
    if (Test-Path $full) {
        Remove-Item $full
    }

    Write-Host "kws engine ready: engines/kws-asr (sherpa-onnx venv + sensevoice + kws)"
}

Write-Host "install-engines OK"
