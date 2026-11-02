# MSI EC / WMI 寄存器参考

> 本文档记录 [`MSI Scenario Lite`](../README.md) 用到的全部底层接口，来自对 MSI Center 托管程序集（`API_NB_Base Module.dll`、`UC_NB_Base Module.dll`、`MSIWMIACPI2.dll`、`ResetUEFIVariable.exe`）与 EC 行为日志（`WmiAcpi2.log`）的静态/动态分析。
>
> **适用范围**：微星笔记本 + `WMIACPI 2.8` + EC 固件 `17KKIMS1.115`（Alpha 17 C7VF）。**其他机型 / 固件版本请先只读验证。**

---

## 1. 调用链

```text
MsiScenarioLite.exe
  └── MSIWMIACPI2.dll  (WmiAcpiLib2.GlobalInstance.InvokeWmiMethod)
        └── root\WMI  (MSI_AP / MSI_Data / MSI_Thermal / MSI_Device / MSI_Fan / MSI_Temperature)
              └── wmiacpi.sys  (Windows 自带)
                    └── 主板 EC  (ACPI DSDT 提供)
```

关键点：这条链路**不依赖任何微星后台服务**。卸载 MSI Center 后，只要 `wmiacpi.sys` 与主板 ACPI 表存在，接口照常可用。

UEFI 变量走另一条独立通道：

```text
UEFIVaribleDll.dll
  └── GetFirmwareEnvironmentVariableW / SetFirmwareEnvironmentVariableW  (kernel32)
        └── UEFI NVRAM
```

---

## 2. WMI 方法

| 方法 | 用途 | 返回 |
| :--- | :--- | :--- |
| `Get_AP(index)` | 读取 EC "AP" 块 | `Data[]` 字节数组 |
| `Get_Data(addr)` | 读 EC 单字节 | `Data[0]` |
| `Set_Data(addr, bytes)` | 写 EC | `Flag == 1` 表示成功 |
| `Get_Thermal(index)` | 读温控/风扇配置块 | `Data[]` |
| `Get_Device(index)` | 读设备状态（含 MUX） | `Data[]` |
| `Get_Fan(index)` | 读风扇转速原始值 | `Data[0..3]` |
| `Get_Temperature(index)` | 读温度 | `Data[0]=CPU`, `Data[1]=GPU` |

调用约定（C#）：

```csharp
var res = WmiAcpiLib2.GlobalInstance.InvokeWmiMethod(
    WmiAcpiLib2.Methods.Set_Data, (byte)0xD2, new byte[] { 0xC4 });

bool ok = res.InvokeMethodResult && res.Flag == 1;
```

---

## 3. EC 寄存器表

| 寄存器 | 读法 | 写值 | 含义 |
| :--- | :--- | :--- | :--- |
| `0xD2` | `Get_AP(0).Data[2]` 或 `Get_Data(0xD2)` | `0xC4` / `0xC1` / `0xC2` | 使用场景（Shift Mode）。`0xC4` = 游戏/极致性能，`0xC1` = 均衡，`0xC2` = 节能静音 |
| `0x98` | `Get_Thermal(3).Data[0]` | bit7 置 1 / 清 0 | 强冷风扇（Cooler Boost）。`0x86` = 开，`0x06` = 关（保留低位的既有配置） |
| `0xD4` | `Get_AP(1).Data[0]` | bit7 清 0 | 交还 EC 原生温度曲线（关闭软件风扇表） |
| `0xD1` | `Get_AP(0).Data[1]` | 见 §4 | 显卡模式（MUX）切换**预约锁存** |
| `0x50` | — | **不要写** | `PL1` 长时功耗限制 |
| `0x51` | — | **不要写** | `PL2` 短时功耗限制 |

> ⚠️ `0x50 / 0x51` 是原版 MSI Center 覆盖 BIOS 功耗墙的入口。本工具刻意不触碰它们。

### 风扇转速换算

```text
raw = (high << 8) | low
rpm = 478000 / raw        // raw == 0 时视为 0 RPM
```

`Get_Fan(0).Data` 布局：`[0..1]` = CPU 风扇（高/低字节），`[2..3]` = GPU 风扇。

---

## 4. 显卡模式（MUX）

### 4.1 读取"当前运行状态"

```csharp
var res = Wmi.InvokeWmiMethod(Methods.Get_Device, (byte)1, null);
bool discreteActive = (res.Data[0] & 0x40) != 0;   // bit6
```

`0x40` 置位 = 独显直连（Discrete），清零 = 混合输出（MSHybrid）。

### 4.2 读取"下次启动的预约状态"

```csharp
var ap = Wmi.InvokeWmiMethod(Methods.Get_AP, (byte)0, null);
bool pending = (ap.Data[1] & 0x03) != 0;
```

`Data[1]` 低两位是**切换锁存**，不是模式编码。

### 4.3 写入语义（关键，容易踩坑）

`EC 0xD1` 的低两位是 **toggle latch（翻转锁存）**，而不是"独显=1 / 混合=2"：

| 操作 | 写入值 | 回读 `Get_AP(0).Data[1]` |
| :--- | :--- | :--- |
| 预约下次重启翻转 MUX | `(b & 0xFC) \| 0x01`（通常为 `0x05`） | `0x06`（bit1 被固件置起，表示"已挂起"） |
| 取消预约 / 回到当前模式 | `b & 0xFC`（通常为 `0x04`） | `0x04` |

因为固件会把 bit0 吸收并置起 bit1，**不要**通过回读值反推"目标模式"；正确做法是结合当前 MUX 状态判断：

```text
pending ? (current == Discrete ? 目标为 MSHybrid : 目标为 Discrete)
        : current
```

### 4.4 UEFI 变量

| 项 | 值 |
| :--- | :--- |
| 变量名 | `MsiDCVarData` |
| GUID | `{DD96BAAF-145E-4F56-B1CF-193256298E99}` |
| 关键字节 | `buffer[58]`：`0` / `1` 之间翻转 |

`ResetUEFIVariable.exe` 是**盲翻转**（读取 → 取反 → 写回）。因此只在"预约状态发生翻转"时调用一次，否则会把标记改回去。

直接 P/Invoke 更快（避免启动子进程）：

```csharp
[DllImport("UEFIVaribleDll.dll", CharSet = CharSet.Unicode)]
static extern bool GetVariable(string name, string guid, byte[] buffer);
[DllImport("UEFIVaribleDll.dll", CharSet = CharSet.Unicode)]
static extern bool SetVariable(string name, string guid, byte[] buffer, int size);
```

---

## 5. 与 MSI Center 的状态同步

### 5.1 内部场景索引（3 卡片界面）

| 界面卡片 | 内部索引 | 注册表 `Performance` | 实际 EC 值 |
| :--- | :---: | :---: | :---: |
| 游戏模式（极致性能） | `1` | `0` | `0xC4` |
| 均衡模式 | `2` | `2` | `0xC1` |
| 节能静音模式（绿叶） | **`4`** | `3` | `0xC2` |
| ~~旧版静音（已隐藏）~~ | `3` | `2` | `0xC1` |

> **坑**：若把 `3` 写进注册表，原版 MSI Center 会去勾选一个已被隐藏的按钮（界面上三个可见卡片全部取消选中），并且它后台的 `shiftTimer` 会在约 2 秒后按 `3_Scenario\Performance = 2` 把 EC 改回 `0xC1` —— 表现为"刚切到节能静音，几秒后自己跳回均衡"。

### 5.2 注册表位置（32 位视图）

```text
HKLM\SOFTWARE\WOW6432Node\MSI\MSI Center\Component\Base Module\User Scenario
    Intelligent = 0 (DWORD)
    Mode        = 1 | 2 | 4 (DWORD)
    ACA_CH      = 4 (DWORD)

HKLM\SOFTWARE\WOW6432Node\MSI\MSI Center\Component\Base Module\GeneralSetting
    OverWolf    = 1 | 2 | 4 (DWORD)   ← 写入会触发原版 UI 的 RegistryValueChangeEvent 监听
    GPU_Switch  = 0 (Discrete) | 1 (MSHybrid)

HKLM\SOFTWARE\WOW6432Node\MSI\MSI Center\Component\AI Engine
    Engine      = 0 (DWORD)           ← 关闭 AI 引擎接管
```

原版 MSI Center 的 UI 在后台挂了 WMI 注册表监听器：

```sql
SELECT * FROM RegistryValueChangeEvent
WHERE Hive='HKEY_LOCAL_MACHINE'
  AND KeyPath='...\Base Module\GeneralSetting'
  AND ValueName='OverWolf'
```

因此写 `OverWolf` 可以让**已打开的原版界面实时跟着变**。若 MSI Center 已卸载，注册表项不存在，跳过即可（不要抛异常）。

---

## 6. 已知坑位汇总

| 坑 | 说明 |
| :--- | :--- |
| `0xD1` 不是模式编码 | 是 toggle latch；回读值不代表目标模式 |
| `ResetUEFIVariable.exe` 是盲翻转 | 重复调用会把标记改回去 |
| 内部索引 `3` 是废弃分支 | 会触发 2 秒后被改回 `0xC1`；静音应使用 `4` |
| 写 `0x50 / 0x51` 会覆盖 BIOS 功耗墙 | 本工具不写；原版 MSI Center 会写 |
| 注册表写入需要管理员 | 程序已内嵌 `requireAdministrator` |
| 强冷位不要整体覆盖 | 应基于 `Get_Thermal(3)` 原值做 bit7 置位/清零，保留其他位 |

---

## 7. 只读验证建议

在信任任何写操作之前，先跑一遍只读检查：

```csharp
byte shift   = ReadShiftByte();               // 0xC1 / 0xC2 / 0xC4
bool boost   = ReadCoolerBoost();             // 强冷是否开启
bool discrete = ReadIsDiscreteGpuMode();      // 当前 MUX
bool? staged  = ReadStagedGpuModeIsDiscrete();// 下次启动预约
```

若这些值与你实际观察到的系统行为一致，说明寄存器语义匹配，可以继续。
