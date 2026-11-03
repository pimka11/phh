using LibreHardwareMonitor.Hardware;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Runtime.Remoting.Lifetime;
using System.Threading;

namespace phh
{
    internal class Program
    {
        private static readonly object ConsoleLock =
            new object();

        private static readonly List<string> Logs =
            new List<string>();

        private static readonly Dictionary<ConsoleKey, int>
            KeyStatistics =
            new Dictionary<ConsoleKey, int>();

        private static readonly Dictionary<string, int>
            ApplicationStatistics =
            new Dictionary<string, int>();

        private static readonly Dictionary<int, string>
            KnownProcesses =
            new Dictionary<int, string>();

        private static Timer processTimer;
        private static Timer pingTimer;
        private static Timer speedTimer;
        private static Timer temperatureTimer;

        private static Computer hardwareComputer;

        private static int totalKeyPresses;
        private static int totalApplicationsStarted;

        private static long lastPing = -1;
        private static double lastDownloadSpeed;

        private static string cpuTemperature = "н/д";
        private static string gpuTemperature = "н/д";

        private static bool speedCheckRunning;
        private static bool shuttingDown;

        private static void Main(string[] args)
        {
            Console.Title = "System Monitor";
            Console.CursorVisible = false;

            ServicePointManager.SecurityProtocol =
                SecurityProtocolType.Tls12;

            try
            {
                InitializeProcessList();

                hardwareComputer = new Computer
                {
                    IsCpuEnabled = true,
                    IsGpuEnabled = true
                };

                hardwareComputer.Open();

                AddLog("Монітор запущено.");
                AddLog("F — статистика, Esc — вихід.");

                processTimer = new Timer(
                    CheckForNewApplications,
                    null,
                    0,
                    500);

                pingTimer = new Timer(
                    CheckPing,
                    null,
                    0,
                    5000);

                speedTimer = new Timer(
                    CheckDownloadSpeed,
                    null,
                    1000,
                    30000);

                temperatureTimer = new Timer(
                    UpdateTemperatures,
                    null,
                    0,
                    3000);

                while (true)
                {
                    ConsoleKeyInfo keyInfo =
                        Console.ReadKey(true);

                    if (keyInfo.Key == ConsoleKey.Escape)
                    {
                        break;
                    }

                    if (keyInfo.Key == ConsoleKey.F)
                    {
                        ShowStatistics();
                        continue;
                    }

                    lock (ConsoleLock)
                    {
                        totalKeyPresses++;

                        if (KeyStatistics.ContainsKey(keyInfo.Key))
                        {
                            KeyStatistics[keyInfo.Key]++;
                        }
                        else
                        {
                            KeyStatistics.Add(keyInfo.Key, 1);
                        }

                        AddLogLocked(
                            "Натиснуто: " +
                            GetKeyName(keyInfo));
                    }
                }
            }
            catch (Exception exception)
            {
                AddLog(
                    "Помилка: " +
                    exception.Message);

                Console.ReadKey(true);
            }
            finally
            {
                Shutdown();
            }
        }

        private static void Shutdown()
        {
            shuttingDown = true;

            if (processTimer != null)
            {
                processTimer.Dispose();
            }

            if (pingTimer != null)
            {
                pingTimer.Dispose();
            }

            if (speedTimer != null)
            {
                speedTimer.Dispose();
            }

            if (temperatureTimer != null)
            {
                temperatureTimer.Dispose();
            }

            if (hardwareComputer != null)
            {
                hardwareComputer.Close();
            }

            Console.CursorVisible = true;
            Console.ResetColor();
            Console.Clear();
            Console.WriteLine("Програму завершено.");
        }

        private static void InitializeProcessList()
        {
            Process[] processes =
                Process.GetProcesses();

            for (int i = 0; i < processes.Length; i++)
            {
                Process process = processes[i];

                try
                {
                    KnownProcesses[process.Id] =
                        process.ProcessName;
                }
                catch
                {
                }
                finally
                {
                    process.Dispose();
                }
            }
        }

        private static void CheckForNewApplications(
            object state)
        {
            if (shuttingDown)
            {
                return;
            }

            try
            {
                Process[] processes =
                    Process.GetProcesses();

                for (int i = 0; i < processes.Length; i++)
                {
                    Process process = processes[i];

                    try
                    {
                        int processId = process.Id;
                        string processName =
                            process.ProcessName;

                        if (!KnownProcesses.ContainsKey(processId))
                        {
                            KnownProcesses[processId] =
                                processName;

                            lock (ConsoleLock)
                            {
                                totalApplicationsStarted++;

                                if (ApplicationStatistics
                                    .ContainsKey(processName))
                                {
                                    ApplicationStatistics[
                                        processName]++;
                                }
                                else
                                {
                                    ApplicationStatistics.Add(
                                        processName,
                                        1);
                                }

                                AddLogLocked(
                                    "Запущено програму: " +
                                    processName);
                            }
                        }
                    }
                    catch
                    {
                    }
                    finally
                    {
                        process.Dispose();
                    }
                }
            }
            catch (Exception exception)
            {
                AddLog(
                    "Помилка процесів: " +
                    exception.Message);
            }
        }

        private static void CheckPing(object state)
        {
            if (shuttingDown)
            {
                return;
            }

            try
            {
                using (Ping ping = new Ping())
                {
                    PingReply reply =
                        ping.Send("1.1.1.1", 2000);

                    lock (ConsoleLock)
                    {
                        if (reply.Status ==
                            IPStatus.Success)
                        {
                            lastPing =
                                reply.RoundtripTime;

                            AddLogLocked(
                                "Ping успішний: " +
                                lastPing +
                                " мс");
                        }
                        else
                        {
                            lastPing = -1;

                            AddLogLocked(
                                "Ping: немає відповіді");
                        }
                    }
                }
            }
            catch (Exception exception)
            {
                lock (ConsoleLock)
                {
                    lastPing = -1;

                    AddLogLocked(
                        "Помилка ping: " +
                        exception.Message);
                }
            }
        }

        private static void CheckDownloadSpeed(
            object state)
        {
            if (shuttingDown)
            {
                return;
            }

            lock (ConsoleLock)
            {
                if (speedCheckRunning)
                {
                    return;
                }

                speedCheckRunning = true;
            }

            try
            {
                const string testUrl =
                    "https://speed.cloudflare.com/" +
                    "__down?bytes=5000000";

                HttpWebRequest request =
                    (HttpWebRequest)
                    WebRequest.Create(testUrl);

                request.Timeout = 15000;
                request.ReadWriteTimeout = 15000;
                request.UserAgent = "system-monitor";

                Stopwatch stopwatch =
                    Stopwatch.StartNew();

                long totalBytes = 0;

                using (HttpWebResponse response =
                       (HttpWebResponse)
                       request.GetResponse())
                using (Stream stream =
                       response.GetResponseStream())
                {
                    byte[] buffer = new byte[8192];
                    int bytesRead;

                    while ((bytesRead =
                           stream.Read(
                               buffer,
                               0,
                               buffer.Length)) > 0)
                    {
                        totalBytes += bytesRead;
                    }
                }

                stopwatch.Stop();

                double seconds =
                    stopwatch.Elapsed.TotalSeconds;

                if (seconds > 0)
                {
                    lock (ConsoleLock)
                    {
                        lastDownloadSpeed =
                            totalBytes *
                            8.0 /
                            seconds /
                            1000000.0;

                        AddLogLocked(
                            string.Format(
                                "Швидкість: {0:F2} Мбіт/с",
                                lastDownloadSpeed));
                    }
                }
            }
            catch (Exception exception)
            {
                lock (ConsoleLock)
                {
                    lastDownloadSpeed = 0;

                    AddLogLocked(
                        "Помилка швидкості: " +
                        exception.Message);
                }
            }
            finally
            {
                lock (ConsoleLock)
                {
                    speedCheckRunning = false;
                }
            }
        }

        private static void UpdateTemperatures(
            object state)
        {
            if (shuttingDown ||
                hardwareComputer == null)
            {
                return;
            }

            try
            {
                string newCpuTemperature = "н/д";
                string newGpuTemperature = "н/д";

                IHardware[] hardwareList =
                    hardwareComputer.Hardware.ToArray();

                for (int i = 0;
                     i < hardwareList.Length;
                     i++)
                {
                    IHardware hardware =
                        hardwareList[i];

                    hardware.Update();

                    ISensor[] sensors =
                        hardware.Sensors.ToArray();

                    for (int j = 0;
                         j < sensors.Length;
                         j++)
                    {
                        ISensor sensor = sensors[j];

                        if (sensor.SensorType !=
                            SensorType.Temperature ||
                            !sensor.Value.HasValue)
                        {
                            continue;
                        }

                        string temperature =
                            string.Format(
                                "{0:F0} °C",
                                sensor.Value.Value);

                        if (hardware.HardwareType ==
                            HardwareType.Cpu)
                        {
                            newCpuTemperature =
                                temperature;
                        }

                        if (hardware.HardwareType ==
                                HardwareType.GpuNvidia ||
                            hardware.HardwareType ==
                                HardwareType.GpuAmd ||
                            hardware.HardwareType ==
                                HardwareType.GpuIntel)
                        {
                            newGpuTemperature =
                                temperature;
                        }
                    }
                }

                lock (ConsoleLock)
                {
                    cpuTemperature =
                        newCpuTemperature;

                    gpuTemperature =
                        newGpuTemperature;

                    DrawDashboardLocked();
                }
            }
            catch (Exception exception)
            {
                AddLog(
                    "Помилка температури: " +
                    exception.Message);
            }
        }

        private static bool IsDiscordRunning()
        {
            string[] discordNames =
            {
                "Discord",
                "DiscordPTB",
                "DiscordCanary"
            };

            for (int i = 0;
                 i < discordNames.Length;
                 i++)
            {
                Process[] processes =
                    Process.GetProcessesByName(
                        discordNames[i]);

                bool running =
                    processes.Length > 0;

                for (int j = 0;
                     j < processes.Length;
                     j++)
                {
                    processes[j].Dispose();
                }

                if (running)
                {
                    return true;
                }
            }

            return false;
        }

        private static void ShowStatistics()
        {
            lock (ConsoleLock)
            {
                AddLogLocked("");
                AddLogLocked("========== СТАТИСТИКА ==========");

                AddLogLocked(
                    "Натискань клавіш: " +
                    totalKeyPresses);

                AddLogLocked(
                    "Запущено програм: " +
                    totalApplicationsStarted);

                AddLogLocked("--- Клавіші ---");

                KeyValuePair<ConsoleKey, int>[] keys =
                    KeyStatistics.ToArray();

                for (int i = 0;
                     i < keys.Length;
                     i++)
                {
                    AddLogLocked(
                        keys[i].Key +
                        ": " +
                        keys[i].Value);
                }

                AddLogLocked("--- Програми ---");

                KeyValuePair<string, int>[] applications =
                    ApplicationStatistics.ToArray();

                for (int i = 0;
                     i < applications.Length;
                     i++)
                {
                    AddLogLocked(
                        applications[i].Key +
                        ": " +
                        applications[i].Value);
                }

                AddLogLocked("==============================");
            }
        }

        private static void AddLog(string message)
        {
            lock (ConsoleLock)
            {
                AddLogLocked(message);
            }
        }

        private static void AddLogLocked(string message)
        {
            string line = string.Format(
                "[{0:HH:mm:ss}] {1}",
                DateTime.Now,
                message);

            Logs.Add(line);

            if (Logs.Count > 100)
            {
                Logs.RemoveAt(0);
            }

            DrawDashboardLocked();
        }

        private static void DrawDashboardLocked()
        {
            try
            {
                int consoleWidth =
                    Console.WindowWidth;

                int consoleHeight =
                    Console.WindowHeight;

                int rightWidth = 31;
                int leftWidth =
                    consoleWidth -
                    rightWidth -
                    1;

                if (leftWidth < 20)
                {
                    rightWidth = 25;
                    leftWidth =
                        consoleWidth -
                        rightWidth -
                        1;
                }

                if (leftWidth < 10 ||
                    consoleHeight < 1)
                {
                    return;
                }

                Console.Clear();

                int firstLog = Math.Max(
                    0,
                    Logs.Count - consoleHeight);

                for (int row = 0;
                     row < consoleHeight;
                     row++)
                {
                    string leftText = "";
                    int logIndex =
                        firstLog + row;

                    if (logIndex < Logs.Count)
                    {
                        leftText =
                            Logs[logIndex];
                    }

                    leftText =
                        FitText(leftText, leftWidth);

                    string rightText = "";

                    if (row == 0)
                    {
                        rightText = "МЕРЕЖА";
                    }
                    else if (row == 2)
                    {
                        rightText =
                            lastPing >= 0
                                ? "Ping: " +
                                  lastPing +
                                  " мс"
                                : "Ping: немає відповіді";
                    }
                    else if (row == 4)
                    {
                        rightText =
                            lastDownloadSpeed > 0
                                ? "Швидкість:"
                                : "Швидкість: очікування";
                    }
                    else if (row == 5 &&
                             lastDownloadSpeed > 0)
                    {
                        rightText = string.Format(
                            "{0:F2} Мбіт/с",
                            lastDownloadSpeed);
                    }
                    else if (row == 8)
                    {
                        rightText = "DISCORD";
                    }
                    else if (row == 10)
                    {
                        rightText =
                            IsDiscordRunning()
                                ? "Статус: відкритий"
                                : "Статус: закритий";
                    }
                    else if (row == 13)
                    {
                        rightText = "ТЕМПЕРАТУРА";
                    }
                    else if (row == 15)
                    {
                        rightText =
                            "CPU: " +
                            cpuTemperature;
                    }
                    else if (row == 16)
                    {
                        rightText =
                            "GPU: " +
                            gpuTemperature;
                    }
                    else if (row == 19)
                    {
                        rightText = "КЕРУВАННЯ";
                    }
                    else if (row == 21)
                    {
                        rightText = "F — статистика";
                    }
                    else if (row == 22)
                    {
                        rightText = "Esc — вихід";
                    }

                    rightText =
                        FitText(rightText, rightWidth);

                    Console.ForegroundColor =
                        GetLogColor(leftText);

                    Console.Write(
                        leftText.PadRight(leftWidth));

                    Console.ForegroundColor =
                        ConsoleColor.DarkGray;

                    Console.Write("|");

                    Console.ForegroundColor =
                        GetRightPanelColor(row);

                    Console.WriteLine(
                        rightText.PadRight(rightWidth));

                    Console.ResetColor();
                }
            }
            catch
            {
            }
        }

        private static ConsoleColor GetLogColor(
            string text)
        {
            if (text.IndexOf(
                    "немає",
                    StringComparison.OrdinalIgnoreCase) >= 0 ||
                text.IndexOf(
                    "Помилка",
                    StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return ConsoleColor.Red;
            }

            if (text.IndexOf(
                    "Запущено програму",
                    StringComparison.OrdinalIgnoreCase) >= 0 ||
                text.IndexOf(
                    "успішний",
                    StringComparison.OrdinalIgnoreCase) >= 0 ||
                text.IndexOf(
                    "Швидкість:",
                    StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return ConsoleColor.Green;
            }

            return ConsoleColor.White;
        }

        private static ConsoleColor GetRightPanelColor(
            int row)
        {
            if (row == 2)
            {
                return lastPing >= 0
                    ? ConsoleColor.Green
                    : ConsoleColor.Red;
            }

            if (row == 4 || row == 5)
            {
                return lastDownloadSpeed > 0
                    ? ConsoleColor.Green
                    : ConsoleColor.Red;
            }

            if (row == 10)
            {
                return IsDiscordRunning()
                    ? ConsoleColor.Green
                    : ConsoleColor.Red;
            }

            if (row == 15)
            {
                return cpuTemperature == "н/д"
                    ? ConsoleColor.Red
                    : ConsoleColor.Green;
            }

            if (row == 16)
            {
                return gpuTemperature == "н/д"
                    ? ConsoleColor.Red
                    : ConsoleColor.Green;
            }

            if (row == 0 ||
                row == 8 ||
                row == 13 ||
                row == 19)
            {
                return ConsoleColor.Yellow;
            }

            return ConsoleColor.White;
        }

        private static string FitText(
            string text,
            int maxLength)
        {
            if (string.IsNullOrEmpty(text) ||
                maxLength <= 0)
            {
                return "";
            }

            if (text.Length > maxLength)
            {
                return text.Substring(0, maxLength);
            }

            return text;
        }

        private static string GetKeyName(
            ConsoleKeyInfo keyInfo)
        {
            if (!char.IsControl(keyInfo.KeyChar))
            {
                return keyInfo.Key +
                       " (" +
                       keyInfo.KeyChar +
                       ")";
            }

            return keyInfo.Key.ToString();
        }
    }
}
