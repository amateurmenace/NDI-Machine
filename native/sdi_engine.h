#pragma once
// Shared scheduled-output engine. Control code runs outside the hardware callbacks.
// Physical observation is still required; SDK completion is not signal proof.
#define NOMINMAX
#include <Windows.h>
#include <avrt.h>
#include <wrl/client.h>
#include <Processing.NDI.Lib.h>
#include <nlohmann/json.hpp>
#include "DeckLinkAPI.h"
#include "media_core.h"
#include "audio_reader.h"
#include <array>
#include <atomic>
#include <chrono>
#include <iostream>
#include <memory>
#include <string>
#include <vector>
#include <sstream>
using Microsoft::WRL::ComPtr;
using json = nlohmann::json;
using Clock = std::chrono::steady_clock;
static void check(HRESULT hr, const char *step)
{
    if (FAILED(hr))
    {
        std::ostringstream s;
        s << step << " HRESULT=0x" << std::hex << static_cast<unsigned long>(hr);
        throw std::runtime_error(s.str());
    }
}
struct Handle
{
    HANDLE p{};
    explicit Handle(HANDLE value) : p(value)
    {
    }
    ~Handle()
    {
        if (p && p != INVALID_HANDLE_VALUE)
            CloseHandle(p);
    }
    Handle(const Handle &) = delete;
    operator HANDLE() const
    {
        return p;
    }
};
struct Com
{
    Com()
    {
        check(CoInitializeEx(nullptr, COINIT_MULTITHREADED), "COM");
    }
    ~Com()
    {
        CoUninitialize();
    }
};
struct Ownership
{
    Handle mutex;
    explicit Ownership(const std::wstring &name) : mutex(CreateMutexW(nullptr, FALSE, name.c_str()))
    {
        if (!mutex.p)
            throw std::runtime_error("Ownership mutex creation failed");
        const auto r = WaitForSingleObject(mutex, 0);
        if (r != WAIT_OBJECT_0 && r != WAIT_ABANDONED)
            throw std::runtime_error("Output/slot already owned by NDI Machine");
    }
    ~Ownership()
    {
        ReleaseMutex(mutex);
    }
};
struct Completed
{
    IDeckLinkVideoFrame *frame{};
    BMDOutputFrameCompletionResult result{};
};
class Callbacks final : public IDeckLinkVideoOutputCallback, public IDeckLinkAudioOutputCallback
{
    std::atomic<ULONG> refs_{1};

  public:
    Handle wake{CreateEventW(nullptr, FALSE, FALSE, nullptr)},
        stopped{CreateEventW(nullptr, TRUE, FALSE, nullptr)};
    nm::Spsc<Completed, 16> completions;
    std::atomic<uint64_t> overflow{0};
    HRESULT STDMETHODCALLTYPE QueryInterface(REFIID iid, void **out) override
    {
        if (!out)
            return E_POINTER;
        *out = nullptr;
        if (iid == IID_IUnknown || iid == IID_IDeckLinkVideoOutputCallback)
            *out = static_cast<IDeckLinkVideoOutputCallback *>(this);
        else if (iid == IID_IDeckLinkAudioOutputCallback)
            *out = static_cast<IDeckLinkAudioOutputCallback *>(this);
        else
            return E_NOINTERFACE;
        AddRef();
        return S_OK;
    }
    ULONG STDMETHODCALLTYPE AddRef() override
    {
        return ++refs_;
    }
    ULONG STDMETHODCALLTYPE Release() override
    {
        auto n = --refs_;
        if (!n)
            delete this;
        return n;
    }
    HRESULT STDMETHODCALLTYPE ScheduledFrameCompleted(IDeckLinkVideoFrame *frame,
                                                      BMDOutputFrameCompletionResult result) override
    {
        if (!completions.push({frame, result}))
            ++overflow;
        SetEvent(wake);
        return S_OK;
    }
    HRESULT STDMETHODCALLTYPE ScheduledPlaybackHasStopped() override
    {
        SetEvent(stopped);
        SetEvent(wake);
        return S_OK;
    }
    HRESULT STDMETHODCALLTYPE RenderAudioSamples(BOOL) override
    {
        SetEvent(wake);
        return S_OK;
    }
};
struct Receiver
{
    NDIlib_recv_instance_t rx{};
    NDIlib_framesync_instance_t sync{};
    explicit Receiver(const std::string &name)
    {
        NDIlib_recv_create_v3_t c{};
        c.source_to_connect_to.p_ndi_name = name.c_str();
        c.color_format = NDIlib_recv_color_format_UYVY_BGRA;
        c.allow_video_fields = true;
        c.p_ndi_recv_name = "NDI Machine SDI qualification";
        rx = NDIlib_recv_create_v3(&c);
        if (!rx)
            throw std::runtime_error("Receiver creation failed");
        sync = NDIlib_framesync_create(rx);
        if (!sync)
        {
            NDIlib_recv_destroy(rx);
            throw std::runtime_error("Frame sync creation failed");
        }
    }
    ~Receiver()
    {
        if (sync)
            NDIlib_framesync_destroy(sync);
        if (rx)
            NDIlib_recv_destroy(rx);
    }
    Receiver(const Receiver &) = delete;
};
static bool usable(const NDIlib_video_frame_v2_t &v)
{
    return v.p_data && v.xres == 1920 && v.yres == 1080 && v.FourCC == NDIlib_FourCC_type_UYVY &&
           v.line_stride_in_bytes >= 3840 && v.frame_format_type == NDIlib_frame_format_type_progressive &&
           v.frame_rate_N == 30000 && v.frame_rate_D == 1001;
}
// Render and convert the labelled SDR Rec.709 test pattern once, before playback.
static std::vector<uint8_t> pattern(int slot, const std::string &device, bool bars)
{
    std::vector<uint8_t> result(1920 * 1080 * 2);
    BITMAPINFO info{};
    info.bmiHeader.biSize = sizeof(BITMAPINFOHEADER);
    info.bmiHeader.biWidth = 1920;
    info.bmiHeader.biHeight = -1080;
    info.bmiHeader.biPlanes = 1;
    info.bmiHeader.biBitCount = 32;
    info.bmiHeader.biCompression = BI_RGB;
    void *pixels{};
    HDC dc = CreateCompatibleDC(nullptr);
    HBITMAP bitmap = CreateDIBSection(dc, &info, DIB_RGB_COLORS, &pixels, nullptr, 0);
    if (!dc || !bitmap || !pixels)
    {
        if (bitmap)
            DeleteObject(bitmap);
        if (dc)
            DeleteDC(dc);
        throw std::runtime_error("Test pattern allocation failed");
    }
    auto previous = SelectObject(dc, bitmap);
    auto *rgb = static_cast<uint32_t *>(pixels);
    constexpr uint32_t colors[] = {0xbfbfbf, 0xbfbf00, 0x00bfbf, 0x00bf00,
                                   0xbf00bf, 0xbf0000, 0x0000bf, 0x000000};
    for (int y = 0; y < 1080; ++y)
        for (int x = 0; x < 1920; ++x)
            rgb[y * 1920 + x] = bars ? colors[x / 240] : 0;
    if (bars)
    {
        HFONT font =
            CreateFontW(66, 0, 0, 0, FW_BOLD, FALSE, FALSE, FALSE, DEFAULT_CHARSET, OUT_DEFAULT_PRECIS,
                        CLIP_DEFAULT_PRECIS, ANTIALIASED_QUALITY, DEFAULT_PITCH, L"Segoe UI");
        auto oldFont = SelectObject(dc, font);
        SetTextColor(dc, RGB(255, 255, 255));
        SetBkColor(dc, RGB(0, 0, 0));
        std::string label = "NDI MACHINE " + std::to_string(slot) + "   " + device + "   1080p 29.97";
        TextOutA(dc, 80, 920, label.c_str(), static_cast<int>(label.size()));
        SelectObject(dc, oldFont);
        DeleteObject(font);
    }
    auto component = [](uint32_t p, int shift) { return static_cast<double>((p >> shift) & 255) / 255; };
    auto quant = [](double v) { return static_cast<uint8_t>(std::clamp(std::lround(v), 0L, 255L)); };
    for (size_t i = 0; i < 1920 * 1080; i += 2)
    {
        double r1 = component(rgb[i], 16), g1 = component(rgb[i], 8), b1 = component(rgb[i], 0),
               r2 = component(rgb[i + 1], 16), g2 = component(rgb[i + 1], 8), b2 = component(rgb[i + 1], 0);
        double y1 = .2126 * r1 + .7152 * g1 + .0722 * b1, y2 = .2126 * r2 + .7152 * g2 + .0722 * b2;
        result[i * 2] = quant(128 + 112 * ((b1 - y1 + b2 - y2) / 1.8556));
        result[i * 2 + 1] = quant(16 + 219 * y1);
        result[i * 2 + 2] = quant(128 + 112 * ((r1 - y1 + r2 - y2) / 1.5748));
        result[i * 2 + 3] = quant(16 + 219 * y2);
    }
    SelectObject(dc, previous);
    DeleteObject(bitmap);
    DeleteDC(dc);
    return result;
}
struct Frame
{
    ComPtr<IDeckLinkMutableVideoFrame> video;
    ComPtr<IDeckLinkVideoBuffer> buffer;
    std::array<int32_t, 1602 * 2> audio{};
    void* routeContext{};
    bool videoGood{}, audioGood{};
};
struct EngineHooks {
    virtual ~EngineHooks() = default;
    virtual Receiver* beginFrame(Frame& frame) noexcept = 0;
    virtual void completedFrame(Frame& frame, bool displayed) noexcept = 0;
    virtual void releaseFrame(Frame& frame) noexcept = 0;
    virtual void metrics(int64_t videoArrivalMs, int64_t audioArrivalMs, bool videoGood, bool audioGood,
        uint64_t completed, uint64_t late, uint64_t dropped, uint64_t fallback, uint64_t repeats) noexcept = 0;
    virtual float gainTarget() const noexcept = 0;
    virtual bool stopRequested() const noexcept = 0;
};
class Output
{
    ComPtr<IDeckLinkOutput> output_;
    ComPtr<Callbacks> callback_;
    std::array<Frame, 4> pool_;
    bool videoEnabled_{}, audioEnabled_{}, started_{};
    std::vector<uint8_t> base_;
    Receiver *rx_;
    int rowBytes_{};
    int64_t nextFrame_{};
    bool bars_;
    nm::Freshness videoFresh_, audioFresh_;
    int64_t previousTimestamp_{INT64_MIN};
    nm::Ramp audioRamp_;
    nm::audio::Reader *externalAudio_{};
    std::array<nm::audio::Stereo, 1602> externalPcm_{};
    std::array<float, 2> lastAudio_{};
    EngineHooks* hooks_{};

  public:
    uint64_t completed{}, late{}, dropped{}, flushed{}, fallbackFrames{}, inputRepeats{};
    uint32_t videoBuffered{}, audioBuffered{};
    uint64_t lastVideoFrames{}, lastAudioFrames{};
    uint64_t audioFallbackBlocks{}, audioNonzeroSamples{};
    double outputPeak[2]{};
    Output(IDeckLink *device, int slot, const std::string &id, bool bars, Receiver *rx,
           nm::audio::Reader *externalAudio, EngineHooks* hooks = nullptr)
        : base_(pattern(slot, id, bars)), rx_(rx), bars_(bars), externalAudio_(externalAudio), hooks_(hooks)
    {
        check(device->QueryInterface(IID_PPV_ARGS(&output_)), "Output interface");
        callback_.Attach(new Callbacks());
        BOOL supported{};
        BMDDisplayMode actual{};
        check(output_->DoesSupportVideoMode(bmdVideoConnectionSDI, bmdModeHD1080p2997, bmdFormat8BitYUV,
                                            bmdNoVideoOutputConversion, bmdSupportedVideoModeDefault, &actual,
                                            &supported),
              "Mode query");
        if (!supported || actual != bmdModeHD1080p2997)
            throw std::runtime_error("1080p29.97 UYVY unsupported on selected output");
        ComPtr<IDeckLinkStatus> status;
        if (SUCCEEDED(device->QueryInterface(IID_PPV_ARGS(&status))))
        {
            LONGLONG busy{};
            if (SUCCEEDED(status->GetInt(bmdDeckLinkStatusBusy, &busy)) && busy != 0)
                throw std::runtime_error("Selected device is in use; occupying process unknown");
        }
        check(output_->RowBytesForPixelFormat(bmdFormat8BitYUV, 1920, &rowBytes_), "Row stride");
        for (auto &f : pool_)
        {
            check(output_->CreateVideoFrame(1920, 1080, rowBytes_, bmdFormat8BitYUV, bmdFrameFlagDefault,
                                            &f.video),
                  "Frame allocation");
            check(f.video.As(&f.buffer), "Video buffer interface");
        }
    }
    ~Output()
    {
        stop();
        if (hooks_) for (auto& frame : pool_) hooks_->releaseFrame(frame);
    }
    void stop() noexcept
    {
        if (!output_)
            return;
        if (started_)
        {
            output_->StopScheduledPlayback(0, nullptr, 30000);
            WaitForSingleObject(callback_->stopped, 2000);
            started_ = false;
        }
        output_->SetScheduledFrameCompletionCallback(nullptr);
        output_->SetAudioCallback(nullptr);
        if (audioEnabled_)
        {
            output_->DisableAudioOutput();
            audioEnabled_ = false;
        }
        if (videoEnabled_)
        {
            output_->DisableVideoOutput();
            videoEnabled_ = false;
        }
    }
    void schedule(Frame &f)
    {
        if (hooks_) {
            auto next = hooks_->beginFrame(f);
            if (next != rx_) { rx_ = next; videoFresh_ = {}; audioFresh_ = {}; previousTimestamp_ = INT64_MIN; }
        }
        const int64_t now =
            std::chrono::duration_cast<std::chrono::milliseconds>(Clock::now().time_since_epoch()).count();
        NDIlib_video_frame_v2_t video{};
        NDIlib_audio_frame_v2_t audio{};
        const int count = nm::audio_count(nextFrame_);
        bool good = false;
        if (rx_)
        {
            NDIlib_recv_performance_t stats{};
            NDIlib_recv_get_performance(rx_->rx, &stats, nullptr);
            lastVideoFrames = stats.video_frames;
            lastAudioFrames = stats.audio_frames;
            videoFresh_.observe(stats.video_frames, now);
            audioFresh_.observe(stats.audio_frames, now);
            NDIlib_framesync_capture_video(rx_->sync, &video);
            good = usable(video) && videoFresh_.fresh(now);
            if (!externalAudio_)
                NDIlib_framesync_capture_audio(rx_->sync, &audio, 48000, 2, count);
        }
        // NDI-owned frames are always freed, including exceptions in DeckLink scheduling.
        struct Release
        {
            Receiver *rx;
            NDIlib_video_frame_v2_t &v;
            NDIlib_audio_frame_v2_t &a;
            ~Release()
            {
                if (rx)
                {
                    NDIlib_framesync_free_video(rx->sync, &v);
                    if (a.p_data) NDIlib_framesync_free_audio(rx->sync, &a);
                }
            }
        } release{rx_, video, audio};
        check(f.buffer->StartAccess(bmdBufferAccessWrite), "Begin frame access");
        void *bytes{};
        auto hr = f.buffer->GetBytes(&bytes);
        if (FAILED(hr))
        {
            f.buffer->EndAccess(bmdBufferAccessWrite);
            check(hr, "Frame bytes");
        }
        for (int row = 0; row < 1080; ++row)
        {
            auto dst = static_cast<uint8_t *>(bytes) + static_cast<size_t>(row) * rowBytes_;
            memcpy(dst,
                   good ? video.p_data + static_cast<size_t>(row) * video.line_stride_in_bytes
                        : base_.data() + static_cast<size_t>(row) * 3840,
                   3840);
            if (rowBytes_ > 3840)
                memset(dst + 3840, 0, rowBytes_ - 3840);
        }
        check(f.buffer->EndAccess(bmdBufferAccessWrite), "End frame access");
        if (!good && !bars_)
            ++fallbackFrames;
        if (good)
        {
            if (video.timestamp == previousTimestamp_)
                ++inputRepeats;
            previousTimestamp_ = video.timestamp;
        }
        const bool goodNdiAudio = rx_ && audioFresh_.fresh(now) && good && audio.p_data &&
                                  audio.no_channels == 2 && audio.no_samples == count &&
                                  audio.channel_stride_in_bytes >= count * static_cast<int>(sizeof(float));
        const bool goodAudio =
            externalAudio_ ? externalAudio_->render(externalPcm_.data(), count) : goodNdiAudio;
        if (!goodAudio && !bars_)
            ++audioFallbackBlocks;
        f.videoGood = good || bars_; f.audioGood = goodAudio || bars_;
        const float target = (bars_ || goodAudio) ? (hooks_ ? hooks_->gainTarget() : 1.0f) : 0.0f;
        if (target != audioRamp_.target)
            audioRamp_.set(target);
        for (int s = 0; s < count; ++s)
        {
            const float gain = audioRamp_.next();
            for (int ch = 0; ch < 2; ++ch)
            {
                float sample = goodAudio || bars_ ? 0 : lastAudio_[ch];
                if (bars_)
                    sample =
                        .1f * static_cast<float>(std::sin(6.283185307179586 * (ch ? 2000 : 1000) *
                                                          (nm::audio_position(nextFrame_) + s) / 48000.0));
                else if (goodAudio && externalAudio_)
                    sample = ch ? externalPcm_[s].right : externalPcm_[s].left;
                else if (goodAudio)
                {
                    auto samples = reinterpret_cast<const float *>(
                        reinterpret_cast<const uint8_t *>(audio.p_data) + ch * audio.channel_stride_in_bytes);
                    sample = nm::calibrate(samples[s], nm::Calibration::Smpte);
                }
                if (goodAudio || bars_) lastAudio_[ch] = sample;
                f.audio[s * 2 + ch] = nm::pcm32(sample * gain);
                outputPeak[ch] = std::max(outputPeak[ch], std::abs(static_cast<double>(sample * gain)));
                if (f.audio[s * 2 + ch])
                    ++audioNonzeroSamples;
            }
        }
        uint32_t offset = 0;
        while (offset < static_cast<uint32_t>(count))
        {
            uint32_t written{};
            check(output_->ScheduleAudioSamples(f.audio.data() + offset * 2, count - offset,
                                                nm::audio_position(nextFrame_) + offset, 48000, &written),
                  "Schedule audio");
            if (!written || written > static_cast<uint32_t>(count) - offset)
                throw std::runtime_error("Audio partial write made no valid progress");
            offset += written;
        }
        check(output_->ScheduleVideoFrame(f.video.Get(), nextFrame_ * 1001, 1001, 30000), "Schedule video");
        ++nextFrame_;
        if (hooks_) hooks_->metrics(videoFresh_.last_change_ms, audioFresh_.last_change_ms, f.videoGood, f.audioGood,
            completed, late, dropped, fallbackFrames, inputRepeats);
    }
    void start()
    {
        check(output_->EnableVideoOutput(bmdModeHD1080p2997, bmdVideoOutputFlagDefault),
              "Enable video (check exclusive access)");
        videoEnabled_ = true;
        check(output_->EnableAudioOutput(bmdAudioSampleRate48kHz, bmdAudioSampleType32bitInteger, 2,
                                         bmdAudioOutputStreamTimestamped),
              "Enable stereo audio");
        audioEnabled_ = true;
        check(output_->SetScheduledFrameCompletionCallback(callback_.Get()), "Video callback");
        check(output_->SetAudioCallback(callback_.Get()), "Audio callback");
        check(output_->BeginAudioPreroll(), "Begin audio preroll");
        for (auto &f : pool_)
            schedule(f);
        check(output_->GetBufferedVideoFrameCount(&videoBuffered), "Preroll video count");
        check(output_->GetBufferedAudioSampleFrameCount(&audioBuffered), "Preroll audio count");
        if (videoBuffered < 4 || audioBuffered < 6406)
            throw std::runtime_error("Insufficient preroll");
        check(output_->EndAudioPreroll(), "End audio preroll");
        check(output_->StartScheduledPlayback(0, 30000, 1.0), "Start scheduled playback");
        started_ = true;
    }
    void run(int seconds)
    {
        start();
        auto end = Clock::now() + std::chrono::seconds(seconds);
        auto lastCompletion = Clock::now();
        DWORD task{};
        HANDLE mmcss = AvSetMmThreadCharacteristicsW(L"Playback", &task);
        struct Revert
        {
            HANDLE h;
            ~Revert()
            {
                if (h)
                    AvRevertMmThreadCharacteristics(h);
            }
        } revert{mmcss};
        while ((seconds == 0 || Clock::now() < end) && (!hooks_ || !hooks_->stopRequested()))
        {
            WaitForSingleObject(callback_->wake, 100);
            if (callback_->overflow)
                throw std::runtime_error("Completion queue overflow");
            Completed c;
            while (callback_->completions.pop(c))
            {
                lastCompletion = Clock::now();
                if (c.result == bmdOutputFrameCompleted)
                    ++completed;
                else if (c.result == bmdOutputFrameDisplayedLate)
                    ++late;
                else if (c.result == bmdOutputFrameDropped)
                    ++dropped;
                else if (c.result == bmdOutputFrameFlushed)
                {
                    ++flushed;
                    throw std::runtime_error("Output unexpectedly flushed");
                }
                bool found = false;
                for (auto &f : pool_)
                    if (f.video.Get() == c.frame)
                    {
                        if (hooks_) hooks_->completedFrame(f, c.result == bmdOutputFrameCompleted);
                        schedule(f);
                        found = true;
                        break;
                    }
                if (!found)
                    throw std::runtime_error("Unknown completed frame");
            }
            if (Clock::now() - lastCompletion > std::chrono::seconds(2))
                throw std::runtime_error("No output completion for two seconds");
            check(output_->GetBufferedVideoFrameCount(&videoBuffered), "Video buffer count");
            check(output_->GetBufferedAudioSampleFrameCount(&audioBuffered), "Audio buffer count");
        }
        stop();
    }
};
