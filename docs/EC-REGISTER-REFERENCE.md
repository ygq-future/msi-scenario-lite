# MSI EC / WMI 寄存器参考

> 本文档记录 [`MSI Scenario Lite`](../README.md) 用到的全部底层接口，来自对 MSI Center 托管程序集（`API_NB_Base Module.dll`、`UC_NB_Base Module.dll`、`MSIWMIACPI2.dll`）与 EC 行为日志（`WmiAcpi2.log`）的静态/动态分析。
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
bool discreteActive = (res.Data[0] & 0x40) != 0;   // bit6: 0x5B=Discrete, 0x1B=MSHybrid
```

`0x40` 置位 = 独显直连（Discrete），清零 = 混合输出（MSHybrid）。

### 4.2 `EC 0xD1`（`Get_AP(0).Data[1]`）固件状态机

MSI Center 在 `C_GeneralSettings.SetStatus("GraphicsSwithc")` 中切换显卡模式时，仅对 `EC 0xD1` 写入 `(b & 0xFC) | 0x01`。

当向 `0xD1` 写入 **bit 0 = 1**（如 `0x01` 或 `0x05`）时，EC 固件内部中断处理函数会：
1. 将「下次开机目标 MUX 模式」翻转（Discrete ↔ MSHybrid）；
2. 自动清零 bit 0（`0x01`），置起 bit 1（`0x02`，表示本次开机周期内触发过切换命令）；
3. 将 **bit 2（`0x04`）** 更新为翻转后的**下次开机目标模式**：

| `Get_AP(0).Data[1]` (`0xD1`) 回读值 | 含义 | 下次重启生效模式 |
| :---: | :--- | :--- |
| `0x00` | 本次开机后未触发过切换 | 保持当前运行模式（`discreteActive`） |
| **`0x06`** (`bit2=1, bit1=1`) | 已标记下次开机为独显直连 | **独显直连 (Discrete)** |
| **`0x02`** (`bit2=0, bit1=1`) | 已标记下次开机为混合输出 | **混合输出 (MSHybrid)** |

因此，读取下次启动预约模式的完整逻辑直接来自 `EC 0xD1`：

```csharp
byte d1 = ReadEcD1(); // Get_AP(0).Data[1]
bool stagedIsDiscrete = ((d1 & 0x02) != 0) ? ((d1 & 0x04) != 0) : discreteActive;
```

### 4.3 写入与取消预约的正确方式

无论是「预约切换到另一模式」还是「取消预约切回原模式」，**都必须向 `0xD1` 写入 `(b & 0xFC) | 0x01`（bit 0 = 1）** 以触发 EC 固件翻转处理器，并在写后回读校验 `Data[1]` 是否变为预期的 `0x06`（独显）或 `0x02`（混合）：

```text
开机初始 (Discrete, 0xD1=0x00)
  ├─ 点击混合输出：写 0x01 → EC 翻转目标并回读 0x02 (Staged = MSHybrid)
  └─ 再次点击独显直连（取消预约）：写 0x01 → EC 再次翻转目标并回读 0x06 (Staged = Discrete)
```

> ⚠️ **切勿向 `0xD1` 写入 `0x04`（bit 0 = 0）**：当 bit 0 为 0 时，EC 固件不会触发内部的 MUX 翻转逻辑（下次重启目标仍停留在上一次翻转后的状态），但却把 `0xD1` 寄存器字节覆盖成了 `0x04`，掩盖了真实的 `0x02 / 0x06` 状态回读。

### 4.4 关于 UEFI 变量 `MsiDCVarData` 的说明

微星目录中的 `ResetUEFIVariable.exe` 与 `UEFIVaribleDll.dll` 操作的 UEFI 变量 `MsiDCVarData`（GUID `{DD96BAAF-145E-4F56-B1CF-193256298E99}`）属于 `API_NB_Base Module.dll` 的 **`WinAndFnChange`（Win/Fn 键位互换）** 及卸载清理逻辑，与显卡 MUX 切换无关。显卡模式切换仅需操作 `EC 0xD1`。

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
| `0xD1` 必须以 bit0=1 触发翻转 | 写 `(b & 0xFC) \| 0x01` 后固件将 `0xD1` 更新为 `0x06`（独显）或 `0x02`（混合）；若写 `0x04`（bit0=0）不会触发固件翻转且会覆盖状态位 |
| `0xD2` 重启后硬件重置为 `0xC1` | 主板 EC 在每次重启后默认将场景恢复为均衡模式（`0xC1`），需持久化的场景可在软件启动时自动重放 |
| 内部索引 `3` 是废弃分支 | 会触发 2 秒后被改回 `0xC1`；静音应使用 `4` |
| 写 `0x50 / 0x51` 会覆盖 BIOS 功耗墙 | 本工具不写；原版 MSI Center 会写 |
| 注册表写入需要管理员 | 程序已内嵌 `requireAdministrator` |
| 强冷位不要整体覆盖 | 应基于 `Get_Thermal(3)` 原值做 bit7 置位/清零，保留其他位 |

---

## 7. 只读验证建议

在信任任何写操作之前，先跑一遍只读检查：

```csharp
byte shift    = ReadShiftByte();                                    // 0xC1 / 0xC2 / 0xC4
bool boost    = ReadCoolerBoost();                                  // 强冷是否开启
bool discrete = ReadIsDiscreteGpuMode();                            // 当前 MUX
byte d1;
bool staged   = ReadStagedGpuModeIsDiscrete(discrete, out d1);      // 下次启动预约 (0x00 / 0x02 / 0x06)
```

若这些值与你实际观察到的系统行为一致，说明寄存器语义匹配，可以继续。
