# 双 CCD Ryzen 9 笔记本调优与稳定性指南（MSI Alpha 17 / 7945HX 参考平台）

> 本文是 [`MSI Scenario Lite`](../README.md) 的配套文档，整理自一次完整的实测调优过程：从「跑分很稳、轻载却莫名黑屏重启」一路追到 WHEA 寄存器层面。
>
> 目标读者：手里有 16 核双 CCD Ryzen 9 移动平台（7945HX / 7845HX 等），想在**保证稳定**的前提下压低温度、释放功耗墙的人。
>
> **数值约定（全文通用）**
>
> | 标记 | 含义 |
> | :--- | :--- |
> | ✅ **实测** | 在 MSI Alpha 17 C7VF（`MS-17KK`，EC `17KKIMS1.115`，BIOS `E17KKAMS.125`）上验证过的具体数值 |
> | ⚠️ **参考** | 按平台规格 / 散热等级给出的建议值，**必须自行验证**；不同机型、不同散热模组差异很大 |
> | ❌ **禁止** | 已知会导致不稳定或损伤的值 |
>
> 文中给了实测结论与具体数字，但这不等于"抄了就对"：不同机型、不同散热模组差异很大。**每一项都配了验证方法，请按顺序走。**

---

## 0. 开始之前：BIOS 黑屏保命法则

调 BIOS（内存频率、Curve Optimizer、PBO Limits）最坏的情况是参数过激导致开机不过自检、屏幕全黑。

**免拆机恢复 CMOS（微星笔记本通用）**：

1. 拔掉电源适配器（必须断开外接供电）。
2. 长按电源键 **35~45 秒**不松手（指示灯会亮起、闪烁，最终熄灭）。
3. 插回电源、正常开机 —— BIOS 已恢复出厂默认。首次开机可能黑屏训练内存 **1~2 分钟**，属于正常现象，不要急着断电。

> 记住这一条，再去动 BIOS。它能把绝大多数"手滑"变成零成本事故。

---

## 1. 怎么进入 BIOS 隐藏菜单（Advanced / AMD Overclocking）

微星笔记本默认 BIOS 只开放很小一部分选项，`PBO Limits`、`Curve Optimizer`、`CPPC`、内存时序全在隐藏页里。

### 1.1 进入方法（✅ 实测）

1. 关机 → 开机时**连按 `Del`**（部分机型是 `F2`）进入 BIOS。
2. 在 BIOS 主界面按组合键：

```text
右 Ctrl + 右 Shift + 左 Alt + F2
```

3. 屏幕会闪一下并多出一整块高级菜单（`Advanced` 下出现 `AMD Overclocking`、`AMD CBS` 等）。**每次进 BIOS 都要重按一次**，这个解锁不持久。

> 如果组合键无效：部分微星机型是 `右 Ctrl + 右 Shift + 左 Alt + F2` 之外的组合，可在机型论坛/官方支持页确认；再不行就只调 BIOS 已开放的选项（第 4 节的功耗墙通常仍可调）。

### 1.2 关键菜单路径表（✅ 实测，Alpha 17 C7VF）

| 要改的东西 | 路径 |
| :--- | :--- |
| **PBO 功耗墙 / 负压 / 温度墙 / 频率偏移** | `Advanced` → `AMD Overclocking` → `Accept` → `Precision Boost Overdrive` |
| **CPPC / 首选核心 / SmartShift** | `Advanced` → `AMD CBS` → `NBIO Common Options` → `SMU Common Options` |
| **内存频率 / 时序** | `Advanced` → `AMD CBS` → `UMC Common Options`（或主界面 `Memory` 页） |
| **保存并退出** | `F10` → `Yes` |

`Precision Boost Overdrive` 页内的关键项（本指南后续反复用到）：

```text
Precision Boost Overdrive          [Advanced]     ← 必须先设为 Advanced 才能改下面的项
  PBO Limits                       [Manual]
    PPT Limit                      [75000]       ← 单位 mW（75~85 W 为 16 核能效甜点）
    TDC Limit                      [108000]      ← 单位 mA
    EDC Limit                      [160000]      ← 单位 mA
  Curve Optimizer                  [Disabled]    ← 推荐保持 Disabled（全 0），见第 5 节
  CPU Boost Clock Override         [Enabled (Negative)]
    Max CPU Boost Clock Override   [250]         ← 单位 MHz（削掉 5.20~5.45 GHz 末端高压尖峰）
  Platform Thermal Throttle Limit  [Manual]
    (数值)                          [90]          ← 单位 °C（85~90 均可，PID 1ms 平滑控温）
```

> ⚠️ 不同 BIOS 版本的选项名可能略有差异（例如 `Curve Optimizer` 也可能叫 `Curve Optimizer Control`）。**改之前先记下原值**，出问题好回退。

---

## 2. 第一步不是超频，是关掉 HVCI

如果你看到下面任一现象，先别怀疑 CPU 体质：

- `AIDA64` 内存延迟 `> 95 ns`（正常应 `70~85 ns`）
- `AIDA64` L3 缓存延迟 `> 30 ns`（正常应 `11~14 ns`）
- `ZenTimings` 打不开、`CPU-Z` 不显示 Uncore Frequency

这些通常是 **HVCI / 内存完整性** 造成的：系统运行在 Hyper-V 虚拟化层下，每次内存访问多一层 SLAT 转换。

**关闭方式**：`Windows 安全中心` → `设备安全性` → `内核隔离详细信息` → 关闭「内存完整性」→ 重启。

如果完全不使用 WSL2 / Docker / Hyper-V，可进一步：

```powershell
bcdedit /set hypervisorlaunchtype off      # 需要时用 auto 恢复
```

**验证**：重跑 AIDA64 缓存与内存测试，L3 延迟应回到 `12 ns` 级别，内存延迟下降 `10~25 ns`。

| 指标 | 开启 HVCI | 关闭 HVCI |
| :--- | :--- | :--- |
| L3 延迟 | `25~40 ns` | ✅ **`11~14 ns` 实测** |
| 内存延迟 | `95~115 ns` | ✅ **`70~85 ns` 实测** |

---

## 3. 内存：参考值与"混插"的物理上限

移动端 Ryzen 的内存控制器对**颗粒排布**极其敏感。判断能不能上高频，看这三件事，而不是标称频率：

| 因素 | 影响 |
| :--- | :--- |
| 单面 (`1Rx8`) vs 双面 (`2Rx8`) | 双面条每 rank 负载更重，控制器更难推高频 |
| 是否对称双通道 | `16G + 32G` 这类非对称组合会走 Flex Mode，跨区访问时电气压力明显增大 |
| 单条容量 | `32GB` 双面条通常是压死频率上限的那一根 |

### 3.1 频率参考表

| 组合 | 建议频率 | 说明 |
| :--- | :--- | :--- |
| `2×16GB` 单面 `1Rx8` | **`5200` 起步，可试 `5600`**（⚠️ 参考） | 最容易上高频的组合 |
| `2×32GB` 双面 `2Rx8` | **`5200`**（⚠️ 参考） | `5600` 大概率不稳 |
| `16GB + 32GB` 非对称 | **`5200`**（✅ 实测稳定；不稳则退 `4800`） | Flex Mode，最考验控制器 |
| 不确定 / 想省事 | **`Auto`（按 JEDEC）** | 优先保稳定 |

内存时序、电压**全部保持 `Auto`**。手动压时序的收益（个位数百分比带宽）远小于稳定性风险。

### 3.2 验证方法

`TestMem5`（anta777 配置）跑满 **3 轮** + 实际重负载（编译、渲染、游戏）各半小时。只跑 `AIDA64` 带宽测试**不足以**证明稳定。

> 内存不稳的典型特征是"能开机、能跑分、一上强度就崩"，且 Bugcheck 代码多变（`MEMORY_MANAGEMENT`、`SYSTEM_SERVICE_EXCEPTION`）。看到这种模式先回 `Auto`。

---

## 4. PBO 功耗墙：参考数值

`Precision Boost Overdrive → PBO Limits = Manual` 下有：

| 参数 | 单位 | 含义 | 谁先撞墙 |
| :--- | :--- | :--- | :--- |
| `PPT` | mW | 封装总功耗上限 | 长时间满载最常见 |
| `TDC` | mA | 持续电流上限 | 多核重载、AVX 场景 |
| `EDC` | mA | 瞬时电流上限 | 高频瞬态、突发负载 |

### 4.1 先取基线，再谈解锁

1. BIOS 里 `PBO Limits = Auto`，进系统用 `HWiNFO64` 跑 `Cinebench R23` 多核；
2. 记录满载时的 `PPT / TDC / EDC` 上限与稳态温度；
3. 这就是你机器的**出厂基线**，之后所有调整都以它为参照，而不是照抄别人的数字。

### 4.2 参考值（按散热等级）

| 档位 | `PPT` | `TDC` | `EDC` | 适用 | 预期 |
| :--- | ---: | ---: | ---: | :--- | :--- |
| 出厂默认 | `55000` | 默认 | 默认 | 完全不动 BIOS | 多核约 `45~55 W` 持续 |
| **推荐（能效甜点）** | **`75000 ~ 85000`** | **`108000`** | **`160000`** | 双风扇笔记本日常 + 重载兼顾 | ✅ 实测：全核落在 `4.4~4.5 GHz` 5nm 甜点区，`85 W` 独显直连下 R23 多核 **`30501 pts`**（反超 `110 W` 撞墙时的 `29908 pts`），满载平均仅 `79°C` |
| 高功耗档 | `110000` | `108000` | `160000` | 强冷满速 + 极佳散热环境 | MSI EC 换算为 `PL1 85 W / PL2 102 W`；超过 `90°C` 后静态漏电激增，跑分与帧率无实质提升 |
| 激进 | `130000` | `120000` | `180000` | 改水冷 / 极限跑分 | 极易触发 `100°C+` 热饱和降频，风扇满转 |

> ⚠️ `TDC / EDC` 的默认值随机型不同。**先读 HWiNFO 的默认上限**；上表的 `108 A / 160 A` 是本平台常用值，不是通用真理。
>
> ⚠️ 判断散热到顶的标准：满载稳态温度 `≥ 95°C` 或频率开始明显来回跳。此时再加功耗只会换更响的风扇，**不会更快**。

### 4.3 解锁后必做：防止被软件改回去

原版 MSI Center 在切场景 / 开机服务启动时会写 `EC 0x50 / 0x51`，把你的功耗墙换成它的预设档（常见 `45 W / 65 W`）——表现为"BIOS 里明明设了 110 W，开机一会儿就变回 45 W"。

处理：用 [`tools/Disable-MSICenter.bat`](../tools/Disable-MSICenter.bat) 禁用其后台服务与 UWP 启动项（本仓库的 `MSI Scenario Lite` 不写 `0x50/0x51`，可放心接管）。

**验证**：`HWiNFO64` 里看 `PL1 / PL2`，满载时 `Package Power` 应能逼近设定值；重启后数值不该被改小。

### 4.4 解锁 PPT 后的关键坑位：务必配合「独显直连」使用

> ⚠️ **实测结论**：在 7945HX 这类双 CCD + IOD 架构上，**日常插电使用务必切到「独显直连（Discrete）」模式**；若停留在「混合输出（MSHybrid）」模式，会出现**即使 CPU 核心占用只有 7%~15%，封装总功耗仍飙到 `75 W+`、待机最低功耗高达 `44.8 W`、温度直冲 `94~103°C`** 的异常高温现象。

原因来自两个硬件机制的叠加：

1. **CPU 内部 IOD 核显（Radeon 610M）的寄生功耗与发热叠加**：
   - 混合模式下，内屏高刷桌面合成（如 `2560×1440@240Hz`）与远程串流/抓屏渲染会压在 CPU 基板内部的 **IOD 核显模块（Radeon 610M，仅 2 CU）** 上，甚至触发核显与独显之间的跨 PCIe 显存拷贝，导致 IOD 控制器、Infinity Fabric 与核显长期锁死在高电压状态。
   - 核显与 IOD 产生的 **`15~25 W` 功耗全部计入 CPU 封装功率（PPT）**，在多核满载时直接挤占 16 颗 CPU 核心的可用功率预算，并与小面积 CCD 争抢同一套热管。
   - 切回**独显直连**后，CPU 内部的 610M 核显直接**物理断电（0 W）**，✅ 实测待机最低封装功耗从 `44.8 W` 暴降至 **`16.2 W`**，日常温度从 `94°C` 降至 `55~65°C`，跑分时 `75~85 W` PPT 预算 100% 留给 16 颗核心。
2. **主板 EC 的整机交叉负载分流（Crossload Limit）**：
   - 在**独显直连**模式下，RTX 4060 全程接管桌面合成、浏览器硬件加速与远程串流编码（NVENC）。主板 EC 检测到独显处于活跃供电状态，会自动触发交叉负载保护，合理约束整机热平衡。

---

## 5. Curve Optimizer 负压实测真相：为什么推荐保持 Disabled（全 0）

### 5.1 理论上的美好 vs. Zen 4 的整线平移缺陷

$$P \approx C \cdot V^2 \cdot f$$

理论上，降低电压可以按平方关系降低同频功耗（`-1` 档 ≈ `3~5 mV`）。但 Zen 3 / Zen 4 的 **Curve Optimizer (CO)** 有一个致命的结构性缺陷（AMD 官方在 Zen 5 发布 `Curve Shaper` 时已公开确认）：

- **CO 是对整条电压-频率（V-F）曲线的无差别平移**（从 `600 MHz` 极轻载一直到 `5.45 GHz` 最高频同时扣减电压）。
- 你为了削掉顶部 `5.2~5.45 GHz` 的高压废热而设的负压，会**按更大比例扣掉底部 `1.5~2.5 GHz`（待机、睡眠唤醒、均衡模式后台轻载）的保命电压**。

### 5.2 核心矛盾：CCD 共享 L3 缓存供电轨的“一票否决效应”

> **在 7945HX 双 CCD 平台上，每个 CCD 内部都自带 32 MB L3 缓存（CCD0 32 MB + CCD1 32 MB = 64 MB），且同一 CCD 的 8 颗核心与其 32 MB L3 缓存共用同一条供电轨（`VDDCR_CPU`）。**

这导致网上传播的“金核多压 / 弱核多压 / 分层微压”套路在共享 L3 面前全部失效：

1. **为什么金核（CPPC 高分核）不能压**：AMD 出厂标定 CPPC 时，已经把金核推到了 `5.45 GHz` 极限频率，高频端电压余量本就被压榨殆尽。
2. **为什么副核簇（`CCD1`）或弱核/中核同样不能压**：
   - **满载烤机的假象**：跑 `Cinebench R23` 全核满载时，CCD 内 8 颗核心全部醒着，高频率核请求的较高电压（`>1.05 V`）顺带把整块 32 MB L3 缓存托得稳稳当当，哪怕设 `-15` 也一次不崩。
   - **轻载单核值班的暴雷**：在日常均衡/静音模式、现代待机（S0）睡眠唤醒、或后台安装小软件完成回落的瞬间，`CCD1` 往往只有 **单独 1 颗核心** 醒着运行在低频段，其余 7 颗核心处于深度休眠（`CC6`，`0 V`）。
   - 此时，**整块 32 MB 共享 L3 缓存能吃到多少电压，100% 取决于当前唯一醒着值班的那颗核心！** 只要这颗核带了负压（无论是全 CCD1 设 `-15`，还是只给中游核设 `-10`），它单独唤醒时向供电轨请求的低电压就会跌破 L3 SRAM 的最低数据保持电压（$V_{\min}$），当场引发 L3 缓存位翻转！

**实测寄存器铁证（可在事件查看器验证）**：

1. `事件查看器` → `Windows 日志 → 系统`，在睡眠唤醒、切换电源模式或轻载时出现 `WHEA-Logger Event ID 19`（`Cache Hierarchy Error`），累计几次单比特翻转后直接触发 AMD SMU **Sync Flood 硬件级断电重启**（`Kernel-Power Event 41`，`BugcheckCode = 0`，无蓝屏、无 dump）。
2. **为什么 `CCD1` 报错，Windows 日志却永远显示 `APIC ID: 0`？**
   - `Event ID 19` 是已更正机器检查（CMC），Windows 内核的后台 CMC 轮询器默认固定在 **0 号逻辑核（BSP，`APIC ID = 0`）** 上轮询共享缓存银行（`MCABank = 7`），所以无论哪颗 CCD 的 L3 报错，文本描述都显示 `APIC ID: 0`。
3. **看 CPER 原始十六进制（`RawData`）才能锁定真实物理位置**：
   - `MCABank = 7`、`MciStat = 0x9C2040000602010B`（`ExtErrorCode = 0x02` L3 数组错误，`ErrorCode = 0x010B` L3 Fill Error）；
   - `MCA_IPID = 0x0007_00B0_2035_0000`：`HardwareID = 0x0B0` 代表 **L3 Cache**，而 SMN 实例地址 `0x20350000`（`0x20150000` 为 CCD0 L3，步长 `0x00200000`）精准指向 **`CCD1` 的 32 MB L3 缓存**！

### 5.3 正确解法：关掉 CO 负压（设 0），用 `CBCO 削顶 + 甜点 PPT` 替代

既然折腾负压的目的只是“削掉顶部高电压尖峰与满载废热”，最干净、100% 不伤底部 L3 稳定性的正道是：

| BIOS 项 | 推荐设置 | 物理效果 |
| :--- | :--- | :--- |
| **`Curve Optimizer`** | **`Disabled`（全核 `0`）** | 底部待机、睡眠唤醒与均衡模式低频电压一毫伏不扣，**共享 L3 缓存永远吃饱出厂电压，彻底根治 `WHEA 19` 与无征兆黑屏重启** |
| **`CPU Boost Clock Override`** | **`Negative 200 ~ 250 MHz`** | 精准切掉顶部 `5.20 ~ 5.45 GHz` 那段需要 `1.35~1.45 V` 高压硬推的“废热尖峰”，完全不动底部轻载电压（等效手动实现 Zen 5 只降高频的思路） |
| **`PPT Limit`** | **`75000 ~ 85000` mW** | 把 16 核全核满载锁定在 `4.4~4.5 GHz`（约 `1.0~1.05 V`）能效甜点区，远离 `95°C+` 静态漏电激增区 |
| **`Platform Thermal Throttle Limit`** | **`85 ~ 90` (°C)** | 由 SMU 内部 PID 以 `1 ms` 周期平缓控温，封死瞬时温度过冲 |

### 5.4 附：如何查看本机 16 核 CPPC 体质分布（仅供查阅）

如果想了解自己机器两颗 CCD 的出厂体质排序，可直接读取 Windows 内核事件 `ID 55`（无需第三方工具）：

```powershell
Get-WinEvent -FilterHashtable @{LogName='System'; ProviderName='Microsoft-Windows-Kernel-Processor-Power'; Id=55} -MaxEvents 32 |
  ForEach-Object {
    if ($_.Message -match '处理器 (\d+)[\s\S]*?最佳性能百分比: (\d+)') {
      [PSCustomObject]@{ LogicalProcessor = [int]$matches[1]; Core = [math]::Floor([int]$matches[1]/2); HighestPerf = [int]$matches[2] }
    }
  } | Sort-Object -Property LogicalProcessor -Unique | Format-Table -AutoSize
```

- 同一物理核的两个线程数值相同（`Core 0~7` 属 `CCD0`，`Core 8~15` 属 `CCD1`）。
- ✅ 本机实测：`CCD0` 最高为 `310`（`Core 0 / 2`），`CCD1` 最高为 `264`（`Core 15`）、最低仅 `218`（`Core 12`）。这也解释了为什么 `CCD1` 在轻载唤醒时基准电压极低、一旦加负压就会拖垮共享 L3。

### 5.5 稳定性验证流程（必过 4 关）

> ⚠️ **注意**：`Cinebench R23` 全核满载是最容易通过的测试（全核高电压托底），**跑分不崩绝不等于日常稳定**。真正的考验在下面第 ②~④ 步：

| 步骤 | 场景 | 判定标准 |
| :--- | :--- | :--- |
| ① `Cinebench R23` 多核 | 检验散热与功耗墙 | 分数正常（`75~85 W` 独显直连下约 `30000+ pts`），平均温度 `< 82°C` |
| ② 现代待机（睡眠）唤醒 + 锁屏解锁 | 检验单核从 `CC6 (0V)` 唤醒瞬间 | 经历数次睡眠唤醒无黑屏重启 |
| ③ 场景频繁切换（静音 ↔ 均衡 ↔ 游戏）+ 游戏反作弊加载 | 检验电流阶跃与瞬态掉压（Vdroop） | 无 `Kernel-Power Event 41` |
| ④ 日常混合使用 2~3 天后查日志 | 检验 L3 缓存零位翻转 | `WHEA-Logger Event 19` 累计数量 **= 0** |

### 5.6 让 WHEA 报错主动弹窗（强烈建议）

```powershell
# 注册事件触发的计划任务：平时 0 内存占用，出现 WHEA 事件立刻置顶弹窗
powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\WHEA-Alert.ps1 -Install

powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\WHEA-Alert.ps1 -Test
powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\WHEA-Alert.ps1 -Uninstall
```

脚本只对**本次开机之后**发生的事件告警，不会开机重弹历史事件。一旦负压不稳，第一次位翻转就会当场提醒你。

---

## 6. 单核温度高：参考数值

高体质核在 `5.2~5.45 GHz` 会用到 `1.35~1.45 V`，比其它核高 `15~20°C` 是正常的。若不需要最后那点单核频率：

### 6.1 方案 A：削掉末端加速频率（最干净）

`Precision Boost Overdrive` → `CPU Boost Clock Override = Enabled (Negative)` → `Max CPU Boost Clock Override (-)`：

| 数值 | 最高频率（以 5.45 GHz 机型为例） | 效果 |
| ---: | :--- | :--- |
| `200` | `5.25 GHz` | ✅ 实测：配 `85 W` PPT 跑出 R23 **`30501 pts`**，大幅削平单核突发高温 |
| **`250 ~ 300`** | **`5.15 ~ 5.20 GHz`** | ✅ **日常最稳推荐**，温度降 `8~10°C`，无需开任何 CO 负压即可告别单核突刺高温 |
| `400` | `5.05 GHz` | 温度降 `12~15°C`，单核跑分 −5% |

**完全不影响多核性能**（多核满载本来跑在 `4.4~4.5 GHz`，远低于这个上限）。

### 6.2 方案 B：1ms 平滑温度墙（PTTL）

`Platform Thermal Throttle Limit = Manual`：

| 数值 | 适用 |
| ---: | :--- |
| **`85 ~ 90`** | ✅ **推荐**，把 `Tdie` 峰值稳稳按在 `85~90°C` 以内，避免高温漏电与 L3 热失稳 |
| `80` | 追求极致安静，长时多核会微降 `5~8%` |
| `95` | 适合高室温下跑极限多核分（✅ 实测设 `95` 时 R23 满载 `Tdie` 最高精准停在 `95°C`，局部热点 `Tccd #0` 短暂 `97°C`） |

> **工作原理与辟谣**：
> 1. **盯的是哪个温度**：`PTTL` 闭环控制的目标是 SMU 滤波后的 **`Tdie`（`Tctl/Tdie`）**，而非瞬时局部热点 `Tccd #0 / #1`（因此局部 `Tccd` 短暂高出设定值 `1~2°C` 属正常现象）。
> 2. **平缓微调而非断崖降频**：很多人被"撞温度墙会锁 `0.5 GHz`、必须重启"吓到。那个故障来自主板外部 **`BD PROCHOT`** 紧急信号（`100°C+` 红线或供电过热硬件闩锁）。而 PBO 内部的 `PTTL` 是 SMU 以 **1 毫秒（1000 Hz）** 周期运行的 PID 闭环控制，接近设定温度时只会以 `25~50 MHz` 步进丝滑微调频率，游戏与日常完全无感。

---

## 7. 显卡侧：两件低风险高收益的事

### 7.1 独显直连（MUX）—— 消除核显寄生功耗的必选项

混合输出模式下，独显渲染的画面要经核显转发，同时占用一部分内存带宽；更关键的是，若留在混合输出，CPU 内部的 610M 核显与 IOD 会独自扛下高刷桌面合成/远程抓屏负载，产生 `15~25 W` 寄生功耗计入 CPU 封装（详见 §4.4），极易出现「CPU 核心占用仅 7%~15%，封装总功耗却飙到 75 W+、温度逼近 94~103°C」。

切到独显直连后：

- **CPU 内部 610M 核显物理断电（0 W）**，待机最低封装功耗从 `44.8 W` 暴降至 **`16.2 W`**，远程编码与桌面合成全部交由散热模组更大的独显处理
- **释放全部 PPT 预算给 16 颗 CPU 核心**，配合 `75~85 W` 甜点 PPT 与主板 EC 动态功耗分流，日常温度回落至 `55~65°C`、满载平均 `< 80°C`
- 游戏帧率通常 `+5%~15%`，并释放核显占用的内存与带宽
- 代价：离电续航变差，且**必须重启才生效**

可以用本仓库的 [`MSI Scenario Lite`](../README.md) 切换，它只写 `EC 0xD1` 预约标记并回读校验、不强制重启，随时可再次点击取消。

### 7.2 解除 CUDA 的 P2 显存降频

跑 PyTorch / Ollama / ComfyUI / Blender 时，NVIDIA 驱动会把显存从 `P0` 降到 `P2`（显存频率降低约 `200 MHz`）。用 [nvidiaProfileInspector](https://github.com/Orbmu2k/nvidiaProfileInspector/releases)：

1. 管理员运行，Profile 保持 `_GLOBAL_DRIVER_PROFILE (Base Profile)`
2. `5 - Common` → `CUDA - Force P2 State` → 改为 `Off`
3. `Apply changes`

---

## 8. 双 CCD 的调度：让游戏只待在一颗芯片上

跨芯片通信要经 I/O Die，延迟从 `~15 ns` 涨到 `~80 ns`。游戏线程被拆到两颗芯片上会出现 1% Low 明显下滑、偶发微卡顿。

**做法**：用 [System Informer](https://github.com/winsiderss/systeminformer) 或 [Process Lasso](https://bitsum.com/)，把游戏主程序绑定到第一颗 CCD（通常 `CPU 0~15`，即 8 核 16 线程），后台程序留在第二颗。

**注意**：不要为了"温度好看"把日常负载均匀撒到两颗 CCD —— 唤醒第二颗芯片的缓存与 Infinity Fabric 本身就要多耗 `5~8 W`，总功耗反而上升，游戏延迟也变差。**默认的"优先单 CCD"策略是正确的**。

---

## 9. 用 MSI Scenario Lite 接管场景与显卡模式

原版 MSI Center 切场景时会覆盖 `PL1/PL2`、强推软件风扇表，并常驻十余个进程。本仓库工具只保留必要写入：

| 功能 | 寄存器 | 说明 |
| :--- | :--- | :--- |
| 场景 | `EC 0xD2 = 0xC4 / 0xC1 / 0xC2` | 游戏 / 均衡 / 节能静音 |
| 强冷 | `EC 0x98` bit 7 | 切游戏模式自动开启 |
| 自动风扇恢复 | `EC 0xD4` bit 7 清零 | 交还 EC 温度曲线 |
| 显卡模式 | `EC 0xD1`（`0x06` 独显 / `0x02` 混合） | 只写标记并回读校验，下次重启生效 |

不碰 `0x50 / 0x51`，所以 BIOS 里的功耗墙不会被吃掉。寄存器细节见 [`EC-REGISTER-REFERENCE.md`](EC-REGISTER-REFERENCE.md)。

---

## 10. 验收清单

| # | 项目 | 怎么验 | 期望 |
| :-: | :--- | :--- | :--- |
| 1 | 内存稳定 | `TestMem5` 3 轮 + 重负载 | 无蓝屏、无 WHEA |
| 2 | 功耗墙生效 | `HWiNFO64` / `Core Temp` 看封装功耗 | 满载稳定在 `75~85 W` 甜点区，待机低至 `16 W` 左右 |
| 3 | 零硬件错误 | 经历睡眠唤醒、切模式、打游戏 2~3 天 | `WHEA-Logger Event 19` 数量 **= 0** |
| 4 | 温度与跑分 | `Cinebench R23` 多核 + `Core Temp` | ✅ 实测：`30500+ pts`，满载平均 `< 80°C`，峰值不超 `PTTL` |
| 5 | MUX | 任务管理器「性能」→ 是否仍有核显 | 独显直连下 610M 核显已断电消失 |
| 6 | 游戏帧率 | 记录 1% Low | 绑定单 CCD 后微卡顿减少 |

---

## 11. 故障速查

| 现象 | 高概率原因 | 处理 |
| :--- | :--- | :--- |
| 瞬间黑屏重启，`Event 41` 且 `BugcheckCode = 0`，无 dump | Curve Optimizer 负压导致轻载/唤醒瞬态欠压触发 SMU Sync Flood | 将 `Curve Optimizer` 改为 **`Disabled`（全核 `0`）** |
| 反复 `WHEA-Logger Event 19` + `Cache Hierarchy Error`，`APIC ID: 0`，`IPID = 0x000700B020350000` | `CCD1` 共享 32 MB L3 缓存在单核轻载唤醒时被 CO 负压拖死（由 `APIC 0` 轮询代报） | 将 `Curve Optimizer` 改为 **`Disabled`（全核 `0`）**，改用 `CBCO -250 MHz` 降温 |
| 蓝屏代码多变（`MEMORY_MANAGEMENT` 等） | 内存频率/时序不稳 | 内存回 `Auto` |
| 温度冲到 `100°C` 后锁 `0.5 GHz`，重启才恢复 | 触发 `BD PROCHOT` 硬件闩锁 | 设 `Platform Thermal Throttle Limit = 85`，检查散热与 VRM |
| 解锁 PPT 后 CPU 核心占用很低（10%~15%），但总功耗 75 W+、温度飙到 95~98°C | 处于混合输出（MSHybrid）模式，CPU 内部 610M 核显/IOD 满载发热且无独显交叉负载分流 | 切回 **独显直连（Discrete）** 并重启（核显物理断电 + 触发 EC 功耗分流） |
| BIOS 改完功耗墙，重启又变回低值 | 原版 MSI Center 后台服务覆盖 EC | 禁用其服务 / UWP 启动项 |
| 显卡切换点了没反应 | 写入被拒（权限）或标记已处于目标态 | 以管理员运行；再点一次原模式取消预约 |
| 进 BIOS 看不到 `AMD Overclocking` | 隐藏菜单未解锁 | `右 Ctrl + 右 Shift + 左 Alt + F2`（每次进 BIOS 都要按） |

---

## 12. 数值总表（一页速查）

| 项目 | 出厂默认 | **黄金稳定甜点（✅ 强烈推荐）** | 高功耗跑分档 |
| :--- | :--- | :--- | :--- |
| `PBO Limits` | `Auto`（约 55 W） | **`PPT 75000 ~ 85000` / `TDC 108000` / `EDC 160000`** | `PPT 110000` / `TDC 108000` / `EDC 160000` |
| `Curve Optimizer` | `Disabled`（全 0） | **`Disabled`（全核 0，彻底杜绝共享 L3 轻载 WHEA 重启）** | `Disabled`（全核 0） |
| 温度墙 `Thermal Limit (PTTL)` | `Auto`（100°C） | **`90`**（或 **`85`**，1ms PID 平滑控温） | `95` |
| 频率上限 `Boost Override (-)` | `Disabled` | **`Negative 250`**（或 **`200`**，削掉末端高压尖峰） | `Negative 200` |
| 内存 | `Auto` | **`5200`，时序 Auto** | `5200 ~ 5600` |
| 显卡模式（MUX） | 混合输出 | **独显直连（Discrete，物理关断 610M 核显寄生功耗）** | 独显直连（Discrete） |
| HVCI（内存完整性） | 开启 | **关闭** | 关闭 |

---

## 免责声明

本文是实测经验整理，不是官方规范。硬件寄存器写入与 BIOS 修改均有风险，请自行评估。不同机型、不同 EC 固件版本的寄存器语义可能不同 —— **先只读验证，再写**。
