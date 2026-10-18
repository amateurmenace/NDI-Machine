#pragma once
#ifndef NOMINMAX
#define NOMINMAX
#endif
#include <Windows.h>
#include <wrl/client.h>
#include <sstream>
#include <stdexcept>
#include <string>

namespace nm
{
inline void hrcheck(HRESULT hr, const char *step)
{
    if (FAILED(hr))
    {
        std::ostringstream message;
        message << step << " HRESULT=0x" << std::hex << static_cast<unsigned long>(hr);
        throw std::runtime_error(message.str());
    }
}
struct WinHandle
{
    HANDLE value{};
    explicit WinHandle(HANDLE handle = nullptr) : value(handle)
    {
    }
    ~WinHandle()
    {
        if (value && value != INVALID_HANDLE_VALUE)
            CloseHandle(value);
    }
    WinHandle(const WinHandle &) = delete;
    WinHandle &operator=(const WinHandle &) = delete;
    operator HANDLE() const
    {
        return value;
    }
};
struct ComScope
{
    ComScope()
    {
        hrcheck(CoInitializeEx(nullptr, COINIT_MULTITHREADED), "COM initialization");
    }
    ~ComScope()
    {
        CoUninitialize();
    }
};
struct NamedOwner
{
    WinHandle handle;
    explicit NamedOwner(const std::wstring &name) : handle(CreateMutexW(nullptr, FALSE, name.c_str()))
    {
        if (!handle.value)
            throw std::runtime_error("Cannot create ownership mutex");
        const auto status = WaitForSingleObject(handle, 0);
        if (status != WAIT_OBJECT_0 && status != WAIT_ABANDONED)
            throw std::runtime_error("Resource already owned by another NDI Machine process");
    }
    ~NamedOwner()
    {
        ReleaseMutex(handle);
    }
};
inline std::string utf8(const std::wstring &text)
{
    if (text.empty())
        return {};
    const auto count = WideCharToMultiByte(CP_UTF8, WC_ERR_INVALID_CHARS, text.data(),
                                           static_cast<int>(text.size()), nullptr, 0, nullptr, nullptr);
    if (!count)
        throw std::runtime_error("Invalid UTF-16 text");
    std::string result(count, '\0');
    WideCharToMultiByte(CP_UTF8, WC_ERR_INVALID_CHARS, text.data(), static_cast<int>(text.size()),
                        result.data(), count, nullptr, nullptr);
    return result;
}
inline int64_t qpc()
{
    LARGE_INTEGER value{};
    QueryPerformanceCounter(&value);
    return value.QuadPart;
}
inline int64_t qpc_frequency()
{
    LARGE_INTEGER value{};
    QueryPerformanceFrequency(&value);
    return value.QuadPart;
}
} // namespace nm
