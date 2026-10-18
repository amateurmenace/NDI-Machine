// One WASAPI capture owner. No audio device defaults, route writes or format changes.
#include "audio_shared.h"
#include "pipe_server.h"
#include <audioclient.h>
#include <mmdeviceapi.h>
#include <functiondiscoverykeys_devpkey.h>
#include <ks.h>
#include <ksmedia.h>
#include <avrt.h>
#include <nlohmann/json.hpp>
#include <algorithm>
#include <chrono>
#include <iostream>
#include <memory>
#include <thread>
#include <atomic>
using Microsoft::WRL::ComPtr;
using json = nlohmann::json;
using namespace nm;
namespace
{
struct CoFree
{
    void operator()(WAVEFORMATEX *value) const
    {
        CoTaskMemFree(value);
    }
};
using Wave = std::unique_ptr<WAVEFORMATEX, CoFree>;
Wave mix_format(IMMDevice *device, ComPtr<IAudioClient> &client)
{
    hrcheck(device->Activate(__uuidof(IAudioClient), CLSCTX_ALL, nullptr, &client),
            "Activate capture endpoint");
    WAVEFORMATEX *format{};
    hrcheck(client->GetMixFormat(&format), "Get endpoint mix format");
    return Wave(format);
}
std::string name_of(IMMDevice *device)
{
    ComPtr<IPropertyStore> properties;
    hrcheck(device->OpenPropertyStore(STGM_READ, &properties), "Endpoint properties");
    PROPVARIANT name;
    PropVariantInit(&name);
    hrcheck(properties->GetValue(PKEY_Device_FriendlyName, &name), "Endpoint name");
    const auto result = name.vt == VT_LPWSTR ? utf8(name.pwszVal) : std::string{};
    PropVariantClear(&name);
    return result;
}
json describe(IMMDevice *device)
{
    LPWSTR raw{};
    hrcheck(device->GetId(&raw), "Endpoint ID");
    std::wstring id(raw);
    CoTaskMemFree(raw);
    DWORD state{};
    hrcheck(device->GetState(&state), "Endpoint state");
    json result = {{"id", utf8(id)}, {"name", name_of(device)}, {"state", state}};
    ComPtr<IMMEndpoint> direction;
    EDataFlow flow{};
    hrcheck(device->QueryInterface(IID_PPV_ARGS(&direction)), "Endpoint direction");
    hrcheck(direction->GetDataFlow(&flow), "Endpoint flow");
    result["flow"] = flow == eRender ? "loopback" : "capture";
    if (state == DEVICE_STATE_ACTIVE)
    {
        ComPtr<IAudioClient> client;
        auto format = mix_format(device, client);
        result["mixFormat"] = {{"sampleRate", format->nSamplesPerSec},
                               {"channels", format->nChannels},
                               {"bits", format->wBitsPerSample},
                               {"formatTag", format->wFormatTag}};
    }
    return result;
}
bool floating(const WAVEFORMATEX &format)
{
    if (format.wFormatTag == WAVE_FORMAT_IEEE_FLOAT)
        return true;
    return format.wFormatTag == WAVE_FORMAT_EXTENSIBLE && format.cbSize >= 22 &&
           reinterpret_cast<const WAVEFORMATEXTENSIBLE &>(format).SubFormat ==
               KSDATAFORMAT_SUBTYPE_IEEE_FLOAT;
}
bool pcm(const WAVEFORMATEX &format)
{
    return format.wFormatTag == WAVE_FORMAT_PCM ||
           (format.wFormatTag == WAVE_FORMAT_EXTENSIBLE && format.cbSize >= 22 &&
            reinterpret_cast<const WAVEFORMATEXTENSIBLE &>(format).SubFormat == KSDATAFORMAT_SUBTYPE_PCM);
}
float decode(const BYTE *sample, WORD bits, bool isFloat)
{
    if (isFloat)
    {
        float v;
        memcpy(&v, sample, 4);
        return v;
    }
    if (bits == 16)
    {
        int16_t v;
        memcpy(&v, sample, 2);
        return v / 32768.0f;
    }
    if (bits == 24)
    {
        int32_t v = static_cast<int32_t>((static_cast<uint32_t>(sample[2]) << 24) |
                                         (static_cast<uint32_t>(sample[1]) << 16) |
                                         (static_cast<uint32_t>(sample[0]) << 8));
        return static_cast<float>(v / 2147483648.0);
    }
    int32_t v;
    memcpy(&v, sample, 4);
    return static_cast<float>(v / 2147483648.0);
}
} // namespace
int wmain(int argc, wchar_t **argv)
{
    try
    {
        bool list = false, daemon = false;
        std::wstring endpoint, key;
        int left = 1, right = 2;
        int seconds = 10;
        for (int i = 1; i < argc; ++i)
        {
            const std::wstring arg = argv[i];
            if (arg == L"--list")
                list = true;
            else if (arg == L"--daemon")
                daemon = true;
            else if (arg == L"--endpoint" && i + 1 < argc)
                endpoint = argv[++i];
            else if (arg == L"--key" && i + 1 < argc) key = argv[++i];
            else if (arg == L"--left" && i + 1 < argc) left = std::stoi(argv[++i]);
            else if (arg == L"--right" && i + 1 < argc) right = std::stoi(argv[++i]);
            else if (arg == L"--seconds" && i + 1 < argc)
                seconds = std::stoi(argv[++i]);
            else
                throw std::runtime_error("Usage: audio-host --list | --endpoint exact-id --seconds 1..3600");
        }
        if (list == !endpoint.empty() || seconds < 1 || seconds > 3600)
            throw std::runtime_error("Choose endpoint capture or inventory, with a bounded duration");
        if (list && daemon) throw std::runtime_error("Inventory cannot be a daemon");
        if ((!key.empty() && (key.size() != 64 || key.find_first_not_of(L"0123456789ABCDEF") != std::wstring::npos)) || left < 1 || right < 1 || left > 64 || right > 64)
            throw std::runtime_error("Invalid bounded capture identity/channels");
        if (daemon) seconds = 0;
        ComScope com;
        ComPtr<IMMDeviceEnumerator> enumerator;
        hrcheck(
            CoCreateInstance(__uuidof(MMDeviceEnumerator), nullptr, CLSCTX_ALL, IID_PPV_ARGS(&enumerator)),
            "Audio enumerator");
        if (list)
        {
            ComPtr<IMMDeviceCollection> collection;
            hrcheck(enumerator->EnumAudioEndpoints(eAll, DEVICE_STATE_ACTIVE, &collection),
                    "Capture endpoints");
            UINT count{};
            hrcheck(collection->GetCount(&count), "Endpoint count");
            json result = json::array();
            for (UINT index = 0; index < count; ++index)
            {
                ComPtr<IMMDevice> device;
                hrcheck(collection->Item(index, &device), "Endpoint");
                try { result.push_back(describe(device.Get())); }
                catch (const std::exception&) { /* A disappearing endpoint cannot hide the others. */ }
            }
            std::cout << result.dump(2) << '\n';
            return 0;
        }
        NamedOwner owner(L"Global\\NDIMachine.AudioHost" + (key.empty() ? L"" : L"." + key));
        ComPtr<IMMDevice> device;
        hrcheck(enumerator->GetDevice(endpoint.c_str(), &device),
                "Configured capture endpoint (no default fallback)");
        ComPtr<IMMEndpoint> direction;
        hrcheck(device.As(&direction), "Endpoint direction");
        EDataFlow flow{};
        hrcheck(direction->GetDataFlow(&flow), "Capture direction");
        json result = describe(device.Get());
        ComPtr<IAudioClient> client;
        auto format = mix_format(device.Get(), client);
        if (!key.empty()) {
            // Let the Windows shared audio engine convert rate/PCM without changing
            // the device's default format. Preserve all channels before selecting L/R.
            if (std::max(left, right) > format->nChannels) throw std::runtime_error("Selected audio channel unavailable");
            WAVEFORMATEX* converted = static_cast<WAVEFORMATEX*>(CoTaskMemAlloc(sizeof(WAVEFORMATEX)));
            if (!converted) throw std::bad_alloc();
            *converted = {WAVE_FORMAT_IEEE_FLOAT, format->nChannels, 48000,
                static_cast<DWORD>(48000 * format->nChannels * 4), static_cast<WORD>(format->nChannels * 4), 32, 0};
            format.reset(converted);
        }
        const bool isFloat = floating(*format);
        if (format->nSamplesPerSec != 48000 || format->nChannels < std::max(left, right) ||
            (isFloat ? format->wBitsPerSample != 32
                     : (!pcm(*format) || (format->wBitsPerSample != 16 && format->wBitsPerSample != 24 &&
                                          format->wBitsPerSample != 32))))
            throw std::runtime_error("Capture requires an existing 48 kHz stereo PCM16/24/32 or float32 mix "
                                     "format; device settings were not changed");
        WinHandle wake(CreateEventW(nullptr, FALSE, FALSE, nullptr));
        if (!wake.value)
            throw std::runtime_error("Capture event creation failed");
        hrcheck(client->Initialize(AUDCLNT_SHAREMODE_SHARED,
                                   AUDCLNT_STREAMFLAGS_EVENTCALLBACK | AUDCLNT_STREAMFLAGS_NOPERSIST |
                                   (flow == eRender ? AUDCLNT_STREAMFLAGS_LOOPBACK : 0) |
                                   (key.empty() ? 0 : AUDCLNT_STREAMFLAGS_AUTOCONVERTPCM | AUDCLNT_STREAMFLAGS_SRC_DEFAULT_QUALITY), 0, 0,
                                   format.get(), nullptr),
                "Initialize shared capture at existing mix format");
        hrcheck(client->SetEventHandle(wake), "Capture event");
        ComPtr<IAudioCaptureClient> capture;
        hrcheck(client->GetService(IID_PPV_ARGS(&capture)), "Capture service");
        const auto mappingName = key.empty() ? std::wstring(audio::default_mapping) : L"Local\\NDIMachine.Audio." + key;
        audio::Mapping mapping(true, mappingName.c_str());
        audio::Writer writer(mapping.get(), utf8(endpoint));
        std::atomic<bool> stopRequested{false};
        WinHandle controlStop(CreateEventW(nullptr, TRUE, FALSE, nullptr));
        std::jthread server;
        if (daemon) server = std::jthread([&] {
            try {
                PipeServer pipe(L"\\\\.\\pipe\\NDIMachine.audio" + (key.empty() ? L"" : L"." + key) + L".v1");
                pipe.serve(controlStop, [&](const json& request) -> json {
                    const auto method = request.value("method", "");
                    if (method == "stop") { stopRequested = true; return {{"ok", true}, {"accepted", true}}; }
                    if (method != "state") throw std::runtime_error("Unknown audio host method");
                    return {{"ok", true}, {"endpointId", utf8(endpoint)}, {"backend", "wasapi"}, {"sampleRate", 48000}, {"channels", {left,right}},
                        {"fresh", audio::fresh(mapping.get(), qpc())}, {"generation", std::to_string(audio::load(mapping.get().generation))},
                        {"frames", audio::load(mapping.get().published)}, {"discontinuities", audio::load(mapping.get().discontinuities)}};
                }, []{});
            } catch (const std::exception& error) { std::cerr << error.what() << '\n'; stopRequested = true; }
        });
        struct EndControl { HANDLE event; ~EndControl(){ SetEvent(event); } } endControl{controlStop};
        uint64_t frames{}, packets{}, silent{}, discontinuities{}, startupDiscontinuities{},
            timestampErrors{};
        double sumSquares[2]{}, peaks[2]{};
        DWORD task{};
        const HANDLE mmcss = AvSetMmThreadCharacteristicsW(L"Pro Audio", &task);
        struct Revert
        {
            HANDLE handle;
            ~Revert()
            {
                if (handle)
                    AvRevertMmThreadCharacteristics(handle);
            }
        } revert{mmcss};
        hrcheck(client->Start(), "Start capture");
        struct Stop
        {
            IAudioClient *client;
            ~Stop()
            {
                client->Stop();
            }
        } stop{client.Get()};
        const auto finish = std::chrono::steady_clock::now() + std::chrono::seconds(seconds);
        while ((daemon || std::chrono::steady_clock::now() < finish) && !stopRequested)
        {
            const auto wait = WaitForSingleObject(wake, 100);
            if (wait == WAIT_FAILED)
                throw std::runtime_error("Capture event wait failed");
            UINT32 available{};
            hrcheck(capture->GetNextPacketSize(&available), "Next capture packet");
            while (available)
            {
                BYTE *bytes{};
                UINT32 count{};
                DWORD flags{};
                UINT64 position{}, timestamp{};
                hrcheck(capture->GetBuffer(&bytes, &count, &flags, &position, &timestamp), "Capture buffer");
                if (flags & AUDCLNT_BUFFERFLAGS_DATA_DISCONTINUITY)
                {
                    ++discontinuities;
                    if (!packets)
                        ++startupDiscontinuities;
                    InterlockedIncrement64(&mapping.get().discontinuities);
                }
                if (flags & AUDCLNT_BUFFERFLAGS_TIMESTAMP_ERROR)
                {
                    ++timestampErrors;
                    InterlockedIncrement64(&mapping.get().timestampErrors);
                }
                if (flags & AUDCLNT_BUFFERFLAGS_SILENT)
                    ++silent;
                const auto stride = format->wBitsPerSample / 8;
                for (UINT32 index = 0; index < count; ++index)
                {
                    audio::Stereo sample{};
                    if (!(flags & AUDCLNT_BUFFERFLAGS_SILENT))
                    {
                        sample.left =
                            decode(bytes + index * format->nBlockAlign + (left - 1) * stride, format->wBitsPerSample, isFloat);
                        sample.right = decode(bytes + index * format->nBlockAlign + (right - 1) * stride,
                                              format->wBitsPerSample, isFloat);
                    }
                    writer.push(sample);
                    const float values[]{sample.left, sample.right};
                    for (int channel = 0; channel < 2; ++channel)
                        if (std::isfinite(values[channel]))
                        {
                            peaks[channel] =
                                std::max(peaks[channel], std::abs(static_cast<double>(values[channel])));
                            sumSquares[channel] += static_cast<double>(values[channel]) * values[channel];
                        }
                }
                hrcheck(capture->ReleaseBuffer(count), "Release capture buffer");
                if (count)
                    writer.publish(qpc());
                frames += count;
                ++packets;
                hrcheck(capture->GetNextPacketSize(&available), "Next capture packet");
            }
        }
        result.update({{"backend", "wasapi-shared"},
                       {"channelsSelected", {left, right}},
                       {"calibration", "unity-normalized-PCM"},
                       {"seconds", seconds},
                       {"frames", frames},
                       {"packets", packets},
                       {"silentPackets", silent},
                       {"discontinuities", discontinuities},
                       {"startupDiscontinuities", startupDiscontinuities},
                       {"timestampErrors", timestampErrors},
                       {"nonfinite", audio::load(mapping.get().nonfinite)},
                       {"peakLinear", {peaks[0], peaks[1]}},
                       {"rmsLinear",
                        {frames ? std::sqrt(sumSquares[0] / frames) : 0,
                         frames ? std::sqrt(sumSquares[1] / frames) : 0}},
                       {"physicalSignalVerified", "Unknown"}});
        std::cout << result.dump(2) << '\n';
        return frames ? 0 : 2;
    }
    catch (const std::exception &error)
    {
        std::cerr << error.what() << '\n';
        return 1;
    }
}
