#include "audio_reader.h"
#include "media_core.h"
#include <iostream>
#include <memory>
#include <thread>
#include <vector>
void require(bool ok, const char *message)
{
    if (!ok)
        throw std::runtime_error(message);
}
int main()
{
    try
    {
        // Accelerated 30-minute clock convergence, both drift directions. No claim
        // of physical drift qualification or real-time soak is implied.
        for (const double ppm : {-500., -250., 250., 500.})
        {
            nm::audio::ClockAdapter adapter;
            double fill = 2432;
            for (int frame = 0; frame < 54000; ++frame)
            {
                const int count = nm::audio_count(frame);
                fill += count * (1 + ppm / 1e6);
                const double ratio = adapter.update(fill - count - 2432, count);
                fill -= count * ratio;
                require(fill > 1200 && fill < 3600, "Clock adapter lost bounded occupancy");
            }
            require(std::abs((adapter.ratio() - 1) * 1e6 - ppm) < 1,
                    "Clock adapter failed drift convergence");
        }
        nm::audio::Sinc sinc;
        nm::audio::Stereo constant[32];
        for (auto &sample : constant)
            sample = {.25f, -.5f};
        for (int phase = 0; phase < 1024; ++phase)
        {
            const auto sample = sinc.sample(constant, phase / 1024.0);
            require(std::abs(sample.left - .25f) < 1e-6 && std::abs(sample.right + .5f) < 1e-6,
                    "Sinc changed DC calibration");
        }
        auto shared = std::make_unique<nm::audio::Shared>();
        {
            nm::audio::Writer writer(*shared);
            for (int i = 0; i < 12000; ++i)
                writer.push({.25f, -.5f});
            writer.publish(nm::qpc());
            auto fast = std::make_unique<nm::audio::Reader>(*shared);
            auto stalled = std::make_unique<nm::audio::Reader>(*shared);
            nm::audio::Stereo output[1602];
            require(fast->ready() && stalled->ready(), "Readers not primed");
            for (int i = 0; i < 4; ++i)
            {
                require(fast->render(output, nm::audio_count(i)), "Fast reader preroll failed");
                require(stalled->render(output, nm::audio_count(i)), "Second reader preroll failed");
            }
            for (int i = 4; i < 80; ++i)
            {
                const auto count = nm::audio_count(i);
                for (int n = 0; n < count; ++n)
                    writer.push({.25f, -.5f});
                writer.publish(nm::qpc());
                require(fast->render(output, count), "Stalled reader blocked healthy reader");
                require(std::abs(output[count - 1].right + .5f) < 1e-6, "Shared PCM corrupted");
            }
            require(!stalled->render(output, 1602) && stalled->overruns == 1,
                    "Stalled reader did not report overrun");
            require(stalled->render(output, 1602), "Stalled reader failed independent recovery");
            nm::audio::save(shared->lastPacketQpc, nm::qpc() - nm::qpc_frequency());
            require(!fast->render(output, 1602), "Stale audio was accepted");
            for (const auto &sample : output)
                require(sample.left == 0 && sample.right == 0, "Stale audio not muted");
        }
        require(nm::audio::load(shared->state) == 0, "Host shutdown remained ready");
        // Racing overwrite stress: readers either see a complete stereo frame or
        // reject its sequence. A delayed reader cannot make the writer wait.
        shared = std::make_unique<nm::audio::Shared>();
        std::atomic<bool> done{false};
        std::thread writerThread([&] {
            nm::audio::Writer writer(*shared);
            for (int i = 1; i <= 250000; ++i)
            {
                writer.push({static_cast<float>(i), static_cast<float>(-i)});
                if (i % 128 == 0)
                    writer.publish(nm::qpc());
            }
            done.store(true);
        });
        bool coherent = true;
        while (!done.load())
        {
            const auto end = nm::audio::load(shared->published);
            if (end < 1)
                continue;
            nm::audio::Stereo sample;
            if (nm::audio::read(*shared, end - 1, sample))
                coherent &= sample.left == -sample.right;
        }
        writerThread.join();
        require(coherent, "Torn shared stereo sample");
        std::cout
            << "Audio clock, sinc calibration, independent readers, stale mute and atomic overwrite passed\n";
        return 0;
    }
    catch (const std::exception &error)
    {
        std::cerr << error.what() << '\n';
        return 1;
    }
}
