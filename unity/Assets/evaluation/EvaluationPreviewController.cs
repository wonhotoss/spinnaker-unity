using System;
using SpinnakerUnity;
using UnityEngine;
using UnityEngine.UIElements;

[RequireComponent(typeof(UIDocument))]
public sealed class EvaluationPreviewController : MonoBehaviour {
    const string overlay_shader_name = "Hidden/SpinnakerUnity/EvaluationOverlay";
    const float minimum_limit_gap = 0.001f;

    [SerializeField, Range(0f, 1f)] float _upper_limit = 0.95f;
    [SerializeField, Range(0f, 1f)] float _lower_limit = 0.05f;
    [SerializeField] bool _auto_open_first_camera = true;
    [SerializeField] bool _auto_start_stream = true;
    [SerializeField] Shader _overlay_shader;
    [SerializeField] Texture2D _zebra_texture;
    [SerializeField] Color _underexposed_tint = new Color(1f, 0f, 1f, 0.38f);
    [SerializeField] Color _zebra_tint = new Color(0f, 0f, 0f, 0.9f);
    [SerializeField, Min(1f)] float _zebra_period_pixels = 24f;

    readonly SpinnakerBridge _bridge = new SpinnakerBridge();
    byte[] _frame_buffer;
    byte[] _packed_frame_buffer;
    Texture2D _camera_texture;
    RenderTexture _evaluation_texture;
    Material _overlay_material;
    bool _owns_zebra_texture;
    bool _ui_bound;
    UIDocument _document;
    CameraInfo _camera_info;
    string _status = "Idle";
    ulong _last_frame_id;
    float _fps_timer;
    int _fps_frames;
    float _fps;
    bool _limit_ui_syncing;

    Image _camera_image;
    Image _evaluation_image;
    Label _empty_label;
    Label _status_label;
    Label _camera_label;
    Label _stream_label;
    Label _fps_label;
    Button _initialize_button;
    Button _open_first_button;
    Button _stream_button;
    Button _refresh_cameras_button;
    Slider _upper_slider;
    Slider _lower_slider;
    FloatField _upper_field;
    FloatField _lower_field;

    void Awake() {
        Application.runInBackground = true;
    }

    void OnEnable() {
        bind_ui_document();
    }

    void Start() {
        if (!_ui_bound) {
            bind_ui_document();
        }

        ensure_overlay_material();
        set_upper_limit(_upper_limit);
        set_lower_limit(_lower_limit);
        update_status_ui();

        if (_auto_open_first_camera) {
            run_action(() => {
                open_first();
                if (_auto_start_stream) {
                    start_stream();
                }
            });
        }
    }

    void Update() {
        if (_bridge.IsStreaming) {
            try {
                if (_bridge.TryGetLatestFrame(ref _frame_buffer, out var frame)) {
                    update_camera_texture(frame);
                    update_evaluation_texture();
                    update_fps(frame.FrameId);
                }
            } catch (Exception exception) {
                _status = exception.Message;
            }
        }

        update_status_ui();
    }

    void OnDisable() {
        if (!_ui_bound) {
            return;
        }

        _initialize_button.clicked -= on_initialize_clicked;
        _open_first_button.clicked -= on_open_first_clicked;
        _stream_button.clicked -= on_stream_clicked;
        _refresh_cameras_button.clicked -= on_refresh_cameras_clicked;
        _upper_slider.UnregisterValueChangedCallback(on_upper_slider_changed);
        _upper_field.UnregisterValueChangedCallback(on_upper_field_changed);
        _lower_slider.UnregisterValueChangedCallback(on_lower_slider_changed);
        _lower_field.UnregisterValueChangedCallback(on_lower_field_changed);
        _ui_bound = false;
    }

    void OnDestroy() {
        try {
            _bridge.Dispose();
        } catch {
            // Unity can unload native plugins before managed object teardown is finished.
        }

        if (_evaluation_texture != null) {
            _evaluation_texture.Release();
            Destroy(_evaluation_texture);
        }

        if (_camera_texture != null) {
            Destroy(_camera_texture);
        }

        if (_overlay_material != null) {
            Destroy(_overlay_material);
        }

        if (_owns_zebra_texture && _zebra_texture != null) {
            Destroy(_zebra_texture);
        }
    }

    void bind_ui_document() {
        if (_ui_bound) {
            return;
        }

        _document = GetComponent<UIDocument>();
        if (_document == null || _document.rootVisualElement == null) {
            Debug.LogError("EvaluationPreviewController requires a UIDocument with a UXML source asset.", this);
            enabled = false;
            return;
        }

        var root = _document.rootVisualElement;
        try {
            _camera_image = require_element<Image>(root, "camera-view");
            _evaluation_image = require_element<Image>(root, "evaluation-view");
            _empty_label = require_element<Label>(root, "empty-label");
            _status_label = require_element<Label>(root, "status-label");
            _camera_label = require_element<Label>(root, "camera-label");
            _stream_label = require_element<Label>(root, "stream-label");
            _fps_label = require_element<Label>(root, "fps-label");
            _initialize_button = require_element<Button>(root, "initialize-button");
            _open_first_button = require_element<Button>(root, "open-first-button");
            _stream_button = require_element<Button>(root, "stream-button");
            _refresh_cameras_button = require_element<Button>(root, "refresh-cameras-button");
            _upper_slider = require_element<Slider>(root, "upper-slider");
            _lower_slider = require_element<Slider>(root, "lower-slider");
            _upper_field = require_element<FloatField>(root, "upper-field");
            _lower_field = require_element<FloatField>(root, "lower-field");
        } catch (Exception exception) {
            Debug.LogError(exception.Message, this);
            enabled = false;
            return;
        }

        _camera_image.scaleMode = ScaleMode.ScaleToFit;
        _evaluation_image.scaleMode = ScaleMode.ScaleToFit;
        _camera_image.pickingMode = PickingMode.Ignore;
        _evaluation_image.pickingMode = PickingMode.Ignore;
        _upper_slider.lowValue = 0f;
        _upper_slider.highValue = 1f;
        _lower_slider.lowValue = 0f;
        _lower_slider.highValue = 1f;

        _initialize_button.clicked += on_initialize_clicked;
        _open_first_button.clicked += on_open_first_clicked;
        _stream_button.clicked += on_stream_clicked;
        _refresh_cameras_button.clicked += on_refresh_cameras_clicked;
        _upper_slider.RegisterValueChangedCallback(on_upper_slider_changed);
        _upper_field.RegisterValueChangedCallback(on_upper_field_changed);
        _lower_slider.RegisterValueChangedCallback(on_lower_slider_changed);
        _lower_field.RegisterValueChangedCallback(on_lower_field_changed);

        _ui_bound = true;
        sync_limit_controls();
        update_status_ui();
    }

    static T require_element<T>(VisualElement root, string name) where T : VisualElement {
        var element = root.Q<T>(name);
        if (element == null) {
            throw new InvalidOperationException($"Missing UXML element '{name}' ({typeof(T).Name}).");
        }

        return element;
    }

    void on_initialize_clicked() {
        run_action(initialize);
    }

    void on_open_first_clicked() {
        run_action(open_first);
    }

    void on_stream_clicked() {
        run_action(toggle_stream);
    }

    void on_refresh_cameras_clicked() {
        run_action(refresh_cameras);
    }

    void on_upper_slider_changed(ChangeEvent<float> evt) {
        if (!_limit_ui_syncing) {
            set_upper_limit(evt.newValue);
        }
    }

    void on_upper_field_changed(ChangeEvent<float> evt) {
        if (!_limit_ui_syncing) {
            set_upper_limit(evt.newValue);
        }
    }

    void on_lower_slider_changed(ChangeEvent<float> evt) {
        if (!_limit_ui_syncing) {
            set_lower_limit(evt.newValue);
        }
    }

    void on_lower_field_changed(ChangeEvent<float> evt) {
        if (!_limit_ui_syncing) {
            set_lower_limit(evt.newValue);
        }
    }

    void ensure_overlay_material() {
        if (_overlay_material != null) {
            return;
        }

        if (_overlay_shader == null) {
            _overlay_shader = Shader.Find(overlay_shader_name);
        }

        if (_overlay_shader == null) {
            _status = $"Missing shader: {overlay_shader_name}";
            return;
        }

        if (_zebra_texture == null) {
            _zebra_texture = create_runtime_zebra_texture();
            _owns_zebra_texture = true;
        }

        _zebra_texture.wrapMode = TextureWrapMode.Repeat;
        _zebra_texture.filterMode = FilterMode.Point;

        _overlay_material = new Material(_overlay_shader) {
            hideFlags = HideFlags.HideAndDontSave
        };
        update_overlay_material_properties();
    }

    Texture2D create_runtime_zebra_texture() {
        const int size = 32;
        const int period = 16;
        const int stripe_width = 4;

        var texture = new Texture2D(size, size, TextureFormat.RGBA32, false, true) {
            name = "Runtime Zebra Pattern",
            wrapMode = TextureWrapMode.Repeat,
            filterMode = FilterMode.Point
        };

        var pixels = new Color32[size * size];
        for (int y = 0; y < size; y++) {
            for (int x = 0; x < size; x++) {
                int offset = (x + y) % period;
                pixels[y * size + x] = offset < stripe_width
                    ? new Color32(255, 255, 255, 255)
                    : new Color32(255, 255, 255, 0);
            }
        }

        texture.SetPixels32(pixels);
        texture.Apply(false, true);
        return texture;
    }

    void update_overlay_material_properties() {
        if (_overlay_material == null) {
            return;
        }

        _overlay_material.SetFloat("_UpperLimit", _upper_limit);
        _overlay_material.SetFloat("_LowerLimit", _lower_limit);
        _overlay_material.SetFloat("_ZebraPeriodPixels", _zebra_period_pixels);
        _overlay_material.SetColor("_ZebraColor", _zebra_tint);
        _overlay_material.SetColor("_UnderColor", _underexposed_tint);
        _overlay_material.SetTexture("_ZebraTex", _zebra_texture);
    }

    void update_camera_texture(SpinnakerFrame frame) {
        if (frame.Width <= 0 || frame.Height <= 0 || _camera_image == null) {
            return;
        }

        if (_camera_texture == null || _camera_texture.width != frame.Width || _camera_texture.height != frame.Height) {
            if (_camera_texture != null) {
                Destroy(_camera_texture);
            }

            _camera_texture = new Texture2D(frame.Width, frame.Height, TextureFormat.RGB24, false, true) {
                name = "Spinnaker Camera Frame",
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear
            };

            _camera_image.image = _camera_texture;
        }

        int packed_stride = frame.Width * 3;
        byte[] upload_data = frame.Data;
        if (frame.Stride != packed_stride) {
            int required_length = packed_stride * frame.Height;
            if (_packed_frame_buffer == null || _packed_frame_buffer.Length != required_length) {
                _packed_frame_buffer = new byte[required_length];
            }

            for (int y = 0; y < frame.Height; y++) {
                Buffer.BlockCopy(frame.Data, y * frame.Stride, _packed_frame_buffer, y * packed_stride, packed_stride);
            }

            upload_data = _packed_frame_buffer;
        }

        _camera_texture.LoadRawTextureData(upload_data);
        _camera_texture.Apply(false);

        if (_empty_label != null) {
            _empty_label.style.display = DisplayStyle.None;
        }
    }

    void update_evaluation_texture() {
        if (_camera_texture == null || _overlay_material == null || _evaluation_image == null) {
            return;
        }

        if (_evaluation_texture == null || _evaluation_texture.width != _camera_texture.width || _evaluation_texture.height != _camera_texture.height) {
            if (_evaluation_texture != null) {
                _evaluation_texture.Release();
                Destroy(_evaluation_texture);
            }

            _evaluation_texture = new RenderTexture(_camera_texture.width, _camera_texture.height, 0, RenderTextureFormat.ARGB32) {
                name = "Evaluation Overlay",
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear
            };
            _evaluation_texture.Create();
            _evaluation_image.image = _evaluation_texture;
        }

        update_overlay_material_properties();
        Graphics.Blit(_camera_texture, _evaluation_texture, _overlay_material);
    }

    void set_upper_limit(float value) {
        _upper_limit = Mathf.Clamp01(value);
        if (_upper_limit < _lower_limit + minimum_limit_gap) {
            _lower_limit = Mathf.Max(0f, _upper_limit - minimum_limit_gap);
        }

        sync_limit_controls();
        update_overlay_material_properties();
    }

    void set_lower_limit(float value) {
        _lower_limit = Mathf.Clamp01(value);
        if (_lower_limit > _upper_limit - minimum_limit_gap) {
            _upper_limit = Mathf.Min(1f, _lower_limit + minimum_limit_gap);
        }

        sync_limit_controls();
        update_overlay_material_properties();
    }

    void sync_limit_controls() {
        if (_upper_slider == null || _upper_field == null || _lower_slider == null || _lower_field == null) {
            return;
        }

        _limit_ui_syncing = true;
        _upper_slider.SetValueWithoutNotify(_upper_limit);
        _lower_slider.SetValueWithoutNotify(_lower_limit);
        _upper_field.SetValueWithoutNotify(_upper_limit);
        _lower_field.SetValueWithoutNotify(_lower_limit);
        _limit_ui_syncing = false;
    }

    void initialize() {
        if (!_bridge.IsInitialized) {
            _bridge.Initialize();
        }

        _bridge.RefreshCameras();
        _status = $"Initialized. Cameras: {_bridge.CameraCount}";
    }

    void refresh_cameras() {
        if (!_bridge.IsInitialized) {
            initialize();
            return;
        }

        _bridge.RefreshCameras();
        _status = $"Cameras: {_bridge.CameraCount}";
    }

    void open_first() {
        if (!_bridge.IsInitialized) {
            initialize();
        }

        if (!_bridge.IsCameraOpen) {
            _bridge.OpenFirstCamera();
        }

        _camera_info = _bridge.CameraCount > 0 ? _bridge.GetCameraInfo(0) : default;
        _status = $"Opened {_camera_info}";
    }

    void toggle_stream() {
        if (_bridge.IsStreaming) {
            _bridge.StopStream();
            _status = "Stream stopped";
        } else {
            if (!_bridge.IsCameraOpen) {
                open_first();
            }

            start_stream();
        }
    }

    void start_stream() {
        if (!_bridge.IsStreaming) {
            _bridge.StartStream();
        }

        _status = "Stream started";
    }

    void run_action(Action action) {
        try {
            action();
        } catch (Exception exception) {
            _status = exception.Message;
        }

        update_status_ui();
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

    void update_status_ui() {
        if (!_ui_bound || _status_label == null) {
            return;
        }

        _status_label.text = $"Status: {_status}";
        _camera_label.text = $"Camera: {(_bridge.IsCameraOpen ? _camera_info.ToString() : "-")}";
        _stream_label.text = $"Stream: {(_bridge.IsStreaming ? "Running" : "Stopped")}";
        _fps_label.text = $"FPS: {_fps:0.0}";
        _stream_button.text = _bridge.IsStreaming ? "Stop" : "Start";

        if (_empty_label != null && _camera_texture == null) {
            _empty_label.style.display = DisplayStyle.Flex;
        }
    }
}
