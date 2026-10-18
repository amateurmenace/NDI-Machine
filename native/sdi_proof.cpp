#include "sdi_engine.h"
int main(int argc, char **argv)
{
    try
    {
        int slot = 0, seconds = 10;
        std::string id, source, audioEndpoint;
        bool bars = false, usbAudio = false;
        for (int i = 1; i < argc; ++i)
        {
            std::string a = argv[i];
            if (a == "--slot" && i + 1 < argc)
                slot = std::stoi(argv[++i]);
            else if (a == "--device" && i + 1 < argc)
                id = argv[++i];
            else if (a == "--source" && i + 1 < argc)
                source = argv[++i];
            else if (a == "--seconds" && i + 1 < argc)
                seconds = std::stoi(argv[++i]);
            else if (a == "--bars")
                bars = true;
            else if (a == "--audio-usb")
                usbAudio = true;
            else if (a == "--audio-endpoint" && i + 1 < argc)
                audioEndpoint = argv[++i];
            else
                throw std::runtime_error("Usage: ndi-sdi-proof --slot 1..4 --device stable-hex-id (--bars | "
                                         "--source exact-name) --seconds 1..3600");
        }
        if (slot < 1 || slot > 4 || id.empty() || seconds < 1 || seconds > 3600 || bars == !source.empty() ||
            (bars && usbAudio))
            throw std::runtime_error("Explicit slot, device, duration and exactly one input required");
        if (usbAudio && audioEndpoint.empty())
            throw std::runtime_error("Explicit USB audio endpoint ID required");
        Com com;
        Ownership slotOwner(L"Global\\NDIMachine.slot." + std::to_wstring(slot));
        size_t used{};
        const auto selected = std::stoull(id, &used, 0);
        if (used != id.size())
            throw std::runtime_error("Invalid stable device ID");
        Ownership deviceOwner(L"Global\\NDIMachine.device." + std::to_wstring(selected));
        ComPtr<IDeckLinkIterator> iterator;
        check(CoCreateInstance(CLSID_CDeckLinkIterator, nullptr, CLSCTX_ALL, IID_PPV_ARGS(&iterator)),
              "DeckLink iterator");
        ComPtr<IDeckLink> device, chosen;
        while (iterator->Next(&device) == S_OK)
        {
            ComPtr<IDeckLinkProfileAttributes> a;
            LONGLONG persistent{};
            if (SUCCEEDED(device.As(&a)) && SUCCEEDED(a->GetInt(BMDDeckLinkPersistentID, &persistent)) &&
                static_cast<uint64_t>(persistent) == selected)
            {
                if (chosen)
                    throw std::runtime_error("Ambiguous stable device identity");
                chosen = device;
            }
            device.Reset();
        }
        if (!chosen)
            throw std::runtime_error("Configured device missing; no replacement selected");
        if (!NDIlib_initialize())
            throw std::runtime_error("NDI runtime unavailable");
        struct Runtime
        {
            ~Runtime()
            {
                NDIlib_destroy();
            }
        } runtime;
        std::unique_ptr<Receiver> rx;
        if (!bars)
        {
            rx = std::make_unique<Receiver>(source);
            auto end = Clock::now() + std::chrono::seconds(10);
            bool ready = false;
            while (Clock::now() < end)
            {
                NDIlib_video_frame_v2_t v{};
                NDIlib_framesync_capture_video(rx->sync, &v);
                ready = usable(v);
                NDIlib_framesync_free_video(rx->sync, &v);
                if (ready)
                    break;
                Sleep(10);
            }
            if (!ready)
                throw std::runtime_error(
                    "Source preparation failed: require fresh 1080p30000/1001 UYVY before enabling SDI");
        }
        std::unique_ptr<nm::audio::Mapping> audioMapping;
        std::unique_ptr<nm::audio::Reader> audioReader;
        if (usbAudio)
        {
            audioMapping = std::make_unique<nm::audio::Mapping>(false);
            audioReader = std::make_unique<nm::audio::Reader>(audioMapping->get(), audioEndpoint);
            const auto end = Clock::now() + std::chrono::seconds(2);
            while (!audioReader->ready() && Clock::now() < end)
                Sleep(10);
            if (!audioReader->ready())
                throw std::runtime_error("USB audio host is not fresh/primed; SDI was not enabled");
        }
        Output output(chosen.Get(), slot, id, bars, rx.get(), audioReader.get());
        output.run(seconds);
        json result = {{"backend", "hardware"},
                       {"slot", slot},
                       {"device", id},
                       {"source", bars ? "labelled bars and -20dBFS tones" : source},
                       {"seconds", seconds},
                       {"completed", output.completed},
                       {"late", output.late},
                       {"dropped", output.dropped},
                       {"flushed", output.flushed},
                       {"fallbackFrames", output.fallbackFrames},
                       {"inputRepeats", output.inputRepeats},
                       {"receivedVideoFrames", output.lastVideoFrames},
                       {"receivedAudioFrames", output.lastAudioFrames},
                       {"audioPolicy", usbAudio ? "fixed-usb-receive-1-2" : "follow-video"},
                       {"audioFallbackBlocks", output.audioFallbackBlocks},
                       {"audioNonzeroSamples", output.audioNonzeroSamples},
                       {"outputAudioPeakLinear", {output.outputPeak[0], output.outputPeak[1]}},
                       {"videoBufferedAtStop", output.videoBuffered},
                       {"audioBufferedAtStop", output.audioBuffered},
                       {"physicalSignalVerified", "Unknown"}};
        if (audioReader)
            result["audioBridge"] = {{"renderedFrames", audioReader->renderedFrames},
                                     {"staleBlocks", audioReader->staleBlocks},
                                     {"overruns", audioReader->overruns},
                                     {"underruns", audioReader->underruns},
                                     {"generationResets", audioReader->resets},
                                     {"bufferFrames", audioReader->bufferFrames},
                                     {"correctionPpm", audioReader->correctionPpm},
                                     {"maxAbsCorrectionPpm", audioReader->maxAbsCorrectionPpm}};
        std::cout << result.dump(2) << '\n';
        return output.late || output.dropped ? 2 : 0;
    }
    catch (const std::exception &e)
    {
        std::cerr << e.what() << '\n';
        return 1;
    }
}
