# MuMuNetSim

[![Release](https://img.shields.io/github/v/release/Sueroyal/MuMuNetSim-?label=release&color=blue)](https://github.com/Sueroyal/MuMuNetSim-/releases/latest)
[![Downloads](https://img.shields.io/github/downloads/Sueroyal/MuMuNetSim-/total?label=downloads)](https://github.com/Sueroyal/MuMuNetSim-/releases)
[![License](https://img.shields.io/badge/license-MIT-green)](LICENSE)

给 **MuMu 模拟器的每个实例单独预设网络环境**的小工具：断网 / 丢包档位 / 自定义延迟、抖动、限速（可只做上行或只做下行），
实例启动后自动套用；也能按 IP 管局域网里的手机。单文件 exe，弱网能力整合了开源的
[clumsy](https://github.com/jagt/clumsy)（WinDivert 驱动 + clumsy 本体）。

## 下载（发给同事就用这两个固定链接，永远指向最新版）

| | 链接 | 说明 |
|---|---|---|
| **单文件版（推荐）** | https://github.com/Sueroyal/MuMuNetSim-/releases/latest/download/MuMuNetSim-single.zip | 解压得到**一个 exe**，内置 adb + clumsy/WinDivert |
| 目录版 | https://github.com/Sueroyal/MuMuNetSim-/releases/latest/download/MuMuNetSim.zip | exe + `adb\` + `clumsy\` + 说明与许可 |
| 全部版本 | https://github.com/Sueroyal/MuMuNetSim-/releases | 每个版本另有带版本号的资产 + SHA256 |

> 未签名，SmartScreen 拦一下时点「更多信息 → 仍要运行」；首次启动点 UAC「是」。

```
每实例单独预设：断网 / 丢包档位 / 延迟ms / 抖动ms / 限速KB/s（上下行可分别设）；实例启动后自动套用
[刷新实例] [全部恢复正常]  □ 全局弱网：延迟200ms+丢包5%（clumsy，整机出站；与每实例延迟互斥）        就绪

 实例 | 名称           | Android | 状态   | 预设环境   | 延迟ms | 抖动ms | 限速KB/s | 上行 | 下行 | 实际状态                        | 操作
  4   | 包体拆分       | 12.0    | 运行中 | 丢包 45% ▾ |  300   |   50   |   200    |  ☑   |  ☑   | 已套用：丢包 45% + 延迟 300ms(上下行)+抖动 ±50ms+限速 200KB/s | 关闭实例
  5   | 未包体拆分     | 12.0    | 运行中 | 断网     ▾ |    0   |    0   |     0    |  ☑   |  ☑   | 已套用：断网                    | 关闭实例
  0   | realese 包 pre | 12.0    | 已停止 | 丢包 15% ▾ |  500   |    0   |     0    |  ☑   |  ☐   | 待启动（启动后自动套用）        | 启动实例
```


## 三种干预，各自怎么实现

| 干预 | 实现 | 影响范围 | 说明 |
|---|---|---|---|
| **断网** | 实例内 `iptables`（双向 DROP） | **仅该实例** | 放行 `lo` + adb 端口，所以断网中也能一键恢复；规则不落盘，重启实例即清空 |
| **丢包 15%~100%（步长 10）** | 实例内 `iptables -m statistic --mode random` | **仅该实例** | 秒级生效；30% 档位实测 20 包样本得到 36%；100% 档 = 出方向全丢（近似断网，但不拦入方向） |
| **延迟 X ms（上行/下行可选）** | 宿主机 WinDivert 抓包 → 定时重发 | **仅该实例** | 每实例一个独立引擎；过滤器 = 该实例 `MuMuVMMHeadless.exe` 占用的 TCP/UDP 端口 |
| **抖动 ±Y ms** | 同上，每包延迟在 `X±Y` 内随机 | **仅该实例** | 实测 300±150ms 时握手耗时从 1.8s 散到 2.7s |
| **限速 Z KB/s** | 同上，按包大小串行发送 | **仅该实例** | 实测 1MB / 200KB/s = 5.32s（直连 0.54s） |
| 全局弱网（clumsy，可选） | 调 `clumsy.exe --filter "outbound and ip"` | 整机出站 | 粗粒度兜底；与每实例延迟互斥 |

## 为什么「延迟」要这么做

实测同一台机器：

| 观察点 | 实际值 |
|---|---|
| 实例内部（wlan0） | `10.0.2.15/24`，VirtualBox 式 NAT |
| 宿主机上的模拟器流量 | 源 IP 全是本机 IP，由 `MuMuVMMHeadless.exe` 用宿主套接字转发 |
| WinDivert 过滤器能力 | 只能按 IP / 端口 / 方向，**不能按 PID** |

所以进程级延迟只能靠「该实例 VMM 进程占用的端口」来圈定：

- 每个实例对应一个独立的 `MuMuVMMHeadless.exe`，命令行里带 `--comment MuMuPlayer-12.0-<实例号>`，映射是确定的；
- 每个实例一个 WinDivert 引擎，过滤器互不相交，**因此多个实例可以同时设不同延迟**（clumsy 本体做不到——它全局只允许一个实例）；
- 新出现的连接最多 3 秒后纳入（刷新过滤器时会先把队列里的包发完，不丢包）；
- ICMP（模拟器内 ping）不走 NAT 端口，因此不会被延迟；游戏/应用的 TCP/UDP 会。

## 快速开始

1. 双击 `MuMuNetSim.exe`（首次弹 UAC，点是）。
2. **点「刷新实例」**：自动扫描本机环境并同步界面（MuMu 安装位置、adb、WinDivert 驱动、权限、实例、端口、手机）。
3. 表格里给每个实例选好 **预设环境** / 填 **延迟ms** / 勾 **上行·下行**，改完立刻生效；
   实例没启动也行（显示「待启动」，实例一起来就自动套用），行尾按钮可直接启停实例。
4. 关窗口或点「全部恢复正常」即恢复。

## 包体内容（自包含，拿到就能用）

**单文件版**（推荐分发）：只有一个 `MuMuNetSim.exe`，首次运行把内置资产解包到 exe 同目录
（目录不可写时自动改用 `%LOCALAPPDATA%\MuMuNetSim`）：

```
MuMuNetSim.exe        主程序（含版本信息与图标）
adb\      adb.exe + AdbWinApi.dll + AdbWinUsbApi.dll   ← 内置官方 platform-tools：手机功能不依赖本机 MuMu/Android SDK
clumsy\   clumsy.exe + WinDivert.dll + WinDivert64.sys ← 全局弱网 + 延迟引擎的抓包驱动
使用说明.txt / 第三方组件与许可.txt
MuMuNetSim.ini        首次保存配置时生成
```

**目录版**：`MuMuNetSim.exe` + `clumsy\` + `adb\` + 两份说明，结构同上。

要点：

- **零安装**：.NET Framework 4.x 是 Win10/11 自带；adb、clumsy、WinDivert 全部随包。
- **MuMu 装在哪都行**：依次按 `ini 配置 → 常见安装路径（扫描各盘符） → 运行中的 MuMu 进程反推 → 注册表卸载项 → PATH` 探测；
  都没找到时会弹窗让你手动定位（写进 ini，只问一次）。
- **只读位置也能跑**：exe 放在网络共享 / Program Files 时，资产与 ini 自动落到 `%LOCALAPPDATA%\MuMuNetSim`。
- **32 位 Windows**：延迟引擎（WinDivert 为 x64）不可用，标题栏与自检里会明确说明；丢包/断网仍可用。

## 环境自检（在别人机器上排障第一步）

「刷新实例」= 全量环境自检 + 界面同步，日志里会输出一份可直接贴出来的报告；也可以命令行跑（免提权）：

```powershell
MuMuNetSim.exe /selfcheck
```

```
版本：MuMu 弱网 / 断网模拟器  v1.4　64 位系统 ✓
管理员：是 ✓（延迟引擎、代理防火墙放行都可用）
程序目录：D:\tools\MuMuNetSim　单文件资产解包：D:\tools\MuMuNetSim
ini：D:\tools\MuMuNetSim\MuMuNetSim.ini　日志：%TEMP%\MuMuNetSim.log
MuMuManager：D:\Program Files\Netease\MuMu\nx_main\MuMuManager.exe　来源：从运行中的 MuMuNxMain 反推
adb：D:\tools\MuMuNetSim\adb\adb.exe　Android Debug Bridge version 1.0.41
WinDivert：D:\tools\MuMuNetSim\clumsy　dll✓ 驱动✓ 服务：已运行
clumsy：D:\tools\MuMuNetSim\clumsy\clumsy.exe
代理入站放行：MuMuNetSim Delay Proxy 8899 → 暂无（启动手机时延代理时会自动添加）
MuMu 实例：共 5 个，运行中 2 个（4·包体拆分、5·未包体拆分）
VMM 进程：实例4 pid=65860、实例5 pid=60204
```

## 构建

```powershell
git clone --recurse-submodules <this-repo>
cd MuMuNetSim
powershell -NoProfile -ExecutionPolicy Bypass -File .\build.ps1                  # 目录版
powershell -NoProfile -ExecutionPolicy Bypass -File .\build.ps1 -Package         # 目录版 + zip
powershell -NoProfile -ExecutionPolicy Bypass -File .\build.ps1 -Single -Package  # 单文件版 + zip
```

- `src/MuMuNetSim.cs` 由系统自带 `csc.exe`（.NET Framework 4.x）编译，构建时写入版本信息与图标；
- `tools/get-clumsy.ps1` 拉官方 clumsy 0.3 发行版（含 WinDivert 2.2）到 `dist\MuMuNetSim\clumsy`，并校验依赖的命令行参数；
- `tools/get-adb.ps1` 拉官方 platform-tools 到 `third_party\adb`（腾讯镜像优先 → Google 兜底 → 回退本机 Android SDK / AdbTools / MuMu 自带）；
- `third_party/clumsy` 是 `jagt/clumsy` 的 git submodule；`third_party/adb` 不入库（见 `.gitignore`），构建时按需下载。

## 命令行 / 自动化

```powershell
MuMuNetSim.exe /apply normal|offline|loss15|loss45|loss80|loss100 [/instance 4] [/noelevate]
MuMuNetSim.exe /delay /instance 4 /ms 300 /dir both|up|down [/seconds 15]
MuMuNetSim.exe /delay /filter "icmp and ip.DstAddr == 8.8.8.8" /ms 800 /dir up /seconds 20
MuMuNetSim.exe /ports            # 打印每个实例构造出来的 WinDivert 过滤器（免提权，用来验证映射）
MuMuNetSim.exe /status
MuMuNetSim.exe /where
```

纯 PowerShell 版仍在 `cli/`（offline / loss / pps / flap / lag / recover，含 `-Verify` 自检）。

## 实测记录（Windows 11 26200 / MuMu 12 / Android 12）

| 项目 | 结果 |
|---|---|
| 断网（双向） | 实例内 ping 100% 丢包，同时间 adb 命令仍可执行 |
| 丢包 30% | 20 包样本 36% |
| 延迟量准确性（`/delay /filter "icmp ..." /ms 800`） | 引擎自报每包实际保持 **800~802ms**；模拟器内 ping 从 190ms → **991ms**（+801ms） |
| 过滤器精确性 | 同一次运行里 ping 1.1.1.1 保持 38~40ms **完全不受影响** |
| 按实例端口过滤 + 真实流量 | 实例 4 的 curl 流量被引擎抓到：收包 176 / 重发 176，保持 300~301ms；端口变化后自动刷新过滤器 |
| 端口隔离 | 实例 4 与实例 5 的过滤器端口集合完全不相交 |

## 常见问题

- **UAC 点「否」**：延迟引擎（WinDivert）不可用，其它功能仍可用。
- **延迟没感觉**：确认勾了上行/下行、延迟 > 0；再看「实际状态」里的 `收包/重发` 有没有涨（涨了说明抓到了该实例的流量）。
- **多个实例同时设延迟**：支持，各自独立引擎、过滤器不相交。
- **想彻底还原**：点「全部恢复正常」，或重启实例（iptables 规则不落盘）。
- **日志**：`%TEMP%\MuMuNetSim.log`。

## 依赖与致谢

- [jagt/clumsy](https://github.com/jagt/clumsy) — MIT，全局弱网引擎；其 WinDivert 也被本程序的每实例延迟引擎复用。
- [WinDivert](https://github.com/basil00/Divert) — LGPL v3 / GPL v2，随 clumsy 官方发行版分发。
- MuMu 实例内命令通过官方 `MuMuManager.exe` 下发，未修改模拟器任何文件。

## License

MIT（见 [LICENSE](LICENSE)）；第三方组件许可见 LICENSE 末尾。
