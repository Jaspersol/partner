# Install or update the local voice engines into engines/.
#
# What each engine costs on disk (see 根目录 本地音频.md for the runtime budget):
#   tts : ~130MB runtime (Qwen3-TTS-GUI vulkan zip) + ~2.4GB of 1.7B-Base model files
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

    # Models: upstream ships the export bundle nowhere — its own "models" release tag carries zero
    # assets, so the six files plus embeddings/ have to come from a re-upload. Solidaxel/Qwen3-TTS-GGUF
    # is a 1.7B-Base export (the only variant mirrored); HuggingFace is unreachable from this network,
    # hf-mirror.com serves the same repo. Embeddings are not optional: AssetsManager raises
    # FileNotFoundError without text_embedding_projected.npy.
    $models = Join-Path $tts 'model'
    $hf = 'https://hf-mirror.com/Solidaxel/Qwen3-TTS-GGUF/resolve/main'
    $modelFiles = @(
        'qwen3_tts_talker.q5_k.gguf'                # 1006MB  LLM backbone (llama.cpp, q5_k)
        'qwen3_tts_predictor.q8_0.gguf'             #  151MB  acoustic predictor
        'qwen3_tts_decoder.fp16.onnx'               #  230MB  renderer (DirectML)
        'qwen3_tts_codec_encoder.fp16.onnx'         #  114MB  cloning
        'qwen3_tts_speaker_encoder.fp16.onnx'       #   24MB  cloning
        'tokenizer.json'                            #   11MB
        'embeddings/text_embedding_projected.npy'   #  622MB
        'embeddings/proj_weight.npy'
        'embeddings/proj_bias.npy'
    ) + (0..15 | ForEach-Object { "embeddings/codec_embedding_$_.npy" })

    foreach ($relative in $modelFiles) {
        $target = Join-Path $models ($relative -replace '/', '\')
        if (Test-Path $target) { continue }
        New-Item -ItemType Directory -Force -Path (Split-Path -Parent $target) | Out-Null
        Get-Url "$hf/$relative" $target
    }

    # tts-server.py needs a real interpreter (the zip's GUI exe is not used by the shell).
    $venv = Join-Path $tts '.venv'
    if (-not (Test-Path (Join-Path $venv 'Scripts\python.exe'))) {
        Write-Host "creating venv (3.13; upstream pins 3.14 but the inference subset installs on 3.13)"
        & py -3.13 -m venv $venv
    }
    & (Join-Path $venv 'Scripts\python.exe') -m pip install --quiet --upgrade pip
    & (Join-Path $venv 'Scripts\pip.exe') install --quiet -r (Join-Path $tts 'requirements.txt')

    # FFmpeg must be on PATH for reference-WAV reading (upstream requirement).
    if (-not (Get-Command ffmpeg -ErrorAction SilentlyContinue)) {
        Write-Warning "ffmpeg not on PATH — TTS reference audio (cloning) will fail until it is."
    }

    Write-Host @"
tts engine ready: engines/tts (internal/ runtime + model/ + .venv)
run it with:
  engines\tts\.venv\Scripts\python.exe engines\tts\tts-server.py --port 45102
"@
}

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
