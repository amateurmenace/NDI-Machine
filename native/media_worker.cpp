#include "sdi_engine.h"
#include "pipe_server.h"
#include <thread>
#include <deque>
#include <iomanip>

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
    std::string source, operation;
    uint64_t revision{};
    int64_t lastObserved{}, lastCount{}, lastArrival{-1}; // control thread until commit
    std::atomic<int> references{0};
    explicit Route(std::string name) : receiver(std::make_unique<Receiver>(name)), source(std::move(name)), operation(operation_id()) {}
};
static std::unique_ptr<Route> prepare_route(const std::string& source)
{
    if (source.empty() || source.size() > 512) throw std::runtime_error("Exact NDI source name required (1..512 UTF-8 bytes)");
    auto route = std::make_unique<Route>(source);
    const auto finish = Clock::now() + std::chrono::seconds(10);
    while (Clock::now() < finish) {
        NDIlib_video_frame_v2_t frame{};
        NDIlib_framesync_capture_video(route->receiver->sync, &frame);
        const bool valid = usable(frame);
        NDIlib_framesync_free_video(route->receiver->sync, &frame);
        NDIlib_recv_performance_t received{};
        NDIlib_recv_get_performance(route->receiver->rx, &received, nullptr);
        if (valid && received.video_frames > 0) {
            route->lastObserved = route->lastArrival = milliseconds(); route->lastCount = received.video_frames;
            return route;
        }
        Sleep(10);
    }
    throw std::runtime_error("Source preparation timed out: require received progressive 1080p29.97 UYVY; existing output preserved");
}
class WorkerControl final : public EngineHooks
{
    int slot_;
    std::string device_, endpoint_, epoch_{operation_id()};
    nm::audio::Shared& audio_;
    std::vector<std::unique_ptr<Route>> owned_; // control thread only; bounded to eight routes.
    Route* prepared_{};
    Route* scheduled_{}; // media thread only after construction
    std::atomic<Route*> pending_{}, displayed_{}, metricsRoute_{};
    std::atomic<int64_t> videoArrival_{-1}, audioArrival_{-1}, lastCompletion_{-1};
    std::atomic<bool> videoGood_{false}, audioGood_{false}, displayGood_{false};
    std::atomic<uint64_t> completed_{}, late_{}, dropped_{}, fallback_{}, repeats_{};
    std::atomic<float> gain_{1};
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
    WorkerControl(int slot, std::string device, std::string endpoint, nm::audio::Shared& audio, std::unique_ptr<Route> route)
        : slot_(slot), device_(std::move(device)), endpoint_(std::move(endpoint)), audio_(audio), scheduled_(route.get())
    {
        route->revision = 1; route->references = 1; owned_.push_back(std::move(route));
    }
    ~WorkerControl() override = default;
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
    float gainTarget() const noexcept override { return gain_.load(); }
    bool stopRequested() const noexcept override { return stop_.load(); }
    void requestStop() { stop_ = true; }
    json state()
    {
        const auto now = milliseconds(); auto route = displayed_.load();
        auto age = [now](int64_t stamp) { return stamp < 0 || now < stamp ? int64_t(2147483647) : now - stamp; };
        const auto audioStamp = nm::audio::load(audio_.lastPacketQpc), frequency = nm::audio::load(audio_.frequency);
        const auto audioAge = frequency > 0 && audioStamp > 0 ? (nm::qpc() - audioStamp) * 1000 / frequency : int64_t(2147483647);
        const bool freshAudio = nm::audio::fresh(audio_, nm::qpc()) && audioGood_.load();
        const bool healthy = !stop_ && route && route == metricsRoute_.load() && age(lastCompletion_) < 500 &&
            age(videoArrival_) < 500 && videoGood_ && displayGood_ && freshAudio && !late_ && !dropped_;
        return {{"slot", slot_}, {"deviceId", device_}, {"source", route ? route->source : ""}, {"backend", "hardware"},
            {"workerInstanceId", epoch_}, {"running", route != nullptr && !stop_}, {"configRevision", route ? route->revision : 0},
            {"committedOperationId", route ? route->operation : ""}, {"videoAgeMs", age(videoArrival_)}, {"scheduleAgeMs", age(lastCompletion_)},
            {"fallback", !videoGood_.load() || !displayGood_.load()}, {"format", "1920x1080p30000/1001"},
            {"health", healthy ? "healthy" : "not-ready"}, {"audio", {{"mode", "fixed"}, {"endpointId", endpoint_},
                {"channels", {1,2}}, {"ageMs", freshAudio ? audioAge : int64_t(2147483647)}, {"muted", gain_ == 0}}},
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
            auto candidate = prepare_route(request.at("source").get<std::string>());
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
        int slot{}; std::string deviceId, source, endpoint;
        for (int i = 1; i < argc; ++i) {
            const std::string arg = argv[i];
            if (i + 1 >= argc) throw std::runtime_error("Worker argument value missing");
            if (arg == "--slot") slot = std::stoi(argv[++i]);
            else if (arg == "--device") deviceId = argv[++i];
            else if (arg == "--source") source = argv[++i];
            else if (arg == "--audio-endpoint") endpoint = argv[++i];
            else throw std::runtime_error("Unknown worker argument");
        }
        if (slot < 1 || slot > 4 || deviceId.empty() || source.empty() || endpoint.empty()) throw std::runtime_error("Explicit slot, stable DeckLink ID, source and audio endpoint required");
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
        nm::audio::Mapping audioMapping(false);
        auto audioReader = std::make_unique<nm::audio::Reader>(audioMapping.get(), endpoint);
        auto route = prepare_route(source);
        if (!audioReader->ready()) throw std::runtime_error("Exact external audio host is not fresh and primed");
        WorkerControl control(slot, deviceId, endpoint, audioMapping.get(), std::move(route));
        std::jthread server([&]{ control.serve(); });
        struct StopServer { WorkerControl& control; ~StopServer(){ SetEvent(control.controlStop); } } stopServer{control};
        Output output(chosen.Get(), slot, deviceId, false, nullptr, audioReader.get(), &control);
        output.run(0);
        return 0;
    } catch (const std::exception& error) { std::cerr << error.what() << '\n'; return 1; }
}
