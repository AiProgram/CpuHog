using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

// CpuHog - sustained multi-threaded CPU load generator
// Run "CpuHog.exe help" for usage.

internal static class CpuHog
{
    private const string Version = "1.3";

    [DllImport("winmm.dll")] private static extern uint timeBeginPeriod(uint ms);
    [DllImport("winmm.dll")] private static extern uint timeEndPeriod(uint ms);

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_POWER_THROTTLING_STATE { public uint Version; public uint ControlMask; public uint StateMask; }

    [StructLayout(LayoutKind.Sequential)]
    private struct THREAD_POWER_THROTTLING_STATE { public uint Version; public uint ControlMask; public uint StateMask; }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetProcessInformation(IntPtr hProcess, int infoClass, ref PROCESS_POWER_THROTTLING_STATE info, uint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetProcessInformation(IntPtr hProcess, int infoClass, ref PROCESS_POWER_THROTTLING_STATE info, uint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetThreadInformation(IntPtr hThread, int infoClass, ref THREAD_POWER_THROTTLING_STATE info, uint size);

    [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentThread();
    [DllImport("kernel32.dll")] private static extern bool SetThreadPriority(IntPtr hThread, int nPriority);

    private const int ProcessPowerThrottling = 4;
    private const int ThreadPowerThrottling = 3;
    private const uint THROTTLING_VERSION = 1;
    private const uint EXECUTION_SPEED = 0x1;
    private const int THREAD_PRIORITY_HIGHEST = 2;

    private static volatile bool _stop;
    private static long _chunks;
    private static int _threads = Environment.ProcessorCount;
    private static int _seconds;
    private static string _mode = "mix";
    private static int _percent = 100;
    private static int _chunkKB = 256;
    private static bool _keepEco;
    private static int _threadOptOut;
    private static string _prio = "abovenormal";

    private static void Main(string[] args)
    {
        if (!Parse(args)) { Environment.Exit(2); }

        if (_threads < 1) _threads = 1;
        if (_percent < 1) _percent = 1;
        if (_percent > 100) _percent = 100;
        if (_chunkKB < 1) _chunkKB = 1;

        timeBeginPeriod(1);
        Console.CancelKeyPress += delegate(object s, ConsoleCancelEventArgs e) { e.Cancel = true; _stop = true; };

        Console.WriteLine(string.Format(
            "CpuHog {0}: threads={1} mode={2} target={3}% duration={4} chunk={5}KB pid={6}",
            Version, _threads, _mode, _percent,
            _seconds == 0 ? "until Ctrl+C" : _seconds + "s",
            _chunkKB, Process.GetCurrentProcess().Id));

        ApplyPerformanceSettings();

        var sw = Stopwatch.StartNew();
        var workers = new Thread[_threads];
        for (int i = 0; i < _threads; i++)
        {
            int id = i;
            workers[i] = new Thread(delegate() { Worker(id); });
            workers[i].IsBackground = true;
            workers[i].Name = "hog-" + i;
            workers[i].Start();
        }

        long lastChunks = 0, lastMs = 0;
        while (!_stop)
        {
            Thread.Sleep(1000);
            long nowMs = sw.ElapsedMilliseconds;
            if (_seconds > 0 && nowMs >= _seconds * 1000L) { _stop = true; break; }

            long c = Interlocked.Read(ref _chunks);
            long dms = nowMs - lastMs;
            Console.WriteLine(string.Format("  t={0,6:N1}s  chunks={1,12:N0}  rate={2,10:N0}/s  cpuTime={3,7:N2}s",
                nowMs / 1000.0, c, dms > 0 ? (c - lastChunks) * 1000.0 / dms : 0,
                Process.GetCurrentProcess().TotalProcessorTime.TotalSeconds));
            lastChunks = c; lastMs = nowMs;
        }

        for (int i = 0; i < workers.Length; i++) workers[i].Join(3000);
        sw.Stop();
        timeEndPeriod(1);
        double cpu = Process.GetCurrentProcess().TotalProcessorTime.TotalSeconds;
        Console.WriteLine(string.Format(
            "stopped: elapsed={0:N2}s chunks={1:N0} cpuTime={2:N1}s avgUtil={3:N1}%",
            sw.Elapsed.TotalSeconds, Interlocked.Read(ref _chunks), cpu,
            sw.Elapsed.TotalSeconds > 0 ? cpu / sw.Elapsed.TotalSeconds / _threads * 100.0 : 0));
        Console.WriteLine(string.Format("per-thread opt-out: {0}/{1} worker threads exempt from throttling",
            Interlocked.CompareExchange(ref _threadOptOut, 0, 0), _threads));
        Environment.Exit(0);
    }

    // Raise priority and opt out of Windows power throttling (EcoQoS) so the
    // load is not capped while the process runs in the background.
    private static void ApplyPerformanceSettings()
    {
        try
        {
            switch (_prio)
            {
                case "normal": Process.GetCurrentProcess().PriorityClass = ProcessPriorityClass.Normal; break;
                case "high": Process.GetCurrentProcess().PriorityClass = ProcessPriorityClass.High; break;
                case "realtime": Process.GetCurrentProcess().PriorityClass = ProcessPriorityClass.RealTime; break;
                default: Process.GetCurrentProcess().PriorityClass = ProcessPriorityClass.AboveNormal; break;
            }
            Console.WriteLine("  priorityClass : " + Process.GetCurrentProcess().PriorityClass);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("  priorityClass : failed (" + ex.Message + ")");
        }

        var want = new PROCESS_POWER_THROTTLING_STATE();
        want.Version = THROTTLING_VERSION;
        want.ControlMask = EXECUTION_SPEED;
        want.StateMask = _keepEco ? EXECUTION_SPEED : 0;

        uint size = (uint)Marshal.SizeOf(typeof(PROCESS_POWER_THROTTLING_STATE));
        bool ok = SetProcessInformation(Process.GetCurrentProcess().Handle, ProcessPowerThrottling, ref want, size);

        PROCESS_POWER_THROTTLING_STATE cur;
        cur.Version = THROTTLING_VERSION;   // GetProcessInformation validates Version
        cur.ControlMask = 0;
        cur.StateMask = 0;
        bool readOk = GetProcessInformation(Process.GetCurrentProcess().Handle, ProcessPowerThrottling, ref cur, size);
        string state = readOk ? ((cur.StateMask & EXECUTION_SPEED) != 0 ? "THROTTLED" : "not throttled") : "unknown";
        Console.WriteLine(string.Format("  powerThrottle : {0} (set={1}, stateMask=0x{2:X})  [eco={3}]",
            state, ok, readOk ? cur.StateMask : 0, _keepEco ? "kept" : "off"));
    }

    private static bool IsHelp(string s)
    {
        switch (s.ToLowerInvariant())
        {
            case "help":
            case "-h":
            case "--help":
            case "-?":
            case "/?":
                return true;
            default:
                return false;
        }
    }

    private static void ShowHelp()
    {
        Console.WriteLine();
        Console.WriteLine("  CpuHog " + Version + " - sustained multi-threaded CPU load generator");
        Console.WriteLine();
        Console.WriteLine("  USAGE");
        Console.WriteLine("    CpuHog.exe [options]");
        Console.WriteLine("    CpuHog.exe help");
        Console.WriteLine();
        Console.WriteLine("  OPTIONS");
        Console.WriteLine("    -t <n>        worker threads (default: logical processor count, here " + Environment.ProcessorCount + ")");
        Console.WriteLine("    -d <sec>      run for <sec> seconds; 0 = until Ctrl+C (default: 0)");
        Console.WriteLine("    -m <mode>     workload type: int | fp | mem | mix (default: mix)");
        Console.WriteLine("    -p <pct>      target CPU percentage per thread, 1..100 (default: 100)");
        Console.WriteLine("    -s <kb>       memory chunk size in KB for mem/mix (default: 256)");
        Console.WriteLine("    -prio <p>     process priority: normal | abovenormal | high | realtime (default: abovenormal)");
        Console.WriteLine("    -eco          keep Windows power throttling on (default: throttling is disabled)");
        Console.WriteLine("    -h, --help    show this help and exit");
        Console.WriteLine("    -v, --version show version and exit");
        Console.WriteLine();
        Console.WriteLine("  MODES");
        Console.WriteLine("    int    integer bit-mixing loop (hottest, lowest memory footprint)");
        Console.WriteLine("    fp     sqrt/sin floating point loop (stresses FP units)");
        Console.WriteLine("    mem    byte fill + strided read over the chunk buffer (memory bandwidth)");
        Console.WriteLine("    mix    alternating int/fp with periodic buffer writes (default)");
        Console.WriteLine();
        Console.WriteLine("  THROTTLING");
        Console.WriteLine("    Windows can cap background processes via EcoQoS (power throttling), which");
        Console.WriteLine("    shows up as a green leaf in Task Manager and silently reduces throughput.");
        Console.WriteLine("    CpuHog clears ProcessPowerThrottling/EXECUTION_SPEED and raises the priority");
        Console.WriteLine("    class, then prints the resulting state so you can verify it took effect.");
        Console.WriteLine("    Pass -eco to keep the default (throttled) behaviour for comparison tests.");
        Console.WriteLine();
        Console.WriteLine("  EXAMPLES");
        Console.WriteLine("    CpuHog.exe                  6 threads at 100% until Ctrl+C");
        Console.WriteLine("    CpuHog.exe -t 4 -d 30       4 threads at 100% for 30 seconds");
        Console.WriteLine("    CpuHog.exe -t 4 -p 60       4 threads at ~60% each (leaves headroom)");
        Console.WriteLine("    CpuHog.exe -m fp -s 512     floating point load, 512 KB chunks");
        Console.WriteLine("    CpuHog.exe -t 3 -p 25 -d 0  3 threads at 25%, run until Ctrl+C");
        Console.WriteLine("    CpuHog.exe -prio high       6 threads at 100%, high priority, no throttling");
        Console.WriteLine("    CpuHog.exe -eco             same but leave Windows throttling enabled");
        Console.WriteLine();
        Console.WriteLine("  OUTPUT");
        Console.WriteLine("    one status line per second: elapsed, completed chunks, chunks/second, CPU time");
        Console.WriteLine("    on exit: total elapsed, chunks, CPU time, and avgUtil (achieved % of one core");
        Console.WriteLine("    per thread, so 100 means every thread kept one core busy)");
        Console.WriteLine();
        Console.WriteLine("  NOTES");
        Console.WriteLine("    Built as AnyCPU IL, so it runs on the native CLR (no x64 emulation overhead).");
        Console.WriteLine("    Ctrl+C stops all worker threads cleanly; exit code 0 on success, 2 on bad arguments.");
        Console.WriteLine("    Stop a background instance with: Stop-Process -Name CpuHog");
        Console.WriteLine();
    }

    private static bool Parse(string[] a)
    {
        for (int i = 0; i < a.Length; i++)
        {
            string k = a[i];
            string v = (i + 1 < a.Length) ? a[i + 1] : null;

            if (IsHelp(k)) { ShowHelp(); Environment.Exit(0); }
            if (k == "-v" || k == "--version") { Console.WriteLine("CpuHog " + Version); Environment.Exit(0); }

            switch (k)
            {
                case "-eco":
                    _keepEco = true;
                    break;
                case "-prio":
                    if (v == null) { BadArg(k, "missing value"); return false; }
                    _prio = v.ToLowerInvariant();
                    if (_prio != "normal" && _prio != "abovenormal" && _prio != "high" && _prio != "realtime")
                    {
                        BadArg(k, "unknown priority '" + v + "' (use normal|abovenormal|high|realtime)");
                        return false;
                    }
                    i++; break;
                case "-t":
                    if (!TakeInt(k, v, out _threads)) return false;
                    i++; break;
                case "-d":
                    if (!TakeInt(k, v, out _seconds)) return false;
                    i++; break;
                case "-p":
                    if (!TakeInt(k, v, out _percent)) return false;
                    i++; break;
                case "-s":
                    if (!TakeInt(k, v, out _chunkKB)) return false;
                    i++; break;
                case "-m":
                    if (v == null) { BadArg(k, "missing value"); return false; }
                    _mode = v.ToLowerInvariant();
                    if (_mode != "int" && _mode != "fp" && _mode != "mem" && _mode != "mix")
                    {
                        BadArg(k, "unknown mode '" + v + "' (use int|fp|mem|mix)");
                        return false;
                    }
                    i++; break;
                default:
                    BadArg(k, "unknown option");
                    return false;
            }
        }
        return true;
    }

    private static bool TakeInt(string opt, string v, out int result)
    {
        result = 0;
        if (v == null) { BadArg(opt, "missing value"); return false; }
        if (!int.TryParse(v, out result)) { BadArg(opt, "not a number: '" + v + "'"); return false; }
        return true;
    }

    private static void BadArg(string opt, string why)
    {
        Console.Error.WriteLine("CpuHog: " + opt + ": " + why);
        Console.Error.WriteLine("Try 'CpuHog.exe help' for usage.");
    }

    private static void Worker(int id)
    {
        // keep this thread out of Windows power throttling and give it priority
        try
        {
            IntPtr th = GetCurrentThread();
            SetThreadPriority(th, THREAD_PRIORITY_HIGHEST);

            var tst = new THREAD_POWER_THROTTLING_STATE();
            tst.Version = THROTTLING_VERSION;
            tst.ControlMask = EXECUTION_SPEED;
            tst.StateMask = _keepEco ? EXECUTION_SPEED : 0;
            if (SetThreadInformation(th, ThreadPowerThrottling, ref tst,
                (uint)Marshal.SizeOf(typeof(THREAD_POWER_THROTTLING_STATE))) && !_keepEco)
            {
                Interlocked.Increment(ref _threadOptOut);
            }
        }
        catch { /* best effort */ }

        // time-based duty cycle with proportional feedback
        int busyMs = 10 * _percent / 100;
        if (busyMs < 1) busyMs = 1;
        long busyTicks = busyMs * Stopwatch.Frequency / 1000L;

        double acc = 1.0 + id;
        long x = (long)(id * 2654435761L) ^ 0x5DEECE66DL;
        long local = 0;
        byte[] buf = (_mode == "mem" || _mode == "mix") ? new byte[_chunkKB * 1024] : null;
        long flushAt = 32;
        var sw = Stopwatch.StartNew();

        double ratio = _percent / 100.0;
        long busyAccum = 0;
        while (!_stop)
        {
            long sliceStart = sw.ElapsedTicks;
            do
            {
                DoChunk(id, ref x, ref acc, buf);
                local++;
                if (local >= flushAt)
                {
                    Interlocked.Add(ref _chunks, flushAt);
                    local = 0;
                }
            } while (!_stop && (sw.ElapsedTicks - sliceStart) < busyTicks);

            busyAccum += (sw.ElapsedTicks - sliceStart);

            if (ratio < 1.0 && !_stop)
            {
                long targetTotal = (long)(busyAccum / ratio);
                long sleepMs = (targetTotal - sw.ElapsedTicks) * 1000L / Stopwatch.Frequency;
                if (sleepMs > 0) Thread.Sleep((int)Math.Min(sleepMs, 50));
            }
        }

        if (local > 0) Interlocked.Add(ref _chunks, local);
        if (x == 42 && acc == 42.0) Console.Error.WriteLine("impossible");
    }

    private static void DoChunk(int id, ref long x, ref double acc, byte[] buf)
    {
        switch (_mode)
        {
            case "int":
                for (int i = 0; i < 20000; i++)
                {
                    x ^= x << 13; x ^= (long)((ulong)x >> 7); x ^= x << 17;
                    x += i;
                }
                break;

            case "fp":
                for (int i = 0; i < 20000; i++)
                {
                    acc = Math.Sqrt(acc * 1.0000001 + 1.234567);
                    acc = acc * 0.9999999 + Math.Sin(acc) * 0.000001;
                }
                break;

            case "mem":
                for (int i = 0; i < buf.Length; i += 64) buf[i] = (byte)(i ^ (int)x);
                for (int i = 0; i < buf.Length; i += 4096) x += buf[i];
                break;

            default: // mix
                for (int i = 0; i < 8000; i++)
                {
                    x ^= x << 13; x ^= (long)((ulong)x >> 7); x ^= x << 17;
                    acc = Math.Sqrt(acc * 1.0000001 + 1.234567);
                }
                if ((x & 7) == 0)
                {
                    for (int i = 0; i < buf.Length; i += 64) buf[i] = (byte)(i ^ (int)x);
                }
                break;
        }
    }
}



