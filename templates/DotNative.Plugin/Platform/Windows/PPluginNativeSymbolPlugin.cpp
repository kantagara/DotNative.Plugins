#include <DotNativePlugins.hpp>
#include <windows.h>
void PPluginNativeSymbolPlugin(const dotnative::PluginRegistrar& registrar) {
    registrar.Channel("dotnative.plugins.pluginchannel").Handle("read", [](const dotnative::Value&, std::shared_ptr<dotnative::Reply> reply) {
        reply->Success("Windows: pid=" + std::to_string(GetCurrentProcessId()));
    });
}