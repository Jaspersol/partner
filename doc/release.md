# 发布流程

**简体中文** | [繁體中文](./release.zh-TW.md) | [English](./release.en.md)

返回 [文档索引](./README.md)。

## 一句话

只有一种 tag：打 `v*` 发安装包。语音引擎不在这个流程里发布，安装包固定下载已发布的 `engines-v1` release。

## 发布些什么

| 资产 | 谁产出 | 放在哪 |
|---|---|---|
| `Partner_<版本>_x64-setup.exe` | `.github/workflows/release.yml` 的 `app` job | 应用 release（tag `vX.Y.Z`） |
| `SHA256SUMS.txt`、`THIRD-PARTY-NOTICES.md` | 同上 | 同上 |

安装包里**不含**引擎：装完在「设置 → 本地引擎」点下载，地址与 SHA-256 来自 `src/Partner.Api/Voice/EnginePackage.cs` 里已提交的常量，指向 `engines-v1` release。

## 触发方式

| 方式 | 做什么 |
|---|---|
| push tag `v0.2.0` | 编译安装包并发 release |
| Actions → release → Run workflow，`tag` 留空 | 只编译，产物进 Actions artifact，不发 release |
| 同上，填 `tag` + 勾 `publish` | 按指定 tag 发安装包 |

版本号由 tag 决定：workflow 用 `scripts/set-version.mjs` 把 `0.2.0` 写进 `src-tauri/tauri.conf.json`、`Cargo.toml`、`ui/package.json`，所以 tag 和安装包版本不会走偏。

## 引擎包

workflow 不打也不发引擎包，所以不需要 self-hosted runner。`engines-v1` 是只读的：已发布的安装包 pin 了它下面文件的 SHA-256，改动任何字节都会让下载在最后校验失败。

这些常量没人再自动生成，所以每次构建（以及本机）都会跑 `scripts/check-engine-pins.mjs` 核对：它读 release 资产在 GitHub 上记录的 SHA-256 digest（不下载 3.4GB），常量打错或资产被换都会当场失败，而不是发一个谁也装不上引擎的安装包。

真要换引擎时，在装有 `engines/` 的机器上手动打包到新 tag，再把 `scripts/pack-engines.ps1` 打印的地址表粘进 `EnginePackage.cs`：

```powershell
powershell -ExecutionPolicy Bypass -File scripts/pack-engines.ps1 -UploadTag engines-v2
```

## 分支与保护

| 分支 | 用途 |
|---|---|
| `master` | 默认分支、受保护：只能通过 PR 合入，禁止强推、禁止删除，要求线性历史 |
| `dev` | 日常开发与试跑 |

发布 tag 打在 `master` 上：`git checkout master && git pull && git tag v0.2.0 && git push github v0.2.0`。

squash 合并会让两边历史分叉：同一批文件在 `master` 和 `dev` 上都算「新增」，下一个 PR 会变成 add/add 冲突。合并完把 `dev` 对齐一次：

```bash
git fetch github
git reset --hard github/master
```

## 本地等价命令

workflow 做的每一步都能在本机跑：

```bash
node scripts/check-docs.mjs
node scripts/check-css-names.mjs
node scripts/check-engine-pins.mjs   # 需要 gh 已登录
npm --prefix ui ci
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
