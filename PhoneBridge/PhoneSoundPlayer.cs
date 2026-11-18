using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;

namespace PhoneBridge;

/// <summary>
/// Plays the iPhone's own sound (iOS 27+ mirroring audio) that the WDA bridge decodes and serves as
/// raw 48 kHz stereo 16-bit PCM on 127.0.0.1:9102. Uses winmm waveOut with a small buffer ring so the
/// sound stays within about 100 ms of the picture; late audio is dropped rather than queued.
/// </summary>
sealed class PhoneSoundPlayer : IDisposable
{
    public const int Port = 9102;
    const int SampleRate = 48000, BlockBytes = SampleRate / 50 * 4; // 20 ms blocks
    const int BlockCount = 6, StartBlocks = 3;                     // at most 120 ms queued

    readonly CancellationTokenSource _cts = new();
    readonly Thread _thread;

    public PhoneSoundPlayer()
    {
        _thread = new Thread(Run) { IsBackground = true, Name = "iPhone sound" };
        _thread.Start();
    }

    public static bool Available()
    {
        try
        {
            return System.Net.NetworkInformation.IPGlobalProperties.GetIPGlobalProperties()
                .GetActiveTcpListeners().Any(e => e.Port == Port);
        }
        catch { return false; }
    }

    void Run()
    {
        while (!_cts.IsCancellationRequested)
        {
            try { PlayOnce(); } catch { }
            _cts.Token.WaitHandle.WaitOne(1000);
        }
    }

    void PlayOnce()
    {
        using var tcp = new TcpClient { NoDelay = true, ReceiveTimeout = 3000 };
        tcp.Connect("127.0.0.1", Port);
        using var stream = tcp.GetStream();
        stream.Write(Encoding.ASCII.GetBytes($"GET / HTTP/1.1\r\nHost: 127.0.0.1:{Port}\r\n\r\n"));
        SkipHeaders(stream);
        using var output = new WaveOut(SampleRate, BlockBytes, BlockCount);
        using var cancel = _cts.Token.Register(() => { try { tcp.Close(); } catch { } });
        var block = new byte[BlockBytes];
        int filled = 0, queued = 0;
        var buffer = new byte[16384];
        while (!_cts.IsCancellationRequested)
        {
            int n = stream.Read(buffer, 0, buffer.Length);
            if (n <= 0) return;
            for (int i = 0; i < n;)
            {
                int take = Math.Min(n - i, BlockBytes - filled);
                Buffer.BlockCopy(buffer, i, block, filled, take);
                filled += take;
                i += take;
                if (filled < BlockBytes) continue;
                filled = 0;
                // Hold a few blocks before starting so small network gaps don't click.
                if (output.Write(block)) queued++;
                if (queued == StartBlocks) output.Resume();
            }
        }
    }

    static void SkipHeaders(NetworkStream stream)
    {
        int matched = 0;
        while (matched < 4)
        {
            int b = stream.ReadByte();
            if (b < 0) throw new IOException("closed");
            matched = (b == (matched % 2 == 0 ? '\r' : '\n')) ? matched + 1 : (b == '\r' ? 1 : 0);
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _thread.Join(2000);
        _cts.Dispose();
    }

    /// <summary>Minimal winmm waveOut ring of fixed-size blocks. Write drops the block when all are busy.</summary>
    sealed class WaveOut : IDisposable
    {
        [StructLayout(LayoutKind.Sequential)]
        struct WaveFormat { public short Tag, Channels; public int SamplesPerSec, AvgBytesPerSec; public short BlockAlign, BitsPerSample, Size; }

        [StructLayout(LayoutKind.Sequential)]
        struct WaveHeader { public IntPtr Data; public int BufferLength, BytesRecorded; public IntPtr User; public int Flags, Loops; public IntPtr Next, Reserved; }

        [DllImport("winmm.dll")] static extern int waveOutOpen(out IntPtr handle, int device, ref WaveFormat format, IntPtr callback, IntPtr instance, int flags);
        [DllImport("winmm.dll")] static extern int waveOutPrepareHeader(IntPtr handle, IntPtr header, int size);
        [DllImport("winmm.dll")] static extern int waveOutUnprepareHeader(IntPtr handle, IntPtr header, int size);
        [DllImport("winmm.dll")] static extern int waveOutWrite(IntPtr handle, IntPtr header, int size);
        [DllImport("winmm.dll")] static extern int waveOutPause(IntPtr handle);
        [DllImport("winmm.dll")] static extern int waveOutRestart(IntPtr handle);
        [DllImport("winmm.dll")] static extern int waveOutReset(IntPtr handle);
        [DllImport("winmm.dll")] static extern int waveOutClose(IntPtr handle);

        const int WaveMapper = -1, WhdrDone = 1;
        static readonly int HeaderSize = Marshal.SizeOf<WaveHeader>();
        readonly IntPtr _handle;
        readonly IntPtr[] _headers, _data;
        readonly bool[] _used;
        readonly int _blockBytes;

        public WaveOut(int rate, int blockBytes, int count)
        {
            var format = new WaveFormat { Tag = 1, Channels = 2, SamplesPerSec = rate, AvgBytesPerSec = rate * 4, BlockAlign = 4, BitsPerSample = 16 };
            if (waveOutOpen(out _handle, WaveMapper, ref format, IntPtr.Zero, IntPtr.Zero, 0) != 0)
                throw new IOException("waveOutOpen failed");
            waveOutPause(_handle);
            _blockBytes = blockBytes;
            _headers = new IntPtr[count];
            _data = new IntPtr[count];
            _used = new bool[count];
            for (int i = 0; i < count; i++)
            {
                _data[i] = Marshal.AllocHGlobal(blockBytes);
                _headers[i] = Marshal.AllocHGlobal(HeaderSize);
                Marshal.StructureToPtr(new WaveHeader { Data = _data[i], BufferLength = blockBytes }, _headers[i], false);
                waveOutPrepareHeader(_handle, _headers[i], HeaderSize);
            }
        }

        public bool Write(byte[] block)
        {
            for (int i = 0; i < _headers.Length; i++)
            {
                var header = Marshal.PtrToStructure<WaveHeader>(_headers[i]);
                if (_used[i] && (header.Flags & WhdrDone) == 0) continue;
                Marshal.Copy(block, 0, _data[i], _blockBytes);
                _used[i] = true;
                waveOutWrite(_handle, _headers[i], HeaderSize);
                return true;
            }
            return false;
        }

        public void Resume() => waveOutRestart(_handle);

        public void Dispose()
        {
            waveOutReset(_handle);
            for (int i = 0; i < _headers.Length; i++)
            {
                waveOutUnprepareHeader(_handle, _headers[i], HeaderSize);
                Marshal.FreeHGlobal(_headers[i]);
                Marshal.FreeHGlobal(_data[i]);
            }
            waveOutClose(_handle);
        }
    }
}
