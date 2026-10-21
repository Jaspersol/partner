# 发布流程

**简体中文** | [繁體中文](./release.zh-TW.md) | [English](./release.en.md)

返回 [文档索引](./README.md)。

## 一句话

打一个 `v*` tag，GitHub Actions 编译安装包、把引擎权重包和安装包一起发到 release。

## 发布些什么

| 资产 | 谁产出 | 放在哪 |
|---|---|---|
| `Partner_<版本>_x64-setup.exe` | `.github/workflows/release.yml` 的 `app` job | 应用 release（tag `vX.Y.Z`） |
| `SHA256SUMS.txt`、`engines-manifest.json`、`THIRD-PARTY-NOTICES.md` | 同上 | 同上 |
| `partner-engine-tts-a-runtime.zip`、`-b-talker`、`-c-codec`、`partner-engine-kws.zip` | `scripts/pack-engines.ps1` | 引擎 release（tag `engines-vN`） |

安装包里**不含**引擎：装完在「设置 → 本地引擎」点下载，地址与 SHA-256 在编译期就固定好了。

## 触发方式

| 方式 | 做什么 |
|---|---|
| push tag `v0.2.0` | 完整发布：引擎包（若开了 runner）+ 安装包 + release |
| Actions → release → Run workflow，`tag` 留空 | 只编译，产物进 Actions artifact，不发 release |
| 同上，填 `tag` + 勾 `publish` | 按指定 tag 发布 |

版本号由 tag 决定：workflow 用 `scripts/set-version.mjs` 把 `0.2.0` 写进 `src-tauri/tauri.conf.json`、`Cargo.toml`、`ui/package.json`，所以 tag 和安装包版本不会走偏。

## 引擎包：为什么默认只在本机打

权重和 venv 不进 git（`engines/` 只有两个服务脚本），GitHub 的托管 runner 上没有 2.3G 模型，**打不出引擎包**。所以：

- **默认**：在装有 `engines/` 的开发机上跑一次

  ```powershell
  powershell -ExecutionPolicy Bypass -File scripts/pack-engines.ps1 -UploadTag engines-v1
  ```

  它会重打可搬运的 Python 运行时、切包、算 SHA-256、写 `NOTICE/`、上传到 `engines-v1`，并把要粘进 `EnginePackage.cs` 的地址表打印出来。

- **自动化**：注册一台 self-hosted runner，标签 `self-hosted, windows, x64`，机器上有 `gh`、`engines/tts/model`，然后设仓库变量 `ENGINE_RUNNER=true`。workflow 的 `engines` job 就会在打 tag 时先打包上传，`app` job 再接着编译；仓库变量 `ENGINE_TARGET_BRANCH`（默认 `master`）决定引擎 tag 建在哪个分支上。

引擎更新开新 tag（`engines-v2`），不要覆盖旧 tag：已经装了旧包的机器半途重试会 SHA-256 不一致。

## 安装包怎么锁定引擎包

`app` job 会：

1. `gh release download engines-v1 --pattern engines-manifest.json`；
2. `node scripts/apply-engines-manifest.mjs dist/engines/engines-manifest.json --check` —— 把清单里的地址和 SHA-256 与 `EnginePackage.cs` 里的常量比对，**不一致就让构建失败**。

所以改了引擎包必须同步常量；手动同步时去掉 `--check` 直接运行该脚本即可（它会改写 `EnginePackage.cs` 里两个数组，保持缩进和换行风格）。

## 分支与保护

| 分支 | 用途 |
|---|---|
| `master` | 默认分支、受保护：只能通过 PR 合入，禁止强推、禁止删除，要求线性历史 |
| `dev` | 日常开发与试跑 |

发布 tag 打在 `master` 上：`git checkout master && git pull && git tag v0.2.0 && git push github v0.2.0`。

## 本地等价命令

workflow 做的每一步都能在本机跑：

```bash
node scripts/check-docs.mjs
node scripts/check-css-names.mjs
npm --prefix ui ci
node scripts/apply-engines-manifest.mjs dist/engines/engines-manifest.json --check
node scripts/set-version.mjs v0.2.0
dotnet build Partner.sln -c Release
dotnet publish src/Partner.Api -c Release -r win-x64 --self-contained true \
  -o src/Partner.Api/bin/Release/net8.0/win-x64/publish
npm --prefix ui exec tauri build
```

生成的安装包在 `src-tauri/target/release/bundle/nsis/`。

## 已知

- 单元测试步骤标了 `continue-on-error`：仓库里有两个与发布无关的既有失败（`AgentsEndpointTests.The_round_count_and_the_prompt_have_limits`、`SettingsEndpointTests.Growing_the_context_does_not_compact`），修好后可以去掉这个豁免。
- `tauri.conf.json` 的 `bundle.targets` 只有 `nsis`，所以只出 exe 安装包，没有 msi。
- 引擎包超大（最大 1.16GB），单资产仍在 GitHub 的 2GiB 上限内；再大就必须继续切包。
