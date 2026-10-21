# Packs the local engines into the release assets the app downloads from GitHub.
#
# The models are not in git (see .gitignore): they are ~3 GB and land in engines/ per machine via
# scripts/install-engines.ps1. Release assets are uploaded from this working tree, not from the repo, so
# nothing has to be committed - this script turns engines/ into the zips EnginePackage fetches.
#
# Why several files: GitHub rejects a release asset of 2 GiB or more and TTS is ~3 GB, so its runtime and its
# models are split into zips that each unpack into the engines root, in the order the app is told to fetch them.
#
# Why the runtime is rebuilt: a venv (tts/.venv, kws-asr/sherpa-onnx) carries an absolute `home` in
# pyvenv.cfg and only runs on the machine that built it. The pack ships a relocatable embeddable CPython at
# <engine>/python/ instead - the path EnginePackage already prefers - so a fresh machine can start the engine.
#
# Usage:
#   powershell -ExecutionPolicy Bypass -File scripts/pack-engines.ps1 -DryRun          # sizes only, no writes
#   powershell -ExecutionPolicy Bypass -File scripts/pack-engines.ps1                 # write dist/engines/*.zip
#   powershell -ExecutionPolicy Bypass -File scripts/pack-engines.ps1 -UploadTag engines-v1
#   powershell -ExecutionPolicy Bypass -File scripts/pack-engines.ps1 -SkipRuntime    # reuse the staged runtime
#
# Output (dist/engines/):
#   partner-engine-tts-a-runtime.zip   interpreter + server script + bundled internal runtime
#   partner-engine-tts-b-talker.zip    talker + predictor weights
#   partner-engine-tts-c-codec.zip     decoder + codec + speaker encoder + embeddings + tokenizer
#   partner-engine-kws.zip             wake word + ASR engine
param(
    [string]$EnginesDir = 'engines',
    [string]$OutDir = 'dist/engines',
    [string]$PythonVersion = '3.13.9',
    [switch]$SkipRuntime,
    [switch]$DryRun,
    [string]$UploadTag,
    [string]$Target = 'dev'
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem

$root = Split-Path -Parent $PSScriptRoot
$engines = Join-Path $root $EnginesDir
$out = Join-Path $root $OutDir
$stage = Join-Path $out '.stage'
$cache = Join-Path $out '.cache'
$limit = 2147483647  # GitHub: "Each file included in a release must be under 2 GiB."

function Fail([string]$message) {
    Write-Host "error: $message" -ForegroundColor Red
    exit 1
}

# What the app calls "installed" and what the engine needs to start are different lists: EnginePackage.Inspect
# checks four core model files, while the engine also loads the codec, the speaker encoder and the embeddings.
# The packs are validated against this stricter list so a missing half fails here and not at runtime.
$ttsFiles = @(
    'tts/tts-server.py',
    'tts/model/qwen3_tts_talker.q5_k.gguf',
    'tts/model/qwen3_tts_predictor.q8_0.gguf',
    'tts/model/qwen3_tts_decoder.fp16.onnx',
    'tts/model/qwen3_tts_codec_encoder.fp16.onnx',
    'tts/model/qwen3_tts_speaker_encoder.fp16.onnx',
    'tts/model/tokenizer.json'
)
$kwsFiles = @(
    'kws-asr/asr-server.py',
    'kws-asr/models/sensevoice/model.int8.onnx',
    'kws-asr/models/sensevoice/tokens.txt',
    'kws-asr/models/kws/tokens.txt'
)

# Voice anchors: a directory of JSON anchors, not one known file name; the pack has to cover all of them.
$requiredDirs = @('tts/model/embeddings')

# Everything the app checks (Inspect) plus the relocatable interpreters the packs provide.
$expectedFiles = @($ttsFiles + $kwsFiles + @('tts/python/python.exe', 'kws-asr/python/python.exe')) |
    ForEach-Object { $_ -replace '\\', '/' }

# The interpreters do not exist yet: building the runtime is what creates them.
$missing = @(($ttsFiles + $kwsFiles + $requiredDirs) | Where-Object { -not (Test-Path (Join-Path $engines $_)) })
if ($missing.Count -gt 0) {
    Fail "engines/ is incomplete (run scripts/install-engines.ps1 first):`n  $($missing -join "`n  ")"
}

New-Item -ItemType Directory -Force -Path $out | Out-Null
New-Item -ItemType Directory -Force -Path $stage | Out-Null
New-Item -ItemType Directory -Force -Path $cache | Out-Null
New-Item -ItemType Directory -Force -Path (Join-Path $out '.notice') | Out-Null

# A relocatable interpreter: the embeddable CPython zip plus the packages the venv had installed. The venv's
# own python.exe resolves its stdlib through pyvenv.cfg's absolute home, so it cannot travel.
function Build-Runtime([string]$name, [string]$sitePackages) {
    $target = Join-Path $stage "$name-runtime"
    if ((Test-Path (Join-Path $target 'python.exe')) -and $SkipRuntime) {
        Write-Host "runtime ${name}: reusing the staged copy"
        return $target
    }

    $zip = Join-Path $cache "python-$PythonVersion-embed-amd64.zip"
    if (-not (Test-Path $zip)) {
        $url = "https://www.python.org/ftp/python/$PythonVersion/python-$PythonVersion-embed-amd64.zip"
        Write-Host "download $url"
        & curl.exe -L --fail --retry 3 -o $zip $url
        if ($LASTEXITCODE -ne 0) { Fail "could not download the embeddable CPython: $url" }
    }

    if (Test-Path $target) { Remove-Item -Recurse -Force $target }
    New-Item -ItemType Directory -Force -Path $target | Out-Null
    Expand-Archive -Path $zip -DestinationPath $target -Force

    # An embeddable runtime ignores site-packages until ._pth names it and enables site.
    $pth = Join-Path $target "python$($PythonVersion.Split('.')[0])$($PythonVersion.Split('.')[1])._pth"
    @("python$($PythonVersion.Split('.')[0])$($PythonVersion.Split('.')[1]).zip", '.', 'Lib\site-packages', 'import site') |
        Set-Content -Path $pth -Encoding ASCII

    $dest = Join-Path $target 'Lib/site-packages'
    New-Item -ItemType Directory -Force -Path $dest | Out-Null
    $source = Join-Path $engines $sitePackages
    if (-not (Test-Path $source)) { Fail "missing $sitePackages (is the venv installed?)" }

    $skip = @('pip', 'pip-*', 'setuptools', 'setuptools-*', 'pkg_resources')
    Get-ChildItem -Path $source -Force | Where-Object {
        $entry = $_.Name
        -not ($skip | Where-Object { $entry -like $_ })
    } | ForEach-Object {
        Copy-Item -Recurse -Force $_.FullName (Join-Path $dest $_.Name)
    }

    # __pycache__ is regenerated on first import and would only add thousands of files to the archive.
    Get-ChildItem -Path $dest -Recurse -Directory -Filter '__pycache__' -ErrorAction SilentlyContinue |
        Remove-Item -Recurse -Force -ErrorAction SilentlyContinue

    return $target
}

function Test-Runtime([string]$label, [string]$python, [string[]]$modules, [string]$extraPath) {
    # An embeddable runtime ships a ._pth file, which makes it ignore PYTHONPATH; the extra root has to be
    # appended inside the process. tts-server.py does the same thing for internal/ at startup.
    $prefix = if ($extraPath) { "import sys;sys.path.append(r'$extraPath');" } else { '' }
    $probe = $prefix + 'import importlib,sys;[importlib.import_module(m) for m in sys.argv[1:]]'
    & $python -c $probe @modules
    if ($LASTEXITCODE -ne 0) { Fail "the packed interpreter cannot import $($modules -join ', '): $python" }
    Write-Host "runtime ok: $label ($($modules -join ', '))"
}

# Each pack is a list of { Source; Entry }: Entry is the path inside the zip, always relative to the engines
# root because the app unpacks straight into it.
function New-Entry([string]$source, [string]$entry) {
    [pscustomobject]@{ Source = $source; Entry = ($entry -replace '\\', '/') }
}

function Add-Tree([System.Collections.Generic.List[object]]$list, [string]$sourceDir, [string]$entryPrefix, [string]$exclude) {
    if (-not (Test-Path $sourceDir)) { Fail "missing directory $sourceDir" }
    $base = (Resolve-Path $sourceDir).Path
    Get-ChildItem -Path $sourceDir -Recurse -File -Force | ForEach-Object {
        $relative = $_.FullName.Substring($base.Length).TrimStart('\', '/')
        if ($relative -match '__pycache__') { return }
        if ($exclude -and $relative -match $exclude) { return }
        $list.Add((New-Entry $_.FullName "$entryPrefix/$relative"))
    }
}

function New-Pack([string]$name, [System.Collections.Generic.List[object]]$entries) {
    $path = Join-Path $out $name

    if ($DryRun) {
        $bytes = 0L
        foreach ($item in $entries) { $bytes += (Get-Item $item.Source).Length }
        Write-Host ("{0,-36} {1,6} files {2,9:N0} MB raw" -f $name, $entries.Count, ($bytes / 1MB))
        if ($bytes -ge $limit) { Fail "$name is $([math]::Round($bytes / 1GB, 2)) GB raw, split it further" }
        return $null
    }

    if (Test-Path $path) { Remove-Item -Force $path }
    $archive = [System.IO.Compression.ZipFile]::Open($path, 'Create')
    try {
        foreach ($item in $entries) {
            # gguf/onnx/dll are already compressed; deflating them again only costs time.
            $level = if ($item.Entry -match '\.(gguf|onnx|dll|exe|pyd|bin|zip)$') {
                [System.IO.Compression.CompressionLevel]::NoCompression
            } else {
                [System.IO.Compression.CompressionLevel]::Optimal
            }
            [void][System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $item.Source, $item.Entry, $level)
        }
    } finally {
        $archive.Dispose()
    }

    $size = (Get-Item $path).Length
    if ($size -ge $limit) {
        Fail "$name is $([math]::Round($size / 1GB, 2)) GB, over GitHub's 2 GiB per-asset limit; split it further"
    }

    $sha = (Get-FileHash -Path $path -Algorithm SHA256).Hash.ToLowerInvariant()
    Write-Host ("{0,-36} {1,9:N0} MB  sha256={2}" -f $name, ($size / 1MB), $sha)
    return [pscustomobject]@{ Name = $name; Bytes = $size; Sha256 = $sha }
}

function Get-LicenseRows([string[]]$roots) {
    $rows = [System.Collections.Generic.List[string]]::new()
    foreach ($path in $roots) {
        if (-not (Test-Path $path)) { continue }
        foreach ($info in Get-ChildItem -Path $path -Directory -Filter '*.dist-info' -ErrorAction SilentlyContinue) {
            $meta = Join-Path $info.FullName 'METADATA'
            if (-not (Test-Path $meta)) { continue }
            $name = ''
            $version = ''
            $license = ''
            foreach ($line in Get-Content -Path $meta -TotalCount 80) {
                if ($line -match '^Name: (.+)$') { $name = $Matches[1] }
                elseif ($line -match '^Version: (.+)$') { $version = $Matches[1] }
                elseif ($line -match '^License-Expression: (.+)$') { $license = $Matches[1] }
                elseif ([string]::IsNullOrWhiteSpace($license) -and $line -match '^License: (.+)$') { $license = $Matches[1] }
            }
            if ($name) { $rows.Add(("{0} {1} -- {2}" -f $name, $version, ($license -replace '\s+', ' ')).Trim()) }
        }
    }
    return @($rows | Sort-Object -Unique)
}

function Add-Notice(
    [System.Collections.Generic.List[object]]$list,
    [string]$pack,
    [string[]]$licenseRoots,
    [string[]]$card) {

    $list.Add((New-Entry (Join-Path $root 'LICENSE') 'NOTICE/LICENSE-partner.txt'))
    Add-Tree $list (Join-Path $root 'third-party') 'NOTICE/LICENSES'

    $table = Get-LicenseRows $licenseRoots
    $tablePath = Join-Path $out ".notice/$pack-THIRD-PARTY-LICENSES.txt"
    @(
        "Third-party packages bundled in $pack",
        '',
        'Every package below keeps its own licence files inside its *.dist-info folder in this pack.',
        'The licence texts of the upstream projects are in NOTICE/LICENSES/.',
        ''
    ) + $table | Set-Content -Path $tablePath -Encoding utf8
    $list.Add((New-Entry $tablePath 'NOTICE/THIRD-PARTY-LICENSES.txt'))

    $cardPath = Join-Path $out ".notice/$pack-MODEL_CARD.md"
    $card | Set-Content -Path $cardPath -Encoding utf8
    $list.Add((New-Entry $cardPath 'NOTICE/MODEL_CARD.md'))
}

# --- tts ---------------------------------------------------------------------------------------------------

$ttsRuntime = Build-Runtime 'tts' 'tts/.venv/Lib/site-packages'
# internal/ holds the qwen3_tts_gguf package (not the venv), so the import test runs through it too.
Test-Runtime 'tts' (Join-Path $ttsRuntime 'python.exe') `
    @('numpy', 'onnxruntime', 'tokenizers', 'soundfile', 'sounddevice', 'qwen3_tts_gguf.inference') `
    (Join-Path $engines 'tts/internal')

$a = [System.Collections.Generic.List[object]]::new()
foreach ($name in @('tts-server.py', 'requirements.txt')) {
    $a.Add((New-Entry (Join-Path $engines "tts/$name") "tts/$name"))
}
Add-Tree $a $ttsRuntime 'tts/python'
Add-Tree $a (Join-Path $engines 'tts/internal') 'tts/internal' '^(log(/|$)|.*\.log$)'
Add-Tree $a (Join-Path $engines 'tts/output/elaborate') 'tts/output/elaborate'
Add-Notice $a 'partner-engine-tts-a-runtime.zip' `
    @((Join-Path $ttsRuntime 'Lib/site-packages'), (Join-Path $engines 'tts/internal')) `
    @(
        '# TTS runtime pack',
        '',
        '## Contents',
        '',
        '- `tts/python/` - embeddable CPython 3.13.9 plus the packages the TTS server imports.',
        '- `tts/tts-server.py` - Partner''s HTTP shell, MIT, see NOTICE/LICENSE-partner.txt.',
        '- `tts/internal/` - the Qwen3-TTS-GGUF GUI runtime (MIT, Copyright (c) 2026 HaujetZhao), which also carries the `qwen3_tts_gguf` inference package.',
        '- `tts/output/elaborate/` - voice anchors exported from the Qwen3-TTS built-in timbres (Apache-2.0 weights). No third-party recordings and no user-cloned voices are included.',
        '',
        '## Upstream',
        '',
        '| Component | Source | Licence |',
        '| --- | --- | --- |',
        '| CPython 3.13.9 (embeddable) | https://www.python.org/downloads/windows/ | PSF-2.0 |',
        '| Qwen3-TTS-GGUF GUI runtime | https://github.com/HaujetZhao/Qwen3-TTS-GGUF (v0.2, Qwen3-TTS-GUI-vulkan-20260905.zip) | MIT |',
        '| Qwen3-TTS built-in timbres | https://huggingface.co/Qwen/Qwen3-TTS-12Hz-1.7B-Base | Apache-2.0 |',
        '| PyInstaller bootloader (inside internal/) | https://github.com/pyinstaller/pyinstaller | GPL-2.0 with Bootloader Exception |',
        '',
        '## Modifications',
        '',
        'No upstream file is modified. The interpreter is assembled at packaging time (embeddable distribution',
        'plus the packages already installed in the venv) because a venv records an absolute path in',
        'pyvenv.cfg and cannot be moved to another machine.',
        '',
        'The Qwen3-TTS Base weights are in the sibling -b-talker and -c-codec packs.',
        '',
        '## Python packages inside internal/',
        '',
        'The GUI runtime is a PyInstaller bundle whose package metadata was stripped, so these are listed here',
        'rather than harvested from *.dist-info. Their licence texts are in NOTICE/LICENSES/packages/.',
        '',
        '| Package | Licence |',
        '| --- | --- |',
        '| numpy | BSD-3-Clause |',
        '| scipy | BSD-3-Clause |',
        '| onnxruntime | MIT |',
        '| tokenizers | Apache-2.0 |',
        '| Pillow | HPND |',
        '| soundfile (libsndfile_x64.dll) | BSD-3-Clause; the bundled libsndfile is LGPL-2.1, shipped as a separate DLL, licence text in NOTICE/LICENSES/packages/LGPL-2.1-libsndfile.txt |',
        '| sounddevice (PortAudio) | MIT |',
        '| PyYAML | MIT |',
        '| certifi | MPL-2.0 |',
        '| charset-normalizer | MIT |',
        '| tqdm | MPL-2.0 AND MIT |',
        '| ttkbootstrap | MIT |',
        '| setuptools | MIT |',
        '| llama.cpp / ggml (llama.dll, ggml-vulkan.dll) | MIT |'
    )

$b = [System.Collections.Generic.List[object]]::new()
foreach ($name in @('qwen3_tts_talker.q5_k.gguf', 'qwen3_tts_predictor.q8_0.gguf')) {
    $b.Add((New-Entry (Join-Path $engines "tts/model/$name") "tts/model/$name"))
}
Add-Notice $b 'partner-engine-tts-b-talker.zip' @() @(
    '# Qwen3-TTS talker pack',
    '',
    '## Contents',
    '',
    '- `tts/model/qwen3_tts_talker.q5_k.gguf` - Qwen3-TTS 1.7B Base talker, Q5_K quantised.',
    '- `tts/model/qwen3_tts_predictor.q8_0.gguf` - predictor, Q8_0 quantised.',
    '',
    '## Upstream',
    '',
    '| Component | Source | Licence |',
    '| --- | --- | --- |',
    '| Qwen3-TTS-12Hz-1.7B-Base | https://huggingface.co/Qwen/Qwen3-TTS-12Hz-1.7B-Base | Apache-2.0 |',
    '| GGUF export | https://huggingface.co/Solidaxel/Qwen3-TTS-GGUF | Apache-2.0 |',
    '| Quantisation tooling | https://github.com/HaujetZhao/Qwen3-TTS-GGUF | MIT |',
    '',
    '## Modifications',
    '',
    'The upstream Base weights were converted to GGUF and quantised to Q5_K / Q8_0 upstream (Solidaxel with',
    'HaujetZhao''s scripts). This pack redistributes those files unchanged.',
    '',
    'Primary source: Alibaba Group / Qwen team.'
)

$c = [System.Collections.Generic.List[object]]::new()
foreach ($name in @('qwen3_tts_decoder.fp16.onnx', 'qwen3_tts_codec_encoder.fp16.onnx', 'qwen3_tts_speaker_encoder.fp16.onnx', 'tokenizer.json')) {
    $c.Add((New-Entry (Join-Path $engines "tts/model/$name") "tts/model/$name"))
}
Add-Tree $c (Join-Path $engines 'tts/model/embeddings') 'tts/model/embeddings'
Add-Notice $c 'partner-engine-tts-c-codec.zip' @() @(
    '# Qwen3-TTS codec pack',
    '',
    '## Contents',
    '',
    '- `tts/model/qwen3_tts_decoder.fp16.onnx`',
    '- `tts/model/qwen3_tts_codec_encoder.fp16.onnx`',
    '- `tts/model/qwen3_tts_speaker_encoder.fp16.onnx`',
    '- `tts/model/tokenizer.json`',
    '- `tts/model/embeddings/`',
    '',
    '## Upstream',
    '',
    '| Component | Source | Licence |',
    '| --- | --- | --- |',
    '| Qwen3-TTS-12Hz-1.7B-Base | https://huggingface.co/Qwen/Qwen3-TTS-12Hz-1.7B-Base | Apache-2.0 |',
    '| ONNX export | https://huggingface.co/Solidaxel/Qwen3-TTS-GGUF | Apache-2.0 |',
    '| Export scripts | https://github.com/HaujetZhao/Qwen3-TTS-GGUF | MIT |',
    '',
    '## Modifications',
    '',
    'Exported to ONNX at fp16 upstream (Solidaxel with HaujetZhao''s scripts). This pack redistributes those',
    'files unchanged.',
    '',
    'Primary source: Alibaba Group / Qwen team.'
)

# --- kws ---------------------------------------------------------------------------------------------------

$kwsRuntime = Build-Runtime 'kws' 'kws-asr/sherpa-onnx/Lib/site-packages'
Test-Runtime 'kws' (Join-Path $kwsRuntime 'python.exe') @('numpy', 'sherpa_onnx', 'pypinyin', 'sentencepiece')

$k = [System.Collections.Generic.List[object]]::new()
$k.Add((New-Entry (Join-Path $engines 'kws-asr/asr-server.py') 'kws-asr/asr-server.py'))
Add-Tree $k $kwsRuntime 'kws-asr/python'
Add-Tree $k (Join-Path $engines 'kws-asr/models') 'kws-asr/models'
Add-Notice $k 'partner-engine-kws.zip' `
    @((Join-Path $kwsRuntime 'Lib/site-packages')) `
    @(
        '# KWS + ASR pack',
        '',
        '## Contents',
        '',
        '- `kws-asr/python/` - embeddable CPython 3.13.9 plus sherpa-onnx.',
        '- `kws-asr/asr-server.py` - Partner''s HTTP shell, MIT, see NOTICE/LICENSE-partner.txt.',
        '- `kws-asr/models/sensevoice/` - SenseVoiceSmall int8.',
        '- `kws-asr/models/kws/` - KWS zipformer, tokens and the wake-word table.',
        '- `kws-asr/models/vad/` - Silero VAD.',
        '',
        '## Upstream',
        '',
        '| Component | Source | Licence |',
        '| --- | --- | --- |',
        '| sherpa-onnx runtime | https://github.com/k2-fsa/sherpa-onnx | Apache-2.0 |',
        '| SenseVoiceSmall (int8 ONNX) | https://github.com/FunAudioLLM/SenseVoice, exported by https://github.com/k2-fsa/sherpa-onnx/releases/tag/asr-models | FunASR Model Open Source Licence v1.1 (Alibaba Group) |',
        '| KWS zipformer (WenetSpeech 3.3M, trained by pkufool) | https://github.com/k2-fsa/sherpa-onnx/releases/tag/kws-models | Apache-2.0 |',
        '| Silero VAD | https://github.com/snakers4/silero-vad | MIT |',
        '| CPython 3.13.9 (embeddable) | https://www.python.org/downloads/windows/ | PSF-2.0 |',
        '',
        '## Attribution',
        '',
        'SenseVoice and SenseVoiceSmall are models of Alibaba Group, published by FunAudioLLM. This pack keeps',
        'the model names and attributes the source and author as required by FunASR Model Open Source Licence',
        'section 2.2; the full text is NOTICE/LICENSES/FunASR-MODEL_LICENSE.txt.',
        '',
        '## Modifications',
        '',
        'No model file is modified. SenseVoice is the int8 ONNX export published by k2-fsa, and the KWS',
        'zipformer is used as published. The interpreter is assembled at packaging time for the same reason as',
        'in the TTS runtime pack: a venv cannot be moved between machines.'
    )

$packs = @(
    (New-Pack 'partner-engine-tts-a-runtime.zip' $a),
    (New-Pack 'partner-engine-tts-b-talker.zip' $b),
    (New-Pack 'partner-engine-tts-c-codec.zip' $c),
    (New-Pack 'partner-engine-kws.zip' $k)
) | Where-Object { $_ }

if ($DryRun) {
    Write-Host "`ndry run: nothing written. Drop -DryRun to produce $OutDir"
    exit 0
}

# The union of the packs has to cover everything the engines need, not just what Inspect checks.
$seen = @{}
foreach ($pack in $packs) {
    $archive = [System.IO.Compression.ZipFile]::OpenRead((Join-Path $out $pack.Name))
    try {
        $names = @($archive.Entries | ForEach-Object { $_.FullName -replace '\\', '/' })
    } finally {
        $archive.Dispose()
    }

    foreach ($required in @('NOTICE/LICENSE-partner.txt', 'NOTICE/MODEL_CARD.md', 'NOTICE/THIRD-PARTY-LICENSES.txt')) {
        if ($names -notcontains $required) { Fail "$($pack.Name) has no $required" }
    }
    if (-not ($names | Where-Object { $_ -like 'NOTICE/LICENSES/*' })) {
        Fail "$($pack.Name) has no licence texts under NOTICE/LICENSES/"
    }

    foreach ($name in $names) { $seen[$name] = $true }
}

$missing = @($expectedFiles | Where-Object { -not $seen.ContainsKey($_) })
foreach ($dir in $requiredDirs) {
    # A directory is covered when at least one entry lives under it; zip files carry no directory entries here.
    if (-not ($seen.Keys | Where-Object { $_ -like "$dir/*" })) { $missing += $dir }
}
if ($missing.Count -gt 0) { Fail "the packs do not add up to a complete engine, missing:`n  $($missing -join "`n  ")" }

$tag = if ($UploadTag) { $UploadTag } else { 'engines-v1' }
$manifest = [pscustomobject]@{
    schema = 1
    tag = $tag
    assets = @($packs | ForEach-Object { [pscustomobject]@{ name = $_.Name; bytes = $_.Bytes; sha256 = $_.Sha256 } })
}
$manifestPath = Join-Path $out 'engines-manifest.json'
$manifest | ConvertTo-Json -Depth 5 | Set-Content -Path $manifestPath -Encoding utf8
Write-Host "`nmanifest: $manifestPath"

$ttsParts = @($packs | Where-Object { $_.Name -like 'partner-engine-tts-*' })
$kwsPart = @($packs | Where-Object { $_.Name -eq 'partner-engine-kws.zip' })[0]
$base = "https://github.com/Jaspersol/partner/releases/download/$tag"

Write-Host @"

Paste into src/Partner.Api/Voice/EnginePackage.cs once the release exists:

    private static readonly (string Url, string Sha256)[] TtsArchives =
    [
$(($ttsParts | ForEach-Object { "        (""$base/$($_.Name)"", ""$($_.Sha256)"")," }) -join "`n")
    ];

    private static readonly (string Url, string Sha256)[] KwsArchives =
    [
        ("$base/$($kwsPart.Name)", "$($kwsPart.Sha256)"),
    ];
"@

if ($UploadTag) {
    $files = @($packs | ForEach-Object { Join-Path $out $_.Name })
    $existing = & gh release list --json tagName -q '.[].tagName' | Where-Object { $_ -eq $UploadTag }
    if (-not $existing) {
        Write-Host "creating release $UploadTag from $Target"
        & gh release create $UploadTag --target $Target --title "Local engines $UploadTag" `
            --notes "Runtime and models for the local voice engines. Each model's licence is in the pack under NOTICE/." @files
        if ($LASTEXITCODE -ne 0) { Fail 'gh release create failed' }
    } else {
        Write-Host "release $UploadTag exists, uploading assets"
        & gh release upload $UploadTag @files --clobber
        if ($LASTEXITCODE -ne 0) { Fail 'gh release upload failed' }
    }
}
