#include "media_core.h"
#include <iostream>
#include <thread>
#include <string>
static void require(bool value, const char *message)
{
    if (!value)
        throw std::runtime_error(message);
}
int main()
{
    try
    {
        int64_t total = 0;
        // 72h plus fractional second. Catch a constant-1602 implementation and rollover errors.
        constexpr int64_t frames = 7'768'232;
        for (int64_t f = 0; f < frames; ++f)
        {
            auto n = nm::audio_count(f);
            require(n == 1601 || n == 1602, "block count");
            total += n;
        }
        require(total == frames * 48000LL * 1001 / 30000, "72h rational accumulation");
        require(nm::audio_position(5) == 8008, "five-frame cycle");
        require(std::abs(nm::calibrate(1, nm::Calibration::Smpte) - .1f) < 1e-6f, "SMPTE -20 dBFS");
        require(std::abs(nm::calibrate(1, nm::Calibration::Ebu) - std::pow(10.f, -18.f / 20)) < 1e-6f,
                "EBU -18 dBFS");
        require(nm::calibrate(std::numeric_limits<float>::quiet_NaN(), nm::Calibration::Unity) == 0,
                "NaN contamination");
        require(nm::pcm32(2) == INT32_MAX && nm::pcm32(-2) == INT32_MIN && nm::pcm32(INFINITY) == 0,
                "clipping policy");
        nm::Ramp ramp;
        ramp.set(1, 240);
        float prior = 0;
        for (int i = 0; i < 240; ++i)
        {
            auto v = ramp.next();
            require(v >= prior && v <= 1.0001f, "ramp continuity");
            prior = v;
        }
        require(ramp.value == 1 && ramp.remaining == 0, "ramp endpoint");
        nm::Spsc<int, 4> bounded;
        require(bounded.push(1) && bounded.push(2) && bounded.push(3) && !bounded.push(4), "queue bound");
        int n{};
        require(bounded.pop(n) && n == 1, "queue retention");
        nm::Spsc<uint32_t, 64> concurrent;
        std::thread writer([&] {
            for (uint32_t i = 0; i < 200000; ++i)
                while (!concurrent.push(i))
                    std::this_thread::yield();
        });
        for (uint32_t i = 0; i < 200000; ++i)
        {
            uint32_t v{};
            while (!concurrent.pop(v))
                std::this_thread::yield();
            require(v == i, "concurrent queue ordering");
        }
        writer.join();
        nm::Freshness fresh;
        require(!fresh.fresh(0), "never received is not ready");
        fresh.observe(1, 100);
        fresh.observe(1, 1200);
        require(!fresh.fresh(1200), "repeated freeze is not fresh");
        fresh.observe(2, 1300);
        require(fresh.fresh(1301), "freshness recovery");
        std::cout << "PASS: 72h rational timeline, calibration, finite samples, clipping, gain ramps, "
                     "bounded concurrent queue, stale source recovery\n";
        return 0;
    }
    catch (const std::exception &e)
    {
        std::cerr << "FAIL: " << e.what() << '\n';
        return 1;
    }
}
