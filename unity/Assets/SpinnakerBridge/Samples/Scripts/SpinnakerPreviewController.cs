using System;
using System.Globalization;
using SpinnakerUnity;
using UnityEngine;

public sealed class SpinnakerPreviewController : MonoBehaviour
{
    private const float AutoControlledParameterRefreshIntervalSeconds = 0.5f;

    private readonly SpinnakerBridge _bridge = new SpinnakerBridge();
    private byte[] _frameBuffer;
    private Texture2D _texture;
    private CameraInfo _cameraInfo;
    private string _status = "Idle";
    private string _genericNodeName = "ExposureTime";
    private string _genericValue = "";
    private string _genericEntries = "";
    private double _exposure;
    private double _exposureMin;
    private double _exposureMax;
    private double _gain;
    private double _gainMin;
    private double _gainMax;
    private double _frameRate;
    private double _frameRateMin;
    private double _frameRateMax;
    private double _gamma;
    private double _gammaMin;
    private double _gammaMax;
    private double _redBalance;
    private double _redBalanceMin;
    private double _redBalanceMax;
    private double _blueBalance;
    private double _blueBalanceMin;
    private double _blueBalanceMax;
    private bool _exposureAuto;
    private bool _gainAuto;
    private bool _frameRateEnabled;
    private bool _gammaEnabled;
    private bool _whiteBalanceAuto;
    private bool _parametersLoaded;
    private bool _flipVertical = true;
    private ulong _lastFrameId;
    private float _fpsTimer;
    private int _fpsFrames;
    private float _fps;
    private float _autoControlledParameterRefreshTimer;

    private void Awake()
    {
        Application.runInBackground = true;
    }

    private void Update()
    {
        if (_bridge.IsStreaming)
        {
            try
            {
                if (_bridge.TryGetLatestFrame(ref _frameBuffer, out SpinnakerFrame frame))
                {
                    UpdateTexture(frame);
                    UpdateFps(frame.FrameId);
                }
            }
            catch (Exception exception)
            {
                _status = exception.Message;
            }
        }

        RefreshAutoControlledParametersIfNeeded();
    }

    private void RefreshAutoControlledParametersIfNeeded()
    {
        if (!_bridge.IsCameraOpen || !_parametersLoaded)
        {
            _autoControlledParameterRefreshTimer = 0f;
            return;
        }

        if (!HasAutoControlledParameters())
        {
            _autoControlledParameterRefreshTimer = 0f;
            return;
        }

        _autoControlledParameterRefreshTimer += Time.unscaledDeltaTime;
        if (_autoControlledParameterRefreshTimer < AutoControlledParameterRefreshIntervalSeconds)
        {
            return;
        }

        _autoControlledParameterRefreshTimer = 0f;

        if (_exposureAuto)
        {
            ApplyExposure(TryGet(() => _bridge.ExposureTime, new NumericNode(_exposure, _exposureMin, _exposureMax, true, true)));
        }

        if (_gainAuto)
        {
            ApplyGain(TryGet(() => _bridge.Gain, new NumericNode(_gain, _gainMin, _gainMax, true, true)));
        }

        if (_whiteBalanceAuto)
        {
            ApplyRedBalance(TryGet(() => _bridge.GetBalanceRatio("Red"), new NumericNode(_redBalance, _redBalanceMin, _redBalanceMax, true, true)));
            ApplyBlueBalance(TryGet(() => _bridge.GetBalanceRatio("Blue"), new NumericNode(_blueBalance, _blueBalanceMin, _blueBalanceMax, true, true)));
        }
    }

    private bool HasAutoControlledParameters()
    {
        return _exposureAuto || _gainAuto || _whiteBalanceAuto;
    }

    private void OnGUI()
    {
        const float panelWidth = 390f;
        Rect previewRect = new Rect(0f, 0f, Screen.width - panelWidth, Screen.height);
        Rect panelRect = new Rect(Screen.width - panelWidth, 0f, panelWidth, Screen.height);

        GUI.Box(panelRect, GUIContent.none);
        DrawPreview(previewRect);
        DrawControls(panelRect);
    }

    private void OnDestroy()
    {
        try
        {
            _bridge.Dispose();
        }
        catch
        {
            // Unity may tear down native plugins while leaving managed objects alive.
        }
    }

    private void DrawPreview(Rect rect)
    {
        if (_texture == null)
        {
            GUI.Label(new Rect(rect.x + 24f, rect.y + 24f, rect.width - 48f, 32f), "No frame");
            return;
        }

        Rect texCoords = _flipVertical ? new Rect(0f, 1f, 1f, -1f) : new Rect(0f, 0f, 1f, 1f);
        GUI.DrawTextureWithTexCoords(rect, _texture, texCoords, true);
    }

    private void DrawControls(Rect panelRect)
    {
        GUILayout.BeginArea(new Rect(panelRect.x + 14f, panelRect.y + 12f, panelRect.width - 28f, panelRect.height - 24f));
        GUILayout.Label("Spinnaker Unity Bridge");
        GUILayout.Space(8f);

        GUILayout.Label($"Status: {_status}");
        GUILayout.Label($"Camera: {(_bridge.IsCameraOpen ? _cameraInfo.ToString() : "-")}");
        GUILayout.Label($"Stream: {(_bridge.IsStreaming ? "Running" : "Stopped")} / FPS {_fps:0.0}");
        GUILayout.Space(8f);

        GUILayout.BeginHorizontal();
        if (GUILayout.Button("Initialize"))
        {
            RunAction(Initialize);
        }
        if (GUILayout.Button("Open First"))
        {
            RunAction(OpenFirst);
        }
        GUILayout.EndHorizontal();

        GUILayout.BeginHorizontal();
        if (GUILayout.Button(_bridge.IsStreaming ? "Stop" : "Start"))
        {
            RunAction(ToggleStream);
        }
        if (GUILayout.Button("Refresh Params"))
        {
            RunAction(RefreshParameters);
        }
        GUILayout.EndHorizontal();

        _flipVertical = GUILayout.Toggle(_flipVertical, "Flip preview vertically");

        GUILayout.Space(12f);
        DrawParameterSection();
        GUILayout.Space(12f);
        DrawGenericNodeSection();
        GUILayout.EndArea();
    }

    private void DrawParameterSection()
    {
        GUILayout.Label("Camera Parameters");

        bool nextExposureAuto = GUILayout.Toggle(_exposureAuto, "Exposure Auto");
        if (nextExposureAuto != _exposureAuto)
        {
            RunAction(() => SetExposureAuto(nextExposureAuto));
        }
        _exposure = DrawSlider("Exposure us", _exposure, _exposureMin, _exposureMax, !_exposureAuto, value => _bridge.SetExposureTime(value));

        bool nextGainAuto = GUILayout.Toggle(_gainAuto, "Gain Auto");
        if (nextGainAuto != _gainAuto)
        {
            RunAction(() => SetGainAuto(nextGainAuto));
        }
        _gain = DrawSlider("Gain dB", _gain, _gainMin, _gainMax, !_gainAuto, value => _bridge.SetGain(value));

        bool nextFrameRateEnabled = GUILayout.Toggle(_frameRateEnabled, "Frame Rate Enable");
        if (nextFrameRateEnabled != _frameRateEnabled)
        {
            RunAction(() => SetFrameRateEnabled(nextFrameRateEnabled));
        }
        _frameRate = DrawSlider("Frame Rate", _frameRate, _frameRateMin, _frameRateMax, _frameRateEnabled, value => _bridge.SetFrameRate(value));

        bool nextGammaEnabled = GUILayout.Toggle(_gammaEnabled, "Gamma Enable");
        if (nextGammaEnabled != _gammaEnabled)
        {
            RunAction(() => SetGammaEnabled(nextGammaEnabled));
        }
        _gamma = DrawSlider("Gamma", _gamma, _gammaMin, _gammaMax, _gammaEnabled, value => _bridge.SetGamma(value));

        bool nextWhiteAuto = GUILayout.Toggle(_whiteBalanceAuto, "White Balance Auto");
        if (nextWhiteAuto != _whiteBalanceAuto)
        {
            RunAction(() => SetWhiteBalanceAuto(nextWhiteAuto));
        }
        _redBalance = DrawSlider("WB Red", _redBalance, _redBalanceMin, _redBalanceMax, !_whiteBalanceAuto, value => _bridge.SetBalanceRatio("Red", value));
        _blueBalance = DrawSlider("WB Blue", _blueBalance, _blueBalanceMin, _blueBalanceMax, !_whiteBalanceAuto, value => _bridge.SetBalanceRatio("Blue", value));

        if (!_parametersLoaded)
        {
            GUILayout.Label("Open a camera, then refresh parameters.");
        }
    }

    private double DrawSlider(string label, double value, double minimum, double maximum, bool enabled, Action<double> setter)
    {
        GUILayout.Label($"{label}: {value:0.###} [{minimum:0.###} - {maximum:0.###}]");
        using (new GuiEnabledScope(enabled && maximum > minimum))
        {
            float next = GUILayout.HorizontalSlider((float)value, (float)minimum, (float)maximum);
            if (Math.Abs(next - value) > Math.Max(0.0001, (maximum - minimum) * 0.0005))
            {
                double nextValue = next;
                value = nextValue;
                RunAction(() => setter(nextValue), refreshAfter: false);
            }
        }

        return value;
    }

    private void DrawGenericNodeSection()
    {
        GUILayout.Label("Generic GenICam Node");
        GUILayout.Label("Node");
        _genericNodeName = GUILayout.TextField(_genericNodeName);
        GUILayout.Label("Value / Entry");
        _genericValue = GUILayout.TextField(_genericValue);

        GUILayout.BeginHorizontal();
        if (GUILayout.Button("Get Float"))
        {
            RunAction(() =>
            {
                NumericNode node = _bridge.GetFloatNode(_genericNodeName);
                _genericValue = node.Value.ToString(CultureInfo.InvariantCulture);
                _status = $"{_genericNodeName}: {node.Value:0.###}";
            }, refreshAfter: false);
        }
        if (GUILayout.Button("Set Float"))
        {
            RunAction(() =>
            {
                if (double.TryParse(_genericValue, NumberStyles.Float, CultureInfo.InvariantCulture, out double value))
                {
                    _bridge.SetFloatNode(_genericNodeName, value);
                }
            });
        }
        GUILayout.EndHorizontal();

        GUILayout.BeginHorizontal();
        if (GUILayout.Button("Get Enum"))
        {
            RunAction(() =>
            {
                EnumNode node = _bridge.GetEnumNode(_genericNodeName);
                _genericValue = node.Value;
                _genericEntries = string.Join(", ", _bridge.GetEnumEntries(_genericNodeName));
                _status = $"{_genericNodeName}: {node.Value}";
            }, refreshAfter: false);
        }
        if (GUILayout.Button("Set Enum"))
        {
            RunAction(() => _bridge.SetEnumNode(_genericNodeName, _genericValue));
        }
        GUILayout.EndHorizontal();

        if (!string.IsNullOrEmpty(_genericEntries))
        {
            GUILayout.Label(_genericEntries);
        }
    }

    private void Initialize()
    {
        _bridge.Initialize();
        _bridge.RefreshCameras();
        int count = _bridge.CameraCount;
        _status = $"Initialized. Cameras: {count}";
    }

    private void OpenFirst()
    {
        if (!_bridge.IsInitialized)
        {
            Initialize();
        }

        if (!_bridge.IsCameraOpen)
        {
            _bridge.OpenFirstCamera();
        }

        _cameraInfo = _bridge.CameraCount > 0 ? _bridge.GetCameraInfo(0) : default;
        RefreshParameters();
        _status = $"Opened {_cameraInfo}";
    }

    private void ToggleStream()
    {
        if (_bridge.IsStreaming)
        {
            _bridge.StopStream();
            _status = "Stream stopped";
        }
        else
        {
            _bridge.StartStream();
            _status = "Stream started";
        }
    }

    private void RefreshParameters()
    {
        _exposureAuto = TryGet(() => _bridge.ExposureAuto, _exposureAuto);
        ApplyExposure(TryGet(() => _bridge.ExposureTime, new NumericNode(_exposure, _exposureMin, _exposureMax, true, true)));

        _gainAuto = TryGet(() => _bridge.GainAuto, _gainAuto);
        ApplyGain(TryGet(() => _bridge.Gain, new NumericNode(_gain, _gainMin, _gainMax, true, true)));

        _frameRateEnabled = TryGet(() => _bridge.FrameRateEnabled, _frameRateEnabled);
        NumericNode frameRate = TryGet(() => _bridge.FrameRate, new NumericNode(_frameRate, _frameRateMin, _frameRateMax, true, true));
        _frameRate = frameRate.Value;
        _frameRateMin = frameRate.Minimum;
        _frameRateMax = frameRate.Maximum;

        _gammaEnabled = TryGet(() => _bridge.GammaEnabled, _gammaEnabled);
        NumericNode gamma = TryGet(() => _bridge.Gamma, new NumericNode(_gamma, _gammaMin, _gammaMax, true, true));
        _gamma = gamma.Value;
        _gammaMin = gamma.Minimum;
        _gammaMax = gamma.Maximum;

        _whiteBalanceAuto = TryGet(() => _bridge.WhiteBalanceAuto, _whiteBalanceAuto);
        ApplyRedBalance(TryGet(() => _bridge.GetBalanceRatio("Red"), new NumericNode(_redBalance, _redBalanceMin, _redBalanceMax, true, true)));
        ApplyBlueBalance(TryGet(() => _bridge.GetBalanceRatio("Blue"), new NumericNode(_blueBalance, _blueBalanceMin, _blueBalanceMax, true, true)));

        _parametersLoaded = true;
    }

    private void ApplyExposure(NumericNode exposure)
    {
        _exposure = exposure.Value;
        _exposureMin = exposure.Minimum;
        _exposureMax = exposure.Maximum;
    }

    private void ApplyGain(NumericNode gain)
    {
        _gain = gain.Value;
        _gainMin = gain.Minimum;
        _gainMax = gain.Maximum;
    }

    private void ApplyRedBalance(NumericNode red)
    {
        _redBalance = red.Value;
        _redBalanceMin = red.Minimum;
        _redBalanceMax = red.Maximum;
    }

    private void ApplyBlueBalance(NumericNode blue)
    {
        _blueBalance = blue.Value;
        _blueBalanceMin = blue.Minimum;
        _blueBalanceMax = blue.Maximum;
    }

    private void SetExposureAuto(bool enabled)
    {
        _bridge.ExposureAuto = enabled;
        _exposureAuto = enabled;
    }

    private void SetGainAuto(bool enabled)
    {
        _bridge.GainAuto = enabled;
        _gainAuto = enabled;
    }

    private void SetFrameRateEnabled(bool enabled)
    {
        _bridge.FrameRateEnabled = enabled;
        _frameRateEnabled = enabled;
    }

    private void SetGammaEnabled(bool enabled)
    {
        _bridge.GammaEnabled = enabled;
        _gammaEnabled = enabled;
    }

    private void SetWhiteBalanceAuto(bool enabled)
    {
        _bridge.WhiteBalanceAuto = enabled;
        _whiteBalanceAuto = enabled;
    }

    private void UpdateTexture(SpinnakerFrame frame)
    {
        if (_texture == null || _texture.width != frame.Width || _texture.height != frame.Height)
        {
            if (_texture != null)
            {
                Destroy(_texture);
            }

            _texture = new Texture2D(frame.Width, frame.Height, TextureFormat.RGB24, false);
            _texture.wrapMode = TextureWrapMode.Clamp;
            _texture.filterMode = FilterMode.Bilinear;
        }

        _texture.LoadRawTextureData(frame.Data);
        _texture.Apply(false);
    }

    private void UpdateFps(ulong frameId)
    {
        if (frameId == _lastFrameId)
        {
            return;
        }

        _lastFrameId = frameId;
        _fpsFrames++;
        _fpsTimer += Time.unscaledDeltaTime;
        if (_fpsTimer >= 0.5f)
        {
            _fps = _fpsFrames / _fpsTimer;
            _fpsFrames = 0;
            _fpsTimer = 0f;
        }
    }

    private void RunAction(Action action, bool refreshAfter = true)
    {
        try
        {
            action();
            if (refreshAfter && _bridge.IsCameraOpen)
            {
                RefreshParameters();
            }
        }
        catch (Exception exception)
        {
            _status = exception.Message;
        }
    }

    private static T TryGet<T>(Func<T> getter, T fallback)
    {
        try
        {
            return getter();
        }
        catch
        {
            return fallback;
        }
    }

    private readonly struct GuiEnabledScope : IDisposable
    {
        private readonly bool _previous;

        public GuiEnabledScope(bool enabled)
        {
            _previous = GUI.enabled;
            GUI.enabled = enabled;
        }

        public void Dispose()
        {
            GUI.enabled = _previous;
        }
    }
}
