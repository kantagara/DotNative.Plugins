#include <DotNativePlugins.hpp>
#include <sys/utsname.h>
void PPluginNativeSymbolPlugin(const dotnative::PluginRegistrar& registrar) {
    registrar.Channel("dotnative.plugins.pluginchannel").Handle("read", [](const dotnative::Value&, std::shared_ptr<dotnative::Reply> reply) {
        utsname info{}; if (uname(&info) != 0) { reply->Error("os_error", "uname failed"); return; }
        reply->Success(std::string(info.sysname) + ": " + info.release);
    });
}