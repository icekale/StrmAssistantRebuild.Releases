# Strm Assistant Rebuild — 发行仓库

这是 Emby 插件 **Strm Assistant Rebuild** 的发行源。已安装的插件在关于页点"检查更新"时，会读取本仓库的 `latest.json`。

| 文件 | 说明 |
|---|---|
| `latest.json` | 当前稳定版的版本号、SHA256、大小和下载地址 |
| `StrmAssistantRebuild.dll` | 插件本体：单个 DLL，net6.0 / AnyCPU，适用于 Emby Server 4.10.2.0 |
| `source/` | 与该 DLL 对应的完整源码，构建方法见 [source/BUILD.md](source/BUILD.md) |

## 安装

1. 停止 Emby Server，备份 `plugins/` 目录。
2. 把 `StrmAssistantRebuild.dll` 复制到 Emby 的 `plugins/` 目录，核对它的 SHA256 是否与 `latest.json` 中的一致。
3. 启动 Emby Server。

以后插件会在关于页提示有新版本。更新时插件会先下载并校验新 DLL，再原子替换旧文件，同时保留一份旧版备份。**插件不会自动重启 Emby**，需要你自己重启。

## 说明

- 这是 StrmAssistant 的重建版，按 [GPL-3.0](LICENSE) 发布。插件 ID 独立，**不能**替代或升级原版 StrmAssistant / StrmAssistantPro，也不会读取原版的发行源。
- 本插件**不是**原版 3.0.0.57 的完整等价实现，部分功能缺失或行为不同。
- "豆瓣推荐"频道（默认关闭）与原版一样，使用豆瓣 Android App 的接口参数访问豆瓣；豆瓣更换参数后频道会显示为空，不影响其他功能。
- 第三方组件及其许可证见 [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md)。
