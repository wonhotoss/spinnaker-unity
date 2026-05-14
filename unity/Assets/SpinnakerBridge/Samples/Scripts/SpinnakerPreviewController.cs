using System;
using System.Globalization;
using SpinnakerUnity;
using UnityEngine;

public sealed class SpinnakerPreviewController : MonoBehaviour {
    const float auto_controlled_parameter_refresh_interval_seconds = 0.5f;

    readonly SpinnakerBridge _bridge = new SpinnakerBridge();
    byte[] _frame_buffer;
    Texture2D _texture;
    CameraInfo _camera_info;
    string _status = "Idle";
    string _generic_node_name = "ExposureTime";
    string _generic_value = "";
    string _generic_entries = "";
    double _exposure;
    double _exposure_min;
    double _exposure_max;
    double _gain;
    double _gain_min;
    double _gain_max;
    double _frame_rate;
    double _frame_rate_min;
    double _frame_rate_max;
    double _gamma;
    double _gamma_min;
    double _gamma_max;
    double _red_balance;
    double _red_balance_min;
    double _red_balance_max;
    double _blue_balance;
    double _blue_balance_min;
    double _blue_balance_max;
    bool _exposure_auto;
    bool _gain_auto;
    bool _frame_rate_enabled;
    bool _gamma_enabled;
    bool _white_balance_auto;
    bool _parameters_loaded;
    bool _flip_vertical = true;
    ulong _last_frame_id;
    float _fps_timer;
    int _fps_frames;
    float _fps;
    float _auto_controlled_parameter_refresh_timer;

    void Awake() {
        Application.runInBackground = true;
    }

    void Update() {
        if (_bridge.IsStreaming) {
            try {
                if (_bridge.TryGetLatestFrame(ref _frame_buffer, out var frame)) {
                    update_texture(frame);
                    update_fps(frame.FrameId);
                }
            } catch (Exception exception) {
                _status = exception.Message;
            }
        }

        refresh_auto_controlled_parameters_if_needed();
    }

    void refresh_auto_controlled_parameters_if_needed() {
        if (!_bridge.IsCameraOpen || !_parameters_loaded) {
            _auto_controlled_parameter_refresh_timer = 0f;
            return;
        }

        if (!has_auto_controlled_parameters()) {
            _auto_controlled_parameter_refresh_timer = 0f;
            return;
        }

        _auto_controlled_parameter_refresh_timer += Time.unscaledDeltaTime;
        if (_auto_controlled_parameter_refresh_timer < auto_controlled_parameter_refresh_interval_seconds) {
            return;
        }

        _auto_controlled_parameter_refresh_timer = 0f;

        if (_exposure_auto) {
            apply_exposure(read_exposure());
        }

        if (_gain_auto) {
            apply_gain(read_gain());
        }

        if (_white_balance_auto) {
            apply_red_balance(read_red_balance());
            apply_blue_balance(read_blue_balance());
        }
    }

    bool has_auto_controlled_parameters() {
        return _exposure_auto || _gain_auto || _white_balance_auto;
    }

    void OnGUI() {
        const float panel_width = 390f;
        var preview_rect = new Rect(0f, 0f, Screen.width - panel_width, Screen.height);
        var panel_rect = new Rect(Screen.width - panel_width, 0f, panel_width, Screen.height);

        GUI.Box(panel_rect, GUIContent.none);
        draw_preview(preview_rect);
        draw_controls(panel_rect);
    }

    void OnDestroy() {
        try {
            _bridge.Dispose();
        } catch {
            // Unity may tear down native plugins while leaving managed objects alive.
        }
    }

    void draw_preview(Rect rect) {
        if (_texture == null) {
            GUI.Label(new Rect(rect.x + 24f, rect.y + 24f, rect.width - 48f, 32f), "No frame");
            return;
        }

        var tex_coords = _flip_vertical ? new Rect(0f, 1f, 1f, -1f) : new Rect(0f, 0f, 1f, 1f);
        GUI.DrawTextureWithTexCoords(rect, _texture, tex_coords, true);
    }

    void draw_controls(Rect panel_rect) {
        GUILayout.BeginArea(new Rect(panel_rect.x + 14f, panel_rect.y + 12f, panel_rect.width - 28f, panel_rect.height - 24f));
        GUILayout.Label("Spinnaker Unity Bridge");
        GUILayout.Space(8f);

        GUILayout.Label($"Status: {_status}");
        GUILayout.Label($"Camera: {(_bridge.IsCameraOpen ? _camera_info.ToString() : "-")}");
        GUILayout.Label($"Stream: {(_bridge.IsStreaming ? "Running" : "Stopped")} / FPS {_fps:0.0}");
        GUILayout.Space(8f);

        GUILayout.BeginHorizontal();
        if (GUILayout.Button("Initialize")) {
            run_action(initialize);
        }

        if (GUILayout.Button("Open First")) {
            run_action(open_first);
        }

        GUILayout.EndHorizontal();

        GUILayout.BeginHorizontal();
        if (GUILayout.Button(_bridge.IsStreaming ? "Stop" : "Start")) {
            run_action(toggle_stream);
        }

        if (GUILayout.Button("Refresh Params")) {
            run_action(refresh_parameters);
        }

        GUILayout.EndHorizontal();

        _flip_vertical = GUILayout.Toggle(_flip_vertical, "Flip preview vertically");

        GUILayout.Space(12f);
        draw_parameter_section();
        GUILayout.Space(12f);
        draw_generic_node_section();
        GUILayout.EndArea();
    }

    void draw_parameter_section() {
        GUILayout.Label("Camera Parameters");

        var next_exposure_auto = GUILayout.Toggle(_exposure_auto, "Exposure Auto");
        if (next_exposure_auto != _exposure_auto) {
            run_action(() => set_exposure_auto(next_exposure_auto));
        }

        _exposure = draw_slider("Exposure us", _exposure, _exposure_min, _exposure_max, !_exposure_auto, value => _bridge.SetExposureTime(value));

        var next_gain_auto = GUILayout.Toggle(_gain_auto, "Gain Auto");
        if (next_gain_auto != _gain_auto) {
            run_action(() => set_gain_auto(next_gain_auto));
        }

        _gain = draw_slider("Gain dB", _gain, _gain_min, _gain_max, !_gain_auto, value => _bridge.SetGain(value));

        var next_frame_rate_enabled = GUILayout.Toggle(_frame_rate_enabled, "Frame Rate Enable");
        if (next_frame_rate_enabled != _frame_rate_enabled) {
            run_action(() => set_frame_rate_enabled(next_frame_rate_enabled));
        }

        _frame_rate = draw_slider("Frame Rate", _frame_rate, _frame_rate_min, _frame_rate_max, _frame_rate_enabled, value => _bridge.SetFrameRate(value));

        var next_gamma_enabled = GUILayout.Toggle(_gamma_enabled, "Gamma Enable");
        if (next_gamma_enabled != _gamma_enabled) {
            run_action(() => set_gamma_enabled(next_gamma_enabled));
        }

        _gamma = draw_slider("Gamma", _gamma, _gamma_min, _gamma_max, _gamma_enabled, value => _bridge.SetGamma(value));

        var next_white_auto = GUILayout.Toggle(_white_balance_auto, "White Balance Auto");
        if (next_white_auto != _white_balance_auto) {
            run_action(() => set_white_balance_auto(next_white_auto));
        }

        _red_balance = draw_slider("WB Red", _red_balance, _red_balance_min, _red_balance_max, !_white_balance_auto, value => _bridge.SetBalanceRatio("Red", value));
        _blue_balance = draw_slider("WB Blue", _blue_balance, _blue_balance_min, _blue_balance_max, !_white_balance_auto, value => _bridge.SetBalanceRatio("Blue", value));

        if (!_parameters_loaded) {
            GUILayout.Label("Open a camera, then refresh parameters.");
        }
    }

    double draw_slider(string label, double value, double minimum, double maximum, bool enabled, Action<double> setter) {
        GUILayout.Label($"{label}: {value:0.###} [{minimum:0.###} - {maximum:0.###}]");
        using (new gui_enabled_scope(enabled && maximum > minimum)) {
            var next = GUILayout.HorizontalSlider((float)value, (float)minimum, (float)maximum);
            if (Math.Abs(next - value) > Math.Max(0.0001, (maximum - minimum) * 0.0005)) {
                var next_value = next;
                value = next_value;
                run_action_without_refresh(() => setter(next_value));
            }
        }

        return value;
    }

    void draw_generic_node_section() {
        GUILayout.Label("Generic GenICam Node");
        GUILayout.Label("Node");
        _generic_node_name = GUILayout.TextField(_generic_node_name);
        GUILayout.Label("Value / Entry");
        _generic_value = GUILayout.TextField(_generic_value);

        GUILayout.BeginHorizontal();
        if (GUILayout.Button("Get Float")) {
            run_action_without_refresh(() => {
                var node = _bridge.GetFloatNode(_generic_node_name);
                _generic_value = node.Value.ToString(CultureInfo.InvariantCulture);
                _status = $"{_generic_node_name}: {node.Value:0.###}";
            });
        }

        if (GUILayout.Button("Set Float")) {
            run_action(() => {
                if (double.TryParse(_generic_value, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)) {
                    _bridge.SetFloatNode(_generic_node_name, value);
                }
            });
        }

        GUILayout.EndHorizontal();

        GUILayout.BeginHorizontal();
        if (GUILayout.Button("Get Enum")) {
            run_action_without_refresh(() => {
                var node = _bridge.GetEnumNode(_generic_node_name);
                _generic_value = node.Value;
                _generic_entries = string.Join(", ", _bridge.GetEnumEntries(_generic_node_name));
                _status = $"{_generic_node_name}: {node.Value}";
            });
        }

        if (GUILayout.Button("Set Enum")) {
            run_action(() => _bridge.SetEnumNode(_generic_node_name, _generic_value));
        }

        GUILayout.EndHorizontal();

        if (!string.IsNullOrEmpty(_generic_entries)) {
            GUILayout.Label(_generic_entries);
        }
    }

    void initialize() {
        _bridge.Initialize();
        _bridge.RefreshCameras();
        var count = _bridge.CameraCount;
        _status = $"Initialized. Cameras: {count}";
    }

    void open_first() {
        if (!_bridge.IsInitialized) {
            initialize();
        }

        if (!_bridge.IsCameraOpen) {
            _bridge.OpenFirstCamera();
        }

        _camera_info = _bridge.CameraCount > 0 ? _bridge.GetCameraInfo(0) : default;
        refresh_parameters();
        _status = $"Opened {_camera_info}";
    }

    void toggle_stream() {
        if (_bridge.IsStreaming) {
            _bridge.StopStream();
            _status = "Stream stopped";
        } else {
            _bridge.StartStream();
            _status = "Stream started";
        }
    }

    void refresh_parameters() {
        _exposure_auto = try_get(() => _bridge.ExposureAuto, _exposure_auto);
        apply_exposure(read_exposure());

        _gain_auto = try_get(() => _bridge.GainAuto, _gain_auto);
        apply_gain(read_gain());

        _frame_rate_enabled = try_get(() => _bridge.FrameRateEnabled, _frame_rate_enabled);
        apply_frame_rate(read_frame_rate());

        _gamma_enabled = try_get(() => _bridge.GammaEnabled, _gamma_enabled);
        apply_gamma(read_gamma());

        _white_balance_auto = try_get(() => _bridge.WhiteBalanceAuto, _white_balance_auto);
        apply_red_balance(read_red_balance());
        apply_blue_balance(read_blue_balance());

        _parameters_loaded = true;
    }

    NumericNode read_exposure() {
        return try_get(() => _bridge.ExposureTime, new NumericNode(_exposure, _exposure_min, _exposure_max, true, true));
    }

    NumericNode read_gain() {
        return try_get(() => _bridge.Gain, new NumericNode(_gain, _gain_min, _gain_max, true, true));
    }

    NumericNode read_frame_rate() {
        return try_get(() => _bridge.FrameRate, new NumericNode(_frame_rate, _frame_rate_min, _frame_rate_max, true, true));
    }

    NumericNode read_gamma() {
        return try_get(() => _bridge.Gamma, new NumericNode(_gamma, _gamma_min, _gamma_max, true, true));
    }

    NumericNode read_red_balance() {
        return try_get(() => _bridge.GetBalanceRatio("Red"), new NumericNode(_red_balance, _red_balance_min, _red_balance_max, true, true));
    }

    NumericNode read_blue_balance() {
        return try_get(() => _bridge.GetBalanceRatio("Blue"), new NumericNode(_blue_balance, _blue_balance_min, _blue_balance_max, true, true));
    }

    void apply_exposure(NumericNode exposure) {
        _exposure = exposure.Value;
        _exposure_min = exposure.Minimum;
        _exposure_max = exposure.Maximum;
    }

    void apply_gain(NumericNode gain) {
        _gain = gain.Value;
        _gain_min = gain.Minimum;
        _gain_max = gain.Maximum;
    }

    void apply_frame_rate(NumericNode frame_rate) {
        _frame_rate = frame_rate.Value;
        _frame_rate_min = frame_rate.Minimum;
        _frame_rate_max = frame_rate.Maximum;
    }

    void apply_gamma(NumericNode gamma) {
        _gamma = gamma.Value;
        _gamma_min = gamma.Minimum;
        _gamma_max = gamma.Maximum;
    }

    void apply_red_balance(NumericNode red) {
        _red_balance = red.Value;
        _red_balance_min = red.Minimum;
        _red_balance_max = red.Maximum;
    }

    void apply_blue_balance(NumericNode blue) {
        _blue_balance = blue.Value;
        _blue_balance_min = blue.Minimum;
        _blue_balance_max = blue.Maximum;
    }

    void set_exposure_auto(bool enabled) {
        _bridge.ExposureAuto = enabled;
        _exposure_auto = enabled;
    }

    void set_gain_auto(bool enabled) {
        _bridge.GainAuto = enabled;
        _gain_auto = enabled;
    }

    void set_frame_rate_enabled(bool enabled) {
        _bridge.FrameRateEnabled = enabled;
        _frame_rate_enabled = enabled;
    }

    void set_gamma_enabled(bool enabled) {
        _bridge.GammaEnabled = enabled;
        _gamma_enabled = enabled;
    }

    void set_white_balance_auto(bool enabled) {
        _bridge.WhiteBalanceAuto = enabled;
        _white_balance_auto = enabled;
    }

    void update_texture(SpinnakerFrame frame) {
        if (_texture == null || _texture.width != frame.Width || _texture.height != frame.Height) {
            if (_texture != null) {
                Destroy(_texture);
            }

            _texture = new Texture2D(frame.Width, frame.Height, TextureFormat.RGB24, false) {
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear
            };
        }

        _texture.LoadRawTextureData(frame.Data);
        _texture.Apply(false);
    }

    void update_fps(ulong frame_id) {
        if (frame_id == _last_frame_id) {
            return;
        }

        _last_frame_id = frame_id;
        _fps_frames++;
        _fps_timer += Time.unscaledDeltaTime;
        if (_fps_timer >= 0.5f) {
            _fps = _fps_frames / _fps_timer;
            _fps_frames = 0;
            _fps_timer = 0f;
        }
    }

    void run_action(Action action) {
        try {
            action();
            if (_bridge.IsCameraOpen) {
                refresh_parameters();
            }
        } catch (Exception exception) {
            _status = exception.Message;
        }
    }

    void run_action_without_refresh(Action action) {
        try {
            action();
        } catch (Exception exception) {
            _status = exception.Message;
        }
    }

    static T try_get<T>(Func<T> getter, T fallback) {
        try {
            return getter();
        } catch {
            return fallback;
        }
    }

    readonly struct gui_enabled_scope : IDisposable {
        readonly bool _previous;

        public gui_enabled_scope(bool enabled) {
            _previous = GUI.enabled;
            GUI.enabled = enabled;
        }

        public void Dispose() {
            GUI.enabled = _previous;
        }
    }
}
