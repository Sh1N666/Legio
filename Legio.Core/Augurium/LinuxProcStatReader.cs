using System.Runtime.InteropServices;

namespace Legio.Core.Augurium;

/// <summary>
/// Zero-allocation Linux /proc/stat reader using a persistent file descriptor and pread(2).
/// Computes total host CPU utilization across all logical cores (Option A.3: ExternalCpuLoad).
/// </summary>
internal sealed unsafe class LinuxProcStatReader : IDisposable
{
    private const int OpenReadOnly = 0;

    private int _fd = -1;
    private readonly byte[] _buffer;
    private ulong _lastTotalJiffies;
    private ulong _lastIdleJiffies;
    private bool _disposed;

    public bool IsAvailable { get; }

    public LinuxProcStatReader()
    {
        _buffer = OperatingSystem.IsLinux() ? new byte[256] : Array.Empty<byte>();
        IsAvailable = OperatingSystem.IsLinux() && TryInit();
    }

    /// <summary>
    /// Samples total host CPU busy ratio in [0.0, 1.0] since the previous call (0 B GC allocation).
    /// </summary>
    public bool TrySampleSystemCpuLoad(out float systemCpuLoad)
    {
        systemCpuLoad = 0.0f;
        if (!IsAvailable || _fd < 0 || _disposed)
            return false;

        if (!TryReadJiffies(out ulong totalJiffies, out ulong idleJiffies))
            return false;

        ulong dTotal = totalJiffies >= _lastTotalJiffies ? totalJiffies - _lastTotalJiffies : 0;
        ulong dIdle = idleJiffies >= _lastIdleJiffies ? idleJiffies - _lastIdleJiffies : 0;
        _lastTotalJiffies = totalJiffies;
        _lastIdleJiffies = idleJiffies;

        if (dTotal == 0)
            return false;

        double busy = dTotal > dIdle ? (double)(dTotal - dIdle) / dTotal : 0.0;
        systemCpuLoad = (float)Math.Clamp(busy, 0.0, 1.0);
        return true;
    }

    private bool TryInit()
    {
        try
        {
            ReadOnlySpan<byte> path = "/proc/stat\0"u8;
            fixed (byte* pPath = path)
            {
                _fd = LinuxOpen(pPath, OpenReadOnly);
            }

            if (_fd >= 0 && TryReadJiffies(out ulong total, out ulong idle))
            {
                _lastTotalJiffies = total;
                _lastIdleJiffies = idle;
                return true;
            }
        }
        catch
        {
            // Ignore in restricted environments
        }

        CloseFd();
        return false;
    }

    private bool TryReadJiffies(out ulong totalJiffies, out ulong idleJiffies)
    {
        totalJiffies = 0;
        idleJiffies = 0;

        nint bytesRead;
        fixed (byte* pBuf = _buffer)
        {
            bytesRead = LinuxPread(_fd, pBuf, (nuint)_buffer.Length, 0);
        }

        if (bytesRead <= 4)
            return false;

        // First line format: "cpu  user nice system idle iowait irq softirq steal ..."
        int len = (int)bytesRead;
        int idx = 0;

        while (idx < len && _buffer[idx] != (byte)' ' && _buffer[idx] != (byte)'\n')
            idx++;

        int field = 0;
        ulong sum = 0;
        ulong idleSum = 0;

        while (idx < len && _buffer[idx] != (byte)'\n')
        {
            while (idx < len && _buffer[idx] == (byte)' ')
                idx++;

            if (idx >= len || _buffer[idx] == (byte)'\n')
                break;

            ulong val = 0;
            bool anyDigit = false;
            while (idx < len)
            {
                byte ch = _buffer[idx];
                if (ch < (byte)'0' || ch > (byte)'9')
                    break;
                val = val * 10UL + (ulong)(ch - (byte)'0');
                anyDigit = true;
                idx++;
            }

            if (!anyDigit)
                break;

            sum += val;
            // field 3 = idle, field 4 = iowait
            if (field == 3 || field == 4)
                idleSum += val;

            field++;
            if (field >= 10)
                break;
        }

        if (field < 4)
            return false;

        totalJiffies = sum;
        idleJiffies = idleSum;
        return true;
    }

    private void CloseFd()
    {
        if (_fd >= 0)
        {
            LinuxClose(_fd);
            _fd = -1;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        CloseFd();
    }

    [DllImport("libc", EntryPoint = "open", ExactSpelling = true)]
    private static extern int LinuxOpen(byte* pathname, int flags);

    [DllImport("libc", EntryPoint = "pread", ExactSpelling = true)]
    private static extern nint LinuxPread(int fd, void* buf, nuint count, long offset);

    [DllImport("libc", EntryPoint = "close", ExactSpelling = true)]
    private static extern int LinuxClose(int fd);
}
