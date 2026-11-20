# MuMuNetSim

给 **MuMu 模拟器的单个实例** 一键切换「弱网 / 断网」的小工具：单文件 exe，一个下拉框选完即生效，
弱网引擎直接整合了开源的 [clumsy](https://github.com/jagt/clumsy)（延迟 / 丢包 / 限速）。

```
┌──────────────────────────────────────────────────────┐
│ 模拟环境：[ 弱网 · 延迟 200ms + 丢包 5%（clumsy） ▾ ] │
│ 目标实例：[ 实例 4 · 包体拆分 (Android 12.0)     ▾ ]  │
│ [立即恢复正常]  已生效：弱网                          │
│ ┌────────────── 日志 ──────────────────────────────┐ │
│ │ [17:24:27] ---- 弱网 · 延迟 200ms + 丢包 5%      │ │
│ │ [17:24:29] 启动 clumsy：--filter "outbound and ip"│ │
│ │ [17:24:31] clumsy 已启动                          │ │
│ └──────────────────────────────────────────────────┘ │
└──────────────────────────────────────────────────────┘
```

## 四种环境

| 下拉项 | 引擎 | 影响范围 | 说明 |
|---|---|---|---|
| 正常（不干预网络） | 清理规则 + 关闭 clumsy | — | 关窗口也会自动执行 |
| 弱网 · 延迟 200ms + 丢包 5% | clumsy（宿主机抓包） | 整机出站，回环除外 | 唯一能做出**真延迟**的路子 |
| 弱网 · 丢包 15% | 实例内 `iptables` | **仅该实例** | 免额外权限、秒级生效、无延迟 |
| 断网 · 完全不通 | 实例内 `iptables` | **仅该实例** | 双向 DROP，保留 adb 通道便于随时恢复 |

- 程序启动时自请求一次管理员权限 → 之后 clumsy 全程**静默**（clumsy 检测到自己已提权，不会再弹 UAC）。
- 断网 / 丢包规则全部带 `mumu-net` 注释标记，可精确清理；只存在于内核内存，**重启实例即消失**。
- adb 控制通道（实例内 5555）始终放行，所以断网状态下仍然能一键恢复。

## 为什么「只影响 MuMu 的延迟」做不到

实测同一台机器（MuMu 12，实例 4）：

| 观察点 | 实际值 |
|---|---|
| 实例内部（wlan0） | `10.0.2.15/24`，VirtualBox 式 NAT 内网地址 |
| 宿主机上的模拟器流量 | 源 IP 全是本机 `172.18.88.157`，由 `MuMuVMMHeadless.exe` 转发 |
| WinDivert 过滤器能力 | 只能按 IP / 端口 / 方向，**不能按进程** |

所以 clumsy 的过滤器里填 `10.0.2.15` 抓不到、填 `172.18.88.157` 等于全机；
想在**单个实例**上做延迟，Windows 上没有免驱动的办法。
最终取舍：

- **延迟** → clumsy + `outbound and ip`（整机出站，`127.0.0.1` 回环不受影响，工具界面/其它本机服务安全），10 分钟自动退出兜底；
- **丢包 / 断网** → 直接在实例内部用 `iptables`，天然只影响这个实例。

## 快速开始

1. 双击 `MuMuNetSim.exe`（首次会弹 UAC，点是）。
2. 下拉框选环境 → 立即生效；选回「正常」或直接关窗口即恢复。
3. 换实例：上面「目标实例」下拉框，或点「刷新」重新扫描。

## 构建

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\build.ps1
```

- `src/MuMuNetSim.cs` 用系统自带 `csc.exe`（.NET Framework 4.x）编译成 `dist/MuMuNetSim/MuMuNetSim.exe`；
- `tools/get-clumsy.ps1` 从官方 release 下载 `clumsy-0.3-win64-a.zip` 解到 `dist/MuMuNetSim/clumsy/`，
  并校验二进制里确实有本工具依赖的参数（`--lag-time` / `--drop-chance` / `--bandwidth-bandwidth` / `--timeout` / `--filter`）；
- `third_party/clumsy` 是 `jagt/clumsy` 的 git submodule（只读参考 / 需要自制构建时用）。

```powershell
git clone --recurse-submodules <this-repo>
```

## 命令行 / 自动化

```powershell
MuMuNetSim.exe /apply normal|offline|weak|weak-guest [/instance 4] [/noelevate]
MuMuNetSim.exe /status
MuMuNetSim.exe /where
```

- `/apply weak` 会自动提权后静默启动 clumsy；`/noelevate` 便于在已提权的 CI/终端里调试；
- 日志固定写 `%TEMP%\MuMuNetSim.log`（GUI 与命令行同一份）。

仓库里还保留了纯 PowerShell 版（不依赖 exe，便于改参数/嵌到别处）：

```powershell
cd cli
powershell -NoProfile -ExecutionPolicy Bypass -File .\mumu-net.ps1 status
powershell -NoProfile -ExecutionPolicy Bypass -File .\mumu-net.ps1 offline
powershell -NoProfile -ExecutionPolicy Bypass -File .\mumu-net.ps1 loss -Percent 30
powershell -NoProfile -ExecutionPolicy Bypass -File .\mumu-net.ps1 flap -OnSeconds 15 -OffSeconds 10 -Cycles 3
powershell -NoProfile -ExecutionPolicy Bypass -File .\mumu-net.ps1 lag -Ms 300 -DropPercent 5 -Scope global -Verify
powershell -NoProfile -ExecutionPolicy Bypass -File .\mumu-net.ps1 recover
```

## 目录结构

```
MuMuNetSim/
├─ src/MuMuNetSim.cs          主程序（WinForms，单文件，无第三方依赖）
├─ build.ps1                  一键构建：编译 + 组装 clumsy + 生成使用说明
├─ tools/get-clumsy.ps1       拉取官方 clumsy 发行版并校验参数
├─ cli/                       纯 PowerShell 版（断网/丢包/间歇断网/延迟，含 -Verify 自检）
├─ third_party/clumsy         git submodule → https://github.com/jagt/clumsy
├─ dist/MuMuNetSim/           构建产物（已 .gitignore）
└─ LICENSE
```

## 实测（Windows 11 26200 / MuMu 12 实例 4 / Android 12）

| 操作 | 实例内 `ping 8.8.8.8` 结果 |
|---|---|
| 基线 | 0% 丢包，RTT≈188ms |
| 断网（双向） | 100% 丢包，同时 adb 命令仍可执行 |
| 弱网 · 丢包 15% | 15%（20 包样本） |
| 恢复正常 | 规则 0 条，丢包回到基线 |

## 常见问题

- **UAC 点了「否」**：程序会继续以普通权限运行，但「弱网（clumsy）」可能仍需单独放行，关闭 clumsy 也可能失败。
- **多个实例同时运行**：默认作用于第一个运行中的实例，用「目标实例」下拉框切换；断网/丢包按实例精确生效。
- **断网后想彻底还原**：下拉框选「正常」；实在异常就重启该实例（规则不落盘）。
- **日志**：`%TEMP%\MuMuNetSim.log`。

## 依赖与致谢

- [jagt/clumsy](https://github.com/jagt/clumsy) — MIT，弱网引擎（以 submodule 引入，运行时调用其命令行）。
- [WinDivert](https://github.com/basil00/Divert) — LGPL v3 / GPL v2，随 clumsy 官方发行版分发。
- MuMu 的实例内 shell 通过官方 `MuMuManager.exe` 调用，未修改模拟器任何文件。

## License

MIT（见 [LICENSE](LICENSE)）；第三方组件许可见 LICENSE 末尾。
