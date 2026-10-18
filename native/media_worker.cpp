#include "sdi_engine.h"
#include "pipe_server.h"
#include "preview.h"
#include <thread>
#include <deque>
#include <iomanip>
#include <fstream>

static int64_t milliseconds()
{
    return std::chrono::duration_cast<std::chrono::milliseconds>(Clock::now().time_since_epoch()).count();
}
static std::string operation_id()
{
    GUID id{}; check(CoCreateGuid(&id), "Operation ID");
    wchar_t text[40]{}; StringFromGUID2(id, text, 40); return nm::utf8(text);
}
struct Route
{
    std::unique_ptr<Receiver> receiver;
    nm::OutputSettings settings;
    std::unique_ptr<nm::audio::Mapping> mapping;
    std::unique_ptr<nm::audio::Reader> audioReader;
    std::string source, operation;
    uint64_t revision{};
    int64_t lastObserved{}, lastCount{}, lastArrival{-1}; // control thread until commit
    std::atomic<int> references{0};
    explicit Route(nm::OutputSettings configuration) : settings(std::move(configuration)), source(settings.source), operation(operation_id()) {
        receiver=std::make_unique<Receiver>(source,settings.url,settings.bandwidth);
        if(settings.audioMode=="fixed") {
            const auto name=settings.audioKey.empty() ? std::wstring(nm::audio::default_mapping) :
                L"Local\\NDIMachine.Audio." + std::wstring(settings.audioKey.begin(),settings.audioKey.end());
            mapping=std::make_unique<nm::audio::Mapping>(false,name.c_str());
            audioReader=std::make_unique<nm::audio::Reader>(mapping->get(),settings.endpoint);
        }
    }
};
static std::unique_ptr<Route> prepare_route(const nm::OutputSettings& settings)
{
    auto route = std::make_unique<Route>(settings);
    const auto finish = Clock::now() + std::chrono::seconds(10);
    while (Clock::now() < finish) {
        NDIlib_video_frame_v2_t frame{};
        NDIlib_framesync_capture_video(route->receiver->sync, &frame);
        const bool valid = usable(frame);
        NDIlib_framesync_free_video(route->receiver->sync, &frame);
        NDIlib_recv_performance_t received{};
        NDIlib_recv_get_performance(route->receiver->rx, &received, nullptr);
        bool ndiAudioReady=true;
        if(settings.audioMode=="ndi") {
            NDIlib_audio_frame_v2_t audio{};
            NDIlib_framesync_capture_audio(route->receiver->sync,&audio,48000,0,1602);
            ndiAudioReady=received.audio_frames>0 && audio.p_data && audio.no_channels>=std::max(settings.left,settings.right);
            if(audio.p_data)NDIlib_framesync_free_audio(route->receiver->sync,&audio);
        }
        if (valid && received.video_frames > 0 && ndiAudioReady && (!route->audioReader || route->audioReader->ready())) {
            route->lastObserved = route->lastArrival = milliseconds(); route->lastCount = received.video_frames;
            return route;
        }
        Sleep(10);
    }
    throw std::runtime_error("Preparation timed out: require fresh decodable NDI video and selected Windows audio; existing output preserved");
}
class WorkerControl final : public EngineHooks
{
    int slot_;
    std::string device_, epoch_{operation_id()};
    nm::preview::Writer preview_;
    std::vector<std::unique_ptr<Route>> owned_; // control thread only; bounded to eight routes.
    Route* prepared_{};
    Route* scheduled_{}; // media thread only after construction
    std::atomic<Route*> pending_{}, displayed_{}, metricsRoute_{};
    std::atomic<int64_t> videoArrival_{-1}, audioArrival_{-1}, lastCompletion_{-1};
    std::atomic<bool> videoGood_{false}, audioGood_{false}, displayGood_{false};
    std::atomic<uint64_t> completed_{}, late_{}, dropped_{}, fallback_{}, repeats_{};
    std::atomic<float> gain_{1};
    std::atomic<float> peakL_{},peakR_{};
    std::atomic<double> bufferFrames_{},correctionPpm_{};
    std::atomic<uint64_t> clips_{};
    std::atomic<int> width_{},height_{},fpsN_{},fpsD_{},fourcc_{};
    std::atomic<bool> stop_{false};
    struct Cached { std::string request; json reply; };
    std::deque<std::pair<std::string, Cached>> operations_; // control thread only, max 64
    void collect()
    {
        if (prepared_) {
            NDIlib_recv_performance_t received{};
            NDIlib_recv_get_performance(prepared_->receiver->rx, &received, nullptr);
            const auto now = milliseconds();
            if (now - prepared_->lastObserved > 500) prepared_->lastArrival = -1;
            else if (received.video_frames != prepared_->lastCount) prepared_->lastArrival = now;
            prepared_->lastCount = received.video_frames; prepared_->lastObserved = now;
        }
        std::erase_if(owned_, [&](const auto& route) { return route.get() != prepared_ && route->references.load() == 0; });
    }
public:
    Handle controlStop{CreateEventW(nullptr, TRUE, FALSE, nullptr)};
    WorkerControl(int slot, std::string device, std::unique_ptr<Route> route)
        : slot_(slot), device_(std::move(device)),
          preview_(L"Local\\NDIMachine.Preview." + std::to_wstring(slot) + L"." + std::wstring(epoch_.begin(), epoch_.end())),
          scheduled_(route.get())
    {
        route->revision = 1; route->references = 1; owned_.push_back(std::move(route));
    }
    ~WorkerControl() override = default;
    void preview(const NDIlib_video_frame_v2_t& frame, bool good) noexcept override {
        preview_.publish(frame.p_data, frame.line_stride_in_bytes, good, scheduled_->revision);
    }
    Receiver* beginFrame(Frame& frame) noexcept override
    {
        if (auto next = pending_.exchange(nullptr)) {
            scheduled_->references.fetch_sub(1); scheduled_ = next;
        }
        scheduled_->references.fetch_add(1); frame.routeContext = scheduled_;
        return scheduled_->receiver.get();
    }
    void completedFrame(Frame& frame, bool shown) noexcept override
    {
        if (shown) {
            auto route = static_cast<Route*>(frame.routeContext);
            route->references.fetch_add(1);
            if (auto previous = displayed_.exchange(route)) previous->references.fetch_sub(1);
            displayGood_ = frame.videoGood;
            lastCompletion_ = milliseconds();
        }
        releaseFrame(frame);
    }
    void releaseFrame(Frame& frame) noexcept override
    {
        if (auto route = static_cast<Route*>(frame.routeContext)) { frame.routeContext = nullptr; route->references.fetch_sub(1); }
    }
    void metrics(int64_t videoMs, int64_t audioMs, bool videoGood, bool audioGood, uint64_t completed,
        uint64_t late, uint64_t dropped, uint64_t fallback, uint64_t repeats) noexcept override
    {
        videoArrival_ = videoMs; audioArrival_ = audioMs; videoGood_ = videoGood; audioGood_ = audioGood;
        completed_ = completed; late_ = late; dropped_ = dropped; fallback_ = fallback; repeats_ = repeats;
        metricsRoute_ = scheduled_;
    }
    const nm::OutputSettings* settings() const noexcept override { return &scheduled_->settings; }
    nm::audio::Reader* audioReader() noexcept override { return scheduled_->audioReader.get(); }
    float gainTarget() const noexcept override {
        const auto& s=scheduled_->settings;
        return s.muted || s.audioMode=="silent" ? 0.0f : static_cast<float>(std::pow(10,s.gainDb/20));
    }
    void details(const NDIlib_video_frame_v2_t& frame, float left, float right, uint64_t clips, double buffer, double correction) noexcept override {
        width_=frame.xres; height_=frame.yres; fpsN_=frame.frame_rate_N; fpsD_=frame.frame_rate_D; fourcc_=frame.FourCC;
        peakL_=left; peakR_=right; clips_+=clips; bufferFrames_=buffer; correctionPpm_=correction;
    }
    bool stopRequested() const noexcept override { return stop_.load(); }
    void requestStop() { stop_ = true; }
    json state()
    {
        const auto now = milliseconds(); auto route = displayed_.load();
        auto age = [now](int64_t stamp) { return stamp < 0 || now < stamp ? int64_t(2147483647) : now - stamp; };
        const auto& settings = (route ? route : scheduled_)->settings;
        int64_t audioAge=age(audioArrival_); bool freshAudio=audioGood_.load();
        if (settings.audioMode=="silent") {audioAge=0;freshAudio=true;}
        else if (route && route->mapping) {
            const auto& audio=route->mapping->get();
            const auto stamp=nm::audio::load(audio.lastPacketQpc), frequency=nm::audio::load(audio.frequency);
            audioAge=frequency>0 && stamp>0 ? (nm::qpc()-stamp)*1000/frequency : int64_t(2147483647);
            freshAudio=freshAudio && nm::audio::fresh(audio,nm::qpc());
        }
        NDIlib_recv_performance_t received{}, dropped{}; NDIlib_recv_queue_t queues{};
        int connections{};
        if(route) {
            NDIlib_recv_get_performance(route->receiver->rx,&received,&dropped);
            NDIlib_recv_get_queue(route->receiver->rx,&queues);
            connections=NDIlib_recv_get_no_connections(route->receiver->rx);
        }
        const bool healthy = !stop_ && route && route == metricsRoute_.load() && age(lastCompletion_) < 500 &&
            age(videoArrival_) < 500 && videoGood_ && displayGood_ && freshAudio && !late_ && !dropped_;
        return {{"slot", slot_}, {"deviceId", device_}, {"source", route ? route->source : ""}, {"backend", "hardware"},
            {"workerInstanceId", epoch_}, {"previewMapping", nm::utf8(preview_.name.c_str())},
            {"running", route != nullptr && !stop_}, {"configRevision", route ? route->revision : 0},
            {"committedOperationId", route ? route->operation : ""}, {"videoAgeMs", age(videoArrival_)}, {"scheduleAgeMs", age(lastCompletion_)},
            {"fallback", !videoGood_.load() || !displayGood_.load()}, {"format", "1920x1080p30000/1001"},
            {"settings",settings.json()}, {"capabilities",{"output-settings","preview"}},
            {"health", healthy ? "healthy" : "not-ready"}, {"audio", {{"mode", settings.audioMode}, {"endpointId", settings.endpoint},
                {"channels", {settings.left,settings.right}}, {"ageMs", freshAudio ? audioAge : int64_t(2147483647)},
                {"muted", settings.muted || settings.audioMode=="silent"},{"gainDb",settings.gainDb},{"delayMs",settings.delayMs},
                {"peak",{peakL_.load(),peakR_.load()}},{"clippedSamples",clips_.load()},
                {"bufferMs",bufferFrames_.load()/48},{"clockCorrectionPpm",correctionPpm_.load()}}},
            {"ndi",{{"width",width_.load()},{"height",height_.load()},{"fpsN",fpsN_.load()},{"fpsD",fpsD_.load()},
                {"fourCC",fourcc_.load()},{"connections",connections},{"videoReceived",received.video_frames},
                {"audioReceived",received.audio_frames},{"videoDropped",dropped.video_frames},{"audioDropped",dropped.audio_frames},
                {"videoQueue",queues.video_frames},{"audioQueue",queues.audio_frames},{"bandwidth",settings.bandwidth}}},
            {"counters", {{"completed", completed_.load()}, {"late", late_.load()}, {"dropped", dropped_.load()},
                {"fallback", fallback_.load()}, {"inputRepeats", repeats_.load()}}}, {"physicalSignalVerified", "Unknown"}};
    }
    json handle(const json& request)
    {
        const auto method = request.value("method", ""); const auto id = request.at("id").get<std::string>();
        if (id.empty() || id.size() > 128) throw std::runtime_error("Bounded request ID required");
        if (method == "state") return {{"ok", true}, {"state", state()}};
        const auto canonical = request.dump();
        for (const auto& [key, value] : operations_) if (key == id) {
            if (value.request != canonical) throw std::runtime_error("Idempotency key reused with a different request");
            return value.reply;
        }
        json result;
        if (method == "stop") { requestStop(); result = {{"ok", true}, {"accepted", true}}; }
        else if (method == "prepare") {
            if (pending_.load()) throw std::runtime_error("A source commit is in flight");
            auto configuration = request.contains("settings") ? nm::OutputSettings::parse(request.at("settings")) : (displayed_.load() ? displayed_.load() : scheduled_)->settings;
            if (!request.contains("settings")) { configuration.source=request.at("source").get<std::string>(); configuration.url=""; }
            auto candidate = prepare_route(configuration);
            prepared_ = nullptr; collect();
            if (owned_.size() >= 8) throw std::runtime_error("Route preparation capacity exhausted");
            prepared_ = candidate.get();
            result = {{"ok", true}, {"preparedOperationId", candidate->operation}, {"source", candidate->source}};
            owned_.push_back(std::move(candidate));
        } else if (method == "commit") {
            collect();
            auto active = displayed_.load();
            if (!active || active != metricsRoute_.load() || pending_.load()) throw std::runtime_error("Output is not stable for commit");
            if (request.at("expectedRevision").get<uint64_t>() != active->revision) throw std::runtime_error("Configuration revision conflict");
            if (!prepared_ || request.at("preparedOperationId") != prepared_->operation) throw std::runtime_error("Prepared operation no longer available");
            if (prepared_->lastArrival < 0 || milliseconds() - prepared_->lastArrival > 500) throw std::runtime_error("Prepared source arrivals are stale; existing route preserved");
            // Revalidate receipt immediately before handing the receiver to the
            // media thread. A rejected preparation never touches the old route.
            NDIlib_video_frame_v2_t frame{};
            NDIlib_framesync_capture_video(prepared_->receiver->sync, &frame);
            const bool valid = usable(frame); NDIlib_framesync_free_video(prepared_->receiver->sync, &frame);
            if (!valid) throw std::runtime_error("Prepared source lost its compatible video before commit");
            prepared_->revision = active->revision + 1;
            prepared_->references.fetch_add(1);
            result = {{"ok", true}, {"accepted", true}, {"operationId", prepared_->operation}, {"revision", prepared_->revision}};
            pending_.store(prepared_); prepared_ = nullptr;
        } else throw std::runtime_error("Unknown worker method");
        operations_.push_back({id, {canonical, result}});
        if (operations_.size() > 64) operations_.pop_front();
        return result;
    }
    void serve()
    {
        try {
            nm::ComScope com;
            nm::PipeServer server(L"\\\\.\\pipe\\NDIMachine.output." + std::to_wstring(slot_) + L".v1");
            server.serve(controlStop, [&](const json& request) { return handle(request); }, [&] { collect(); });
        } catch (const std::exception& error) { std::cerr << error.what() << '\n'; requestStop(); }
    }
};
int main(int argc, char** argv)
{
    try {
        int slot{}; std::string deviceId, source, endpoint, configPath;
        for (int i = 1; i < argc; ++i) {
            const std::string arg = argv[i];
            if (i + 1 >= argc) throw std::runtime_error("Worker argument value missing");
            if (arg == "--slot") slot = std::stoi(argv[++i]);
            else if (arg == "--device") deviceId = argv[++i];
            else if (arg == "--source") source = argv[++i];
            else if (arg == "--audio-endpoint") endpoint = argv[++i];
            else if (arg == "--config") configPath = argv[++i];
            else throw std::runtime_error("Unknown worker argument");
        }
        if (slot < 1 || slot > 4 || deviceId.empty()) throw std::runtime_error("Explicit slot and stable DeckLink ID required");
        nm::OutputSettings settings;
        if (!configPath.empty()) {
            std::ifstream stream(configPath); json configuration; stream >> configuration;
            settings=nm::OutputSettings::parse(configuration);
        } else settings=nm::OutputSettings::parse({{"source",source},{"audioMode","fixed"},{"audioEndpoint",endpoint}});
        Com com;
        Ownership slotOwner(L"Global\\NDIMachine.slot." + std::to_wstring(slot));
        size_t used{}; const auto selected = std::stoull(deviceId, &used, 0);
        if (used != deviceId.size()) throw std::runtime_error("Invalid DeckLink identity");
        Ownership deviceOwner(L"Global\\NDIMachine.device." + std::to_wstring(selected));
        ComPtr<IDeckLinkIterator> iterator; check(CoCreateInstance(CLSID_CDeckLinkIterator, nullptr, CLSCTX_ALL, IID_PPV_ARGS(&iterator)), "DeckLink iterator");
        ComPtr<IDeckLink> device, chosen;
        while (iterator->Next(&device) == S_OK) {
            ComPtr<IDeckLinkProfileAttributes> attributes; LONGLONG persistent{};
            if (SUCCEEDED(device.As(&attributes)) && SUCCEEDED(attributes->GetInt(BMDDeckLinkPersistentID, &persistent)) && static_cast<uint64_t>(persistent) == selected) {
                if (chosen) throw std::runtime_error("DeckLink identity ambiguous"); chosen = device;
            }
            device.Reset();
        }
        if (!chosen) throw std::runtime_error("Configured DeckLink missing; no substitution");
        if (!NDIlib_initialize()) throw std::runtime_error("NDI runtime unavailable");
        struct Runtime { ~Runtime(){ NDIlib_destroy(); } } runtime;
        auto route = prepare_route(settings);
        WorkerControl control(slot, deviceId, std::move(route));
        std::jthread server([&]{ control.serve(); });
        struct StopServer { WorkerControl& control; ~StopServer(){ SetEvent(control.controlStop); } } stopServer{control};
        Output output(chosen.Get(), slot, deviceId, false, nullptr, nullptr, &control);
        output.run(0);
        return 0;
    } catch (const std::exception& error) { std::cerr << error.what() << '\n'; return 1; }
}
