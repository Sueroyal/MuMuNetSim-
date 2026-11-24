# MuMuNetSim

给 **MuMu 模拟器的每个实例单独预设网络环境**的小工具：断网 / 丢包档位 / 自定义延迟（可只做上行或只做下行），
实例启动后自动套用。单文件 exe，弱网能力整合了开源的 [clumsy](https://github.com/jagt/clumsy)（WinDivert 驱动 + clumsy 本体）。

```
每实例单独预设：断网 / 丢包档位 / 延迟(ms，可分别只做上行或下行)；实例启动后自动套用
[刷新实例] [全部恢复正常]  □ 全局弱网：延迟200ms+丢包5%（clumsy，整机出站；与每实例延迟互斥）        就绪

 实例 | 名称           | Android | 状态   | 预设环境   | 延迟ms | 上行 | 下行 | 实际状态                              | 操作
  4   | 包体拆分       | 12.0    | 运行中 | 丢包 45% ▾ |  300   |  ☑   |  ☑   | 已套用：丢包 45% + 延迟 300ms(上下行) · 收包 176/重发 176 | 关闭实例
  5   | 未包体拆分     | 12.0    | 运行中 | 断网     ▾ |    0   |  ☑   |  ☑   | 已套用：断网                          | 关闭实例
  0   | realese 包 pre | 12.0    | 已停止 | 丢包 15% ▾ |  500   |  ☑   |  ☐   | 待启动（启动后自动套用）              | 启动实例
```

## 三种干预，各自怎么实现

| 干预 | 实现 | 影响范围 | 说明 |
|---|---|---|---|
| **断网** | 实例内 `iptables`（双向 DROP） | **仅该实例** | 放行 `lo` + adb 端口，所以断网中也能一键恢复；规则不落盘，重启实例即清空 |
| **丢包 15%~80%（步长 10）** | 实例内 `iptables -m statistic --mode random` | **仅该实例** | 秒级生效；实测 30% 档位 20 包样本得到 36% |
| **延迟 X ms（上行/下行可选）** | 宿主机 WinDivert 抓包 → 定时重发 | **仅该实例** | 每实例一个独立引擎；过滤器 = 该实例 `MuMuVMMHeadless.exe` 占用的 TCP/UDP 端口 |
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
2. 表格里给每个实例选好 **预设环境** / 填 **延迟ms** / 勾 **上行·下行**，改完立刻生效。
3. 实例没启动也行：状态列显示「待启动」，实例一起来就自动套用；行尾按钮可直接启停实例。
4. 关窗口或点「全部恢复正常」即恢复。

## 构建

```powershell
git clone --recurse-submodules <this-repo>
cd MuMuNetSim
powershell -NoProfile -ExecutionPolicy Bypass -File .\build.ps1
```

- `src/MuMuNetSim.cs` 用系统自带 `csc.exe`（.NET Framework 4.x）编译成 `dist/MuMuNetSim/MuMuNetSim.exe`；
- `tools/get-clumsy.ps1` 从官方 release 拉 `clumsy-0.3-win64-a.zip` 到 `dist/MuMuNetSim/clumsy/`（延迟引擎用它的 WinDivert 2.2），并校验参数；
- `third_party/clumsy` 是 `jagt/clumsy` 的 git submodule。

## 命令行 / 自动化

```powershell
MuMuNetSim.exe /apply normal|offline|loss15|loss45|loss80 [/instance 4] [/noelevate]
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
