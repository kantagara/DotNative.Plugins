// Native desktop transport; linked with the plugin SDK, independently of any renderer.
#include <cstdint>
#include <cstring>
#include <deque>
#include <map>
#include <mutex>
#include <thread>
#include <vector>
#include <string>
namespace {
using Handler = void (*)(const uint8_t*, size_t);
constexpr size_t Limit = 1048576;
std::mutex gate;
std::thread::id owner;
std::map<uint32_t, Handler> handlers;
std::map<uint64_t, uint32_t> pending;
std::deque<std::vector<uint8_t>> replies;
size_t queuedBytes = 0;
bool bindThread() {
    std::lock_guard<std::mutex> lock(gate);
    if (owner == std::thread::id{}) owner = std::this_thread::get_id();
    return owner == std::this_thread::get_id();
}
uint64_t read(const uint8_t* data, int count) {
    uint64_t value = 0;
    for (int i = 0; i < count; ++i) value |= uint64_t(data[i]) << (8 * i);
    return value;
}
void number(std::vector<uint8_t>& data, uint64_t value, int count) {
    for (int i = 0; i < count; ++i) data.push_back(uint8_t(value >> (8 * i)));
}
std::vector<uint8_t> failure(uint64_t id, const char* code, const char* message) {
    std::vector<uint8_t> data;
    number(data, id, 8); number(data, 1, 4);
    data.push_back(6); number(data, 2, 4);
    for (const auto* text : {"code", code, "message", message}) {
        auto length = std::strlen(text);
        data.push_back(4); number(data, length, 4);
        data.insert(data.end(), text, text + length);
    }
    return data;
}
}
extern "C" int32_t dotnative_register_channel(uint32_t id, Handler handler) {
    if (!bindThread() || !id || !handler) return -1;
    std::lock_guard<std::mutex> lock(gate);
    if (handlers.count(id) || handlers.size() >= 256) return -1;
    handlers[id] = handler;
    return 0;
}
extern "C" int32_t dotnative_plugin_reply(const uint8_t* bytes, size_t length) {
    if (!bytes || length < 13 || length > Limit + 12 || read(bytes + 8, 4) > 2) return -1;
    const auto id = read(bytes, 8);
    std::lock_guard<std::mutex> lock(gate);
    if (!pending.erase(id)) return 1;
    const bool overloaded = queuedBytes + length > 15 * Limit;
    auto reply = overloaded ? failure(id, "busy", "Plugin reply queue is full.") : std::vector<uint8_t>(bytes, bytes + length);
    queuedBytes += reply.size(); replies.push_back(std::move(reply));
    return overloaded ? 2 : 0;
}
extern "C" int32_t dotnative_send_plugin(const uint8_t* bytes, size_t length) {
    if (!bindThread() || !bytes || length < 16 || length > Limit + 16) return -1;
    auto id = read(bytes, 8);
    auto channel = uint32_t(read(bytes + 8, 4));
    auto kind = read(bytes + 12, 4);
    if (!id || (kind != 1 && kind != 2) || (kind == 2 && length != 16)) return -1;
    Handler handler = nullptr;
    {
        std::lock_guard<std::mutex> lock(gate);
        if (kind == 1) {
            if (pending.count(id) || pending.size() + replies.size() >= 256) return -1;
            pending[id] = channel;
        } else {
            auto entry = pending.find(id);
            if (entry == pending.end() || entry->second != channel) return 0;
            pending.erase(entry);
        }
        auto found = handlers.find(channel);
        if (found != handlers.end()) handler = found->second;
    }
    if (handler) handler(bytes, length);
    else if (kind == 1) {
        auto reply = failure(id, "missing_plugin", "Plugin channel is not registered on this platform.");
        dotnative_plugin_reply(reply.data(), reply.size());
    }
    return 0;
}
extern "C" size_t dotnative_poll_plugin(uint8_t* buffer, size_t capacity) {
    std::lock_guard<std::mutex> lock(gate);
    if (replies.empty()) return 0;
    auto size = replies.front().size();
    if (!buffer || capacity < size) return size;
    std::memcpy(buffer, replies.front().data(), size);
    replies.pop_front(); queuedBytes -= size;
    return size;
}
extern "C" int32_t dotnative_plugins_reset() {
    if (!bindThread()) return -1;
    std::map<uint32_t, Handler> callbacks;
    {
        std::lock_guard<std::mutex> lock(gate);
        pending.clear(); replies.clear(); queuedBytes = 0; callbacks = handlers;
    }
    for (auto entry : callbacks) {
        std::vector<uint8_t> packet;
        number(packet, 0, 8); number(packet, entry.first, 4); number(packet, 3, 4);
        entry.second(packet.data(), packet.size());
    }
    return 0;
}
