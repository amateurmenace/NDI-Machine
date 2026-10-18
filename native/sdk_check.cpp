// Read-only SDK/driver compatibility check. Never enables output or changes profiles.
#define NOMINMAX
#include <Windows.h>
#include <wrl/client.h>
#include <iostream>
#include <sstream>
#include <iomanip>
#include <string>
#include <stdexcept>
#include "DeckLinkAPI.h"
#include "DeckLinkAPIVersion.h"
using Microsoft::WRL::ComPtr;

static std::string utf8(const wchar_t *s)
{
    if (!s)
        return {};
    int count = WideCharToMultiByte(CP_UTF8, 0, s, -1, nullptr, 0, nullptr, nullptr);
    std::string result(static_cast<size_t>(count), '\0');
    WideCharToMultiByte(CP_UTF8, 0, s, -1, result.data(), count, nullptr, nullptr);
    result.pop_back();
    return result;
}
static std::string quoted(const std::string &s)
{
    std::ostringstream o;
    o << '"';
    for (const auto c : s)
    {
        auto b = static_cast<unsigned char>(c);
        if (c == '"' || c == '\\')
            o << '\\' << c;
        else if (b < 32)
            o << "\\u" << std::hex << std::setw(4) << std::setfill('0') << static_cast<unsigned>(b);
        else
            o << c;
    }
    o << '"';
    return o.str();
}
static std::string hexid(LONGLONG value)
{
    std::ostringstream o;
    o << '"' << "0x" << std::hex << static_cast<unsigned long long>(value) << '"';
    return o.str();
}
static std::string attr(IDeckLinkProfileAttributes *a, BMDDeckLinkAttributeID id, bool hex = false)
{
    LONGLONG value{};
    if (!a || FAILED(a->GetInt(id, &value)))
        return "null";
    return hex ? hexid(value) : std::to_string(value);
}
static void check(HRESULT hr, const char *step)
{
    if (FAILED(hr))
    {
        std::ostringstream s;
        s << step << " HRESULT=0x" << std::hex << static_cast<unsigned long>(hr);
        throw std::runtime_error(s.str());
    }
}
struct ComScope
{
    ComScope()
    {
        check(CoInitializeEx(nullptr, COINIT_MULTITHREADED), "CoInitializeEx");
    }
    ~ComScope()
    {
        CoUninitialize();
    }
};
int main()
{
    try
    {
        ComScope com;
        ComPtr<IDeckLinkIterator> iterator;
        check(CoCreateInstance(CLSID_CDeckLinkIterator, nullptr, CLSCTX_ALL, IID_PPV_ARGS(&iterator)),
              "DeckLink iterator");
        ComPtr<IDeckLinkAPIInformation> info;
        check(iterator.As(&info), "API information");
        BSTR version{};
        check(info->GetString(BMDDeckLinkAPIVersion, &version), "Driver API version");
        std::cout << "{\"readOnly\":true,\"compiledSdk\":" << quoted(BLACKMAGIC_DECKLINK_API_VERSION_STRING)
                  << ",\"runtimeApi\":" << quoted(utf8(version)) << ",\"devices\":[";
        SysFreeString(version);
        bool first = true;
        unsigned count{};
        HRESULT next{};
        ComPtr<IDeckLink> device;
        while ((next = iterator->Next(&device)) == S_OK)
        {
            if (!first)
                std::cout << ',';
            first = false;
            ++count;
            BSTR model{}, name{};
            check(device->GetModelName(&model), "Model name");
            check(device->GetDisplayName(&name), "Display name");
            ComPtr<IDeckLinkProfileAttributes> a;
            device.As(&a);
            std::cout << "{\"model\":" << quoted(utf8(model)) << ",\"displayName\":" << quoted(utf8(name))
                      << ",\"persistentId\":" << attr(a.Get(), BMDDeckLinkPersistentID, true)
                      << ",\"deviceGroupId\":" << attr(a.Get(), BMDDeckLinkDeviceGroupID, true)
                      << ",\"subdeviceIndex\":" << attr(a.Get(), BMDDeckLinkSubDeviceIndex)
                      << ",\"profileId\":" << attr(a.Get(), BMDDeckLinkProfileID, true)
                      << ",\"duplex\":" << attr(a.Get(), BMDDeckLinkDuplex, true)
                      << ",\"videoIOSupport\":" << attr(a.Get(), BMDDeckLinkVideoIOSupport)
                      << ",\"maxAudioChannels\":" << attr(a.Get(), BMDDeckLinkMaximumAudioChannels);
            SysFreeString(model);
            SysFreeString(name);
            ComPtr<IDeckLinkStatus> status;
            device.As(&status);
            LONGLONG busy{};
            BOOL locked{};
            std::cout << ",\"busyFlags\":"
                      << (status && SUCCEEDED(status->GetInt(bmdDeckLinkStatusBusy, &busy))
                              ? std::to_string(busy)
                              : "null")
                      << ",\"referenceLocked\":"
                      << (status &&
                                  SUCCEEDED(status->GetFlag(bmdDeckLinkStatusReferenceSignalLocked, &locked))
                              ? (locked ? "true" : "false")
                              : "null");
            ComPtr<IDeckLinkOutput> output;
            BOOL supported = FALSE;
            BMDDisplayMode actual{};
            const auto outputHr = device.As(&output);
            HRESULT modeHr = E_NOINTERFACE;
            if (SUCCEEDED(outputHr))
                modeHr = output->DoesSupportVideoMode(bmdVideoConnectionSDI, bmdModeHD1080p2997,
                                                      bmdFormat8BitYUV, bmdNoVideoOutputConversion,
                                                      bmdSupportedVideoModeDefault, &actual, &supported);
            std::cout << ",\"supports1080p2997Uyvy\":"
                      << (SUCCEEDED(modeHr) ? (supported && actual == bmdModeHD1080p2997 ? "true" : "false")
                                            : "null")
                      << ",\"modeQueryHresult\":" << hexid(modeHr) << ",\"profiles\":[";
            ComPtr<IDeckLinkProfileManager> manager;
            if (SUCCEEDED(device.As(&manager)))
            {
                ComPtr<IDeckLinkProfileIterator> profiles;
                if (SUCCEEDED(manager->GetProfiles(&profiles)))
                {
                    ComPtr<IDeckLinkProfile> profile;
                    bool firstProfile = true;
                    while (profiles->Next(&profile) == S_OK)
                    {
                        ComPtr<IDeckLinkProfileAttributes> pa;
                        profile.As(&pa);
                        BOOL active{};
                        profile->IsActive(&active);
                        if (!firstProfile)
                            std::cout << ',';
                        firstProfile = false;
                        std::cout << "{\"id\":" << attr(pa.Get(), BMDDeckLinkProfileID, true)
                                  << ",\"active\":" << (active ? "true" : "false") << '}';
                        profile.Reset();
                    }
                }
            }
            std::cout << "]}";
            device.Reset();
        }
        if (next != S_FALSE)
            check(next, "Device enumeration");
        std::cout << "],\"deviceCount\":" << count << ",\"physicalOutputTest\":\"Not Run\"}\n";
        return count ? 0 : 2;
    }
    catch (const std::exception &e)
    {
        std::cerr << e.what() << '\n';
        return 1;
    }
}
