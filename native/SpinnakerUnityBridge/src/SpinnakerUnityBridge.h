#pragma once

#include <cstdint>

#if defined(_WIN32)
#define SUB_API extern "C" __declspec(dllexport)
#else
#define SUB_API extern "C"
#endif

enum SubResult
{
    SUB_OK = 0,
    SUB_ERROR = -1,
    SUB_NOT_INITIALIZED = -2,
    SUB_NO_CAMERA = -3,
    SUB_CAMERA_NOT_OPEN = -4,
    SUB_ALREADY_STREAMING = -5,
    SUB_NOT_STREAMING = -6,
    SUB_INVALID_ARGUMENT = -7,
    SUB_BUFFER_TOO_SMALL = -8,
    SUB_NODE_UNAVAILABLE = -9,
    SUB_NO_FRAME = -10
};

SUB_API int sub_initialize();
SUB_API void sub_shutdown();
SUB_API int sub_get_last_error(char* buffer, int bufferLength);

SUB_API int sub_refresh_cameras();
SUB_API int sub_get_camera_count();
SUB_API int sub_get_camera_info(
    int index,
    char* serial,
    int serialLength,
    char* model,
    int modelLength,
    char* vendor,
    int vendorLength);

SUB_API int sub_open_first_camera();
SUB_API int sub_open_camera_by_index(int index);
SUB_API int sub_close_camera();
SUB_API int sub_is_camera_open();
SUB_API int sub_is_streaming();

SUB_API int sub_start_stream();
SUB_API int sub_stop_stream();
SUB_API int sub_get_frame_size(int* width, int* height, int* stride);
SUB_API int sub_get_latest_frame(
    uint8_t* buffer,
    int bufferLength,
    int* width,
    int* height,
    int* stride,
    uint64_t* frameId,
    double* timestampMs);

SUB_API int sub_get_float_node(
    const char* nodeName,
    double* value,
    double* minimum,
    double* maximum,
    int* readable,
    int* writable);
SUB_API int sub_set_float_node(const char* nodeName, double value, int clampToRange);

SUB_API int sub_get_int_node(
    const char* nodeName,
    int64_t* value,
    int64_t* minimum,
    int64_t* maximum,
    int* readable,
    int* writable);
SUB_API int sub_set_int_node(const char* nodeName, int64_t value, int clampToRange);

SUB_API int sub_get_bool_node(const char* nodeName, int* value, int* readable, int* writable);
SUB_API int sub_set_bool_node(const char* nodeName, int value);

SUB_API int sub_get_enum_node(
    const char* nodeName,
    char* value,
    int valueLength,
    int* readable,
    int* writable);
SUB_API int sub_set_enum_node(const char* nodeName, const char* entryName);
SUB_API int sub_get_enum_entries(const char* nodeName, char* entries, int entriesLength);

SUB_API int sub_get_exposure_auto(int* enabled);
SUB_API int sub_set_exposure_auto(int enabled);
SUB_API int sub_get_exposure_time(double* value, double* minimum, double* maximum);
SUB_API int sub_set_exposure_time(double valueUs);

SUB_API int sub_get_gain_auto(int* enabled);
SUB_API int sub_set_gain_auto(int enabled);
SUB_API int sub_get_gain(double* value, double* minimum, double* maximum);
SUB_API int sub_set_gain(double valueDb);

SUB_API int sub_get_frame_rate_enabled(int* enabled);
SUB_API int sub_set_frame_rate_enabled(int enabled);
SUB_API int sub_get_frame_rate(double* value, double* minimum, double* maximum);
SUB_API int sub_set_frame_rate(double valueFps);

SUB_API int sub_get_gamma_enabled(int* enabled);
SUB_API int sub_set_gamma_enabled(int enabled);
SUB_API int sub_get_gamma(double* value, double* minimum, double* maximum);
SUB_API int sub_set_gamma(double value);

SUB_API int sub_get_white_balance_auto(int* enabled);
SUB_API int sub_set_white_balance_auto(int enabled);
SUB_API int sub_get_balance_ratio(
    const char* selector,
    double* value,
    double* minimum,
    double* maximum);
SUB_API int sub_set_balance_ratio(const char* selector, double value);
