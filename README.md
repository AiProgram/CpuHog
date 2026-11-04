# CpuHog

持续性多线程 CPU 压力工具。单文件 C# 源码，只依赖 Windows 自带的 .NET Framework 编译器，无需安装 SDK。

## 构建

```powershell
build.bat
# 或
powershell -ExecutionPolicy Bypass -File build.ps1
```

产物：`bin\CpuHog.exe`

输出为 **AnyCPU (IL)**，在 ARM64 机器上由原生 CLR 执行，不经过 x64 模拟层，因此测得的负载是真实负载。

## 用法

```
CpuHog.exe [-t 线程数] [-d 秒数] [-m 模式] [-p 百分比] [-s KB] [-prio 优先级] [-eco]
```

| 参数 | 说明 | 默认 |
|---|---|---|
| `-t <n>` | 工作线程数 | 逻辑核数 |
| `-d <sec>` | 运行秒数，`0` = 一直跑到 Ctrl+C | 0 |
| `-m <mode>` | `int` / `fp` / `mem` / `mix` | `mix` |
| `-p <pct>` | 每线程目标 CPU 百分比 1..100 | 100 |
| `-s <kb>` | `mem`/`mix` 模式的内存块大小 | 256 KB |
| `-prio <p>` | `normal` / `abovenormal` / `high` / `realtime` | `abovenormal` |
| `-eco` | 保留系统电源压制（默认关闭压制） | 关闭 |
| `-h` / `--help` / `help` | 帮助 | — |
| `-v` / `--version` | 版本 | — |

### 负载模式

| 模式 | 特点 |
|---|---|
| `int` | 整数位混合循环，最热、内存占用最低 |
| `fp` | sqrt/sin 浮点循环，压浮点单元 |
| `mem` | 字节填充 + 跨步读取，压内存带宽 |
| `mix` | 整数/浮点交替，周期性写缓冲（默认） |

## 后台不被压制

Windows 11 会对非前台进程施加 **EcoQoS（电源限制）**，表现为降频、限到能效核，任务管理器里显示绿叶图标。CpuHog 在启动时做三件事：

1. `SetProcessInformation(ProcessPowerThrottling)` 清除 `EXECUTION_SPEED`，进程级退出限速；
2. 每个工作线程 `SetThreadInformation(ThreadPowerThrottling)` 单独退出限速；
3. `SetThreadPriority(THREAD_PRIORITY_HIGHEST)` + 提升进程优先级类。

启动时会打印**回读的真实状态**，便于确认是否生效：

```
  priorityClass : AboveNormal
  powerThrottle : not throttled (set=True, stateMask=0x0)  [eco=off]
```

加 `-eco` 可保留系统压制，用于对照组测试（回读会显示 `THROTTLED`）。

## 示例

```powershell
CpuHog.exe                     # 满载，直到 Ctrl+C
CpuHog.exe -t 4 -d 30          # 4 线程满载 30 秒
CpuHog.exe -t 4 -p 60          # 4 线程各占 60%
CpuHog.exe -m fp -s 512        # 浮点负载，512 KB 块
CpuHog.exe -prio high          # 高优先级满载
CpuHog.exe -eco                # 对照组：保留系统压制

# 后台常驻
Start-Process .\bin\CpuHog.exe -ArgumentList '-t','4','-p','60'

# 停止
Stop-Process -Name CpuHog
```

## 实现要点

- **占空比控制用比例反馈**：每轮记录实际忙碌时间，按目标比例反推该睡多久，并配合 `timeBeginPeriod(1)` 提升定时器精度，因此 `-p 50` 实测能稳定落在 50% 左右，而不是固定 sleep 导致的偏差。
- **Ctrl+C 优雅退出**：置停止标志后 join 所有工作线程，退出码 0；参数错误退出码 2。
- **计数器**：每 32 个 chunk 刷新一次全局计数，低速率模式下也不会出现首秒 `rate=0`。

## 注意

- `realtime` 优先级可能让系统 UI 明显卡顿，仅在隔离测试时使用。
- 压满全部核心会让同机其他进程（包括被测量的应用）排队，做延迟对比测试时建议用 `-p` 留出余量。

## 验证记录（本机 6 vCPU / Windows 11 ARM64）

| 场景 | 结果 |
|---|---|
| 后台隐藏窗口，6 线程 100% | 3 秒墙钟消耗 17.58 s CPU ≈ 5.86 核（97.7%） |
| `-t 4 -p 50` | 实测 50.7% |
| `-t 4 -p 25` | 实测 24.4% |
| `-eco` 对照 | 回读 `THROTTLED (stateMask=0x1)` |
