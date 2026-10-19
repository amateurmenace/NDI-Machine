#include "sdi_engine.h"
#include <iostream>
#include <thread>

// Bounded receive-only check using exactly the production Receiver/frame-sync
// path. No DeckLink COM object, media-worker pipe or router is opened.
int main(int argc,char** argv){
    try {
        if(argc!=3 && argc!=5)throw std::runtime_error("Usage: ndi-receiver-check <exact source name> <discovered address> [background name background address]");
        Com com;
        if(!NDIlib_initialize())throw std::runtime_error("NDI runtime initialization failed");
        json result;
        {
        std::unique_ptr<Receiver> background;std::jthread consume;
        if(argc==5){background=std::make_unique<Receiver>(argv[3],argv[4]);consume=std::jthread([&](std::stop_token stop){Com threadCom;while(!stop.stop_requested()){NDIlib_video_frame_v2_t frame{};NDIlib_framesync_capture_video(background->sync,&frame);NDIlib_framesync_free_video(background->sync,&frame);Sleep(33);}});Sleep(1500);}
        std::exception_ptr failure;
        std::jthread target([&]{try{Com threadCom;Receiver receiver(argv[1],argv[2]);auto end=Clock::now()+std::chrono::seconds(8);int valid=0,channels=0,width=0,height=0;NDIlib_recv_performance_t received{},dropped{};
        while(Clock::now()<end){NDIlib_video_frame_v2_t video{};NDIlib_framesync_capture_video(receiver.sync,&video);if(usable(video)){++valid;width=video.xres;height=video.yres;}NDIlib_framesync_free_video(receiver.sync,&video);
            NDIlib_audio_frame_v2_t audio{};NDIlib_framesync_capture_audio(receiver.sync,&audio,48000,0,1602);channels=std::max(channels,audio.no_channels);if(audio.p_data)NDIlib_framesync_free_audio(receiver.sync,&audio);Sleep(10);}
        NDIlib_recv_get_performance(receiver.rx,&received,&dropped);
        result={{"source",argv[1]},{"videoFrames",received.video_frames},{"audioFrames",received.audio_frames},{"usableVideoSamples",valid},{"width",width},{"height",height},{"audioChannels",channels},{"videoDrops",dropped.video_frames},{"sdiOrRouterWrites",false},{"backgroundReceiver",argc==5}};
        }catch(...){failure=std::current_exception();}});target.join();if(failure)std::rethrow_exception(failure);}
        NDIlib_destroy();std::cout<<result.dump()<<std::endl;return result["usableVideoSamples"]>0 && result["videoFrames"]>0?0:2;
    }catch(const std::exception& error){std::cerr<<error.what()<<std::endl;return 1;}
}
