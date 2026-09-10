# 元信旁路由 (GatewaySwitch)

一键切换 Wi-Fi 默认网关到旁路由（192.168.3.187），并测试 ChatGPT 连通性。

## 功能

- 一键将 Wi-Fi 网关和 DNS 切换到旁路由
- 一键恢复原始网络配置
- ChatGPT 延迟测试（TCP 连接 chatgpt.com:443）
- 切换网关后自动测试连通性
- 自动检测网关状态变化
- 系统托盘最小化，后台常驻

## 后台常驻与恢复保护（Windows）

- 状态检查在后台执行，同一时间只运行一轮检查或一次网络切换；托盘隐藏后仍继续检查，但不会阻塞界面线程。
- 启动或状态读取失败时显示“网络状态待确认”，不再默认判定为“配置待恢复”。每次点击电源都会先重新读取网关和备份状态，再决定启用还是恢复；操作成功、失败或回滚后都重新同步界面。关闭自动启用不影响手动切换。
- SSID 命令限时 5 秒，PowerShell 命令限时 20 秒，输出读取另限时 2 秒。命令进程由 Windows Job Object 管理，超时或应用退出会清理已纳入管理的子进程。
- 指定 Wi-Fi 连续稳定至少 10 秒才触发自动操作；无法读取 SSID、短暂断线或唤醒中的未知状态不触发恢复，也不会清除手动关闭标记。
- 关闭设置中的“自动启用”会同时暂停自动启用和自动恢复。自动操作失败不弹阻塞式错误框，按 15、30、60、120 秒退避；连续失败 5 次暂停，手动操作、保存设置或稳定切换到其他 Wi-Fi 后重置。
- 原始备份记录网卡 GUID、SSID、IPv4 子网、两类 DNS 获取方式，以及被修改地址族的默认路由、路由度量值和持久存储。已有待恢复备份时不会再次启用或覆盖备份；界面会提示点击电源恢复。
- 启用中途失败会尝试回滚；恢复验证通过后才删除备份。网络已经变化时不写回旧网关；仅原来使用自动 DNS 的相关地址族允许跨网络清理。旧网络使用静态 DNS 时保留备份并提示回原网络处理。
- 未配置 IPv6 旁路由时，不修改 IPv6 默认路由和 IPv6 DNS。
- 托盘退出会等待正在执行的检查或网络修改结束，并在有配置待恢复时询问“恢复后退出”或“保留配置退出”。窗口右上角关闭仍只是隐藏到托盘。系统强制终止或关机不能保证完成恢复。

**升级提醒：**旧版备份缺少可靠的网络身份和完整路由信息，新版不会猜测或自动覆盖这类备份。如果旧版仍处于启用状态，请先用旧版恢复网络，再退出旧版并运行新版。备份保存在当前用户注册表 `Software\YuanxinGatewaySwitch\Original`，不要在网络尚未恢复时直接删除它。

后台状态表示路由和 DNS 配置，不代表旁路由健康状态。旁路由断电或上游网络故障仍需手动恢复；本次没有增加根据 ChatGPT 测试结果自动切换网络的策略。

## 环境要求

- Windows 10 / 11
- .NET Framework 4.8（Windows 10+ 自带）
- 管理员权限（修改网关需要）

## 本地构建

### 方式一：项目本地构建脚本（推荐）

需要安装 [.NET SDK 8.0+](https://dotnet.microsoft.com/download/dotnet/8.0)：

```bash
winget install Microsoft.DotNet.SDK.8
```

首次构建前先运行 `dotnet restore GatewaySwitch.csproj`，之后使用项目指定脚本：

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\local-build.ps1
```

产物位于 `bin\Release\GatewaySwitch-yyyy-MM-dd-HHmmss\GatewaySwitch.exe`，每次构建输出到独立的时间戳目录。

### 方式二：csc.exe（无需额外安装）

Windows 自带的 .NET Framework 编译器：

```cmd
"C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /nologo /target:winexe /platform:x64 /optimize+ /out:dist\GatewaySwitch.exe /win32icon:assets\app.ico /win32manifest:GatewaySwitch.manifest /reference:System.dll /reference:System.Drawing.dll /reference:System.Windows.Forms.dll GatewaySwitch.cs
```

产物位于 `dist\GatewaySwitch.exe`。

## CI/CD

项目使用 GitHub Actions 自动构建。

- 推送到 main 分支：自动编译，产物可在 Actions Artifacts 下载
- 打 tag（如 `v1.1.0`）：自动编译并创建 GitHub Release

发版流程：

```bash
git tag v1.1.0
git push origin v1.1.0
```

## 使用说明

1. 以管理员身份运行 `GatewaySwitch.exe`
2. 点击电源按钮切换到旁路由
3. 切换成功后自动测试 ChatGPT 连通性
4. 也可手动点击"测试延迟"按钮检测
5. 再次点击电源按钮恢复原始网关
6. 点击关闭按钮会最小化到托盘，右键托盘图标可退出
