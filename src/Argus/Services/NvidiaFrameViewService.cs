using System.Runtime.InteropServices;
using System.IO;

namespace Argus.Services;

public sealed class NvidiaFrameViewService : IDisposable
{
    private const uint Success = 0;
    private const uint InsufficientBuffer = 0x10;
    private const uint AverageFpsMetric = 0x6C4B1348;
    private const uint GpuFanSpeedMetric = 0x4D8E9794;
    private readonly IntPtr library;
    private readonly IntPtr session;
    private readonly FvSdkCreateSession createSession = null!;
    private readonly FvSdkDestroySession destroySession = null!;
    private readonly FvSdkEnableMetrics enableMetrics = null!;
    private readonly FvSdkStartSession startSession = null!;
    private readonly FvSdkSampleData sampleData = null!;
    private readonly FvSdkReadData readData = null!;
    private readonly FvSdkStopSession stopSession = null!;
    private readonly FvSdkShutdown shutdown = null!;
    private bool started;

    public NvidiaFrameViewService()
    {
        string path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "NVIDIA Corporation",
            "FrameViewSDK",
            "SDK",
            "Public_Release",
            "FvSDK_x64.dll");

        if (!File.Exists(path))
        {
            return;
        }

        try
        {
            library = NativeLibrary.Load(path);
            var initialize = Load<FvSdkInitialize>("FvSDK_Initialize");
            createSession = Load<FvSdkCreateSession>("FvSDK_CreateSession");
            destroySession = Load<FvSdkDestroySession>("FvSDK_DestroySession");
            enableMetrics = Load<FvSdkEnableMetrics>("FvSDK_EnableMetrics");
            startSession = Load<FvSdkStartSession>("FvSDK_StartSession");
            sampleData = Load<FvSdkSampleData>("FvSDK_SampleData");
            readData = Load<FvSdkReadData>("FvSDK_ReadData");
            stopSession = Load<FvSdkStopSession>("FvSDK_StopSession");
            shutdown = Load<FvSdkShutdown>("FvSDK_Shutdown");

            if (initialize() != Success)
            {
                return;
            }

            IntPtr createdSession = IntPtr.Zero;
            if (createSession(out createdSession) != Success || createdSession == IntPtr.Zero)
            {
                return;
            }

            session = createdSession;
            uint[] metrics = { AverageFpsMetric, GpuFanSpeedMetric };
            if (enableMetrics(session, metrics, (uint)metrics.Length) != Success || startSession(session) != Success)
            {
                destroySession(session);
                session = IntPtr.Zero;
                return;
            }

            started = true;
        }
        catch (Exception)
        {
            if (library != IntPtr.Zero)
            {
                NativeLibrary.Free(library);
            }
        }
    }

    public double ReadFps()
    {
        if (!started || session == IntPtr.Zero)
        {
            return 0;
        }

        try
        {
            return ReadMetricValue(AverageFpsMetric, 48, 1000);
        }
        catch (Exception)
        {
            return 0;
        }
    }

    public double ReadGpuFanRpm()
    {
        if (!started || session == IntPtr.Zero)
        {
            return 0;
        }

        try
        {
            return ReadMetricValue(GpuFanSpeedMetric, 48, 10000);
        }
        catch (Exception)
        {
            return 0;
        }
    }

    public void Dispose()
    {
        if (started && session != IntPtr.Zero)
        {
            stopSession(session);
            destroySession(session);
            shutdown();
            started = false;
        }

        if (library != IntPtr.Zero)
        {
            NativeLibrary.Free(library);
        }
    }

    private T Load<T>(string name) where T : Delegate
    {
        IntPtr address = NativeLibrary.GetExport(library, name);
        return Marshal.GetDelegateForFunctionPointer<T>(address);
    }

    private double ReadMetricValue(uint metric, int valueOffset, double maximum)
    {
        if (sampleData(session, 0) != Success)
        {
            return 0;
        }

        SamplesNative samples = new()
        {
            Version = (uint)(Marshal.SizeOf<SamplesNative>() | (1u << 16)),
            Type = metric,
            Data = IntPtr.Zero,
            Count = UIntPtr.Zero
        };

        uint status = readData(session, ref samples, 1);
        if (status != InsufficientBuffer || samples.Count == UIntPtr.Zero)
        {
            return 0;
        }

        int count = checked((int)samples.Count.ToUInt64());
        const int itemSize = 56;
        IntPtr buffer = Marshal.AllocHGlobal(checked(itemSize * count));
        try
        {
            samples.Data = buffer;
            if (readData(session, ref samples, 1) != Success)
            {
                return 0;
            }

            double value = 0;
            for (int index = 0; index < count; index++)
            {
                long bits = Marshal.ReadInt64(buffer + (index * itemSize) + valueOffset);
                double candidate = BitConverter.Int64BitsToDouble(bits);
                if (candidate > value && candidate <= maximum)
                {
                    value = candidate;
                }
            }

            return value;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SamplesNative
    {
        public uint Version;
        public uint Type;
        public UIntPtr Count;
        public IntPtr Data;
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate uint FvSdkInitialize();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate uint FvSdkCreateSession(out IntPtr session);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate uint FvSdkDestroySession(IntPtr session);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate uint FvSdkEnableMetrics(IntPtr session, uint[] metrics, uint count);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate uint FvSdkStartSession(IntPtr session);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate uint FvSdkSampleData(IntPtr session, uint flags);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate uint FvSdkReadData(IntPtr session, ref SamplesNative samples, uint count);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate uint FvSdkStopSession(IntPtr session);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate uint FvSdkShutdown();
}
