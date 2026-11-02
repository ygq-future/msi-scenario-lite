# 双 CCD Ryzen 9 笔记本调优与稳定性指南（MSI Alpha 17 / 7945HX 参考平台）

> 本文是 [`MSI Scenario Lite`](../README.md) 的配套文档，整理自一次完整的实测调优过程：从「跑分很稳、轻载却莫名黑屏重启」一路追到 WHEA 寄存器层面。
>
> 目标读者：手里有 16 核双 CCD Ryzen 9 移动平台（7945HX / 7845HX 等），想在**保证 100% 稳定**的前提下压低温度、释放功耗墙的人。
>
> 文中所有数值都是**方法论示例**，不是"抄了就对"的配方 —— 体质差异、散热能力、内存颗粒组合都会改变结论。

---

## 0. 开始之前：BIOS 黑屏保命法则

调 BIOS 参数（内存频率、Curve Optimizer、PBO Limits）最坏的情况是参数过激导致开机不过自检、屏幕全黑。

**免拆机恢复 CMOS（微星笔记本通用）**：

1. 拔掉电源适配器（必须断开外接供电）。
2. 长按电源键 **35~45 秒**不松手（指示灯会亮起、闪烁，最终熄灭）。
3. 插回电源、正常开机 —— BIOS 已恢复出厂默认。首次开机可能黑屏训练内存 1~2 分钟，属于正常现象。

> 记住这一条，再去动 BIOS。它能把绝大多数"手滑"变成零成本事故。

---

## 1. 第一步不是超频，是关掉 HVCI

如果你看到下面任一现象，先别怀疑 CPU 体质：

- `AIDA64` 内存延迟 `> 95 ns`（正常应 `~70~85 ns`）
- `AIDA64` L3 缓存延迟 `> 30 ns`（正常应 `~11~14 ns`）
- `ZenTimings` 打不开、`CPU-Z` 不显示 Uncore Frequency

这些通常是 **HVCI / 内存完整性** 造成的：系统运行在 Hyper-V 虚拟化层下，每次内存访问多一层 SLAT 转换，L3 命中延迟可以被拉高数倍。

**关闭方式**：`Windows 安全中心` → `设备安全性` → `内核隔离详细信息` → 关闭「内存完整性」。

如果完全不使用 WSL2 / Docker / Hyper-V，可进一步：

```powershell
bcdedit /set hypervisorlaunchtype off
```

（需要时用 `bcdedit /set hypervisorlaunchtype auto` 恢复。）

**验证**：重新跑 AIDA64 缓存与内存测试，L3 延迟应回到 `~12 ns` 级别，内存延迟下降 `10~25 ns`。这一步纯赚，没有任何副作用（代价是失去 HVCI 提供的部分内核缓解措施，请自行权衡）。

---

## 2. 内存：先认清"混插"的物理上限

移动端 Ryzen 的内存控制器对**颗粒排布**极其敏感。判断能不能上高频，看的是这三件事，而不是标称频率：

| 因素 | 影响 |
| :--- | :--- |
| 单面 (`1Rx8`) vs 双面 (`2Rx8`) | 双面条每 rank 负载更重，控制器更难推高频 |
| 是否对称双通道 | `16G + 32G` 这类非对称组合会走 Flex Mode，跨区访问时电气压力明显增大 |
| 单条容量 | 32GB 双面条通常是压死频率上限的那一根 |

**经验结论**：`2Rx8 32GB` 参与的组合，DDR5-5200 往往是稳定甜点；强行拉到 5600 时，跑分软件可能通过，但一旦负载跨越对称区进入 Flex Mode 区域，就容易 `MEMORY_MANAGEMENT` / `SYSTEM_SERVICE_EXCEPTION` 蓝屏。

**验证方法**：`TestMem5`（anta777 配置）跑满 3 轮 + 实际重负载（编译、渲染、游戏）各半小时。只跑 `AIDA64` 带宽测试**不足以**证明稳定。

> 内存不稳的典型特征是"能开机、能跑分、一上强度就崩"，而且崩溃点随机、Bugcheck 代码多变 —— 看到这种模式先回默认频率。

---

## 3. PBO 功耗墙：理解三个限制再动

`Precision Boost Overdrive → PBO Limits = Manual` 下有：

| 参数 | 单位 | 含义 | 谁先撞墙 |
| :--- | :--- | :--- | :--- |
| `PPT` | mW | 封装总功耗上限 | 长时间满载最常见 |
| `TDC` | mA | 持续电流上限 | 多核重载、AVX 场景 |
| `EDC` | mA | 瞬时电流上限 | 高频瞬态、突发负载 |

移动平台原厂常把 `PPT` 压得很低（例如 55 W 档），解锁后由 EC 在 `PL1 / PL2` 之间动态分配。

**调法建议**：

1. 先只解锁功耗墙，`Curve Optimizer` 保持 `Disabled`，跑 `Cinebench R23` 多核 3 轮，记录稳态温度和频率。
2. 温度顶到 `95~100°C` 就说明散热到顶了 —— 继续加功耗只会换来更响的风扇和更低的实际频率，没有意义。
3. 想降温优先用负压（下一节），而不是继续堆功耗。

> ⚠️ 解锁功耗墙后，请确认**没有软件在开机时把它改回去**。原版 MSI Center 会在切换场景 / 开机服务启动时写 `EC 0x50 / 0x51`，把你的 `PPT` 换成它自己的预设档（常见 `45 W / 65 W`）。这正是本仓库提供 [`tools/Disable-MSICenter.bat`](../tools/Disable-MSICenter.bat) 的原因。

---

## 4. Curve Optimizer：本文最重要的一节

### 4.1 为什么负压既降温又提性能

动态功耗近似为：

$$P \approx C \cdot V^2 \cdot f$$

电压是**平方项**。降低 5% 电压（约 `50~60 mV`），同频功耗就下降约 10%。而 AMD 的加速算法是"功耗 / 电流 / 温度预算内尽可能拉高频"，所以省下来的功耗会立刻变成更高的全核频率 —— 这就是"负压反而更快"的由来。

出厂电压曲线里普遍含 `50~80 mV` 的冗余（为了兼容体质最差的芯片）。负压的本质是**把这部分冗余挤掉**。

### 4.2 但"金核"不能照搬普通核的负压值

同一个 CCD 里，各核心的 `CPPC` 最高性能值不同。数值高的核心出厂就被推到更高的加速频率（移动端 `5.3~5.45 GHz`），此时电压余量已经很小。

典型故障形态：

- 全核烤机（`~4.6 GHz`）**完全稳定**，`Cinebench` 一次不崩；
- 日常轻载或游戏里，单核瞬态冲到最高频时**直接断电重启**，`Kernel-Power Event 41` 的 `BugcheckCode = 0`，连蓝屏都不给。

**结论**：负压要按核心/CCD 分层，而不是一个数字全核套用。

### 4.3 最容易踩的坑：CCD 共享 L3 缓存供电轨

这是本指南最有价值的一条实测结论：

> **Zen 4 双 CCD 移动平台上，同一 CCD 的 8 个核心与其 32 MB L3 缓存共用一条供电轨（SVI3 / VDDCR_CPU）。**

推论非常反直觉：

- 烤机时所有核心都醒着，高体质核请求较高电压，**顺带托住了整块 L3 缓存**，所以负压看起来"很稳"。
- 轻载时高体质核进入深度休眠（CC6，接近 0 V），此时若同 CCD 上另一颗**带深负压**的核心被唤醒，它请求的低电压会直接作用到整块共享 L3 缓存上。电压跌破 SRAM 数据保持临界值后，缓存开始位翻转。

**现场特征**（可以在自己的机器上验证）：

1. `事件查看器` → `Windows 日志 → 系统`，反复出现 `WHEA-Logger Event ID 19`，描述为 `Cache Hierarchy Error`。
2. 事件里的 `APIC ID` 永远是同一个（通常是 `0`）—— 因为共享单元报错固定由该 CCD 的首个在线逻辑核代为上报，**并不代表真的是那颗核坏了**。
3. 导出该事件的 CPER 原始数据（`事件属性 → 详细信息 → 友好视图/XML`），看 `MCI_IPID` 与 `MCI_SYND`：
   - `MCI_IPID` 中 `HardwareID = 0xB0` 表示 **L3 缓存**；
   - `MCI_SYND` 低字节指示具体缓存切片编号（`0x04~0x07` 之类）。
   - 若错误集中在 L3 切片而非某个核心的私有缓存，就是本节描述的场景。

### 4.4 推荐的落地策略

| 配置 | 建议 |
| :--- | :--- |
| 第一颗 CCD（高体质核所在，通常 `Core 0~7`） | **全部 `0`（不降压）**；它的 L3 缓存与高频核共享供电轨，任何一颗带负压都可能拖垮缓存 |
| 第二颗 CCD（频率较低、独立 L3） | 可尝试 `-10 ~ -15`，多核满载的降温主力 |
| 若只想动一个数字 | 用 **`Per CCD`** 模式而非 `Per Core`，避免把单核设成极端值 |
| 稳定性验证 | 不能只跑 `Cinebench`；必须包含**轻载 / 单核瞬态**场景（游戏、网页滚动、音视频播放）持续数小时 |

> 判断是否"真的稳"，看的是**有没有 `WHEA-Logger Event 19`**，而不是"今天没崩"。事件日志是免费的早期预警。

### 4.5 让 WHEA 报错主动弹窗（可选）

`WHEA-Logger` 默认只写日志，你根本不会注意。本仓库的 [`tools/WHEA-Alert.ps1`](../tools/WHEA-Alert.ps1) 把这件事做成一条命令：

```powershell
# 注册"事件触发"计划任务：平时 0 内存占用，一旦出现 WHEA 事件立刻置顶弹窗
powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\WHEA-Alert.ps1 -Install

# 测试弹窗 / 卸载
powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\WHEA-Alert.ps1 -Test
powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\WHEA-Alert.ps1 -Uninstall
```

脚本只对**本次开机之后**发生的事件告警，不会开机时把历史事件重弹一遍。这样一旦负压不稳，第一次位翻转就会当场提醒你，而不是等到某天突然黑屏。

---

## 5. 单核温度高，不等于要加负压

高体质核在 `5.2~5.45 GHz` 区间会用到 `1.35~1.45 V`，温度比其它核心高 `15~20°C` 是正常的。如果你不需要最后那点单核频率：

**方案 A —— 削掉末端加速频率（推荐，最干净）**

`Precision Boost Overdrive` → `CPU Boost Clock Override` = `Enabled (Negative)` → `Max CPU Boost Clock Override (-)` 填 `300 ~ 400`（MHz）。

避开 `1.4 V` 暴热区，且**完全不影响多核性能**（多核本来跑不到那个频率）。副作用是单核跑分会下降几个百分点。

**方案 B —— 平滑温度墙**

`Precision Boost Overdrive` → `Platform Thermal Throttle Limit` = `Manual`，填 `80 ~ 85`（°C）。

> **辟谣**：很多人被"撞温度墙会锁 0.5 GHz、必须重启"吓到。那个故障来自 **`BD PROCHOT` 外部紧急信号**（主板 EC 在 `100°C` 红线或 VRM 过热时拉低引脚），是**硬件闩锁**，与 PBO 内部的 `Platform Thermal Throttle Limit` 是两套机制。后者距离红线还有 `15~20°C` 余量，只会以 `25 MHz` 步进微调频率，**不会锁低频**。反过来，它正是避免你撞到 `100°C` 触发 `BD PROCHOT` 的保护伞。

---

## 6. 显卡侧：两件低风险高收益的事

### 6.1 独显直连（MUX）

混合输出模式下，独显渲染的画面要经核显转发，同时占用一部分内存带宽。切到独显直连后：

- 游戏帧率通常 `+5%~15%`
- 释放核显占用的内存与带宽
- 代价：续航变差，且**必须重启才生效**

可以用本仓库的 [`MSI Scenario Lite`](../README.md) 切换，它只写标记、不强制重启。

### 6.2 解除 CUDA 的 P2 显存降频

跑 PyTorch / Ollama / ComfyUI / Blender 等 CUDA 负载时，NVIDIA 驱动会把显存从 `P0` 降到 `P2`（显存频率降低约 `200 MHz`）。用 [nvidiaProfileInspector](https://github.com/Orbmu2k/nvidiaProfileInspector/releases)：

1. 管理员运行，Profile 保持 `_GLOBAL_DRIVER_PROFILE (Base Profile)`
2. 找到 `5 - Common` → `CUDA - Force P2 State` → 改为 `Off`
3. 点击 `Apply changes`

---

## 7. 双 CCD 的调度问题：让游戏只待在一颗芯片上

双 CCD 的跨芯片通信要经过 I/O Die，延迟从 `~15 ns` 涨到 `~80 ns`。游戏线程如果被拆到两颗芯片上，会出现 1% Low 帧明显下滑、偶发微卡顿。

**做法**：用 [System Informer](https://github.com/winsiderss/systeminformer) 或 [Process Lasso](https://bitsum.com/)，把游戏主程序绑定到第一颗 CCD（通常 `CPU 0~15`，即 8 核 16 线程），让后台程序留在第二颗芯片。

**注意**：不要为了"温度好看"而把日常负载均匀撒到两颗 CCD 上 —— 唤醒第二颗芯片的缓存与 Infinity Fabric 本身就要多耗 `5~8 W`，总功耗反而上升，游戏延迟也变差。**默认的"优先单 CCD"策略是正确的**。

---

## 8. 用 MSI Scenario Lite 接管场景与显卡模式

原版 MSI Center 会在切换场景时覆盖 `PL1/PL2`、强推软件风扇表，并常驻十余个进程。本仓库的工具只保留必要写入：

- 场景：`EC 0xD2 = 0xC4 / 0xC1 / 0xC2`
- 强冷：`EC 0x98` bit 7
- 自动风扇恢复：`EC 0xD4` bit 7 清零
- 显卡模式：`EC 0xD1` + UEFI `MsiDCVarData`

不碰 `0x50 / 0x51`，所以你在 BIOS 里设的功耗墙不会被吃掉。寄存器细节见 [`EC-REGISTER-REFERENCE.md`](EC-REGISTER-REFERENCE.md)。

---

## 9. 验收清单

| # | 项目 | 怎么验 | 期望 |
| :-: | :--- | :--- | :--- |
| 1 | 内存稳定 | `TestMem5` 3 轮 + 重负载 | 无蓝屏、无 WHEA |
| 2 | 功耗墙生效 | `HWiNFO64` 看 `PL1 / PL2` 与实际封装功耗 | 满载能冲到设定值，且重启后不被改写 |
| 3 | 负压稳定 | 连续数小时轻载 + 游戏，查 `WHEA-Logger Event 19` | 事件数为 0 |
| 4 | 单核温度 | `Core Temp` / `HWiNFO64` 观察峰值 | 峰值可控（如 `≤ 85°C`） |
| 5 | MUX | 任务管理器「性能」→ 是否仍有核显 | 独显直连下无核显活动 |
| 6 | 游戏帧率 | 记录 1% Low | 绑定单 CCD 后微卡顿减少 |

---

## 10. 故障速查

| 现象 | 高概率原因 | 处理 |
| :--- | :--- | :--- |
| 瞬间黑屏重启，`Event 41` 且 `BugcheckCode = 0`，无 dump | 供电瞬态不足（负压过深 / 高体质核欠压） | 回退该 CCD 或该核负压 |
| 反复 `WHEA-Logger Event 19` + `Cache Hierarchy Error`，`APIC ID` 固定 | CCD 共享 L3 被低载低电压拖垮 | 该 CCD 全部负压归零 |
| 蓝屏代码多变（`MEMORY_MANAGEMENT` 等） | 内存频率/时序不稳 | 回默认 `Auto` |
| 温度冲到 `100°C` 后锁 `0.5 GHz`，重启才恢复 | 触发 `BD PROCHOT` 硬件闩锁 | 设 `Platform Thermal Throttle Limit`，检查散热与 VRM |
| BIOS 改完功耗墙，重启又变回低值 | 原版 MSI Center 后台服务覆盖 EC | 禁用其服务或卸载 |
| 显卡切换点了没反应 | 写入被拒（权限）或标记已处于目标态 | 以管理员运行；再点一次原模式取消预约 |

---

## 免责声明

本文是实测经验整理，不是官方规范。硬件寄存器写入与 BIOS 修改均有风险，请自行评估。不同机型、不同 EC 固件版本的寄存器语义可能不同 —— **先只读验证，再写**。
