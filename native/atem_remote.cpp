#include <Windows.h>
#include <wrl/client.h>
#include <iostream>
#include <string>
#include <stdexcept>
#include <atomic>
#include <thread>
#include <chrono>
#include <BMDSwitcherAPI.h>
#include <nlohmann/json.hpp>
using Microsoft::WRL::ComPtr;
using json=nlohmann::json;
class Connection final:public IBMDSwitcherCallback {
 std::atomic<ULONG> references{1};
public:
 std::atomic<bool> connected{true};
 HRESULT STDMETHODCALLTYPE QueryInterface(REFIID iid,void** value)override{if(iid==IID_IUnknown || iid==IID_IBMDSwitcherCallback){*value=this;AddRef();return S_OK;}*value=nullptr;return E_NOINTERFACE;}
 ULONG STDMETHODCALLTYPE AddRef()override{return ++references;}
 ULONG STDMETHODCALLTYPE Release()override{auto n=--references;if(!n)delete this;return n;}
 HRESULT STDMETHODCALLTYPE Notify(BMDSwitcherEventType event,BMDSwitcherVideoMode)override{if(event==bmdSwitcherEventTypeDisconnected)connected=false;return S_OK;}
};
static void check(HRESULT hr,const char* what){if(FAILED(hr))throw std::runtime_error(std::string(what)+" HRESULT "+std::to_string(static_cast<unsigned long>(hr)));}
static std::string utf8(BSTR value){if(!value)return {};int n=WideCharToMultiByte(CP_UTF8,0,value,SysStringLen(value),nullptr,0,nullptr,nullptr);std::string s(n,' ');WideCharToMultiByte(CP_UTF8,0,value,SysStringLen(value),s.data(),n,nullptr,nullptr);SysFreeString(value);return s;}
static json snapshot(IBMDSwitcher* sw){
  ComPtr<IBMDSwitcherRemoteSourceConfiguration> config;check(sw->QueryInterface(IID_PPV_ARGS(&config)),"Remote sources");
  ComPtr<IBMDSwitcherRemoteSourceIterator> it;check(config->CreateIterator(IID_IBMDSwitcherRemoteSourceIterator,reinterpret_cast<void**>(it.GetAddressOf())),"Remote iterator");
  json sources=json::array();ComPtr<IBMDSwitcherRemoteSource> source;
  while(it->Next(&source)==S_OK && source){
    unsigned int id=0;BOOL linked=FALSE,connected=FALSE,enabled=FALSE;BMDSwitcherInputId input=0;BSTR name=nullptr,model=nullptr;
    check(source->GetSourceId(&id),"Source ID");check(source->IsLinked(&linked),"Link state");if(linked)check(source->GetLinkedInput(&input),"Linked input");
    check(source->GetName(&name),"Source name");source->GetModelName(&model);check(source->GetConnected(&connected),"Connected");check(source->GetEnabled(&enabled),"Enabled");
    ComPtr<IBMDSwitcherRemoteSourceCloud> cloud;bool isCloud=SUCCEEDED(source.As(&cloud));
    sources.push_back({{"id",id},{"name",utf8(name)},{"model",utf8(model)},{"cloud",isCloud},{"linkedInput",linked?input:0},{"connected",connected!=FALSE},{"enabled",enabled!=FALSE}});source.Reset();
  }
  ComPtr<IBMDSwitcherInputIterator> inputs;check(sw->CreateIterator(IID_IBMDSwitcherInputIterator,reinterpret_cast<void**>(inputs.GetAddressOf())),"Input iterator");
  json destinations=json::array();ComPtr<IBMDSwitcherInput> input;
  while(inputs->Next(&input)==S_OK && input){
    BMDSwitcherInputId id=0;check(input->GetInputId(&id),"Input ID");
    if(id>=1 && id<=8){BSTR name=nullptr;BMDSwitcherExternalPortType port{},available{};BOOL program=FALSE,preview=FALSE;
      check(input->GetLongName(&name),"Input name");check(input->GetCurrentExternalPortType(&port),"Input port");check(input->GetAvailableExternalPortTypes(&available),"Available ports");input->IsProgramTallied(&program);input->IsPreviewTallied(&preview);
      destinations.push_back({{"id",id},{"name",utf8(name)},{"port",static_cast<int>(port)},{"available",static_cast<int>(available)},{"program",program!=FALSE},{"preview",preview!=FALSE}});
    }input.Reset();
  }
  return {{"schemaVersion",1},{"sources",sources},{"inputs",destinations}};
}
static json topology(const json& state){json result={{"inputs",json::array()},{"sources",json::array()}};for(auto& i:state.at("inputs"))result["inputs"].push_back({i.at("id"),i.at("port"),i.at("available")});for(auto& s:state.at("sources"))result["sources"].push_back({s.at("id"),s.at("linkedInput"),s.at("enabled"),s.at("name")});return result;}
static json command(IBMDSwitcher* sw,const json& request){
 auto state=snapshot(sw);const auto method=request.value("method","snapshot");if(method=="snapshot")return state;
 if(method!="input")throw std::runtime_error("Unsupported ATEM command");
 if(!request.contains("expected") || topology(state)!=topology(request.at("expected")))throw std::runtime_error("ATEM input/source mapping changed; select again");
 const int id=request.at("input");if(id<1 || id>8)throw std::runtime_error("ATEM input must be 1 through 8");
 const int remote=request.value("source",0);
 ComPtr<IBMDSwitcherInputIterator> inputs;check(sw->CreateIterator(IID_IBMDSwitcherInputIterator,reinterpret_cast<void**>(inputs.GetAddressOf())),"Input iterator");
 ComPtr<IBMDSwitcherInput> input;check(inputs->GetById(id,&input),"Target input");
 // Remote links replace the physical connector; they are not a selectable
 // external-port type. See SDK section 16.3.4.4 and 16.3.4.5.
 if(remote){
   auto found=std::find_if(state["sources"].begin(),state["sources"].end(),[&](auto& s){return s.at("id")==remote;});
   if(found==state["sources"].end() || !found->at("enabled").get<bool>())throw std::runtime_error("Cloud source missing or disabled");
   if(found->at("linkedInput")!=0 && found->at("linkedInput")!=id)throw std::runtime_error("Cloud source is linked to another input; return that input to SDI first");
   ComPtr<IBMDSwitcherRemoteSourceConfiguration> config;check(sw->QueryInterface(IID_PPV_ARGS(&config)),"Remote sources");
   ComPtr<IBMDSwitcherRemoteSourceIterator> iterator;check(config->CreateIterator(IID_IBMDSwitcherRemoteSourceIterator,reinterpret_cast<void**>(iterator.GetAddressOf())),"Remote iterator");
   ComPtr<IBMDSwitcherRemoteSource> source;while(iterator->Next(&source)==S_OK && source){unsigned int sid=0;source->GetSourceId(&sid);if(sid==remote){check(source->SetLinkedInput(id),"Link remote input");break;}source.Reset();}
   if(!source)throw std::runtime_error("Remote source disappeared");
 } else {
   ComPtr<IBMDSwitcherRemoteSourceConfiguration> config;check(sw->QueryInterface(IID_PPV_ARGS(&config)),"Remote sources");
   ComPtr<IBMDSwitcherRemoteSourceIterator> iterator;check(config->CreateIterator(IID_IBMDSwitcherRemoteSourceIterator,reinterpret_cast<void**>(iterator.GetAddressOf())),"Remote iterator");
   ComPtr<IBMDSwitcherRemoteSource> source;while(iterator->Next(&source)==S_OK && source){BOOL linked=FALSE;BMDSwitcherInputId target=0;source->IsLinked(&linked);if(linked && SUCCEEDED(source->GetLinkedInput(&target)) && target==id)check(source->UnlinkInput(),"Unlink streaming source");source.Reset();}
 }
 for(int attempt=0;attempt<25;++attempt){state=snapshot(sw);auto i=std::find_if(state["inputs"].begin(),state["inputs"].end(),[&](auto& x){return x.at("id")==id;});
   const bool linked=std::any_of(state["sources"].begin(),state["sources"].end(),[&](auto& x){return x.at("id")==remote && x.at("linkedInput")==id;});
   const bool anyLinked=std::any_of(state["sources"].begin(),state["sources"].end(),[&](auto& x){return x.at("linkedInput")==id;});
   if(i!=state["inputs"].end() && (remote ? linked : !anyLinked))return state;
   std::this_thread::sleep_for(std::chrono::milliseconds(100));}
 throw std::runtime_error("ATEM change sent but readback not confirmed; no replay");
}
int wmain(int argc,wchar_t** argv){
  try{
    if(argc!=2 && argc!=3)throw std::runtime_error("Usage: atem-remote <ATEM IPv4> [--server]");
    const bool server=argc==3 && std::wstring(argv[2])==L"--server";
    check(CoInitializeEx(nullptr,COINIT_MULTITHREADED),"COM initialization");
    ComPtr<IBMDSwitcherDiscovery> discovery;check(CoCreateInstance(CLSID_CBMDSwitcherDiscovery,nullptr,CLSCTX_ALL,IID_PPV_ARGS(&discovery)),"ATEM runtime");
    ComPtr<IBMDSwitcher> sw;BMDSwitcherConnectToFailure failure{};BSTR address=SysAllocString(argv[1]);auto hr=discovery->ConnectTo(address,&sw,&failure);SysFreeString(address);check(hr,"ATEM connection");
    ComPtr<Connection> connection;connection.Attach(new Connection);check(sw->AddCallback(connection.Get()),"Disconnect callback");
    if(!server)std::cout<<snapshot(sw.Get()).dump()<<std::endl;
    else {std::string line;while(std::getline(std::cin,line))try{if(line.size()>65536)throw std::runtime_error("Request too large");if(!connection->connected)throw std::runtime_error("ATEM disconnected; reconnect before retry");auto result=command(sw.Get(),json::parse(line));if(!connection->connected)throw std::runtime_error("ATEM disconnected during operation");std::cout<<result.dump()<<std::endl;}catch(const std::exception& error){std::cout<<json({{"error",error.what()}}).dump()<<std::endl;if(!connection->connected)break;}}
    sw->RemoveCallback(connection.Get());
    return 0;
  }catch(const std::exception& error){std::cerr<<error.what()<<std::endl;return 1;}
}
