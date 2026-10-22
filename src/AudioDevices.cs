using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace XiaoJie
{
    /// <summary>稳定设备标识用于本次录制；空标识在开始时解析为系统默认设备。</summary>
    internal sealed class AudioDeviceInfo
    {
        internal string Id { get; private set; }
        internal string Name { get; private set; }
        internal AudioDeviceInfo(string id, string name) { Id = id ?? ""; Name = name ?? "音频设备"; }
        public override string ToString() { return Name; }
    }

    internal sealed class AudioProbeSnapshot
    {
        internal float Peak { get; private set; }
        internal bool Completed { get; private set; }
        internal AudioProbeSnapshot(float peak, bool completed) { Peak = peak; Completed = completed; }
    }

    /// <summary>枚举只读取设备元数据。Start 只能响应用户明确的测试动作。</summary>
    internal interface IAudioServices
    {
        IList<AudioDeviceInfo> List(bool system);
        IAudioProbe Start(int mode, string systemDevice, string microphoneDevice);
    }

    internal interface IAudioProbe : IDisposable
    {
        AudioProbeSnapshot Poll();
        byte[] GetPcm();
    }

    internal sealed class NativeAudioServices : IAudioServices
    {
        public IList<AudioDeviceInfo> List(bool system)
        {
            List<AudioDeviceInfo> items = new List<AudioDeviceInfo>();
            items.Add(new AudioDeviceInfo("", system ? "默认播放设备" : "默认麦克风"));
            StringBuilder buffer = new StringBuilder(65536);
            Check(RcEnumerateAudioDevices(system ? 1 : 0, buffer, buffer.Capacity));
            foreach (string line in buffer.ToString().Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                int separator = line.IndexOf('\t');
                if (separator > 0) items.Add(new AudioDeviceInfo(line.Substring(0, separator), line.Substring(separator + 1)));
            }
            return items;
        }

        public IAudioProbe Start(int mode, string systemDevice, string microphoneDevice)
        {
            return new NativeAudioProbe(mode, systemDevice, microphoneDevice);
        }

        internal static void Check(int result)
        {
            if (result >= 0) return;
            StringBuilder text = new StringBuilder(512);
            RcGetError(IntPtr.Zero, text, text.Capacity);
            throw new InvalidOperationException((text.Length > 0 ? text.ToString() : "声音设备操作失败") +
                " (0x" + unchecked((uint)result).ToString("X8") + ")");
        }

        [DllImport("RecorderNative.dll", CharSet = CharSet.Unicode, CallingConvention = CallingConvention.Cdecl)]
        private static extern int RcEnumerateAudioDevices(int system, StringBuilder buffer, int capacity);
        [DllImport("RecorderNative.dll", CharSet = CharSet.Unicode, CallingConvention = CallingConvention.Cdecl)]
        private static extern void RcGetError(IntPtr handle, StringBuilder buffer, int capacity);
    }

    /// <summary>测试 PCM 只留在内存，最多五秒；释放后立即关闭音源。</summary>
    internal sealed class NativeAudioProbe : IAudioProbe
    {
        private IntPtr _handle;
        private readonly short[] _buffer = new short[48000];
        private readonly MemoryStream _pcm = new MemoryStream();
        private bool _completed;
        internal NativeAudioProbe(int mode, string systemDevice, string microphoneDevice)
        {
            NativeAudioServices.Check(RcAudioTestStart(mode, systemDevice ?? "", microphoneDevice ?? "", out _handle));
        }
        public AudioProbeSnapshot Poll()
        {
            if (_handle == IntPtr.Zero) throw new ObjectDisposedException("NativeAudioProbe");
            if (_completed) return new AudioProbeSnapshot(0f, true);
            int count, complete;
            float peak;
            NativeAudioServices.Check(RcAudioTestRead(_handle, _buffer, _buffer.Length, out count, out peak, out complete));
            if (count < 0 || count > _buffer.Length) throw new InvalidOperationException("声音测试返回无效缓冲区");
            byte[] bytes = new byte[count * 2];
            Buffer.BlockCopy(_buffer, 0, bytes, 0, bytes.Length);
            if (_pcm.Length + bytes.Length > 48000 * 2 * 2 * 5) throw new InvalidOperationException("声音测试超过时长限制");
            _pcm.Write(bytes, 0, bytes.Length);
            _completed = complete != 0;
            return new AudioProbeSnapshot(peak, _completed);
        }
        public byte[] GetPcm() { return _pcm.ToArray(); }
        public void Dispose()
        {
            if (_handle != IntPtr.Zero) { RcAudioTestDestroy(_handle); _handle = IntPtr.Zero; }
            _pcm.Dispose();
        }
        [DllImport("RecorderNative.dll", CharSet = CharSet.Unicode, CallingConvention = CallingConvention.Cdecl)]
        private static extern int RcAudioTestStart(int mode, string systemDevice, string microphoneDevice, out IntPtr handle);
        [DllImport("RecorderNative.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern int RcAudioTestRead(IntPtr handle, [Out] short[] buffer, int capacity, out int samples, out float peak, out int completed);
        [DllImport("RecorderNative.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern void RcAudioTestDestroy(IntPtr handle);
    }

    internal static class AudioWave
    {
        internal static MemoryStream WrapPcm(byte[] pcm)
        {
            pcm = pcm ?? new byte[0];
            MemoryStream wave = new MemoryStream(44 + pcm.Length);
            using (BinaryWriter writer = new BinaryWriter(wave, Encoding.ASCII, true))
            {
                writer.Write(Encoding.ASCII.GetBytes("RIFF")); writer.Write(36 + pcm.Length);
                writer.Write(Encoding.ASCII.GetBytes("WAVEfmt ")); writer.Write(16); writer.Write((short)1);
                writer.Write((short)2); writer.Write(48000); writer.Write(48000 * 4); writer.Write((short)4); writer.Write((short)16);
                writer.Write(Encoding.ASCII.GetBytes("data")); writer.Write(pcm.Length); writer.Write(pcm);
            }
            wave.Position = 0;
            return wave;
        }
        internal static MemoryStream CreateTone(double frequency, double seconds)
        {
            byte[] pcm = new byte[(int)(48000 * seconds) * 4];
            for (int sample = 0; sample < pcm.Length / 4; ++sample)
            {
                short value = (short)(Math.Sin(2 * Math.PI * frequency * sample / 48000) * 0.2 * short.MaxValue);
                pcm[sample * 4] = pcm[sample * 4 + 2] = (byte)value;
                pcm[sample * 4 + 1] = pcm[sample * 4 + 3] = (byte)(value >> 8);
            }
            return WrapPcm(pcm);
        }
    }
}
