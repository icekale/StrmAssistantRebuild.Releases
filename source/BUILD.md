# 从源码构建 StrmAssistantRebuild.dll

这份源码对应发布在本仓库根目录的 `StrmAssistantRebuild.dll`（版本见 `latest.json`）。按 GPL-3.0 公开。

## 需要准备

- .NET SDK 10（构建工具链；插件目标框架是 `net6.0`）。
- Python 3。
- **Emby Server 4.10.2.0 的参考程序集**，放到 `refs/`。这些文件属于 Emby，本仓库不分发。从你自己安装的 Emby Server `system/` 目录复制以下文件，并核对 SHA256：

| 文件 | SHA256 |
|---|---|
| Emby.Media.Model.dll | 6a0592d862d410dbb36aa9325c03fe99ddebe3fd4c4b8e1bd9cad05eec123037 |
| Emby.Web.GenericEdit.dll | d5649d7dc93a997c2610ce5534e381fe97dfd4cbdad91a32ff035423899613c2 |
| Emby.Web.GenericUI.dll | 177b715aca16171cde160c6c8eff01d1e0e7c835e707788b1cbda6d52cab1786 |
| MediaBrowser.Common.dll | 9feaab42f6a5cd75831952d9cb73f7cb0176394f8c02df64ef54e6b4cfbf7817 |
| MediaBrowser.Controller.dll | eaa6b9edebb21fe3af9c272def8c913d9ec6b177b552e1b789fee15cb59289da |
| MediaBrowser.Model.dll | 2b7da29239811f8c035784df7b3f48a1668e9537784b6524dde43b3905572617 |

插件只在编译时引用它们（`Private=false`），不会把它们打进 DLL。

## 步骤

```bash
python3 tools/HarmonyCompatibility/restore.py   # Harmony 2.4.2、ILRepack 2.0.48（NuGet，带 SHA256 校验）
python3 tools/Pinyin/restore.py                  # ToolGood.Words 3.1.0.3（NuGet，带 SHA256 校验）
dotnet build StrmAssistantRebuild.csproj -c Release
```

`restore.py` 在 macOS arm64 上还会下载一份隔离的 .NET 6.0.36 运行时，ILRepack 合并时用它解析框架引用；其它平台需要自己准备对应的运行时，并调整 `StrmAssistantRebuild.csproj` 里 `/lib:` 指向的路径。

产物在 `bin/Release/net6.0/StrmAssistantRebuild.dll`：一个单独的 DLL，已内联 Harmony 和 ToolGood.Words。

构建是可复现的：编译时只按 HintPath 解析引用，开启确定性编译和路径映射，也不写入 git/SourceLink 信息，所以输出与源码所在目录、是否有 `.git` 都无关。在 macOS arm64 上使用 .NET SDK 10.0.402，参考程序集与上表一致时，产物应与仓库根目录的 `StrmAssistantRebuild.dll` 逐字节相同，SHA256 见 `latest.json`。我们已在三个不同目录构建验证过。其它 SDK 版本或平台不保证得到相同字节。

## 说明

- `artifacts/rebuild/native-dashboard/` 下面是 Emby 4.10.2.0 网页客户端的原始文件。插件运行时用它们比对宿主文件，只有内容完全一致才替换成 `assets/dashboard/` 里的改写版，否则保留原生文件。
- `scripts/make-release-manifest.py` 用来生成本仓库根目录的 `latest.json`。
- 开发期的测试、逆向分析和验证证据不在这份源码里。
