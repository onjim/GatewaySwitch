# 元信旁路由 (GatewaySwitch)

一键切换 Wi-Fi 默认网关到旁路由（192.168.3.187），并测试 ChatGPT 连通性。

## 功能

- 一键将 Wi-Fi 网关和 DNS 切换到旁路由
- 一键恢复原始网络配置
- ChatGPT 延迟测试（TCP 连接 chatgpt.com:443）
- 切换网关后自动测试连通性
- 自动检测网关状态变化
- 系统托盘最小化，后台常驻

## 目标网卡选择（Windows）

- 设置中可以选择“自动选择”、指定无线网卡或指定有线网卡；选择会保存到当前用户配置。
- 指定有线网卡后不再要求 SSID，启用、恢复、DHCP 路由守护和健康检查都只操作这块网卡。
- 指定无线网卡时可以填写 SSID，只有该网卡连接到指定 SSID 后才会自动启用；选择“自动选择”时保留旧版按活动网卡判断的行为。
- 如果存在尚未恢复的备份，建议先恢复原网络，再切换目标网卡；程序不会用新网卡覆盖旧备份。

## 后台常驻与恢复保护（Windows）

- 状态检查在后台执行，同一时间只运行一轮检查或一次网络切换；托盘隐藏后仍继续检查，但不会阻塞界面线程。
- 启动或状态读取失败时显示“网络状态待确认”，不再默认判定为“配置待恢复”。每次点击电源都会先重新读取网关和备份状态，再决定启用还是恢复；操作成功、失败或回滚后都重新同步界面。关闭自动启用不影响手动切换。
- SSID 命令限时 5 秒，PowerShell 命令限时 20 秒，输出读取另限时 2 秒。命令进程由 Windows Job Object 管理，超时或应用退出会清理已纳入管理的子进程。
- 原网卡的连接状态、SSID、子网连续稳定至少 10 秒才触发自动操作。确认 Wi-Fi 已断开（包括切到有线或没有活动网络）时，只清理备份对应的原网卡；无线网卡仍显示已连接但无法读取 SSID 时视为未知，不自动修改网络。短暂断线、唤醒不清除同一 Wi-Fi 的手动关闭标记。
- “自动启用”只控制连接指定 Wi-Fi 后的自动开启；关闭它不会停用已接管网络的路由守护、健康检查及离网清理。自动操作失败不弹阻塞式错误框，按 15、30、60、120 秒退避；连续失败 5 次暂停，手动操作、保存设置或稳定改变网络后重置。
- 原始备份记录网卡 GUID、SSID、IPv4 子网、两类 DNS 获取方式，以及被修改地址族的默认路由、路由度量值和持久存储。已有待恢复备份时不会再次启用或覆盖备份；界面会提示点击电源恢复。
- 启用中途失败会尝试回滚；恢复验证通过后才删除备份。网络已经变化或断开时不写回旧网关；仅原来使用自动 DNS 的相关地址族允许跨网络清理。旧网络使用静态 DNS 时保留备份并提示回原网络处理。原网卡被拔出或不存在时保留备份，绝不退回选择当前有线网卡；接回原网卡后再恢复。
- 未配置 IPv6 旁路由时，不修改 IPv6 默认路由和 IPv6 DNS。
- 托盘退出会等待正在执行的检查或网络修改结束，并在有配置待恢复时询问“恢复后退出”或“保留配置退出”。窗口右上角关闭仍只是隐藏到托盘。系统强制终止或关机不能保证完成恢复。

**升级与备份兼容：**支持读取旧版备份；若旧备份缺少可确认的原网关或网卡信息，恢复可能需要手动处理，软件会保留备份而不是猜测。备份保存在当前用户注册表 `Software\YuanxinGatewaySwitch\Original`。

### DHCP 路由守护

- 已有原始备份且确认仍在同一网卡、同一网络时，后台轮询会检查 IPv4 默认路由；只有启用时配置了 IPv6 旁路由，才维护 IPv6 默认路由。
- 目标路由使用 `RouteMetric 0`，但不依赖与 DHCP 路由并列的度量值。发现 DHCP 或其他软件重新加入默认网关时，先确认目标路由存在，再移除该网卡上其他下一跳的默认路由；不先删除目标路由，不修改其他网卡、非默认路由或 DNS，不覆盖原始备份。
- 守护使用启用时备份的网关，而不是随后编辑的设置。关闭旁路由后停止守护，恢复原路由和度量值。守护是轮询式，不能保证 DHCP 注入到下一轮检查之间没有短暂流量绕行；也不接管其他网卡或 VPN 的更具体路由。

### 健康检查与自动回退

- 每轮结束后至少间隔 30 秒，绑定原网卡 IPv4 地址，向旁路由 DNS 查询随机 `example.com` 子域；有效答案或 NXDOMAIN 视为 DNS 递归可用。失败后尝试 `1.1.1.1:443`、`8.8.8.8:443` TCP 连接，单个探测限时 1.5 秒。
- 不依赖 Ping 或 ChatGPT 单一站点。连续 3 轮全部探测失败才恢复原网络，并暂停同一 Wi-Fi 下的自动开启；成功一轮即清零失败计数。探测时网络或路由变化的结果被丢弃。
- 这不是完整的应用层连通性保证。网络策略若同时屏蔽上述 DNS 查询和两个 TCP 目标，仍可能触发回退；DNS 缓存也可能掩盖部分上游故障。健康回退后检查旁路由，再点击电源手动启用。

开机自启动的任务动作对可执行文件路径和参数分别正确转义，支持含空格及中文的路径。已有旧任务需要在新版设置中勾选自启动并保存一次，才能更新其启动命令；此操作不会在构建时自动执行。

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

### 回归测试（不修改真实网络）

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\run-regression.ps1
```

测试使用模拟网关操作及 PowerShell 网络命令桩，覆盖异步恢复、路由守护、离网清理、DNS 响应校验、自启动转义和健康检查回退；不运行真实路由/DNS 修改命令，也不创建计划任务。实际 DHCP 续租、睡眠唤醒和旁路由断电仍需在测试网络中验证。

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
