#include <Processing.NDI.Lib.h>
#include <nlohmann/json.hpp>
#include <chrono>
#include <iostream>
#include <string>
#include <thread>
#include <cmath>
#include <vector>
#include <stdexcept>
using json = nlohmann::json;
using Clock = std::chrono::steady_clock;
int main(int argc, char **argv)
{
    if (!NDIlib_initialize())
    {
        std::cerr << "NDI initialization failed; no simulation fallback\n";
        return 1;
    }
    struct Runtime
    {
        ~Runtime()
        {
            NDIlib_destroy();
        }
    } runtime;
    try
    {
        std::string target, groups, ips;
        int seconds = 10;
        for (int i = 1; i < argc; ++i)
        {
            std::string a = argv[i];
            if (a == "--source" && i + 1 < argc)
                target = argv[++i];
            else if (a == "--seconds" && i + 1 < argc)
                seconds = std::stoi(argv[++i]);
            else if (a == "--groups" && i + 1 < argc)
                groups = argv[++i];
            else if (a == "--extra-ips" && i + 1 < argc)
                ips = argv[++i];
            else
                throw std::runtime_error("Usage: ndi-probe [--source exact-discovery-name] [--seconds 1..60] "
                                         "[--groups groups] [--extra-ips comma-separated-ips]");
        }
        if (seconds < 1 || seconds > 60)
            throw std::runtime_error("seconds must be 1..60");
        NDIlib_find_create_t settings{};
        settings.show_local_sources = true;
        settings.p_groups = groups.empty() ? nullptr : groups.c_str();
        settings.p_extra_ips = ips.empty() ? nullptr : ips.c_str();
        auto finder = NDIlib_find_create_v2(&settings);
        if (!finder)
            throw std::runtime_error("NDI finder creation failed");
        struct Find
        {
            NDIlib_find_instance_t p;
            ~Find()
            {
                NDIlib_find_destroy(p);
            }
        } find{finder};
        const auto end = Clock::now() + std::chrono::seconds(3);
        while (Clock::now() < end)
            NDIlib_find_wait_for_sources(finder, 250);
        uint32_t count{};
        auto sources = NDIlib_find_get_current_sources(finder, &count);
        json result = {{"runtime", NDIlib_version()},
                       {"sources", json::array()},
                       {"codec", "Unknown"},
                       {"acceleration", "Unknown"}};
        std::string name, url;
        unsigned matches{};
        for (uint32_t i = 0; i < count; ++i)
        {
            result["sources"].push_back(
                {{"name", sources[i].p_ndi_name},
                 {"endpoint", sources[i].p_url_address ? sources[i].p_url_address : ""}});
            if (target == sources[i].p_ndi_name)
            {
                ++matches;
                name = target;
                url = sources[i].p_url_address ? sources[i].p_url_address : "";
            }
        }
        if (target.empty())
        {
            std::cout << result.dump(2) << '\n';
            return 0;
        }
        if (matches != 1)
        {
            result["error"] = matches ? "Ambiguous source name" : "Source offline or exact name not found";
            std::cout << result.dump(2) << '\n';
            return 2;
        }
        NDIlib_recv_create_v3_t settingsRx{};
        settingsRx.source_to_connect_to.p_ndi_name = name.c_str();
        settingsRx.source_to_connect_to.p_url_address = url.c_str();
        settingsRx.color_format = NDIlib_recv_color_format_UYVY_BGRA;
        settingsRx.bandwidth = NDIlib_recv_bandwidth_highest;
        settingsRx.allow_video_fields = true;
        settingsRx.p_ndi_recv_name = "NDI Machine qualification probe";
        auto rx = NDIlib_recv_create_v3(&settingsRx);
        if (!rx)
            throw std::runtime_error("NDI receiver creation failed");
        struct Rx
        {
            NDIlib_recv_instance_t p;
            ~Rx()
            {
                NDIlib_recv_destroy(p);
            }
        } receive{rx};
        int64_t videoFrames = 0, audioFrames = 0, audioSamples = 0, nonfinite = 0;
        double peak = 0;
        json format;
        auto start = Clock::now();
        auto lastVideo = start;
        auto lastAudio = start;
        while (Clock::now() - start < std::chrono::seconds(seconds))
        {
            NDIlib_video_frame_v2_t v{};
            NDIlib_audio_frame_v3_t a{};
            switch (NDIlib_recv_capture_v3(rx, &v, &a, nullptr, 100))
            {
            case NDIlib_frame_type_video:
                ++videoFrames;
                lastVideo = Clock::now();
                format = {{"width", v.xres},
                          {"height", v.yres},
                          {"fpsN", v.frame_rate_N},
                          {"fpsD", v.frame_rate_D},
                          {"frameFormat", v.frame_format_type},
                          {"fourCC", v.FourCC},
                          {"stride", v.line_stride_in_bytes}};
                NDIlib_recv_free_video_v2(rx, &v);
                break;
            case NDIlib_frame_type_audio:
                ++audioFrames;
                audioSamples += a.no_samples;
                lastAudio = Clock::now();
                result["audioFormat"] = {
                    {"sampleRate", a.sample_rate}, {"channels", a.no_channels}, {"fourCC", a.FourCC}};
                if (a.FourCC == NDIlib_FourCC_audio_type_FLTP && a.p_data &&
                    a.channel_stride_in_bytes >= a.no_samples * static_cast<int>(sizeof(float)))
                {
                    for (int ch = 0; ch < a.no_channels; ++ch)
                    {
                        auto samples =
                            reinterpret_cast<const float *>(a.p_data + ch * a.channel_stride_in_bytes);
                        for (int n = 0; n < a.no_samples; ++n)
                        {
                            if (std::isfinite(samples[n]))
                                peak = std::max(peak, std::abs(static_cast<double>(samples[n])) / 10);
                            else
                                ++nonfinite;
                        }
                    }
                }
                NDIlib_recv_free_audio_v3(rx, &a);
                break;
            default:
                break;
            }
        }
        NDIlib_recv_performance_t total{}, dropped{};
        NDIlib_recv_get_performance(rx, &total, &dropped);
        result["probe"] = {
            {"source", name},
            {"seconds", seconds},
            {"videoFrames", videoFrames},
            {"audioFrames", audioFrames},
            {"audioSamples", audioSamples},
            {"nonfiniteSamples", nonfinite},
            {"peakNormalizedSmpte", peak},
            {"format", format},
            {"videoDroppedAtReceiver", dropped.video_frames},
            {"audioDroppedAtReceiver", dropped.audio_frames},
            {"lastVideoAgeMs",
             videoFrames
                 ? std::chrono::duration_cast<std::chrono::milliseconds>(Clock::now() - lastVideo).count()
                 : -1},
            {"lastAudioAgeMs",
             audioFrames
                 ? std::chrono::duration_cast<std::chrono::milliseconds>(Clock::now() - lastAudio).count()
                 : -1}};
        std::cout << result.dump(2) << '\n';
        return videoFrames ? 0 : 3;
    }
    catch (const std::exception &e)
    {
        std::cerr << e.what() << '\n';
        return 1;
    }
}
