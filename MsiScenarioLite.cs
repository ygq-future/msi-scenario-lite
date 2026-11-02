using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using Microsoft.Win32;
using MSIWMIACPI2;
using WinForms = System.Windows.Forms;
using Drawing = System.Drawing;

[assembly: AssemblyTitle("MSI Scenario Lite")]
[assembly: AssemblyProduct("MSI Scenario Lite (MSI EC / WMIACPI)")]
[assembly: AssemblyVersion("1.3.0.0")]

namespace MsiScenarioLite
{
    public static class EcHardwareController
    {
        [DllImport("UEFIVaribleDll.dll", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
        private static extern bool CheckPrivileges();

        [DllImport("UEFIVaribleDll.dll", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
        private static extern bool GetVariable(string name, string guid, byte[] buffer);

        [DllImport("UEFIVaribleDll.dll", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
        private static extern bool SetVariable(string name, string guid, byte[] buffer, int size);

        private const string MsiUefiVarName = "MsiDCVarData";
        private const string MsiUefiVarGuid = "{DD96BAAF-145E-4F56-B1CF-193256298E99}";

        private static WmiAcpiLib2 Wmi
        {
            get { return WmiAcpiLib2.GlobalInstance; }
        }

        public static bool IsAdministrator()
        {
            using (WindowsIdentity id = WindowsIdentity.GetCurrent())
            {
                WindowsPrincipal principal = new WindowsPrincipal(id);
                return principal.IsInRole(WindowsBuiltInRole.Administrator);
            }
        }

        public static byte ReadShiftByte()
        {
            var res = Wmi.InvokeWmiMethod(WmiAcpiLib2.Methods.Get_AP, (byte)0, null);
            if (res.InvokeMethodResult && res.Flag == 1 && res.Data != null && res.Data.Length > 2)
            {
                return res.Data[2];
            }
            var fallback = Wmi.InvokeWmiMethod(WmiAcpiLib2.Methods.Get_Data, (byte)0xD2, null);
            if (fallback.InvokeMethodResult && fallback.Flag == 1 && fallback.Data != null && fallback.Data.Length > 0)
            {
                return fallback.Data[0];
            }
            return 0xC1;
        }

        public static bool WriteShiftMode(byte modeCode, bool enableCoolerBoost, out string syncStatus)
        {
            // 1. Write target Shift Mode into EC 0xD2 (0xC4 = Gaming/Extreme, 0xC1 = Balanced, 0xC2 = Silent/ECO)
            // Strictly avoids writing EC 0x50 (PL1) or 0x51 (PL2)
            var rShift = Wmi.InvokeWmiMethod(WmiAcpiLib2.Methods.Set_Data, (byte)0xD2, new byte[] { modeCode });
            bool ok = rShift.InvokeMethodResult && rShift.Flag == 1;

            // 2. Update Cooler Boost bit (bit 7 of Get_Thermal(3) written to EC 0x98)
            SetCoolerBoost(enableCoolerBoost);

            // 3. Ensure EC Auto Fan bit (EC 0xD4 bit 7 = 0) when CoolerBoost is off
            if (!enableCoolerBoost)
            {
                EnsureAutoFan();
            }

            // 4. Safely sync MSI Center Registry if MSI Center is still installed; never throws if uninstalled!
            syncStatus = SyncMsiCenterRegistryAndNotifyUI(modeCode);
            return ok;
        }

        public static string SyncMsiCenterRegistryAndNotifyUI(byte modeCode)
        {
            try
            {
                // In MSI Center (UC_User_P_ScenarioIntelligent):
                //   Index 1 = 游戏模式 (Extreme Performance, EC 0xC4, 1_Scenario\Performance=0)
                //   Index 2 = 均衡模式 (Balanced, EC 0xC1, 2_Scenario\Performance=2)
                //   Index 3 = 旧版隐藏静音 (Silent_Mode_Copy, 3_Scenario\Performance=2 -> EC 0xC1!)
                //   Index 4 = 节能静音模式 (Battery_Mode / NewECO leaf card, EC 0xC2, 4_Scenario\Performance=3!)
                int msiModeIndex = (modeCode == 0xC4 || modeCode == 0xC0) ? 1 : (modeCode == 0xC2 || modeCode == 0xC3 ? 4 : 2);

                using (RegistryKey baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32))
                {
                    using (RegistryKey usKey = baseKey.OpenSubKey(@"SOFTWARE\MSI\MSI Center\Component\Base Module\User Scenario", true))
                    {
                        if (usKey == null)
                        {
                            // MSI Center is already uninstalled -> 100% standalone mode, no error!
                            return "独立运行(未装MSI Center)";
                        }
                        usKey.SetValue("Intelligent", 0, RegistryValueKind.DWord);
                        usKey.SetValue("Mode", msiModeIndex, RegistryValueKind.DWord);
                        usKey.SetValue("ACA_CH", 4, RegistryValueKind.DWord);
                    }
                    using (RegistryKey aiKey = baseKey.OpenSubKey(@"SOFTWARE\MSI\MSI Center\Component\AI Engine", true))
                    {
                        if (aiKey != null)
                        {
                            aiKey.SetValue("Engine", 0, RegistryValueKind.DWord);
                        }
                    }
                    using (RegistryKey gsKey = baseKey.OpenSubKey(@"SOFTWARE\MSI\MSI Center\Component\Base Module\GeneralSetting", true))
                    {
                        if (gsKey != null)
                        {
                            gsKey.SetValue("OverWolf", 0, RegistryValueKind.DWord);
                            gsKey.SetValue("OverWolf", msiModeIndex, RegistryValueKind.DWord);
                        }
                    }
                }
                return "已同步";
            }
            catch
            {
                return "需管理员权限";
            }
        }

        public static bool ReadCoolerBoost()
        {
            var res = Wmi.InvokeWmiMethod(WmiAcpiLib2.Methods.Get_Thermal, (byte)3, null);
            if (res.InvokeMethodResult && res.Flag == 1 && res.Data != null && res.Data.Length > 0)
            {
                return (res.Data[0] & 0x80) != 0;
            }
            var fallback = Wmi.InvokeWmiMethod(WmiAcpiLib2.Methods.Get_Data, (byte)0x98, null);
            if (fallback.InvokeMethodResult && fallback.Flag == 1 && fallback.Data != null && fallback.Data.Length > 0)
            {
                return (fallback.Data[0] & 0x80) != 0;
            }
            return false;
        }

        public static bool SetCoolerBoost(bool enable)
        {
            byte baseVal = 0x06;
            var res = Wmi.InvokeWmiMethod(WmiAcpiLib2.Methods.Get_Thermal, (byte)3, null);
            if (res.InvokeMethodResult && res.Flag == 1 && res.Data != null && res.Data.Length > 0)
            {
                baseVal = res.Data[0];
            }
            byte nextVal = enable ? (byte)(baseVal | 0x80) : (byte)(baseVal & 0x7F);
            var w = Wmi.InvokeWmiMethod(WmiAcpiLib2.Methods.Set_Data, (byte)0x98, new byte[] { nextVal });
            return w.InvokeMethodResult && w.Flag == 1;
        }

        public static void EnsureAutoFan()
        {
            var res = Wmi.InvokeWmiMethod(WmiAcpiLib2.Methods.Get_AP, (byte)1, null);
            if (res.InvokeMethodResult && res.Flag == 1 && res.Data != null && res.Data.Length > 0)
            {
                byte val = (byte)(res.Data[0] & 0x7F);
                Wmi.InvokeWmiMethod(WmiAcpiLib2.Methods.Set_Data, (byte)0xD4, new byte[] { val });
            }
        }

        public static bool ReadIsDiscreteGpuMode()
        {
            var res = Wmi.InvokeWmiMethod(WmiAcpiLib2.Methods.Get_Device, (byte)1, null);
            if (res.InvokeMethodResult && res.Flag == 1 && res.Data != null && res.Data.Length > 0)
            {
                return (res.Data[0] & 0x40) != 0;
            }
            return true;
        }

        public static bool? ReadStagedGpuModeIsDiscrete()
        {
            bool currentDiscrete = ReadIsDiscreteGpuMode();
            var rAp = Wmi.InvokeWmiMethod(WmiAcpiLib2.Methods.Get_AP, (byte)0, null);
            if (rAp.InvokeMethodResult && rAp.Flag == 1 && rAp.Data != null && rAp.Data.Length > 1)
            {
                byte b = rAp.Data[1];
                bool switchPending = (b & 0x03) != 0;
                return switchPending ? !currentDiscrete : currentDiscrete;
            }
            return currentDiscrete;
        }

        private static void ToggleUefiGpuVariableFast()
        {
            try
            {
                if (CheckPrivileges())
                {
                    byte[] buf = new byte[4096];
                    if (GetVariable(MsiUefiVarName, MsiUefiVarGuid, buf))
                    {
                        buf[58] = (buf[58] == 1) ? (byte)0 : (byte)1;
                        if (SetVariable(MsiUefiVarName, MsiUefiVarGuid, buf, 72))
                        {
                            return;
                        }
                    }
                }
            }
            catch { }

            // Fallback to ResetUEFIVariable.exe only if direct P/Invoke fails
            try
            {
                string baseDir = AppDomain.CurrentDomain.BaseDirectory;
                string resetExe = System.IO.Path.Combine(baseDir, "ResetUEFIVariable.exe");
                if (!File.Exists(resetExe))
                {
                    resetExe = @"C:\Program Files (x86)\MSI\MSI NBFoundation Service\ResetUEFIVariable.exe";
                }
                if (File.Exists(resetExe))
                {
                    ProcessStartInfo psi = new ProcessStartInfo(resetExe)
                    {
                        WorkingDirectory = System.IO.Path.GetDirectoryName(resetExe),
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        WindowStyle = ProcessWindowStyle.Hidden
                    };
                    using (Process p = Process.Start(psi))
                    {
                        if (p != null) p.WaitForExit(2000);
                    }
                }
            }
            catch { }
        }

        public static bool StageGpuModeForNextBoot(bool toDiscrete, bool wasDiffBefore, out string errorMsg)
        {
            errorMsg = null;
            try
            {
                bool currentDiscrete = ReadIsDiscreteGpuMode();
                bool wantSwitchOnBoot = (toDiscrete != currentDiscrete);

                var rAp = Wmi.InvokeWmiMethod(WmiAcpiLib2.Methods.Get_AP, (byte)0, null);
                byte b = 0x04;
                if (rAp.InvokeMethodResult && rAp.Flag == 1 && rAp.Data != null && rAp.Data.Length > 1)
                {
                    b = rAp.Data[1];
                }

                byte writeVal = wantSwitchOnBoot ? (byte)((b & 0xFC) | 0x01) : (byte)(b & 0xFC);

                var w = Wmi.InvokeWmiMethod(WmiAcpiLib2.Methods.Set_Data, (byte)0xD1, new byte[] { writeVal });
                if (!w.InvokeMethodResult || w.Flag != 1)
                {
                    errorMsg = "写入 EC 0xD1 失败（请确保以管理员身份运行）。";
                    return false;
                }

                var rVerify = Wmi.InvokeWmiMethod(WmiAcpiLib2.Methods.Get_AP, (byte)0, null);
                if (rVerify.InvokeMethodResult && rVerify.Flag == 1 && rVerify.Data != null && rVerify.Data.Length > 1)
                {
                    bool latchAfter = (rVerify.Data[1] & 0x03) != 0;
                    if (latchAfter != wantSwitchOnBoot)
                    {
                        errorMsg = "EC 0xD1 状态未更新（请以管理员身份运行）。";
                        return false;
                    }
                }

                try
                {
                    using (RegistryKey baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32))
                    using (RegistryKey gsKey = baseKey.OpenSubKey(@"SOFTWARE\MSI\MSI Center\Component\Base Module\GeneralSetting", true))
                    {
                        if (gsKey != null)
                        {
                            gsKey.SetValue("GPU_Switch", toDiscrete ? 0 : 1, RegistryValueKind.DWord);
                        }
                    }
                }
                catch { }

                if (wasDiffBefore != wantSwitchOnBoot)
                {
                    ToggleUefiGpuVariableFast();
                }
                return true;
            }
            catch (Exception ex)
            {
                errorMsg = ex.Message;
                return false;
            }
        }

        public static void ReadTelemetry(out int cpuTemp, out int gpuTemp, out int cpuRpm, out int gpuRpm)
        {
            cpuTemp = 0;
            gpuTemp = 0;
            cpuRpm = 0;
            gpuRpm = 0;

            var rTmp = Wmi.InvokeWmiMethod(WmiAcpiLib2.Methods.Get_Temperature, (byte)0, null);
            if (rTmp.InvokeMethodResult && rTmp.Flag == 1 && rTmp.Data != null && rTmp.Data.Length >= 2)
            {
                cpuTemp = rTmp.Data[0];
                gpuTemp = rTmp.Data[1];
            }

            var rFan = Wmi.InvokeWmiMethod(WmiAcpiLib2.Methods.Get_Fan, (byte)0, null);
            if (rFan.InvokeMethodResult && rFan.Flag == 1 && rFan.Data != null && rFan.Data.Length >= 4)
            {
                cpuRpm = ConvertFanSpeed(rFan.Data[0], rFan.Data[1]);
                gpuRpm = ConvertFanSpeed(rFan.Data[2], rFan.Data[3]);
            }
        }

        private static int ConvertFanSpeed(byte highByte, byte lowByte)
        {
            int raw = (highByte << 8) + lowByte;
            if (raw <= 0) return 0;
            int rpm = (int)(478000.0 / raw);
            return rpm < 0 ? 0 : rpm;
        }
    }

    public class MainWindow : Window
    {
        private Border cardGaming;
        private Border cardBalanced;
        private Border cardSilent;

        private TextBlock badgeGaming;
        private TextBlock badgeBalanced;
        private TextBlock badgeSilent;

        private Border btnCoolerBoost;
        private TextBlock txtCoolerBoostState;
        private Ellipse dotCoolerBoost;

        private Grid gpuBarGrid;
        private Border gpuSlidingPill;
        private TranslateTransform gpuPillTransform;
        private Border segDiscrete;
        private Border segHybrid;
        private TextBlock lblDiscreteTitle;
        private TextBlock lblHybridTitle;
        private TextBlock txtGpuCurrentBadge;

        private Border centerHandle;
        private TextBlock txtCenterArrow;
        private Canvas spinnerCanvas;
        private RotateTransform spinnerRotate;
        private bool isBusySwitching = false;

        private TextBlock txtTelemetry;
        private TextBlock txtEcStatus;
        private DispatcherTimer pollTimer;

        private WinForms.NotifyIcon trayIcon;
        private WinForms.MenuItem menuGaming;
        private WinForms.MenuItem menuBalanced;
        private WinForms.MenuItem menuSilent;
        private WinForms.MenuItem menuCoolerBoost;
        private WinForms.MenuItem menuGpuDiscrete;
        private WinForms.MenuItem menuGpuHybrid;
        private bool allowRealExit = false;

        private byte currentShiftByte = 0xC1;
        private bool currentCoolerBoost = false;
        private bool currentIsDiscrete = true;
        private bool? stagedIsDiscrete = null;
        private bool? lastAnimatedDiscrete = null;

        public MainWindow()
        {
            Title = "MSI Scenario Lite";
            Width = 780;
            SizeToContent = SizeToContent.Height;
            ResizeMode = ResizeMode.NoResize;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;

            string logoPngPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Assets", "logo.png");
            string icoPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Assets", "app_v2.ico");
            if (!File.Exists(icoPath))
            {
                icoPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Assets", "app.ico");
            }
            if (File.Exists(logoPngPath))
            {
                try
                {
                    Icon = LoadAsset("logo.png");
                }
                catch { }
            }

            BuildUI();
            InitSystemTray(icoPath);
            RefreshHardwareState(false);

            pollTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(2.0)
            };
            pollTimer.Tick += (s, e) =>
            {
                if (!isBusySwitching) RefreshHardwareState(true);
            };
            pollTimer.Start();
        }

        private void InitSystemTray(string icoPath)
        {
            trayIcon = new WinForms.NotifyIcon
            {
                Text = "MSI Scenario Lite (双击打开面板 / 右键快速切换)",
                Visible = true
            };

            if (File.Exists(icoPath))
            {
                try { trayIcon.Icon = new Drawing.Icon(icoPath); }
                catch { trayIcon.Icon = Drawing.SystemIcons.Application; }
            }
            else
            {
                trayIcon.Icon = Drawing.SystemIcons.Application;
            }

            WinForms.ContextMenu menu = new WinForms.ContextMenu();

            WinForms.MenuItem menuOpen = new WinForms.MenuItem("打开控制面板", (s, e) => ShowMainWindow());
            menuOpen.DefaultItem = true;
            menu.MenuItems.Add(menuOpen);
            menu.MenuItems.Add("-");

            menuGaming = new WinForms.MenuItem("游戏模式 (极致性能 + 自动开启强冷)", (s, e) => ApplyScenarioAsync(0xC4, true));
            menuBalanced = new WinForms.MenuItem("均衡模式 (日常均衡 + 自动温控风扇)", (s, e) => ApplyScenarioAsync(0xC1, false));
            menuSilent = new WinForms.MenuItem("节能静音模式 (低噪办公 + 自动温控风扇)", (s, e) => ApplyScenarioAsync(0xC2, false));
            menu.MenuItems.Add(menuGaming);
            menu.MenuItems.Add(menuBalanced);
            menu.MenuItems.Add(menuSilent);
            menu.MenuItems.Add("-");

            menuCoolerBoost = new WinForms.MenuItem("手动开关强冷模式 (Cooler Boost)", (s, e) =>
            {
                EcHardwareController.SetCoolerBoost(!currentCoolerBoost);
                RefreshHardwareState(true);
            });
            menu.MenuItems.Add(menuCoolerBoost);
            menu.MenuItems.Add("-");

            menuGpuDiscrete = new WinForms.MenuItem("显卡模式：独显直连 (Discrete)", (s, e) => OnGpuSwitchClickedAsync(true));
            menuGpuHybrid = new WinForms.MenuItem("显卡模式：混合输出 (MSHybrid)", (s, e) => OnGpuSwitchClickedAsync(false));
            menu.MenuItems.Add(menuGpuDiscrete);
            menu.MenuItems.Add(menuGpuHybrid);
            menu.MenuItems.Add("-");

            WinForms.MenuItem menuExit = new WinForms.MenuItem("完全退出程序", (s, e) =>
            {
                allowRealExit = true;
                if (trayIcon != null)
                {
                    trayIcon.Visible = false;
                    trayIcon.Dispose();
                    trayIcon = null;
                }
                Close();
            });
            menu.MenuItems.Add(menuExit);

            trayIcon.ContextMenu = menu;
            trayIcon.DoubleClick += (s, e) => ShowMainWindow();
        }

        private void ShowMainWindow()
        {
            Show();
            if (WindowState == WindowState.Minimized)
            {
                WindowState = WindowState.Normal;
            }
            Activate();
            RefreshHardwareState(false);
        }

        protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
            if (!allowRealExit && trayIcon != null)
            {
                e.Cancel = true;
                Hide();
                return;
            }
            if (trayIcon != null)
            {
                trayIcon.Visible = false;
                trayIcon.Dispose();
                trayIcon = null;
            }
            base.OnClosing(e);
        }

        private BitmapImage LoadAsset(string fileName)
        {
            try
            {
                string path = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Assets", fileName);
                if (File.Exists(path))
                {
                    BitmapImage bmp = new BitmapImage();
                    bmp.BeginInit();
                    bmp.CacheOption = BitmapCacheOption.OnLoad;
                    bmp.UriSource = new Uri(path, UriKind.Absolute);
                    bmp.EndInit();
                    bmp.Freeze();
                    return bmp;
                }
            }
            catch { }
            return null;
        }

        // Creates a razor-sharp vector gauge badge for the top-left title bar (never blurry at any DPI!)
        private UIElement CreateCrispVectorLogoBadge()
        {
            Border badge = new Border
            {
                Width = 24,
                Height = 24,
                CornerRadius = new CornerRadius(6),
                Margin = new Thickness(0, 0, 9, 0),
                VerticalAlignment = VerticalAlignment.Center,
                BorderThickness = new Thickness(1.6),
                Background = new SolidColorBrush(Color.FromRgb(16, 19, 36)),
                BorderBrush = new LinearGradientBrush(
                    Color.FromRgb(245, 48, 82),
                    Color.FromRgb(58, 142, 255),
                    new Point(0, 0),
                    new Point(1, 1))
            };

            Canvas cv = new Canvas { Width = 20, Height = 20, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };

            // Gauge arc
            System.Windows.Shapes.Path arcPath = new System.Windows.Shapes.Path
            {
                Stroke = new LinearGradientBrush(Color.FromRgb(110, 185, 255), Color.FromRgb(255, 72, 102), new Point(0, 0), new Point(1, 0)),
                StrokeThickness = 2.2,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                Data = Geometry.Parse("M 4.5,14.5 A 6.5,6.5 0 1 1 15.5,14.5")
            };
            cv.Children.Add(arcPath);

            // Gauge needle
            Line needle = new Line
            {
                X1 = 10,
                Y1 = 11.5,
                X2 = 14.5,
                Y2 = 7.2,
                Stroke = Brushes.White,
                StrokeThickness = 2.0,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round
            };
            cv.Children.Add(needle);

            Ellipse pivot = new Ellipse
            {
                Width = 3.6,
                Height = 3.6,
                Fill = new SolidColorBrush(Color.FromRgb(255, 65, 95))
            };
            Canvas.SetLeft(pivot, 8.2);
            Canvas.SetTop(pivot, 9.7);
            cv.Children.Add(pivot);

            badge.Child = cv;
            return badge;
        }

        private void BuildUI()
        {
            Border outerBorder = new Border
            {
                CornerRadius = new CornerRadius(12),
                BorderThickness = new Thickness(1),
                BorderBrush = new SolidColorBrush(Color.FromRgb(42, 48, 78)),
                SnapsToDevicePixels = true,
                UseLayoutRounding = true,
                Background = new LinearGradientBrush(
                    Color.FromRgb(11, 13, 24),
                    Color.FromRgb(17, 21, 38),
                    new Point(0, 0),
                    new Point(1, 1))
            };

            Grid rootGrid = new Grid();
            rootGrid.SizeChanged += (s, e) =>
            {
                rootGrid.Clip = new RectangleGeometry(new Rect(0, 0, e.NewSize.Width, e.NewSize.Height), 11, 11);
            };

            rootGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(46) }); // Top Bar
            rootGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });     // Content (Auto-sized so top & bottom gaps are 100% symmetrical!)
            rootGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(32) }); // Bottom Status Bar

            BitmapImage chipBmp = LoadAsset("chip_bg.png");
            if (chipBmp != null)
            {
                Image bgChip = new Image
                {
                    Source = chipBmp,
                    Width = 240,
                    Height = 240,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    VerticalAlignment = VerticalAlignment.Top,
                    Margin = new Thickness(0, 6, 12, 0),
                    Opacity = 0.24,
                    IsHitTestVisible = false
                };
                Grid.SetRowSpan(bgChip, 2);
                rootGrid.Children.Add(bgChip);
            }

            // ================= 1. TOP BAR =================
            Border topBarBorder = new Border
            {
                CornerRadius = new CornerRadius(11, 11, 0, 0),
                Background = new SolidColorBrush(Color.FromArgb(130, 14, 17, 30)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(28, 34, 58)),
                BorderThickness = new Thickness(0, 0, 0, 1)
            };
            topBarBorder.MouseLeftButtonDown += (s, e) =>
            {
                if (e.ButtonState == MouseButtonState.Pressed) DragMove();
            };

            Grid topBar = new Grid();

            StackPanel brandPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(24, 0, 0, 0)
            };

            brandPanel.Children.Add(CreateCrispVectorLogoBadge());

            TextBlock txtBrand = new TextBlock
            {
                Text = "MSI Scenario Lite",
                Foreground = Brushes.White,
                FontSize = 15,
                FontWeight = FontWeights.Bold,
                VerticalAlignment = VerticalAlignment.Center
            };
            Border liteTag = new Border
            {
                Background = new LinearGradientBrush(Color.FromRgb(229, 37, 69), Color.FromRgb(68, 104, 245), new Point(0, 0), new Point(1, 0)),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(6, 1.5, 6, 1.5),
                Margin = new Thickness(8, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock
                {
                    Text = "PURE EC LITE",
                    Foreground = Brushes.White,
                    FontSize = 9.5,
                    FontWeight = FontWeights.Bold
                }
            };

            brandPanel.Children.Add(txtBrand);
            brandPanel.Children.Add(liteTag);
            topBar.Children.Add(brandPanel);

            StackPanel rightTopPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 12, 0)
            };

            Border telemetryPill = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(180, 24, 29, 52)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(48, 58, 96)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(10, 4, 10, 4),
                Margin = new Thickness(0, 0, 12, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            txtTelemetry = new TextBlock
            {
                Text = "CPU --°C (-- RPM)  |  GPU --°C (-- RPM)",
                Foreground = new SolidColorBrush(Color.FromRgb(186, 198, 232)),
                FontFamily = new FontFamily("Consolas, Microsoft YaHei UI"),
                FontSize = 11.5,
                VerticalAlignment = VerticalAlignment.Center
            };
            telemetryPill.Child = txtTelemetry;
            rightTopPanel.Children.Add(telemetryPill);

            Button btnMin = CreateWindowButton("—", false, "最小化到任务栏");
            btnMin.Click += (s, e) => WindowState = WindowState.Minimized;
            Button btnClose = CreateWindowButton("✕", true, "隐藏到右下角系统托盘 (右键托盘图标可完全退出)");
            btnClose.Click += (s, e) => Hide();

            rightTopPanel.Children.Add(btnMin);
            rightTopPanel.Children.Add(btnClose);
            topBar.Children.Add(rightTopPanel);

            topBarBorder.Child = topBar;
            Grid.SetRow(topBarBorder, 0);
            rootGrid.Children.Add(topBarBorder);

            // ================= 2. MAIN CONTENT (Exact 12px top, 12px middle, 12px bottom symmetry!) =================
            Grid contentGrid = new Grid
            {
                Margin = new Thickness(24, 12, 24, 12)
            };
            contentGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // Heading "场景设置" + Cooler Boost
            contentGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // 3 Full-Width Scenario Cards
            contentGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // GPU Switch Section

            Grid scenarioHeader = new Grid
            {
                Margin = new Thickness(0, 0, 0, 10)
            };
            StackPanel titleStack = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center
            };
            TextBlock lblScenarioTitle = new TextBlock
            {
                Text = "场景设置",
                Foreground = Brushes.White,
                FontSize = 20,
                FontWeight = FontWeights.Bold,
                VerticalAlignment = VerticalAlignment.Center
            };
            TextBlock lblScenarioSub = new TextBlock
            {
                Text = "EC 直写 + 实时同步 MSI Center · 游戏模式自动开启强冷",
                Foreground = new SolidColorBrush(Color.FromRgb(132, 144, 180)),
                FontSize = 12,
                Margin = new Thickness(10, 3, 0, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            titleStack.Children.Add(lblScenarioTitle);
            titleStack.Children.Add(lblScenarioSub);
            scenarioHeader.Children.Add(titleStack);

            btnCoolerBoost = new Border
            {
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center,
                Background = new SolidColorBrush(Color.FromRgb(25, 31, 56)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(58, 70, 114)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(7),
                Padding = new Thickness(12, 5, 12, 5),
                Cursor = Cursors.Hand
            };
            StackPanel cbStack = new StackPanel { Orientation = Orientation.Horizontal };
            dotCoolerBoost = new Ellipse
            {
                Width = 8,
                Height = 8,
                Fill = new SolidColorBrush(Color.FromRgb(110, 120, 150)),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 7, 0)
            };
            txtCoolerBoostState = new TextBlock
            {
                Text = "强冷风扇: 自动",
                Foreground = new SolidColorBrush(Color.FromRgb(200, 210, 240)),
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center
            };
            cbStack.Children.Add(dotCoolerBoost);
            cbStack.Children.Add(txtCoolerBoostState);
            btnCoolerBoost.Child = cbStack;
            btnCoolerBoost.MouseLeftButtonDown += (s, e) =>
            {
                bool next = !currentCoolerBoost;
                EcHardwareController.SetCoolerBoost(next);
                RefreshHardwareState(true);
            };
            scenarioHeader.Children.Add(btnCoolerBoost);

            Grid.SetRow(scenarioHeader, 0);
            contentGrid.Children.Add(scenarioHeader);

            // Section 1 Cards: 12px bottom margin to GPU section (matches 12px top and 12px bottom of contentGrid!)
            Grid cardsGrid = new Grid
            {
                Margin = new Thickness(0, 0, 0, 12)
            };
            cardsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            cardsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
            cardsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            cardsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
            cardsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            cardGaming = CreateScenarioCard(
                "游戏模式",
                "极致性能 + 自动强冷 (0xC4)",
                "保留 BIOS 110W 上限并开启满速散热",
                "extreme.png",
                out badgeGaming,
                () => ApplyScenarioAsync(0xC4, true));

            cardBalanced = CreateScenarioCard(
                "均衡模式",
                "日常均衡 + 自动温控 (0xC1)",
                "自动温控风扇 · 动静皆宜",
                "balanced.png",
                out badgeBalanced,
                () => ApplyScenarioAsync(0xC1, false));

            cardSilent = CreateScenarioCard(
                "节能静音模式",
                "低噪静音 + 自动温控 (0xC2)",
                "轻载低频低噪 · 专注办公",
                "silent.png",
                out badgeSilent,
                () => ApplyScenarioAsync(0xC2, false));

            Grid.SetColumn(cardGaming, 0);
            Grid.SetColumn(cardBalanced, 2);
            Grid.SetColumn(cardSilent, 4);
            cardsGrid.Children.Add(cardGaming);
            cardsGrid.Children.Add(cardBalanced);
            cardsGrid.Children.Add(cardSilent);

            Grid.SetRow(cardsGrid, 1);
            contentGrid.Children.Add(cardsGrid);

            // ================= 3. GPU SWITCH SECTION =================
            StackPanel gpuSection = new StackPanel
            {
                Orientation = Orientation.Vertical,
                Margin = new Thickness(0, 0, 0, 0)
            };

            StackPanel gpuHeader = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(0, 0, 0, 8)
            };
            TextBlock lblGpuRed = new TextBlock
            {
                Text = "显卡模式切换",
                Foreground = new SolidColorBrush(Color.FromRgb(235, 50, 75)),
                FontSize = 17,
                FontWeight = FontWeights.Bold,
                VerticalAlignment = VerticalAlignment.Center
            };
            TextBlock lblGpuNote = new TextBlock
            {
                Text = "(点击平滑切换预约标记，下次正常重启生效，再次点击可随时切回)",
                Foreground = new SolidColorBrush(Color.FromRgb(132, 144, 180)),
                FontSize = 12,
                Margin = new Thickness(10, 2, 12, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            txtGpuCurrentBadge = new TextBlock
            {
                Text = "",
                Foreground = new SolidColorBrush(Color.FromRgb(110, 220, 160)),
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center
            };
            gpuHeader.Children.Add(lblGpuRed);
            gpuHeader.Children.Add(lblGpuNote);
            gpuHeader.Children.Add(txtGpuCurrentBadge);
            gpuSection.Children.Add(gpuHeader);

            gpuBarGrid = new Grid
            {
                Height = 46
            };
            gpuBarGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            gpuBarGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            Border trackLeft = new Border
            {
                CornerRadius = new CornerRadius(23, 0, 0, 23),
                Background = new SolidColorBrush(Color.FromRgb(18, 20, 34)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(95, 36, 54)),
                BorderThickness = new Thickness(1.5, 1.5, 0.5, 1.5)
            };
            Border trackRight = new Border
            {
                CornerRadius = new CornerRadius(0, 23, 23, 0),
                Background = new SolidColorBrush(Color.FromRgb(16, 24, 48)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(38, 72, 142)),
                BorderThickness = new Thickness(0.5, 1.5, 1.5, 1.5)
            };
            Grid.SetColumn(trackLeft, 0);
            Grid.SetColumn(trackRight, 1);
            gpuBarGrid.Children.Add(trackLeft);
            gpuBarGrid.Children.Add(trackRight);

            gpuPillTransform = new TranslateTransform(0, 0);
            gpuSlidingPill = new Border
            {
                CornerRadius = new CornerRadius(23),
                BorderThickness = new Thickness(2),
                RenderTransform = gpuPillTransform,
                IsHitTestVisible = false,
                Background = new LinearGradientBrush(
                    Color.FromRgb(68, 20, 38),
                    Color.FromRgb(32, 20, 46),
                    new Point(0, 0),
                    new Point(1, 0)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(240, 52, 82))
            };
            Grid.SetColumn(gpuSlidingPill, 0);
            gpuBarGrid.Children.Add(gpuSlidingPill);

            segDiscrete = new Border
            {
                Background = Brushes.Transparent,
                Cursor = Cursors.Hand
            };
            lblDiscreteTitle = new TextBlock
            {
                Text = "独显直连 (Discrete GPU)",
                FontSize = 14.5,
                FontWeight = FontWeights.Bold,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            segDiscrete.Child = lblDiscreteTitle;
            segDiscrete.MouseLeftButtonDown += (s, e) => OnGpuSwitchClickedAsync(true);

            segHybrid = new Border
            {
                Background = Brushes.Transparent,
                Cursor = Cursors.Hand
            };
            lblHybridTitle = new TextBlock
            {
                Text = "混合输出 (MSHybrid)",
                FontSize = 14.5,
                FontWeight = FontWeights.Bold,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            segHybrid.Child = lblHybridTitle;
            segHybrid.MouseLeftButtonDown += (s, e) => OnGpuSwitchClickedAsync(false);

            Grid.SetColumn(segDiscrete, 0);
            Grid.SetColumn(segHybrid, 1);
            gpuBarGrid.Children.Add(segDiscrete);
            gpuBarGrid.Children.Add(segHybrid);

            // Center Handle with both static "⇄" arrow and animated Loading Spinner ring!
            Grid handleContent = new Grid();
            txtCenterArrow = new TextBlock
            {
                Text = "⇄",
                Foreground = Brushes.White,
                FontSize = 13.5,
                FontWeight = FontWeights.Bold,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };

            spinnerCanvas = new Canvas
            {
                Width = 18,
                Height = 18,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Visibility = Visibility.Collapsed,
                RenderTransformOrigin = new Point(0.5, 0.5)
            };
            spinnerRotate = new RotateTransform(0);
            spinnerCanvas.RenderTransform = spinnerRotate;

            System.Windows.Shapes.Path spinnerArc = new System.Windows.Shapes.Path
            {
                Stroke = new LinearGradientBrush(Color.FromRgb(255, 80, 110), Color.FromRgb(75, 165, 255), new Point(0, 0), new Point(1, 1)),
                StrokeThickness = 2.4,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                Data = Geometry.Parse("M 9,2 A 7,7 0 1 1 2.2,7.2")
            };
            spinnerCanvas.Children.Add(spinnerArc);

            handleContent.Children.Add(txtCenterArrow);
            handleContent.Children.Add(spinnerCanvas);

            centerHandle = new Border
            {
                Width = 30,
                Height = 48,
                CornerRadius = new CornerRadius(9),
                Background = new SolidColorBrush(Color.FromRgb(22, 26, 46)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(110, 130, 210)),
                BorderThickness = new Thickness(2),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                IsHitTestVisible = false,
                Child = handleContent
            };
            Grid.SetColumnSpan(centerHandle, 2);
            gpuBarGrid.Children.Add(centerHandle);

            gpuSection.Children.Add(gpuBarGrid);

            Grid gpuSubLabels = new Grid
            {
                Margin = new Thickness(8, 5, 8, 0)
            };
            TextBlock lblPerfLeft = new TextBlock
            {
                Text = "性能 · 绕过核显直通 RTX 4060",
                Foreground = new SolidColorBrush(Color.FromRgb(235, 58, 82)),
                FontSize = 11.5,
                FontWeight = FontWeights.SemiBold,
                HorizontalAlignment = HorizontalAlignment.Left
            };
            TextBlock lblBatRight = new TextBlock
            {
                Text = "续航 · 核显/独显动态切换",
                Foreground = new SolidColorBrush(Color.FromRgb(64, 148, 255)),
                FontSize = 11.5,
                FontWeight = FontWeights.SemiBold,
                HorizontalAlignment = HorizontalAlignment.Right
            };
            gpuSubLabels.Children.Add(lblPerfLeft);
            gpuSubLabels.Children.Add(lblBatRight);
            gpuSection.Children.Add(gpuSubLabels);

            Grid.SetRow(gpuSection, 2);
            contentGrid.Children.Add(gpuSection);

            Grid.SetRow(contentGrid, 1);
            rootGrid.Children.Add(contentGrid);

            // ================= 4. FOOTER STATUS BAR =================
            Border statusBar = new Border
            {
                CornerRadius = new CornerRadius(0, 0, 11, 11),
                Background = new SolidColorBrush(Color.FromArgb(170, 10, 12, 22)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(30, 36, 62)),
                BorderThickness = new Thickness(0, 1, 0, 0),
                Padding = new Thickness(24, 0, 24, 0)
            };
            Grid statusGrid = new Grid();
            txtEcStatus = new TextBlock
            {
                Text = "就绪",
                Foreground = new SolidColorBrush(Color.FromRgb(140, 154, 190)),
                FontFamily = new FontFamily("Consolas, Microsoft YaHei UI"),
                FontSize = 11.5,
                VerticalAlignment = VerticalAlignment.Center
            };
            TextBlock txtAdminHint = new TextBlock
            {
                Text = EcHardwareController.IsAdministrator()
                    ? "● 管理员就绪 · 关闭窗口自动驻留托盘"
                    : "⚠️ 普通权限 (切换需管理员运行) · 关闭驻留托盘",
                Foreground = EcHardwareController.IsAdministrator()
                    ? new SolidColorBrush(Color.FromRgb(92, 214, 148))
                    : new SolidColorBrush(Color.FromRgb(245, 182, 66)),
                FontSize = 11.5,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center
            };
            statusGrid.Children.Add(txtEcStatus);
            statusGrid.Children.Add(txtAdminHint);
            statusBar.Child = statusGrid;

            Grid.SetRow(statusBar, 2);
            rootGrid.Children.Add(statusBar);

            outerBorder.Child = rootGrid;
            Content = outerBorder;
        }

        private void SetCenterLoadingState(bool loading)
        {
            isBusySwitching = loading;
            if (loading)
            {
                txtCenterArrow.Visibility = Visibility.Collapsed;
                spinnerCanvas.Visibility = Visibility.Visible;
                centerHandle.BorderBrush = new SolidColorBrush(Color.FromRgb(245, 182, 66));
                DoubleAnimation spinAnim = new DoubleAnimation(0, 360, TimeSpan.FromMilliseconds(550))
                {
                    RepeatBehavior = RepeatBehavior.Forever
                };
                spinnerRotate.BeginAnimation(RotateTransform.AngleProperty, spinAnim);
            }
            else
            {
                spinnerRotate.BeginAnimation(RotateTransform.AngleProperty, null);
                spinnerCanvas.Visibility = Visibility.Collapsed;
                txtCenterArrow.Visibility = Visibility.Visible;
                centerHandle.BorderBrush = new SolidColorBrush(Color.FromRgb(110, 130, 210));
            }
        }

        private Button CreateWindowButton(string text, bool isClose, string tooltip)
        {
            Button btn = new Button
            {
                Content = text,
                ToolTip = tooltip,
                Width = 32,
                Height = 26,
                Margin = new Thickness(4, 0, 0, 0),
                Background = Brushes.Transparent,
                Foreground = new SolidColorBrush(Color.FromRgb(180, 190, 220)),
                BorderThickness = new Thickness(0),
                FontSize = 12,
                Cursor = Cursors.Hand
            };

            ControlTemplate tpl = new ControlTemplate(typeof(Button));
            FrameworkElementFactory bd = new FrameworkElementFactory(typeof(Border));
            bd.Name = "bd";
            bd.SetValue(Border.CornerRadiusProperty, new CornerRadius(5));
            bd.SetValue(Border.BackgroundProperty, Brushes.Transparent);
            FrameworkElementFactory cp = new FrameworkElementFactory(typeof(ContentPresenter));
            cp.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            cp.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
            bd.AppendChild(cp);
            tpl.VisualTree = bd;

            Trigger hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
            hover.Setters.Add(new Setter(Border.BackgroundProperty,
                isClose ? new SolidColorBrush(Color.FromRgb(215, 42, 66)) : new SolidColorBrush(Color.FromRgb(38, 46, 78)), "bd"));
            hover.Setters.Add(new Setter(Control.ForegroundProperty, Brushes.White));
            tpl.Triggers.Add(hover);

            btn.Template = tpl;
            return btn;
        }

        private Border CreateScenarioCard(string title, string sub, string desc, string iconName, out TextBlock stateBadge, Action onClick)
        {
            Border card = new Border
            {
                Height = 204,
                CornerRadius = new CornerRadius(12),
                BorderThickness = new Thickness(2),
                Background = new SolidColorBrush(Color.FromArgb(220, 22, 26, 46)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(42, 50, 82)),
                RenderTransformOrigin = new Point(0.5, 0.5),
                RenderTransform = new ScaleTransform(1.0, 1.0),
                Cursor = Cursors.Hand
            };

            Grid g = new Grid { Margin = new Thickness(16, 14, 16, 14) };
            g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            g.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            stateBadge = new TextBlock
            {
                Text = "点击切换",
                FontSize = 11,
                FontWeight = FontWeights.Bold,
                Foreground = new SolidColorBrush(Color.FromRgb(120, 132, 170)),
                HorizontalAlignment = HorizontalAlignment.Right
            };
            Grid.SetRow(stateBadge, 0);
            g.Children.Add(stateBadge);

            BitmapImage bmp = LoadAsset(iconName);
            if (bmp != null)
            {
                Image img = new Image
                {
                    Source = bmp,
                    Width = 82,
                    Height = 82,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                };
                Grid.SetRow(img, 1);
                g.Children.Add(img);
            }

            StackPanel bottomStack = new StackPanel { Orientation = Orientation.Vertical };
            TextBlock lblTitle = new TextBlock
            {
                Text = title,
                Foreground = Brushes.White,
                FontSize = 17,
                FontWeight = FontWeights.Bold,
                Margin = new Thickness(0, 0, 0, 3)
            };
            TextBlock lblSub = new TextBlock
            {
                Text = sub,
                Foreground = new SolidColorBrush(Color.FromRgb(165, 178, 218)),
                FontSize = 11.5,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 0, 0, 2)
            };
            TextBlock lblDesc = new TextBlock
            {
                Text = desc,
                Foreground = new SolidColorBrush(Color.FromRgb(115, 128, 166)),
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap
            };
            bottomStack.Children.Add(lblTitle);
            bottomStack.Children.Add(lblSub);
            bottomStack.Children.Add(lblDesc);

            Grid.SetRow(bottomStack, 2);
            g.Children.Add(bottomStack);

            card.Child = g;
            card.MouseLeftButtonDown += (s, e) => onClick();
            return card;
        }

        private async void ApplyScenarioAsync(byte shiftCode, bool coolerBoost)
        {
            if (isBusySwitching) return;
            isBusySwitching = true;

            string syncState = "";
            bool ok = await Task.Run(() => EcHardwareController.WriteShiftMode(shiftCode, coolerBoost, out syncState));

            isBusySwitching = false;
            RefreshHardwareState(true);

            string modeName = shiftCode == 0xC4 ? "游戏模式 + 强冷 (0xC4)" : (shiftCode == 0xC2 ? "节能静音模式 (0xC2)" : "均衡模式 (0xC1)");
            if (ok)
            {
                txtEcStatus.Text = string.Format("✓ 已切换【{0}】| EC 0xD2=0x{1:X2} | MSI Center:{2}",
                    modeName, currentShiftByte, syncState);
            }
            else
            {
                txtEcStatus.Text = string.Format("⚠️ 切换【{0}】失败，请以管理员身份运行", modeName);
            }
        }

        private async void OnGpuSwitchClickedAsync(bool targetDiscrete)
        {
            if (isBusySwitching) return;

            bool effectiveBefore = stagedIsDiscrete.HasValue ? stagedIsDiscrete.Value : currentIsDiscrete;
            bool wasDiffBefore = (effectiveBefore != currentIsDiscrete);

            if (targetDiscrete == effectiveBefore)
            {
                if (wasDiffBefore)
                {
                    txtEcStatus.Text = string.Format("📌 当前已标记下次重启切换为【{0}】（点击另一侧可取消预约并恢复当前模式）。", targetDiscrete ? "独显直连" : "混合输出");
                }
                else
                {
                    txtEcStatus.Text = string.Format("● 当前硬件正处于【{0}】模式。", targetDiscrete ? "独显直连" : "混合输出");
                }
                return;
            }

            // Immediately start center loading spinner AND slide the highlight pill so user gets instant visual feedback!
            SetCenterLoadingState(true);
            stagedIsDiscrete = targetDiscrete;
            UpdateGpuVisuals(currentIsDiscrete, stagedIsDiscrete, true);
            txtEcStatus.Text = "⏳ 正在写入微星 EC 0xD1 与主板 UEFI NVRAM 标记...";

            string err = null;
            bool ok = await Task.Run(() =>
            {
                // Ensure spinner is visible for at least 260ms so the transition feels deliberate and smooth
                Stopwatch sw = Stopwatch.StartNew();
                bool res = EcHardwareController.StageGpuModeForNextBoot(targetDiscrete, wasDiffBefore, out err);
                int remain = 260 - (int)sw.ElapsedMilliseconds;
                if (remain > 0) System.Threading.Thread.Sleep(remain);
                return res;
            });

            SetCenterLoadingState(false);
            RefreshHardwareState(false);

            if (ok)
            {
                string targetName = targetDiscrete ? "独显直连 (Discrete)" : "混合输出 (MSHybrid)";
                if (targetDiscrete == currentIsDiscrete)
                {
                    txtEcStatus.Text = string.Format("✓ 已取消切换预约，恢复保持当前【{0}】模式（EC 0xD1 与 UEFI 已还原）。", targetName);
                }
                else
                {
                    txtEcStatus.Text = string.Format("📌 已打好【{0}】硬件与 BIOS 标记！无需现在重启，下次重启电脑时自动生效。", targetName);
                }
            }
            else
            {
                txtEcStatus.Text = "⚠️ " + err;
            }
        }

        private void RefreshHardwareState(bool animate)
        {
            try
            {
                currentShiftByte = EcHardwareController.ReadShiftByte();
                currentCoolerBoost = EcHardwareController.ReadCoolerBoost();
                currentIsDiscrete = EcHardwareController.ReadIsDiscreteGpuMode();
                bool? hwStaged = EcHardwareController.ReadStagedGpuModeIsDiscrete();
                if (hwStaged.HasValue)
                {
                    stagedIsDiscrete = hwStaged.Value;
                }

                int cpuT, gpuT, cpuRpm, gpuRpm;
                EcHardwareController.ReadTelemetry(out cpuT, out gpuT, out cpuRpm, out gpuRpm);
                txtTelemetry.Text = string.Format("CPU {0}°C ({1} RPM)  |  GPU {2}°C ({3} RPM)", cpuT, cpuRpm, gpuT, gpuRpm);

                Brush activeBorder = new LinearGradientBrush(
                    Color.FromRgb(235, 45, 75),
                    Color.FromRgb(55, 130, 255),
                    new Point(0, 0),
                    new Point(1, 1));
                Brush inactiveBorder = new SolidColorBrush(Color.FromRgb(42, 50, 82));

                Brush activeBg = new SolidColorBrush(Color.FromArgb(245, 30, 36, 66));
                Brush inactiveBg = new SolidColorBrush(Color.FromArgb(195, 20, 24, 42));

                bool isGaming = (currentShiftByte == 0xC4 || currentShiftByte == 0xC0);
                bool isSilent = (currentShiftByte == 0xC2 || currentShiftByte == 0xC3);
                bool isBalanced = !isGaming && !isSilent;

                ApplyCardStyle(cardGaming, badgeGaming, isGaming, activeBorder, inactiveBorder, activeBg, inactiveBg, animate);
                ApplyCardStyle(cardBalanced, badgeBalanced, isBalanced, activeBorder, inactiveBorder, activeBg, inactiveBg, animate);
                ApplyCardStyle(cardSilent, badgeSilent, isSilent, activeBorder, inactiveBorder, activeBg, inactiveBg, animate);

                if (currentCoolerBoost)
                {
                    btnCoolerBoost.BorderBrush = new SolidColorBrush(Color.FromRgb(235, 55, 82));
                    btnCoolerBoost.Background = new SolidColorBrush(Color.FromRgb(48, 24, 42));
                    dotCoolerBoost.Fill = new SolidColorBrush(Color.FromRgb(255, 70, 95));
                    txtCoolerBoostState.Text = "强冷模式 (Cooler Boost): 开启中";
                    txtCoolerBoostState.Foreground = Brushes.White;
                }
                else
                {
                    btnCoolerBoost.BorderBrush = new SolidColorBrush(Color.FromRgb(58, 70, 114));
                    btnCoolerBoost.Background = new SolidColorBrush(Color.FromRgb(25, 31, 56));
                    dotCoolerBoost.Fill = new SolidColorBrush(Color.FromRgb(92, 214, 148));
                    txtCoolerBoostState.Text = "强冷模式 (Cooler Boost): 自动温控";
                    txtCoolerBoostState.Foreground = new SolidColorBrush(Color.FromRgb(200, 210, 240));
                }

                bool hasPendingSwitch = stagedIsDiscrete.HasValue && stagedIsDiscrete.Value != currentIsDiscrete;
                UpdateGpuVisuals(currentIsDiscrete, stagedIsDiscrete, animate);
                if (hasPendingSwitch)
                {
                    txtGpuCurrentBadge.Text = string.Format("📌 已标记下次重启切换为：{0}", stagedIsDiscrete.Value ? "独显直连" : "混合输出");
                    txtGpuCurrentBadge.Foreground = new SolidColorBrush(Color.FromRgb(255, 196, 76));
                }
                else
                {
                    txtGpuCurrentBadge.Text = string.Format("● 当前生效：{0}", currentIsDiscrete ? "独显直连" : "混合输出");
                    txtGpuCurrentBadge.Foreground = new SolidColorBrush(Color.FromRgb(110, 220, 160));
                }

                if (menuGaming != null)
                {
                    menuGaming.Checked = isGaming;
                    menuBalanced.Checked = isBalanced;
                    menuSilent.Checked = isSilent;
                    menuCoolerBoost.Checked = currentCoolerBoost;
                    bool targetMux = stagedIsDiscrete.HasValue ? stagedIsDiscrete.Value : currentIsDiscrete;
                    menuGpuDiscrete.Checked = targetMux;
                    menuGpuHybrid.Checked = !targetMux;

                    string modeShort = isGaming ? "游戏" : (isSilent ? "静音" : "均衡");
                    string trayTip = string.Format("MSI Scenario Lite [{0}] CPU:{1}°C GPU:{2}°C", modeShort, cpuT, gpuT);
                    if (trayTip.Length < 63) trayIcon.Text = trayTip;
                }

                if (txtEcStatus.Text == "就绪")
                {
                    txtEcStatus.Text = string.Format("EC 状态: Shift(0xD2)=0x{0:X2} | Thermal(0x98)=0x{1:X2} | MUX={2}",
                        currentShiftByte,
                        currentCoolerBoost ? 0x86 : 0x06,
                        currentIsDiscrete ? "Discrete" : "MSHybrid");
                }
            }
            catch (Exception ex)
            {
                txtEcStatus.Text = "读取 EC 状态异常: " + ex.Message;
            }
        }

        private void ApplyCardStyle(Border card, TextBlock badge, bool active, Brush activeBorder, Brush inactiveBorder, Brush activeBg, Brush inactiveBg, bool animate)
        {
            card.BorderBrush = active ? activeBorder : inactiveBorder;
            card.Background = active ? activeBg : inactiveBg;
            badge.Text = active ? "● 当前激活" : "点击切换";
            badge.Foreground = active
                ? new SolidColorBrush(Color.FromRgb(92, 225, 156))
                : new SolidColorBrush(Color.FromRgb(120, 132, 170));

            double targetOpacity = active ? 1.0 : 0.74;
            double targetScale = active ? 1.0 : 0.985;
            if (animate)
            {
                DoubleAnimation opAnim = new DoubleAnimation(targetOpacity, TimeSpan.FromMilliseconds(220));
                card.BeginAnimation(UIElement.OpacityProperty, opAnim);
                ScaleTransform st = card.RenderTransform as ScaleTransform;
                if (st != null)
                {
                    CubicEase ease = new CubicEase { EasingMode = EasingMode.EaseOut };
                    st.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(targetScale, TimeSpan.FromMilliseconds(220)) { EasingFunction = ease });
                    st.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(targetScale, TimeSpan.FromMilliseconds(220)) { EasingFunction = ease });
                }
            }
            else
            {
                card.Opacity = targetOpacity;
            }
        }

        private void UpdateGpuVisuals(bool isDiscreteActive, bool? stagedDiscrete, bool animate)
        {
            bool highlightDiscrete = stagedDiscrete.HasValue ? stagedDiscrete.Value : isDiscreteActive;
            bool isStagedDiff = stagedDiscrete.HasValue && stagedDiscrete.Value != isDiscreteActive;

            double halfWidth = gpuBarGrid.ActualWidth > 10 ? (gpuBarGrid.ActualWidth / 2.0) : 366.0;
            double targetX = highlightDiscrete ? 0.0 : halfWidth;

            if (animate && lastAnimatedDiscrete.HasValue && lastAnimatedDiscrete.Value != highlightDiscrete)
            {
                CubicEase ease = new CubicEase { EasingMode = EasingMode.EaseInOut };
                DoubleAnimation slideAnim = new DoubleAnimation(targetX, TimeSpan.FromMilliseconds(260))
                {
                    EasingFunction = ease
                };
                gpuPillTransform.BeginAnimation(TranslateTransform.XProperty, slideAnim);
            }
            else if (!animate || !lastAnimatedDiscrete.HasValue)
            {
                gpuPillTransform.BeginAnimation(TranslateTransform.XProperty, null);
                gpuPillTransform.X = targetX;
            }
            lastAnimatedDiscrete = highlightDiscrete;

            if (highlightDiscrete)
            {
                gpuSlidingPill.CornerRadius = new CornerRadius(23, 0, 0, 23);
                gpuSlidingPill.Background = new LinearGradientBrush(
                    Color.FromRgb(64, 18, 34),
                    Color.FromRgb(28, 18, 40),
                    new Point(0, 0),
                    new Point(1, 0));
                gpuSlidingPill.BorderBrush = new SolidColorBrush(Color.FromRgb(235, 45, 75));

                lblDiscreteTitle.Foreground = Brushes.White;
                lblDiscreteTitle.Text = isStagedDiff ? "📌 独显直连 (已标记·重启生效)" : "● 独显直连 (当前生效)";

                lblHybridTitle.Foreground = new SolidColorBrush(Color.FromRgb(150, 175, 225));
                lblHybridTitle.Text = isStagedDiff ? "混合输出 (当前运行·点击取消切换)" : "混合输出 (点击标记切换)";
            }
            else
            {
                gpuSlidingPill.CornerRadius = new CornerRadius(0, 23, 23, 0);
                gpuSlidingPill.Background = new LinearGradientBrush(
                    Color.FromRgb(28, 72, 168),
                    Color.FromRgb(45, 125, 245),
                    new Point(0, 0),
                    new Point(1, 0));
                gpuSlidingPill.BorderBrush = new SolidColorBrush(Color.FromRgb(95, 175, 255));

                lblDiscreteTitle.Foreground = new SolidColorBrush(Color.FromRgb(175, 145, 160));
                lblDiscreteTitle.Text = isStagedDiff ? "独显直连 (当前运行·点击取消切换)" : "独显直连 (点击标记切换)";

                lblHybridTitle.Foreground = Brushes.White;
                lblHybridTitle.Text = isStagedDiff ? "📌 混合输出 (已标记·重启生效)" : "● 混合输出 (当前生效)";
            }
        }
    }

    public static class Program
    {
        [STAThread]
        public static int Main(string[] args)
        {
            Application app = new Application();
            app.Run(new MainWindow());
            return 0;
        }
    }
}
