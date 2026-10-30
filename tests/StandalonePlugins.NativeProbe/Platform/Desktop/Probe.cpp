#include <DotNativePlugins.hpp>
#ifdef _WIN32
#define NOMINMAX
#include <windows.h>
#include <bcrypt.h>
#else
#include <openssl/sha.h>
#endif

void StandaloneProbe(const dotnative::PluginRegistrar& registrar) {
    auto& channel = registrar.Channel("dotnative.standalone.probe");
    channel.Handle("sha256", [](const dotnative::Value& value, auto reply) {
        const auto& input = value.As<dotnative::Value::Bytes>();
        dotnative::Value::Bytes digest(32);
#ifdef _WIN32
        if (BCryptHash(BCRYPT_SHA256_ALG_HANDLE, nullptr, 0,
            const_cast<PUCHAR>(input.data()), static_cast<ULONG>(input.size()),
            digest.data(), static_cast<ULONG>(digest.size())) < 0) {
            reply->Error("crypto", "BCryptHash failed");
            return;
        }
#else
        if (!SHA256(input.data(), input.size(), digest.data())) {
            reply->Error("crypto", "OpenSSL SHA256 failed");
            return;
        }
#endif
        reply->Success(std::move(digest));
    });
    channel.Handle("echo", [](const dotnative::Value& value, auto reply) { reply->Success(value); });
    channel.Handle("fail", [](const auto&, auto reply) { reply->Error("probe", "Expected native failure"); });
    channel.Handle("wait", [](const auto&, auto) {});
}
