#include "SpinnakerUnityBridge.h"

#include "Spinnaker.h"
#include "SpinGenApi/SpinnakerGenApi.h"

#include <algorithm>
#include <atomic>
#include <chrono>
#include <cstring>
#include <mutex>
#include <sstream>
#include <string>
#include <thread>
#include <vector>

using namespace Spinnaker;
using namespace Spinnaker::GenApi;
using namespace Spinnaker::GenICam;

namespace
{
constexpr int kFrameTimeoutMs = 1000;

struct FrameBuffer
{
    std::vector<uint8_t> data;
    int width = 0;
    int height = 0;
    int stride = 0;
    uint64_t frameId = 0;
    double timestampMs = 0.0;
};

struct BridgeState
{
    std::mutex stateMutex;
    std::mutex cameraMutex;
    std::mutex frameMutex;
    std::mutex errorMutex;
    SystemPtr system;
    CameraList cameras;
    CameraPtr camera;
    std::thread acquisitionThread;
    std::atomic<bool> streaming{false};
    FrameBuffer latestFrame;
    std::string lastError;
};

BridgeState& bridge_state()
{
    static BridgeState* instance = new BridgeState();
    return *instance;
}

#define g_state bridge_state()

void set_error(const std::string& message)
{
    std::lock_guard<std::mutex> lock(g_state.errorMutex);
    g_state.lastError = message;
}

void set_error(const char* context, const Spinnaker::Exception& exception)
{
    std::ostringstream stream;
    stream << context << ": " << exception.what();
    set_error(stream.str());
}

void clear_error()
{
    set_error("");
}

int copy_string(const std::string& value, char* buffer, int bufferLength)
{
    if (buffer == nullptr || bufferLength <= 0)
    {
        return SUB_INVALID_ARGUMENT;
    }

#if defined(_WIN32)
    strncpy_s(buffer, static_cast<size_t>(bufferLength), value.c_str(), _TRUNCATE);
#else
    std::strncpy(buffer, value.c_str(), static_cast<size_t>(bufferLength - 1));
    buffer[bufferLength - 1] = '\0';
#endif
    return SUB_OK;
}

bool has_system()
{
    return g_state.system.IsValid();
}

bool has_camera()
{
    return g_state.camera.IsValid() && g_state.camera->IsInitialized();
}

int ensure_initialized()
{
    if (!has_system())
    {
        set_error("Spinnaker system is not initialized.");
        return SUB_NOT_INITIALIZED;
    }

    return SUB_OK;
}

int ensure_camera_open()
{
    int result = ensure_initialized();
    if (result != SUB_OK)
    {
        return result;
    }

    if (!has_camera())
    {
        set_error("No camera is open.");
        return SUB_CAMERA_NOT_OPEN;
    }

    return SUB_OK;
}

std::string get_string_node(INodeMap& nodeMap, const char* nodeName)
{
    CStringPtr node = nodeMap.GetNode(nodeName);
    if (!IsReadable(node))
    {
        return "";
    }

    return node->ToString().c_str();
}

int set_enum_if_available(INodeMap& nodeMap, const char* nodeName, const char* entryName, bool failIfUnavailable)
{
    CEnumerationPtr node = nodeMap.GetNode(nodeName);
    if (!IsReadable(node) || !IsWritable(node))
    {
        if (failIfUnavailable)
        {
            set_error(std::string("Enum node is not readable/writable: ") + nodeName);
            return SUB_NODE_UNAVAILABLE;
        }

        return SUB_OK;
    }

    CEnumEntryPtr entry = node->GetEntryByName(entryName);
    if (!IsReadable(entry))
    {
        if (failIfUnavailable)
        {
            set_error(std::string("Enum entry is not readable: ") + nodeName + "." + entryName);
            return SUB_NODE_UNAVAILABLE;
        }

        return SUB_OK;
    }

    node->SetIntValue(entry->GetValue());
    return SUB_OK;
}

int set_bool_if_available(INodeMap& nodeMap, const char* nodeName, bool value, bool failIfUnavailable)
{
    CBooleanPtr node = nodeMap.GetNode(nodeName);
    if (!IsReadable(node) || !IsWritable(node))
    {
        if (failIfUnavailable)
        {
            set_error(std::string("Boolean node is not readable/writable: ") + nodeName);
            return SUB_NODE_UNAVAILABLE;
        }

        return SUB_OK;
    }

    node->SetValue(value);
    return SUB_OK;
}

int set_stream_enum_if_available(const char* nodeName, const char* entryName)
{
    INodeMap& streamNodeMap = g_state.camera->GetTLStreamNodeMap();
    return set_enum_if_available(streamNodeMap, nodeName, entryName, false);
}

int configure_camera_for_stream()
{
    std::lock_guard<std::mutex> cameraLock(g_state.cameraMutex);
    int result = ensure_camera_open();
    if (result != SUB_OK)
    {
        return result;
    }

    INodeMap& nodeMap = g_state.camera->GetNodeMap();

    set_stream_enum_if_available("StreamBufferHandlingMode", "NewestOnly");
    set_stream_enum_if_available("StreamMode", "TeledyneGigeVision");
    set_enum_if_available(nodeMap, "AcquisitionMode", "Continuous", true);
    set_enum_if_available(nodeMap, "TriggerMode", "Off", false);

    return SUB_OK;
}

void acquisition_loop()
{
    ImageProcessor processor;
    processor.SetColorProcessing(SPINNAKER_COLOR_PROCESSING_ALGORITHM_HQ_LINEAR);

    while (g_state.streaming.load())
    {
        try
        {
            ImagePtr rawImage;
            {
                std::lock_guard<std::mutex> cameraLock(g_state.cameraMutex);
                if (!has_camera())
                {
                    set_error("Camera was closed while streaming.");
                    g_state.streaming.store(false);
                    break;
                }

                rawImage = g_state.camera->GetNextImage(kFrameTimeoutMs);
            }

            if (!rawImage.IsValid())
            {
                continue;
            }

            if (rawImage->IsIncomplete())
            {
                rawImage->Release();
                continue;
            }

            ImagePtr rgbImage = processor.Convert(rawImage, PixelFormat_RGB8);
            const int width = static_cast<int>(rgbImage->GetWidth());
            const int height = static_cast<int>(rgbImage->GetHeight());
            const int stride = width * 3;
            const size_t byteCount = static_cast<size_t>(stride) * static_cast<size_t>(height);

            FrameBuffer frame;
            frame.width = width;
            frame.height = height;
            frame.stride = stride;
            frame.frameId = rawImage->GetFrameID();
            frame.timestampMs = static_cast<double>(rawImage->GetTimeStamp()) / 1000000.0;
            frame.data.resize(byteCount);
            std::memcpy(frame.data.data(), rgbImage->GetData(), byteCount);

            rawImage->Release();

            {
                std::lock_guard<std::mutex> frameLock(g_state.frameMutex);
                g_state.latestFrame = std::move(frame);
            }
        }
        catch (const Spinnaker::Exception& exception)
        {
            set_error("Frame acquisition failed", exception);
        }
        catch (const std::exception& exception)
        {
            set_error(std::string("Frame acquisition failed: ") + exception.what());
        }
    }
}

int get_float_node_internal(const char* nodeName, double* value, double* minimum, double* maximum, int* readable, int* writable)
{
    if (nodeName == nullptr || value == nullptr || minimum == nullptr || maximum == nullptr)
    {
        set_error("Invalid argument passed to get_float_node.");
        return SUB_INVALID_ARGUMENT;
    }

    std::lock_guard<std::mutex> cameraLock(g_state.cameraMutex);
    int result = ensure_camera_open();
    if (result != SUB_OK)
    {
        return result;
    }

    CFloatPtr node = g_state.camera->GetNodeMap().GetNode(nodeName);
    const bool canRead = IsReadable(node);
    const bool canWrite = IsWritable(node);
    if (readable != nullptr)
    {
        *readable = canRead ? 1 : 0;
    }
    if (writable != nullptr)
    {
        *writable = canWrite ? 1 : 0;
    }

    if (!canRead)
    {
        set_error(std::string("Float node is not readable: ") + nodeName);
        return SUB_NODE_UNAVAILABLE;
    }

    *value = node->GetValue();
    *minimum = node->GetMin();
    *maximum = node->GetMax();
    return SUB_OK;
}

int set_float_node_internal(const char* nodeName, double value, int clampToRange)
{
    if (nodeName == nullptr)
    {
        set_error("Invalid argument passed to set_float_node.");
        return SUB_INVALID_ARGUMENT;
    }

    std::lock_guard<std::mutex> cameraLock(g_state.cameraMutex);
    int result = ensure_camera_open();
    if (result != SUB_OK)
    {
        return result;
    }

    CFloatPtr node = g_state.camera->GetNodeMap().GetNode(nodeName);
    if (!IsWritable(node))
    {
        set_error(std::string("Float node is not writable: ") + nodeName);
        return SUB_NODE_UNAVAILABLE;
    }

    double nextValue = value;
    if (clampToRange != 0)
    {
        nextValue = std::max(node->GetMin(), std::min(node->GetMax(), nextValue));
    }

    node->SetValue(nextValue);
    return SUB_OK;
}

int get_enum_enabled(const char* nodeName, int* enabled)
{
    if (enabled == nullptr)
    {
        return SUB_INVALID_ARGUMENT;
    }

    char current[128] = {};
    int readable = 0;
    int writable = 0;
    int result = sub_get_enum_node(nodeName, current, sizeof(current), &readable, &writable);
    if (result != SUB_OK)
    {
        return result;
    }

    *enabled = std::strcmp(current, "Off") == 0 ? 0 : 1;
    return SUB_OK;
}

int set_auto_enum(const char* nodeName, int enabled)
{
    return sub_set_enum_node(nodeName, enabled != 0 ? "Continuous" : "Off");
}

void stop_stream_locked()
{
    if (!g_state.streaming.load())
    {
        return;
    }

    g_state.streaming.store(false);
    if (g_state.acquisitionThread.joinable())
    {
        g_state.acquisitionThread.join();
    }

    std::lock_guard<std::mutex> cameraLock(g_state.cameraMutex);
    if (has_camera())
    {
        try
        {
            g_state.camera->EndAcquisition();
        }
        catch (const Spinnaker::Exception& exception)
        {
            set_error("EndAcquisition failed", exception);
        }
    }
}
}

int sub_initialize()
{
    std::lock_guard<std::mutex> lock(g_state.stateMutex);

    try
    {
        if (!has_system())
        {
            g_state.system = System::GetInstance();
        }

        clear_error();
        return sub_refresh_cameras();
    }
    catch (const Spinnaker::Exception& exception)
    {
        set_error("Spinnaker initialization failed", exception);
        return SUB_ERROR;
    }
}

void sub_shutdown()
{
    std::lock_guard<std::mutex> lock(g_state.stateMutex);

    stop_stream_locked();
    sub_close_camera();

    try
    {
        g_state.cameras.Clear();
        if (has_system())
        {
            g_state.system->ReleaseInstance();
            g_state.system = nullptr;
        }
    }
    catch (const Spinnaker::Exception& exception)
    {
        set_error("Spinnaker shutdown failed", exception);
    }
}

int sub_get_last_error(char* buffer, int bufferLength)
{
    std::lock_guard<std::mutex> lock(g_state.errorMutex);
    return copy_string(g_state.lastError, buffer, bufferLength);
}

int sub_refresh_cameras()
{
    try
    {
        int result = ensure_initialized();
        if (result != SUB_OK)
        {
            return result;
        }

        g_state.cameras.Clear();
        g_state.cameras = g_state.system->GetCameras();
        return SUB_OK;
    }
    catch (const Spinnaker::Exception& exception)
    {
        set_error("Camera refresh failed", exception);
        return SUB_ERROR;
    }
}

int sub_get_camera_count()
{
    int result = ensure_initialized();
    if (result != SUB_OK)
    {
        return result;
    }

    return static_cast<int>(g_state.cameras.GetSize());
}

int sub_get_camera_info(
    int index,
    char* serial,
    int serialLength,
    char* model,
    int modelLength,
    char* vendor,
    int vendorLength)
{
    try
    {
        int result = ensure_initialized();
        if (result != SUB_OK)
        {
            return result;
        }

        if (index < 0 || index >= static_cast<int>(g_state.cameras.GetSize()))
        {
            set_error("Camera index is out of range.");
            return SUB_INVALID_ARGUMENT;
        }

        CameraPtr camera = g_state.cameras.GetByIndex(static_cast<unsigned int>(index));
        INodeMap& tlNodeMap = camera->GetTLDeviceNodeMap();

        copy_string(get_string_node(tlNodeMap, "DeviceSerialNumber"), serial, serialLength);
        copy_string(get_string_node(tlNodeMap, "DeviceModelName"), model, modelLength);
        copy_string(get_string_node(tlNodeMap, "DeviceVendorName"), vendor, vendorLength);
        return SUB_OK;
    }
    catch (const Spinnaker::Exception& exception)
    {
        set_error("Camera info read failed", exception);
        return SUB_ERROR;
    }
}

int sub_open_first_camera()
{
    return sub_open_camera_by_index(0);
}

int sub_open_camera_by_index(int index)
{
    std::lock_guard<std::mutex> lock(g_state.stateMutex);

    try
    {
        int result = ensure_initialized();
        if (result != SUB_OK)
        {
            return result;
        }

        if (g_state.cameras.GetSize() == 0)
        {
            result = sub_refresh_cameras();
            if (result != SUB_OK)
            {
                return result;
            }

            if (g_state.cameras.GetSize() == 0)
            {
                set_error("No Spinnaker camera was detected.");
                return SUB_NO_CAMERA;
            }
        }

        if (index < 0 || index >= static_cast<int>(g_state.cameras.GetSize()))
        {
            set_error("Camera index is out of range.");
            return SUB_INVALID_ARGUMENT;
        }

        sub_close_camera();
        g_state.camera = g_state.cameras.GetByIndex(static_cast<unsigned int>(index));
        g_state.camera->Init();

        {
            std::lock_guard<std::mutex> frameLock(g_state.frameMutex);
            g_state.latestFrame = FrameBuffer{};
        }

        clear_error();
        return SUB_OK;
    }
    catch (const Spinnaker::Exception& exception)
    {
        set_error("Camera open failed", exception);
        return SUB_ERROR;
    }
}

int sub_close_camera()
{
    try
    {
        stop_stream_locked();

        std::lock_guard<std::mutex> cameraLock(g_state.cameraMutex);
        if (g_state.camera.IsValid())
        {
            if (g_state.camera->IsInitialized())
            {
                g_state.camera->DeInit();
            }
            g_state.camera = nullptr;
        }

        return SUB_OK;
    }
    catch (const Spinnaker::Exception& exception)
    {
        set_error("Camera close failed", exception);
        return SUB_ERROR;
    }
}

int sub_is_camera_open()
{
    return has_camera() ? 1 : 0;
}

int sub_is_streaming()
{
    return g_state.streaming.load() ? 1 : 0;
}

int sub_start_stream()
{
    std::lock_guard<std::mutex> lock(g_state.stateMutex);

    if (g_state.streaming.load())
    {
        return SUB_ALREADY_STREAMING;
    }

    try
    {
        int result = configure_camera_for_stream();
        if (result != SUB_OK)
        {
            return result;
        }

        {
            std::lock_guard<std::mutex> cameraLock(g_state.cameraMutex);
            g_state.camera->BeginAcquisition();
        }

        g_state.streaming.store(true);
        g_state.acquisitionThread = std::thread(acquisition_loop);
        clear_error();
        return SUB_OK;
    }
    catch (const Spinnaker::Exception& exception)
    {
        g_state.streaming.store(false);
        set_error("Start stream failed", exception);
        return SUB_ERROR;
    }
}

int sub_stop_stream()
{
    std::lock_guard<std::mutex> lock(g_state.stateMutex);
    if (!g_state.streaming.load())
    {
        return SUB_NOT_STREAMING;
    }

    stop_stream_locked();
    return SUB_OK;
}

int sub_get_frame_size(int* width, int* height, int* stride)
{
    if (width == nullptr || height == nullptr || stride == nullptr)
    {
        return SUB_INVALID_ARGUMENT;
    }

    std::lock_guard<std::mutex> frameLock(g_state.frameMutex);
    if (g_state.latestFrame.width <= 0 || g_state.latestFrame.height <= 0)
    {
        *width = 0;
        *height = 0;
        *stride = 0;
        return SUB_NO_FRAME;
    }

    *width = g_state.latestFrame.width;
    *height = g_state.latestFrame.height;
    *stride = g_state.latestFrame.stride;
    return SUB_OK;
}

int sub_get_latest_frame(
    uint8_t* buffer,
    int bufferLength,
    int* width,
    int* height,
    int* stride,
    uint64_t* frameId,
    double* timestampMs)
{
    if (width == nullptr || height == nullptr || stride == nullptr || frameId == nullptr || timestampMs == nullptr)
    {
        return SUB_INVALID_ARGUMENT;
    }

    std::lock_guard<std::mutex> frameLock(g_state.frameMutex);
    if (g_state.latestFrame.data.empty())
    {
        *width = 0;
        *height = 0;
        *stride = 0;
        *frameId = 0;
        *timestampMs = 0.0;
        return SUB_NO_FRAME;
    }

    *width = g_state.latestFrame.width;
    *height = g_state.latestFrame.height;
    *stride = g_state.latestFrame.stride;
    *frameId = g_state.latestFrame.frameId;
    *timestampMs = g_state.latestFrame.timestampMs;

    const int requiredBytes = g_state.latestFrame.stride * g_state.latestFrame.height;
    if (buffer == nullptr || bufferLength < requiredBytes)
    {
        return SUB_BUFFER_TOO_SMALL;
    }

    std::memcpy(buffer, g_state.latestFrame.data.data(), static_cast<size_t>(requiredBytes));
    return SUB_OK;
}

int sub_get_float_node(
    const char* nodeName,
    double* value,
    double* minimum,
    double* maximum,
    int* readable,
    int* writable)
{
    try
    {
        return get_float_node_internal(nodeName, value, minimum, maximum, readable, writable);
    }
    catch (const Spinnaker::Exception& exception)
    {
        set_error("Float node read failed", exception);
        return SUB_ERROR;
    }
}

int sub_set_float_node(const char* nodeName, double value, int clampToRange)
{
    try
    {
        return set_float_node_internal(nodeName, value, clampToRange);
    }
    catch (const Spinnaker::Exception& exception)
    {
        set_error("Float node write failed", exception);
        return SUB_ERROR;
    }
}

int sub_get_int_node(
    const char* nodeName,
    int64_t* value,
    int64_t* minimum,
    int64_t* maximum,
    int* readable,
    int* writable)
{
    if (nodeName == nullptr || value == nullptr || minimum == nullptr || maximum == nullptr)
    {
        return SUB_INVALID_ARGUMENT;
    }

    try
    {
        std::lock_guard<std::mutex> cameraLock(g_state.cameraMutex);
        int result = ensure_camera_open();
        if (result != SUB_OK)
        {
            return result;
        }

        CIntegerPtr node = g_state.camera->GetNodeMap().GetNode(nodeName);
        const bool canRead = IsReadable(node);
        const bool canWrite = IsWritable(node);
        if (readable != nullptr)
        {
            *readable = canRead ? 1 : 0;
        }
        if (writable != nullptr)
        {
            *writable = canWrite ? 1 : 0;
        }

        if (!canRead)
        {
            set_error(std::string("Integer node is not readable: ") + nodeName);
            return SUB_NODE_UNAVAILABLE;
        }

        *value = node->GetValue();
        *minimum = node->GetMin();
        *maximum = node->GetMax();
        return SUB_OK;
    }
    catch (const Spinnaker::Exception& exception)
    {
        set_error("Integer node read failed", exception);
        return SUB_ERROR;
    }
}

int sub_set_int_node(const char* nodeName, int64_t value, int clampToRange)
{
    if (nodeName == nullptr)
    {
        return SUB_INVALID_ARGUMENT;
    }

    try
    {
        std::lock_guard<std::mutex> cameraLock(g_state.cameraMutex);
        int result = ensure_camera_open();
        if (result != SUB_OK)
        {
            return result;
        }

        CIntegerPtr node = g_state.camera->GetNodeMap().GetNode(nodeName);
        if (!IsWritable(node))
        {
            set_error(std::string("Integer node is not writable: ") + nodeName);
            return SUB_NODE_UNAVAILABLE;
        }

        int64_t nextValue = value;
        if (clampToRange != 0)
        {
            nextValue = std::max(node->GetMin(), std::min(node->GetMax(), nextValue));
        }

        node->SetValue(nextValue);
        return SUB_OK;
    }
    catch (const Spinnaker::Exception& exception)
    {
        set_error("Integer node write failed", exception);
        return SUB_ERROR;
    }
}

int sub_get_bool_node(const char* nodeName, int* value, int* readable, int* writable)
{
    if (nodeName == nullptr || value == nullptr)
    {
        return SUB_INVALID_ARGUMENT;
    }

    try
    {
        std::lock_guard<std::mutex> cameraLock(g_state.cameraMutex);
        int result = ensure_camera_open();
        if (result != SUB_OK)
        {
            return result;
        }

        CBooleanPtr node = g_state.camera->GetNodeMap().GetNode(nodeName);
        const bool canRead = IsReadable(node);
        const bool canWrite = IsWritable(node);
        if (readable != nullptr)
        {
            *readable = canRead ? 1 : 0;
        }
        if (writable != nullptr)
        {
            *writable = canWrite ? 1 : 0;
        }

        if (!canRead)
        {
            set_error(std::string("Boolean node is not readable: ") + nodeName);
            return SUB_NODE_UNAVAILABLE;
        }

        *value = node->GetValue() ? 1 : 0;
        return SUB_OK;
    }
    catch (const Spinnaker::Exception& exception)
    {
        set_error("Boolean node read failed", exception);
        return SUB_ERROR;
    }
}

int sub_set_bool_node(const char* nodeName, int value)
{
    if (nodeName == nullptr)
    {
        return SUB_INVALID_ARGUMENT;
    }

    try
    {
        std::lock_guard<std::mutex> cameraLock(g_state.cameraMutex);
        int result = ensure_camera_open();
        if (result != SUB_OK)
        {
            return result;
        }

        return set_bool_if_available(g_state.camera->GetNodeMap(), nodeName, value != 0, true);
    }
    catch (const Spinnaker::Exception& exception)
    {
        set_error("Boolean node write failed", exception);
        return SUB_ERROR;
    }
}

int sub_get_enum_node(
    const char* nodeName,
    char* value,
    int valueLength,
    int* readable,
    int* writable)
{
    if (nodeName == nullptr || value == nullptr)
    {
        return SUB_INVALID_ARGUMENT;
    }

    try
    {
        std::lock_guard<std::mutex> cameraLock(g_state.cameraMutex);
        int result = ensure_camera_open();
        if (result != SUB_OK)
        {
            return result;
        }

        CEnumerationPtr node = g_state.camera->GetNodeMap().GetNode(nodeName);
        const bool canRead = IsReadable(node);
        const bool canWrite = IsWritable(node);
        if (readable != nullptr)
        {
            *readable = canRead ? 1 : 0;
        }
        if (writable != nullptr)
        {
            *writable = canWrite ? 1 : 0;
        }

        if (!canRead)
        {
            set_error(std::string("Enum node is not readable: ") + nodeName);
            return SUB_NODE_UNAVAILABLE;
        }

        CEnumEntryPtr current = node->GetCurrentEntry();
        if (!IsReadable(current))
        {
            set_error(std::string("Current enum entry is not readable: ") + nodeName);
            return SUB_NODE_UNAVAILABLE;
        }

        return copy_string(current->GetSymbolic().c_str(), value, valueLength);
    }
    catch (const Spinnaker::Exception& exception)
    {
        set_error("Enum node read failed", exception);
        return SUB_ERROR;
    }
}

int sub_set_enum_node(const char* nodeName, const char* entryName)
{
    if (nodeName == nullptr || entryName == nullptr)
    {
        return SUB_INVALID_ARGUMENT;
    }

    try
    {
        std::lock_guard<std::mutex> cameraLock(g_state.cameraMutex);
        int result = ensure_camera_open();
        if (result != SUB_OK)
        {
            return result;
        }

        return set_enum_if_available(g_state.camera->GetNodeMap(), nodeName, entryName, true);
    }
    catch (const Spinnaker::Exception& exception)
    {
        set_error("Enum node write failed", exception);
        return SUB_ERROR;
    }
}

int sub_get_enum_entries(const char* nodeName, char* entries, int entriesLength)
{
    if (nodeName == nullptr || entries == nullptr || entriesLength <= 0)
    {
        return SUB_INVALID_ARGUMENT;
    }

    try
    {
        std::lock_guard<std::mutex> cameraLock(g_state.cameraMutex);
        int result = ensure_camera_open();
        if (result != SUB_OK)
        {
            return result;
        }

        CEnumerationPtr node = g_state.camera->GetNodeMap().GetNode(nodeName);
        if (!IsReadable(node))
        {
            set_error(std::string("Enum node is not readable: ") + nodeName);
            return SUB_NODE_UNAVAILABLE;
        }

        NodeList_t entryNodes;
        node->GetEntries(entryNodes);

        std::string joined;
        for (auto& entryNode : entryNodes)
        {
            CEnumEntryPtr entry = entryNode;
            if (!IsAvailable(entry) || !IsReadable(entry))
            {
                continue;
            }

            if (!joined.empty())
            {
                joined += ";";
            }
            joined += entry->GetSymbolic().c_str();
        }

        return copy_string(joined, entries, entriesLength);
    }
    catch (const Spinnaker::Exception& exception)
    {
        set_error("Enum entries read failed", exception);
        return SUB_ERROR;
    }
}

int sub_get_exposure_auto(int* enabled)
{
    return get_enum_enabled("ExposureAuto", enabled);
}

int sub_set_exposure_auto(int enabled)
{
    return set_auto_enum("ExposureAuto", enabled);
}

int sub_get_exposure_time(double* value, double* minimum, double* maximum)
{
    return sub_get_float_node("ExposureTime", value, minimum, maximum, nullptr, nullptr);
}

int sub_set_exposure_time(double valueUs)
{
    return sub_set_float_node("ExposureTime", valueUs, 1);
}

int sub_get_gain_auto(int* enabled)
{
    return get_enum_enabled("GainAuto", enabled);
}

int sub_set_gain_auto(int enabled)
{
    return set_auto_enum("GainAuto", enabled);
}

int sub_get_gain(double* value, double* minimum, double* maximum)
{
    return sub_get_float_node("Gain", value, minimum, maximum, nullptr, nullptr);
}

int sub_set_gain(double valueDb)
{
    return sub_set_float_node("Gain", valueDb, 1);
}

int sub_get_frame_rate_enabled(int* enabled)
{
    return sub_get_bool_node("AcquisitionFrameRateEnable", enabled, nullptr, nullptr);
}

int sub_set_frame_rate_enabled(int enabled)
{
    return sub_set_bool_node("AcquisitionFrameRateEnable", enabled);
}

int sub_get_frame_rate(double* value, double* minimum, double* maximum)
{
    return sub_get_float_node("AcquisitionFrameRate", value, minimum, maximum, nullptr, nullptr);
}

int sub_set_frame_rate(double valueFps)
{
    return sub_set_float_node("AcquisitionFrameRate", valueFps, 1);
}

int sub_get_gamma_enabled(int* enabled)
{
    return sub_get_bool_node("GammaEnable", enabled, nullptr, nullptr);
}

int sub_set_gamma_enabled(int enabled)
{
    return sub_set_bool_node("GammaEnable", enabled);
}

int sub_get_gamma(double* value, double* minimum, double* maximum)
{
    return sub_get_float_node("Gamma", value, minimum, maximum, nullptr, nullptr);
}

int sub_set_gamma(double value)
{
    return sub_set_float_node("Gamma", value, 1);
}

int sub_get_white_balance_auto(int* enabled)
{
    return get_enum_enabled("BalanceWhiteAuto", enabled);
}

int sub_set_white_balance_auto(int enabled)
{
    return set_auto_enum("BalanceWhiteAuto", enabled);
}

int sub_get_balance_ratio(const char* selector, double* value, double* minimum, double* maximum)
{
    if (selector == nullptr)
    {
        return SUB_INVALID_ARGUMENT;
    }

    int result = sub_set_enum_node("BalanceRatioSelector", selector);
    if (result != SUB_OK)
    {
        return result;
    }

    return sub_get_float_node("BalanceRatio", value, minimum, maximum, nullptr, nullptr);
}

int sub_set_balance_ratio(const char* selector, double value)
{
    if (selector == nullptr)
    {
        return SUB_INVALID_ARGUMENT;
    }

    int result = sub_set_enum_node("BalanceRatioSelector", selector);
    if (result != SUB_OK)
    {
        return result;
    }

    return sub_set_float_node("BalanceRatio", value, 1);
}
