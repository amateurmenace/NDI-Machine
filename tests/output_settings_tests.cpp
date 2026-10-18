#include "output_settings.h"
#include "video_convert.h"
#include <iostream>
#include <stdexcept>
#include <vector>
static void require(bool value) {if(!value)throw std::runtime_error("Output processing assertion failed");}
int main() {
    auto silent=nm::OutputSettings::parse({{"source","Camera"}});require(silent.audioMode=="silent");
    for(const auto& invalid: {nlohmann::json{{"source","Camera"},{"delayMs",501}},nlohmann::json{{"source","Camera"},{"gainDb",13}},
        nlohmann::json{{"source","Camera"},{"audioMode","fixed"}},nlohmann::json{{"source","Camera"},{"left",0}}}) {
        bool rejected=false;try{nm::OutputSettings::parse(invalid);}catch(const std::exception&){rejected=true;}require(rejected);
    }
    nm::AudioDelay delay;delay.reset(1);
    for(int n=0;n<48;n++){auto v=delay.process({1,-1});require(v.left==0 && v.right==0);}
    auto v=delay.process({0,0});require(v.left==1 && v.right==-1);
    delay.reset(0);v=delay.process({.25f,-.5f});require(v.left==.25f && v.right==-.5f);
    std::vector<uint8_t> source(1920*1080*2+16,0x5a),output(1920*1080*2+16,0xee);
    nm::video::convert(source.data(),1920,1080,3840,false,output.data(),3840,"fit");
    require(memcmp(source.data(),output.data(),1920*1080*2)==0);require(output[1920*1080*2]==0xee);
    std::vector<uint8_t> square(640*480*4,255);
    nm::video::convert(square.data(),640,480,2560,true,output.data(),3840,"fit");
    require(output[1]==16 && output[(540*1920+960)*2+1]>=234); // black pillarbox, white center
    nm::video::convert(square.data(),640,480,2560,true,output.data(),3840,"fill");require(output[1]>=234);
    nm::video::convert(square.data(),640,480,2560,true,output.data(),3840,"stretch");require(output[1]>=234);
    require(output[1920*1080*2]==0xee);
    std::cout<<"Output bounds, exact delay, format conversion, sizing and buffer guards passed\n";
}
