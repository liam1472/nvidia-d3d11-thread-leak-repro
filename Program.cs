using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

/// <summary>
/// Minimal repro for a three-way interoperability bug between
/// GStreamer, NVIDIA's D3D11 driver, and Intel's D3D11 driver.
///
/// BACKGROUND
/// ----------
/// A Windows desktop app (WPF + GStreamer-sharp) crashed after ~6 Enter/Exit
/// playback cycles on machines with NVIDIA GPUs (RTX 3070, RTX 5090).
/// Thread count grew by ~230 per cycle and never recovered. Intel iGPU ran
/// the same code indefinitely with no crash.
///
/// This repro isolates the three contributing factors:
///
///   Test A — Proper release
///     D3D11CreateDevice → Flush → Release (refcount → 0)
///     NVIDIA: threads released cleanly. Intel: same.
///     → NVIDIA driver is well-behaved when the contract is met.
///
///   Test B — Dangling ref (GStreamer-like cache)
///     D3D11CreateDevice → Flush → Release ONE ref but hold another
///     (simulates GStreamer's GstD3D11Device global cache retaining a ref
///     after the pipeline is disposed)
///     NVIDIA: ~17 threads accumulate per cycle, never released → crash.
///     Intel:  ~0 threads per cycle → stays manageable, no crash.
///     → Same bug, opposite outcomes due to driver thread count per device.
///
///   Test C — Cache drain
///     After Test B accumulates N cycles, release the held refs all at once.
///     NVIDIA: threads finally collapse back toward baseline.
///     → Confirms threads are tied to device lifetime, not a true driver leak.
///
/// CONCLUSION
/// ----------
///   GStreamer:  owns the ref-count bug — cache must release before next cycle.
///   NVIDIA:     high per-device thread count amplifies the GStreamer bug
///               into a crash. A shared thread pool (like Intel uses) would
///               make the driver resilient to this class of application error.
///   Intel:      low per-device thread count (~0) masks the GStreamer bug —
///               the app survives indefinitely even with the dangling ref.
///
/// BUILD
/// -----
///   C:\Windows\Microsoft.NET\Framework64\v4.0.30319\MSBuild.exe D3D11ThreadTest.csproj /p:Platform=x64
///
/// RUN
/// ---
///   bin\D3D11ThreadTest.exe              (all adapters, all tests)
///   bin\D3D11ThreadTest.exe --test a     (Test A only)
///   bin\D3D11ThreadTest.exe --test b     (Test B only)
///   bin\D3D11ThreadTest.exe --test c     (Test B + C drain)
///   bin\D3D11ThreadTest.exe --iter 10 --sleep 500
/// </summary>
class Program
{
    // -----------------------------------------------------------------------
    // P/Invoke — D3D11
    // -----------------------------------------------------------------------

    const int D3D_DRIVER_TYPE_UNKNOWN  = 0;
    const int D3D_DRIVER_TYPE_HARDWARE = 1;
    const int D3D11_SDK_VERSION        = 7;

    [DllImport("d3d11.dll", CallingConvention = CallingConvention.Winapi)]
    static extern int D3D11CreateDevice(
        IntPtr  pAdapter,
        int     DriverType,
        IntPtr  Software,
        int     Flags,
        IntPtr  pFeatureLevels,
        int     FeatureLevels,
        int     SDKVersion,
        out IntPtr ppDevice,
        out int    pFeatureLevel,
        out IntPtr ppImmediateContext);

    // ID3D11DeviceContext::Flush is at vtable slot 70 (0-indexed).
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    delegate void FlushDelegate(IntPtr self);

    static void CallFlush(IntPtr context)
    {
        IntPtr vtable    = Marshal.ReadIntPtr(context);
        IntPtr flushSlot = Marshal.ReadIntPtr(vtable, 70 * IntPtr.Size);
        var    flush     = (FlushDelegate)Marshal.GetDelegateForFunctionPointer(
                               flushSlot, typeof(FlushDelegate));
        flush(context);
    }

    // -----------------------------------------------------------------------
    // P/Invoke — DXGI (adapter enumeration)
    // -----------------------------------------------------------------------

    static readonly Guid IID_IDXGIFactory = new Guid("7b7166ec-21c7-44ae-b21a-c9ae321ae369");

    [DllImport("dxgi.dll", CallingConvention = CallingConvention.Winapi)]
    static extern int CreateDXGIFactory(ref Guid riid, out IntPtr ppFactory);

    // IDXGIFactory::EnumAdapters — vtable slot 7
    // IUnknown(3) + IDXGIObject(4) = 7
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    delegate int EnumAdaptersDelegate(IntPtr self, uint index, out IntPtr ppAdapter);

    static bool EnumAdapters(IntPtr factory, uint index, out IntPtr adapter)
    {
        IntPtr vtable = Marshal.ReadIntPtr(factory);
        IntPtr slot   = Marshal.ReadIntPtr(vtable, 7 * IntPtr.Size);
        var    fn     = (EnumAdaptersDelegate)Marshal.GetDelegateForFunctionPointer(
                            slot, typeof(EnumAdaptersDelegate));
        int hr = fn(factory, index, out adapter);
        return hr >= 0;
    }

    // IDXGIAdapter::GetDesc — vtable slot 8
    // IUnknown(3) + IDXGIObject(4) + EnumOutputs(1) = 8
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct DXGI_ADAPTER_DESC
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string Description;
        public uint   VendorId;
        public uint   DeviceId;
        public uint   SubSysId;
        public uint   Revision;
        public UIntPtr DedicatedVideoMemory;
        public UIntPtr DedicatedSystemMemory;
        public UIntPtr SharedSystemMemory;
        public long    AdapterLuid;
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    delegate int GetDescDelegate(IntPtr self, out DXGI_ADAPTER_DESC pDesc);

    static string GetAdapterName(IntPtr adapter)
    {
        try
        {
            IntPtr vtable = Marshal.ReadIntPtr(adapter);
            IntPtr slot   = Marshal.ReadIntPtr(vtable, 8 * IntPtr.Size);
            var    fn     = (GetDescDelegate)Marshal.GetDelegateForFunctionPointer(
                                slot, typeof(GetDescDelegate));
            DXGI_ADAPTER_DESC desc;
            fn(adapter, out desc);
            return desc.Description ?? "(unknown)";
        }
        catch { return "(unknown)"; }
    }

    static List<IntPtr> GetAllAdapters()
    {
        var adapters = new List<IntPtr>();
        Guid iid = IID_IDXGIFactory;
        IntPtr factory;
        if (CreateDXGIFactory(ref iid, out factory) < 0) return adapters;

        uint i = 0;
        IntPtr adapter;
        while (EnumAdapters(factory, i++, out adapter))
            adapters.Add(adapter);

        Marshal.Release(factory);
        return adapters;
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    static int CountThreads()
    {
        var p = Process.GetCurrentProcess();
        p.Refresh();
        return p.Threads.Count;
    }

    static bool CreateDevice(IntPtr adapter, out IntPtr device, out IntPtr context)
    {
        int featureLevel;
        int driverType = adapter == IntPtr.Zero
            ? D3D_DRIVER_TYPE_HARDWARE
            : D3D_DRIVER_TYPE_UNKNOWN;

        int hr = D3D11CreateDevice(
            adapter, driverType, IntPtr.Zero, 0,
            IntPtr.Zero, 0, D3D11_SDK_VERSION,
            out device, out featureLevel, out context);
        return hr >= 0;
    }

    static void PrintHeader(string title)
    {
        Console.WriteLine();
        Console.WriteLine(new string('=', 65));
        Console.WriteLine(string.Format("  {0}", title));
        Console.WriteLine(new string('=', 65));
        Console.WriteLine(string.Format("{0,-6} {1,-14} {2,-14} {3}",
            "Cycle", "AfterCreate", "AfterRelease", "Delta"));
        Console.WriteLine(new string('-', 65));
    }

    // -----------------------------------------------------------------------
    // Test A: Proper release
    // -----------------------------------------------------------------------

    static void RunTestA(IntPtr adapter, string adapterName, int iterations, int sleepMs)
    {
        PrintHeader(string.Format("TEST A — Proper release [{0}]", adapterName));
        Console.WriteLine("  Expect: threads released each cycle on ALL GPUs.");
        Console.WriteLine(new string('-', 65));

        int baseline = CountThreads();
        Console.WriteLine(string.Format("  Baseline: {0} threads", baseline));
        Console.WriteLine();

        for (int i = 0; i < iterations; i++)
        {
            IntPtr device, context;
            if (!CreateDevice(adapter, out device, out context))
            {
                Console.WriteLine("  D3D11CreateDevice failed — aborting.");
                return;
            }

            try { CallFlush(context); } catch { }

            int afterCreate = CountThreads();

            Marshal.Release(context);
            Marshal.Release(device);

            Thread.Sleep(sleepMs);

            int afterRelease = CountThreads();
            int delta        = afterRelease - afterCreate;
            Console.WriteLine(string.Format("{0,-6} {1,-14} {2,-14} {3:+0;-0;0}",
                i, afterCreate, afterRelease, delta));
        }

        Thread.Sleep(2000);
        int final = CountThreads();
        Console.WriteLine();
        Console.WriteLine(string.Format("  Final (after 2s): {0} threads  |  vs baseline: {1:+0;-0;0}",
            final, final - baseline));
    }

    // -----------------------------------------------------------------------
    // Test B: Dangling ref — simulates GStreamer GstD3D11Device cache
    // -----------------------------------------------------------------------

    static void RunTestB(IntPtr adapter, string adapterName, int iterations, int sleepMs,
                         out List<IntPtr> heldRefs)
    {
        PrintHeader(string.Format("TEST B — Dangling ref [{0}]", adapterName));
        Console.WriteLine("  One extra AddRef() held — device refcount stays at 1.");
        Console.WriteLine("  Simulates GStreamer's GstD3D11Device global cache.");
        Console.WriteLine(new string('-', 65));

        heldRefs = new List<IntPtr>();

        int baseline = CountThreads();
        Console.WriteLine(string.Format("  Baseline: {0} threads", baseline));
        Console.WriteLine();

        for (int i = 0; i < iterations; i++)
        {
            IntPtr device, context;
            if (!CreateDevice(adapter, out device, out context))
            {
                Console.WriteLine("  D3D11CreateDevice failed — likely hit OS thread/handle limit.");
                Console.WriteLine(string.Format("  Crashed at cycle {0} with {1} threads.", i, CountThreads()));
                return;
            }

            try { CallFlush(context); } catch { }

            int afterCreate = CountThreads();

            Marshal.AddRef(device);
            heldRefs.Add(device);

            Marshal.Release(context);
            Marshal.Release(device); // refcount → 1, device stays alive

            Thread.Sleep(sleepMs);

            int afterRelease = CountThreads();
            int delta        = afterRelease - baseline;
            Console.WriteLine(string.Format("{0,-6} {1,-14} {2,-14} accumulated: +{3}",
                i, afterCreate, afterRelease, delta));
        }

        Thread.Sleep(2000);
        int final = CountThreads();
        Console.WriteLine();
        Console.WriteLine(string.Format("  After {0} cycles: {1} threads  |  accumulated: +{2}",
            iterations, final, final - baseline));
    }

    // -----------------------------------------------------------------------
    // Test C: Drain
    // -----------------------------------------------------------------------

    static void RunTestC(List<IntPtr> heldRefs, string adapterName)
    {
        if (heldRefs == null || heldRefs.Count == 0) return;

        Console.WriteLine();
        Console.WriteLine(new string('=', 65));
        Console.WriteLine(string.Format("  TEST C — Cache drain [{0}] ({1} refs)",
            adapterName, heldRefs.Count));
        Console.WriteLine(new string('=', 65));
        Console.WriteLine("  Releasing all simulated GStreamer cache refs.");
        Console.WriteLine(new string('-', 65));

        int before = CountThreads();
        Console.WriteLine(string.Format("  Before drain: {0} threads", before));

        foreach (IntPtr d in heldRefs)
        {
            try { Marshal.Release(d); } catch { }
        }

        Thread.Sleep(2000);

        int after = CountThreads();
        Console.WriteLine(string.Format("  After drain (2s): {0} threads  |  recovered: {1:+0;-0;0}",
            after, after - before));
    }

    // -----------------------------------------------------------------------
    // Entry point
    // -----------------------------------------------------------------------

    static int Main(string[] args)
    {
        int    iterations = 10;
        int    sleepMs    = 200;
        string testMode   = "all";

        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--iter"  && i + 1 < args.Length) iterations = int.Parse(args[++i]);
            if (args[i] == "--sleep" && i + 1 < args.Length) sleepMs    = int.Parse(args[++i]);
            if (args[i] == "--test"  && i + 1 < args.Length) testMode   = args[++i].ToLower();
        }

        Console.WriteLine("D3D11 Thread Leak Repro — GStreamer / NVIDIA / AMD / Intel");
        Console.WriteLine(string.Format("Platform:   {0}", IntPtr.Size == 8 ? "x64" : "x86"));
        Console.WriteLine(string.Format("Iterations: {0}   Sleep: {1}ms   Mode: {2}",
            iterations, sleepMs, testMode));

        var adapters = GetAllAdapters();

        if (adapters.Count == 0)
        {
            Console.WriteLine("No DXGI adapters found — falling back to default hardware device.");
            adapters.Add(IntPtr.Zero);
        }
        else
        {
            Console.WriteLine(string.Format("\nFound {0} adapter(s):", adapters.Count));
            for (int i = 0; i < adapters.Count; i++)
                Console.WriteLine(string.Format("  [{0}] {1}", i, GetAdapterName(adapters[i])));
        }

        foreach (IntPtr adapter in adapters)
        {
            string name = adapter == IntPtr.Zero ? "Default" : GetAdapterName(adapter);

            // Skip Microsoft Basic Render Driver (software fallback)
            if (name.IndexOf("Microsoft Basic", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                Console.WriteLine(string.Format("\nSkipping software adapter: {0}", name));
                continue;
            }

            Console.WriteLine(string.Format("\n{0}", new string('*', 65)));
            Console.WriteLine(string.Format("  ADAPTER: {0}", name));
            Console.WriteLine(new string('*', 65));

            List<IntPtr> heldRefs = null;

            if (testMode == "all" || testMode == "a")
                RunTestA(adapter, name, iterations, sleepMs);

            if (testMode == "all" || testMode == "b" || testMode == "c")
                RunTestB(adapter, name, iterations, sleepMs, out heldRefs);

            if (testMode == "all" || testMode == "c")
                RunTestC(heldRefs, name);

            if (adapter != IntPtr.Zero)
                Marshal.Release(adapter);
        }

        Console.WriteLine();
        Console.WriteLine("Done. Press Enter to exit.");
        Console.ReadLine();
        return 0;
    }
}
