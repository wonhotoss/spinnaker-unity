using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace SpinnakerUnity
{
    public enum SpinnakerBridgeResult
    {
        Ok = 0,
        Error = -1,
        NotInitialized = -2,
        NoCamera = -3,
        CameraNotOpen = -4,
        AlreadyStreaming = -5,
        NotStreaming = -6,
        InvalidArgument = -7,
        BufferTooSmall = -8,
        NodeUnavailable = -9,
        NoFrame = -10
    }

    public sealed class SpinnakerBridgeException : Exception
    {
        public SpinnakerBridgeResult Result { get; }

        public SpinnakerBridgeException(SpinnakerBridgeResult result, string message)
            : base($"{result}: {message}")
        {
            Result = result;
        }
    }

    public readonly struct CameraInfo
    {
        public CameraInfo(string serial, string model, string vendor)
        {
            Serial = serial;
            Model = model;
            Vendor = vendor;
        }

        public string Serial { get; }
        public string Model { get; }
        public string Vendor { get; }

        public override string ToString()
        {
            return string.IsNullOrEmpty(Serial)
                ? $"{Vendor} {Model}".Trim()
                : $"{Vendor} {Model} ({Serial})".Trim();
        }
    }

    public readonly struct NumericNode
    {
        public NumericNode(double value, double minimum, double maximum, bool readable, bool writable)
        {
            Value = value;
            Minimum = minimum;
            Maximum = maximum;
            Readable = readable;
            Writable = writable;
        }

        public double Value { get; }
        public double Minimum { get; }
        public double Maximum { get; }
        public bool Readable { get; }
        public bool Writable { get; }
    }

    public readonly struct IntegerNode
    {
        public IntegerNode(long value, long minimum, long maximum, bool readable, bool writable)
        {
            Value = value;
            Minimum = minimum;
            Maximum = maximum;
            Readable = readable;
            Writable = writable;
        }

        public long Value { get; }
        public long Minimum { get; }
        public long Maximum { get; }
        public bool Readable { get; }
        public bool Writable { get; }
    }

    public readonly struct EnumNode
    {
        public EnumNode(string value, bool readable, bool writable)
        {
            Value = value;
            Readable = readable;
            Writable = writable;
        }

        public string Value { get; }
        public bool Readable { get; }
        public bool Writable { get; }
    }

    public readonly struct SpinnakerFrame
    {
        public SpinnakerFrame(byte[] data, int width, int height, int stride, ulong frameId, double timestampMs)
        {
            Data = data;
            Width = width;
            Height = height;
            Stride = stride;
            FrameId = frameId;
            TimestampMs = timestampMs;
        }

        public byte[] Data { get; }
        public int Width { get; }
        public int Height { get; }
        public int Stride { get; }
        public ulong FrameId { get; }
        public double TimestampMs { get; }
    }

    public sealed class SpinnakerBridge : IDisposable
    {
        private const int TextBufferLength = 1024;
        private bool _initialized;
        private bool _disposed;

        public void Initialize()
        {
            ThrowIfDisposed();
            Check(Native.sub_initialize());
            _initialized = true;
        }

        public void Shutdown()
        {
            if (!_initialized)
            {
                return;
            }

            Native.sub_shutdown();
            _initialized = false;
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            Shutdown();
            _disposed = true;
            GC.SuppressFinalize(this);
        }

        public void RefreshCameras()
        {
            ThrowIfDisposed();
            Check(Native.sub_refresh_cameras());
        }

        public int CameraCount
        {
            get
            {
                ThrowIfDisposed();
                int count = Native.sub_get_camera_count();
                if (count < 0)
                {
                    Check(count);
                }

                return count;
            }
        }

        public CameraInfo GetCameraInfo(int index)
        {
            ThrowIfDisposed();
            var serial = new StringBuilder(TextBufferLength);
            var model = new StringBuilder(TextBufferLength);
            var vendor = new StringBuilder(TextBufferLength);
            Check(Native.sub_get_camera_info(index, serial, serial.Capacity, model, model.Capacity, vendor, vendor.Capacity));
            return new CameraInfo(serial.ToString(), model.ToString(), vendor.ToString());
        }

        public void OpenFirstCamera()
        {
            ThrowIfDisposed();
            Check(Native.sub_open_first_camera());
        }

        public void OpenCamera(int index)
        {
            ThrowIfDisposed();
            Check(Native.sub_open_camera_by_index(index));
        }

        public void CloseCamera()
        {
            ThrowIfDisposed();
            Check(Native.sub_close_camera());
        }

        public bool IsCameraOpen => _initialized && Native.sub_is_camera_open() != 0;
        public bool IsStreaming => _initialized && Native.sub_is_streaming() != 0;
        public bool IsInitialized => _initialized;

        public void StartStream()
        {
            ThrowIfDisposed();
            Check(Native.sub_start_stream(), SpinnakerBridgeResult.AlreadyStreaming);
        }

        public void StopStream()
        {
            ThrowIfDisposed();
            Check(Native.sub_stop_stream(), SpinnakerBridgeResult.NotStreaming);
        }

        public bool TryGetFrameSize(out int width, out int height, out int stride)
        {
            ThrowIfDisposed();
            int result = Native.sub_get_frame_size(out width, out height, out stride);
            if ((SpinnakerBridgeResult)result == SpinnakerBridgeResult.NoFrame)
            {
                return false;
            }

            Check(result);
            return true;
        }

        public bool TryGetLatestFrame(ref byte[] buffer, out SpinnakerFrame frame)
        {
            ThrowIfDisposed();
            frame = default;

            int bufferLength = buffer?.Length ?? 0;
            int result = Native.sub_get_latest_frame(
                buffer,
                bufferLength,
                out int width,
                out int height,
                out int stride,
                out ulong frameId,
                out double timestampMs);

            SpinnakerBridgeResult bridgeResult = (SpinnakerBridgeResult)result;
            if (bridgeResult == SpinnakerBridgeResult.NoFrame)
            {
                return false;
            }

            if (bridgeResult == SpinnakerBridgeResult.BufferTooSmall)
            {
                int requiredBytes = stride * height;
                if (requiredBytes <= 0)
                {
                    return false;
                }

                buffer = new byte[requiredBytes];
                Check(Native.sub_get_latest_frame(
                    buffer,
                    buffer.Length,
                    out width,
                    out height,
                    out stride,
                    out frameId,
                    out timestampMs));
            }
            else
            {
                Check(result);
            }

            frame = new SpinnakerFrame(buffer, width, height, stride, frameId, timestampMs);
            return true;
        }

        public NumericNode GetFloatNode(string nodeName)
        {
            ThrowIfDisposed();
            Check(Native.sub_get_float_node(nodeName, out double value, out double minimum, out double maximum, out int readable, out int writable));
            return new NumericNode(value, minimum, maximum, readable != 0, writable != 0);
        }

        public void SetFloatNode(string nodeName, double value, bool clampToRange = true)
        {
            ThrowIfDisposed();
            Check(Native.sub_set_float_node(nodeName, value, clampToRange ? 1 : 0));
        }

        public IntegerNode GetIntNode(string nodeName)
        {
            ThrowIfDisposed();
            Check(Native.sub_get_int_node(nodeName, out long value, out long minimum, out long maximum, out int readable, out int writable));
            return new IntegerNode(value, minimum, maximum, readable != 0, writable != 0);
        }

        public void SetIntNode(string nodeName, long value, bool clampToRange = true)
        {
            ThrowIfDisposed();
            Check(Native.sub_set_int_node(nodeName, value, clampToRange ? 1 : 0));
        }

        public bool GetBoolNode(string nodeName)
        {
            ThrowIfDisposed();
            Check(Native.sub_get_bool_node(nodeName, out int value, out _, out _));
            return value != 0;
        }

        public void SetBoolNode(string nodeName, bool value)
        {
            ThrowIfDisposed();
            Check(Native.sub_set_bool_node(nodeName, value ? 1 : 0));
        }

        public EnumNode GetEnumNode(string nodeName)
        {
            ThrowIfDisposed();
            var value = new StringBuilder(TextBufferLength);
            Check(Native.sub_get_enum_node(nodeName, value, value.Capacity, out int readable, out int writable));
            return new EnumNode(value.ToString(), readable != 0, writable != 0);
        }

        public void SetEnumNode(string nodeName, string entryName)
        {
            ThrowIfDisposed();
            Check(Native.sub_set_enum_node(nodeName, entryName));
        }

        public string[] GetEnumEntries(string nodeName)
        {
            ThrowIfDisposed();
            var value = new StringBuilder(4096);
            Check(Native.sub_get_enum_entries(nodeName, value, value.Capacity));
            string joined = value.ToString();
            return string.IsNullOrEmpty(joined)
                ? Array.Empty<string>()
                : joined.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries);
        }

        public bool ExposureAuto
        {
            get
            {
                Check(Native.sub_get_exposure_auto(out int enabled));
                return enabled != 0;
            }
            set => Check(Native.sub_set_exposure_auto(value ? 1 : 0));
        }

        public NumericNode ExposureTime => GetFixedFloat(Native.sub_get_exposure_time);
        public void SetExposureTime(double valueUs) => Check(Native.sub_set_exposure_time(valueUs));

        public bool GainAuto
        {
            get
            {
                Check(Native.sub_get_gain_auto(out int enabled));
                return enabled != 0;
            }
            set => Check(Native.sub_set_gain_auto(value ? 1 : 0));
        }

        public NumericNode Gain => GetFixedFloat(Native.sub_get_gain);
        public void SetGain(double valueDb) => Check(Native.sub_set_gain(valueDb));

        public bool FrameRateEnabled
        {
            get
            {
                Check(Native.sub_get_frame_rate_enabled(out int enabled));
                return enabled != 0;
            }
            set => Check(Native.sub_set_frame_rate_enabled(value ? 1 : 0));
        }

        public NumericNode FrameRate => GetFixedFloat(Native.sub_get_frame_rate);
        public void SetFrameRate(double valueFps) => Check(Native.sub_set_frame_rate(valueFps));

        public bool GammaEnabled
        {
            get
            {
                Check(Native.sub_get_gamma_enabled(out int enabled));
                return enabled != 0;
            }
            set => Check(Native.sub_set_gamma_enabled(value ? 1 : 0));
        }

        public NumericNode Gamma => GetFixedFloat(Native.sub_get_gamma);
        public void SetGamma(double value) => Check(Native.sub_set_gamma(value));

        public bool WhiteBalanceAuto
        {
            get
            {
                Check(Native.sub_get_white_balance_auto(out int enabled));
                return enabled != 0;
            }
            set => Check(Native.sub_set_white_balance_auto(value ? 1 : 0));
        }

        public NumericNode GetBalanceRatio(string selector)
        {
            Check(Native.sub_get_balance_ratio(selector, out double value, out double minimum, out double maximum));
            return new NumericNode(value, minimum, maximum, true, true);
        }

        public void SetBalanceRatio(string selector, double value)
        {
            Check(Native.sub_set_balance_ratio(selector, value));
        }

        public static string LastError
        {
            get
            {
                var buffer = new StringBuilder(2048);
                Native.sub_get_last_error(buffer, buffer.Capacity);
                return buffer.ToString();
            }
        }

        private delegate int FixedFloatGetter(out double value, out double minimum, out double maximum);

        private static NumericNode GetFixedFloat(FixedFloatGetter getter)
        {
            Check(getter(out double value, out double minimum, out double maximum));
            return new NumericNode(value, minimum, maximum, true, true);
        }

        private static void Check(int result, params SpinnakerBridgeResult[] allowedResults)
        {
            if (result >= 0)
            {
                return;
            }

            var bridgeResult = (SpinnakerBridgeResult)result;
            foreach (SpinnakerBridgeResult allowed in allowedResults)
            {
                if (bridgeResult == allowed)
                {
                    return;
                }
            }

            throw new SpinnakerBridgeException(bridgeResult, LastError);
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(SpinnakerBridge));
            }
        }

        private static class Native
        {
            private const string DllName = "SpinnakerUnityBridge";

            static Native()
            {
                ConfigureSpinnakerDllSearchPath();
            }

            [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Unicode)]
            private static extern bool SetDllDirectory(string lpPathName);

            private static void ConfigureSpinnakerDllSearchPath()
            {
                string sdkDir = Environment.GetEnvironmentVariable("SPINNAKER_SDK_DIR");
                string[] candidates =
                {
                    string.IsNullOrEmpty(sdkDir) ? null : Path.Combine(sdkDir, "bin64", "vs2015"),
                    string.IsNullOrEmpty(sdkDir) ? null : Path.Combine(sdkDir, "bin64", "vs2017"),
                    @"C:\Program Files\Teledyne\Spinnaker\bin64\vs2015",
                    @"C:\Program Files\Teledyne\Spinnaker\bin64\vs2017",
                    @"C:\Program Files\FLIR Systems\Spinnaker\bin64\vs2015",
                    @"C:\Program Files\FLIR Systems\Spinnaker\bin64\vs2017"
                };

                foreach (string candidate in candidates)
                {
                    if (!string.IsNullOrEmpty(candidate) && File.Exists(Path.Combine(candidate, "Spinnaker_v140.dll")))
                    {
                        SetDllDirectory(candidate);
                        return;
                    }
                }
            }

            [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
            internal static extern int sub_initialize();

            [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
            internal static extern void sub_shutdown();

            [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
            internal static extern int sub_get_last_error(StringBuilder buffer, int bufferLength);

            [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
            internal static extern int sub_refresh_cameras();

            [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
            internal static extern int sub_get_camera_count();

            [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
            internal static extern int sub_get_camera_info(
                int index,
                StringBuilder serial,
                int serialLength,
                StringBuilder model,
                int modelLength,
                StringBuilder vendor,
                int vendorLength);

            [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
            internal static extern int sub_open_first_camera();

            [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
            internal static extern int sub_open_camera_by_index(int index);

            [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
            internal static extern int sub_close_camera();

            [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
            internal static extern int sub_is_camera_open();

            [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
            internal static extern int sub_is_streaming();

            [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
            internal static extern int sub_start_stream();

            [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
            internal static extern int sub_stop_stream();

            [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
            internal static extern int sub_get_frame_size(out int width, out int height, out int stride);

            [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
            internal static extern int sub_get_latest_frame(
                [Out] byte[] buffer,
                int bufferLength,
                out int width,
                out int height,
                out int stride,
                out ulong frameId,
                out double timestampMs);

            [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
            internal static extern int sub_get_float_node(
                string nodeName,
                out double value,
                out double minimum,
                out double maximum,
                out int readable,
                out int writable);

            [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
            internal static extern int sub_set_float_node(string nodeName, double value, int clampToRange);

            [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
            internal static extern int sub_get_int_node(
                string nodeName,
                out long value,
                out long minimum,
                out long maximum,
                out int readable,
                out int writable);

            [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
            internal static extern int sub_set_int_node(string nodeName, long value, int clampToRange);

            [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
            internal static extern int sub_get_bool_node(string nodeName, out int value, out int readable, out int writable);

            [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
            internal static extern int sub_set_bool_node(string nodeName, int value);

            [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
            internal static extern int sub_get_enum_node(
                string nodeName,
                StringBuilder value,
                int valueLength,
                out int readable,
                out int writable);

            [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
            internal static extern int sub_set_enum_node(string nodeName, string entryName);

            [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
            internal static extern int sub_get_enum_entries(string nodeName, StringBuilder entries, int entriesLength);

            [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
            internal static extern int sub_get_exposure_auto(out int enabled);

            [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
            internal static extern int sub_set_exposure_auto(int enabled);

            [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
            internal static extern int sub_get_exposure_time(out double value, out double minimum, out double maximum);

            [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
            internal static extern int sub_set_exposure_time(double valueUs);

            [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
            internal static extern int sub_get_gain_auto(out int enabled);

            [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
            internal static extern int sub_set_gain_auto(int enabled);

            [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
            internal static extern int sub_get_gain(out double value, out double minimum, out double maximum);

            [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
            internal static extern int sub_set_gain(double valueDb);

            [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
            internal static extern int sub_get_frame_rate_enabled(out int enabled);

            [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
            internal static extern int sub_set_frame_rate_enabled(int enabled);

            [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
            internal static extern int sub_get_frame_rate(out double value, out double minimum, out double maximum);

            [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
            internal static extern int sub_set_frame_rate(double valueFps);

            [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
            internal static extern int sub_get_gamma_enabled(out int enabled);

            [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
            internal static extern int sub_set_gamma_enabled(int enabled);

            [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
            internal static extern int sub_get_gamma(out double value, out double minimum, out double maximum);

            [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
            internal static extern int sub_set_gamma(double value);

            [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
            internal static extern int sub_get_white_balance_auto(out int enabled);

            [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
            internal static extern int sub_set_white_balance_auto(int enabled);

            [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
            internal static extern int sub_get_balance_ratio(
                string selector,
                out double value,
                out double minimum,
                out double maximum);

            [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
            internal static extern int sub_set_balance_ratio(string selector, double value);
        }
    }
}
