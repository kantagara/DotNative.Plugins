#pragma once
// Service plugin SDK. Register/Handle/OnReset run on the renderer's UI thread.
// A copied Reply may complete on a worker thread. Native window pointers are
// borrowed and may only be used on the UI thread during the host's lifetime.
#include <atomic>
#include <cstdint>
#include <cstring>
#include <functional>
#include <map>
#include <memory>
#include <mutex>
#include <stdexcept>
#include <string>
#include <string_view>
#include <type_traits>
#include <utility>
#include <variant>
#include <vector>

extern "C" {
int32_t dotnative_register_channel(uint32_t, void (*)(const uint8_t *, size_t));
int32_t dotnative_plugin_reply(const uint8_t *, size_t);
}
namespace dotnative {
struct ChannelDescriptor {
  std::string_view name;
  uint32_t version = 1;
};
namespace detail {
// The wire ID stays private to the SDK. All languages hash UTF-8 name/vN.
inline uint32_t ChannelIdentity(std::string_view name, uint32_t version) {
  if (name.empty() ||
      name.find_first_not_of(" \t\r\n") == std::string_view::npos || !version)
    throw std::invalid_argument("Channel name and version must be valid");
  std::string text(name);
  text += "/v" + std::to_string(version);
  std::vector<uint8_t> bytes(text.begin(), text.end());
  const uint64_t bits = uint64_t(bytes.size()) * 8;
  bytes.push_back(0x80);
  while (bytes.size() % 64 != 56)
    bytes.push_back(0);
  for (int i = 7; i >= 0; --i)
    bytes.push_back(uint8_t(bits >> (i * 8)));
  constexpr uint32_t constants[] = {
      0x428a2f98, 0x71374491, 0xb5c0fbcf, 0xe9b5dba5, 0x3956c25b, 0x59f111f1,
      0x923f82a4, 0xab1c5ed5, 0xd807aa98, 0x12835b01, 0x243185be, 0x550c7dc3,
      0x72be5d74, 0x80deb1fe, 0x9bdc06a7, 0xc19bf174, 0xe49b69c1, 0xefbe4786,
      0x0fc19dc6, 0x240ca1cc, 0x2de92c6f, 0x4a7484aa, 0x5cb0a9dc, 0x76f988da,
      0x983e5152, 0xa831c66d, 0xb00327c8, 0xbf597fc7, 0xc6e00bf3, 0xd5a79147,
      0x06ca6351, 0x14292967, 0x27b70a85, 0x2e1b2138, 0x4d2c6dfc, 0x53380d13,
      0x650a7354, 0x766a0abb, 0x81c2c92e, 0x92722c85, 0xa2bfe8a1, 0xa81a664b,
      0xc24b8b70, 0xc76c51a3, 0xd192e819, 0xd6990624, 0xf40e3585, 0x106aa070,
      0x19a4c116, 0x1e376c08, 0x2748774c, 0x34b0bcb5, 0x391c0cb3, 0x4ed8aa4a,
      0x5b9cca4f, 0x682e6ff3, 0x748f82ee, 0x78a5636f, 0x84c87814, 0x8cc70208,
      0x90befffa, 0xa4506ceb, 0xbef9a3f7, 0xc67178f2};
  uint32_t state[] = {0x6a09e667, 0xbb67ae85, 0x3c6ef372, 0xa54ff53a,
                      0x510e527f, 0x9b05688c, 0x1f83d9ab, 0x5be0cd19};
  auto rotate = [](uint32_t value, int n) {
    return (value >> n) | (value << (32 - n));
  };
  for (size_t offset = 0; offset < bytes.size(); offset += 64) {
    uint32_t w[64];
    for (int i = 0; i < 16; ++i) {
      const auto *p = bytes.data() + offset + i * 4;
      w[i] = uint32_t(p[0]) << 24 | uint32_t(p[1]) << 16 | uint32_t(p[2]) << 8 |
             p[3];
    }
    for (int i = 16; i < 64; ++i) {
      const auto x = w[i - 15], y = w[i - 2];
      w[i] = w[i - 16] + (rotate(x, 7) ^ rotate(x, 18) ^ (x >> 3)) + w[i - 7] +
             (rotate(y, 17) ^ rotate(y, 19) ^ (y >> 10));
    }
    auto a = state[0], b = state[1], c = state[2], d = state[3], e = state[4],
         f = state[5], g = state[6], h = state[7];
    for (int i = 0; i < 64; ++i) {
      const auto t1 = h + (rotate(e, 6) ^ rotate(e, 11) ^ rotate(e, 25)) +
                      ((e & f) ^ (~e & g)) + constants[i] + w[i];
      const auto t2 = (rotate(a, 2) ^ rotate(a, 13) ^ rotate(a, 22)) +
                      ((a & b) ^ (a & c) ^ (b & c));
      h = g;
      g = f;
      f = e;
      e = d + t1;
      d = c;
      c = b;
      b = a;
      a = t1 + t2;
    }
    state[0] += a;
    state[1] += b;
    state[2] += c;
    state[3] += d;
    state[4] += e;
    state[5] += f;
    state[6] += g;
    state[7] += h;
  }
  const auto value = state[0];
  const uint32_t id = (value >> 24) | ((value >> 8) & 0xff00) |
                      ((value << 8) & 0xff0000) | (value << 24);
  if (!id)
    throw std::invalid_argument(
        "Channel name resolves to a reserved transport ID");
  return id;
}
} // namespace detail

struct Value {
  using Bytes = std::vector<uint8_t>;
  using List = std::vector<Value>;
  using Map = std::map<std::string, Value>;
  using Storage = std::variant<std::monostate, bool, int64_t, double,
                               std::string, Bytes, List, Map>;
  Storage data;
  Value() = default;
  Value(std::nullptr_t) {}
  Value(bool v) : data(v) {}
  Value(int v) : data(int64_t(v)) {}
  Value(int64_t v) : data(v) {}
  Value(double v) : data(v) {}
  Value(const char *v) : data(std::string(v)) {}
  Value(std::string v) : data(std::move(v)) {}
  Value(Bytes v) : data(std::move(v)) {}
  Value(List v) : data(std::move(v)) {}
  Value(Map v) : data(std::move(v)) {}
  template <class T> const T &As() const { return std::get<T>(data); }
};
struct Codec {
  static constexpr size_t Limit = 1048576;
  static void Number(Value::Bytes &out, uint64_t n, size_t count) {
    for (size_t i = 0; i < count; ++i)
      out.push_back(uint8_t(n >> (i * 8)));
  }
  static bool Utf8(const std::string &s) {
    size_t i = 0;
    while (i < s.size()) {
      uint8_t c = uint8_t(s[i++]);
      if (c < 128)
        continue;
      int n = c >= 0xc2 && c <= 0xdf   ? 1
              : c >= 0xe0 && c <= 0xef ? 2
              : c >= 0xf0 && c <= 0xf4 ? 3
                                       : -1;
      if (n < 0 || i + size_t(n) > s.size())
        return false;
      uint32_t cp = c & ((1u << (6 - n)) - 1);
      for (int j = 0; j < n; ++j) {
        uint8_t b = uint8_t(s[i++]);
        if ((b & 0xc0) != 0x80)
          return false;
        cp = (cp << 6) | (b & 63);
      }
      if (cp < (n == 1   ? 128u
                : n == 2 ? 2048u
                         : 65536u) ||
          cp > 0x10ffff || (cp >= 0xd800 && cp <= 0xdfff))
        return false;
    }
    return true;
  }
  static void Write(Value::Bytes &out, const Value &v, int depth = 0) {
    if (depth > 16 || out.size() > Limit)
      throw std::runtime_error("Plugin value limit");
    std::visit(
        [&](const auto &x) {
          using T = std::decay_t<decltype(x)>;
          if constexpr (std::is_same_v<T, std::monostate>)
            out.push_back(0);
          else if constexpr (std::is_same_v<T, bool>)
            out.push_back(x ? 2 : 1);
          else if constexpr (std::is_same_v<T, int64_t>) {
            out.push_back(3);
            Number(out, uint64_t(x), 8);
          } else if constexpr (std::is_same_v<T, double>) {
            out.push_back(8);
            uint64_t bits;
            std::memcpy(&bits, &x, 8);
            Number(out, bits, 8);
          } else if constexpr (std::is_same_v<T, std::string> ||
                               std::is_same_v<T, Value::Bytes>) {
            if constexpr (std::is_same_v<T, std::string>) {
              if (!Utf8(x))
                throw std::runtime_error("Invalid UTF-8");
            }
            if (x.size() > Limit || out.size() + 5 + x.size() > Limit)
              throw std::runtime_error("Plugin blob limit");
            out.push_back(std::is_same_v<T, std::string> ? 4 : 5);
            Number(out, x.size(), 4);
            out.insert(out.end(), x.begin(), x.end());
          } else {
            if (x.size() > 4096)
              throw std::runtime_error("Plugin collection limit");
            out.push_back(std::is_same_v<T, Value::Map> ? 6 : 7);
            Number(out, x.size(), 4);
            for (const auto &item : x) {
              if constexpr (std::is_same_v<T, Value::Map>) {
                Write(out, Value(item.first), depth + 1);
                Write(out, item.second, depth + 1);
              } else
                Write(out, item, depth + 1);
            }
          }
        },
        v.data);
    if (out.size() > Limit)
      throw std::runtime_error("Plugin message limit");
  }
  struct Reader {
    const uint8_t *bytes;
    size_t size, offset = 0;
    uint64_t Number(size_t count) {
      if (count > size - offset)
        throw std::runtime_error("Truncated plugin message");
      uint64_t n = 0;
      for (size_t i = 0; i < count; ++i)
        n |= uint64_t(bytes[offset++]) << (8 * i);
      return n;
    }
    Value Read(int depth = 0) {
      if (depth > 16)
        throw std::runtime_error("Plugin nesting limit");
      auto tag = Number(1);
      if (tag == 0)
        return {};
      if (tag == 1 || tag == 2)
        return Value(tag == 2);
      if (tag == 3) {
        auto bits = Number(8);
        int64_t n;
        std::memcpy(&n, &bits, 8);
        return Value(n);
      }
      if (tag == 8) {
        auto bits = Number(8);
        double n;
        std::memcpy(&n, &bits, 8);
        return Value(n);
      }
      if (tag == 4 || tag == 5) {
        auto n = Number(4);
        if (n > Limit || n > size - offset)
          throw std::runtime_error("Plugin blob limit");
        auto begin = bytes + offset;
        offset += size_t(n);
        if (tag == 5)
          return Value(Value::Bytes(begin, begin + n));
        std::string s(reinterpret_cast<const char *>(begin), size_t(n));
        if (!Utf8(s))
          throw std::runtime_error("Invalid UTF-8");
        return Value(std::move(s));
      }
      if (tag != 6 && tag != 7)
        throw std::runtime_error("Unknown plugin tag");
      auto count = Number(4);
      if (count > 4096)
        throw std::runtime_error("Plugin collection limit");
      if (tag == 7) {
        Value::List list;
        for (size_t i = 0; i < count; ++i)
          list.push_back(Read(depth + 1));
        return Value(std::move(list));
      }
      Value::Map map;
      for (size_t i = 0; i < count; ++i) {
        auto key = Read(depth + 1).As<std::string>();
        auto value = Read(depth + 1);
        if (!map.emplace(std::move(key), std::move(value)).second)
          throw std::runtime_error("Duplicate map key");
      }
      return Value(std::move(map));
    }
  };
  static Value::Bytes Encode(const Value &v) {
    Value::Bytes out;
    Write(out, v);
    return out;
  }
  static Value Decode(const uint8_t *bytes, size_t count) {
    if (count > Limit || (!bytes && count))
      throw std::runtime_error("Plugin message limit");
    Reader r{bytes, count};
    auto v = r.Read();
    if (r.offset != count)
      throw std::runtime_error("Trailing plugin data");
    return v;
  }
};
class Reply;
namespace detail {
inline std::mutex pending_mutex;
inline std::map<uint64_t, std::shared_ptr<Reply>> pending;
} // namespace detail
class Reply {
  uint64_t id_;
  std::atomic<bool> completed_{false};
  bool Finish(uint32_t status, const Value &value) {
    auto payload = Codec::Encode(value);
    if (completed_.exchange(true))
      return false;
    {
      std::lock_guard<std::mutex> lock(detail::pending_mutex);
      detail::pending.erase(id_);
    }
    Value::Bytes packet;
    Codec::Number(packet, id_, 8);
    Codec::Number(packet, status, 4);
    packet.insert(packet.end(), payload.begin(), payload.end());
    return dotnative_plugin_reply(packet.data(), packet.size()) == 0;
  }

public:
  explicit Reply(uint64_t id) : id_(id) {}
  std::function<void()> OnCancel;
  bool Success(const Value &value = {}) { return Finish(0, value); }
  bool Error(const std::string &code, const std::string &message,
             const Value &details = {}) {
    return Finish(
        1,
        Value::Map{{"code", code}, {"message", message}, {"details", details}});
  }
  void Cancel() {
    if (!completed_.exchange(true) && OnCancel) {
      try {
        OnCancel();
      } catch (...) {
      }
    }
  }
};
class MethodChannel {
public:
  using Handler = std::function<void(const Value &, std::shared_ptr<Reply>)>;
  std::map<std::string, Handler> handlers;
  std::function<void()> OnReset;
  void Handle(const std::string &method, Handler handler) {
    if (!handlers.emplace(method, std::move(handler)).second)
      throw std::runtime_error("Duplicate plugin method");
  }
};
namespace detail {
inline std::map<uint32_t, MethodChannel> channels;
inline void Dispatch(const uint8_t *bytes, size_t count) noexcept {
  std::shared_ptr<Reply> reply;
  try {
    if (count < 16 || count > Codec::Limit + 16)
      return;
    Codec::Reader r{bytes, count};
    auto id = r.Number(8);
    auto channel = uint32_t(r.Number(4));
    auto kind = r.Number(4);
    if (kind == 2 || kind == 3) {
      std::vector<std::shared_ptr<Reply>> cancelled;
      {
        std::lock_guard<std::mutex> lock(pending_mutex);
        if (kind == 3) {
          for (auto &item : pending)
            cancelled.push_back(item.second);
          pending.clear();
        } else {
          auto it = pending.find(id);
          if (it != pending.end()) {
            cancelled.push_back(it->second);
            pending.erase(it);
          }
        }
      }
      for (auto &call : cancelled)
        call->Cancel();
      auto found = channels.find(channel);
      if (kind == 3 && found != channels.end() && found->second.OnReset)
        found->second.OnReset();
      return;
    }
    if (kind != 1)
      return;
    reply = std::make_shared<Reply>(id);
    {
      std::lock_guard<std::mutex> lock(pending_mutex);
      if (!pending.emplace(id, reply).second)
        return;
    }
    auto value = Codec::Decode(bytes + 16, count - 16);
    const auto &call = value.As<Value::Map>();
    const auto &method = call.at("method").As<std::string>();
    auto found = channels.find(channel);
    if (found == channels.end() || !found->second.handlers.count(method)) {
      reply->Error("not_implemented", "Unknown plugin method");
      return;
    }
    found->second.handlers.at(method)(call.at("arguments"), reply);
  } catch (const std::exception &error) {
    if (reply) {
      try {
        reply->Error("plugin_error", error.what());
      } catch (...) {
      }
    }
  } catch (...) {
    if (reply) {
      try {
        reply->Error("plugin_error", "Native plugin threw an exception");
      } catch (...) {
      }
    }
  }
}
} // namespace detail
class PluginRegistrar {
  void *window_;

public:
  explicit PluginRegistrar(void *window) : window_(window) {}
  void *NativeWindow() const { return window_; }
  MethodChannel &Channel(std::string_view name, uint32_t version = 1) const {
    return Channel(detail::ChannelIdentity(name, version));
  }
  MethodChannel &Channel(ChannelDescriptor descriptor) const {
    return Channel(descriptor.name, descriptor.version);
  }
  MethodChannel &Channel(uint32_t id) const {
    auto it = detail::channels.find(id);
    if (it != detail::channels.end())
      return it->second;
    if (!id || dotnative_register_channel(id, detail::Dispatch) != 0)
      throw std::runtime_error("Channel registration failed");
    return detail::channels.emplace(id, MethodChannel{}).first->second;
  }
};
} // namespace dotnative
