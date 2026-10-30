#pragma once
#include "DotNativePlugins.hpp"
#include <deque>

namespace dotnative {
// Event channels reuse the request/reply transport with one pending read.
// Producers never block the UI thread; a bounded FIFO reports overflow to the
// consumer.
class EventSink {
  std::function<void(Value)> emit_;

public:
  explicit EventSink(std::function<void(Value)> emit)
      : emit_(std::move(emit)) {}
  void Success(Value value) const {
    emit_(Value::Map{{"event", "data"}, {"value", std::move(value)}});
  }
  void Error(std::string code, std::string message, Value details = {}) const {
    emit_(Value::Map{{"event", "error"},
                     {"code", std::move(code)},
                     {"message", std::move(message)},
                     {"details", std::move(details)}});
  }
  void EndOfStream() const { emit_(Value::Map{{"event", "end"}}); }
};
class EventChannel {
public:
  using Listen = std::function<void(const Value &, EventSink)>;
  using Cancel = std::function<void(const Value &)>;

private:
  struct State {
    std::mutex mutex;
    bool active = false, ended = false;
    uint64_t generation = 0;
    Value arguments;
    std::deque<Value> queue;
    std::shared_ptr<Reply> waiting;
    Listen listen;
    Cancel cancel;
    void Close() {
      std::shared_ptr<Reply> reply;
      Value args;
      bool was_active;
      {
        std::lock_guard<std::mutex> lock(mutex);
        was_active = active;
        active = false;
        ended = false;
        ++generation;
        queue.clear();
        reply = std::move(waiting);
        args = std::move(arguments);
      }
      if (reply)
        reply->Success(Value::Map{{"event", "end"}});
      if (was_active && cancel)
        cancel(args);
    }
    void Emit(uint64_t token, Value packet) {
      // Validate before queuing so invalid values cannot strand a reader.
      try {
        Codec::Encode(packet);
      } catch (...) {
        packet = Value::Map{{"event", "error"},
                            {"code", "invalid_event"},
                            {"message", "Unsupported native event"}};
      }
      std::shared_ptr<Reply> reply;
      {
        std::lock_guard<std::mutex> lock(mutex);
        if (!active || ended || token != generation)
          return;
        ended = packet.As<Value::Map>().at("event").As<std::string>() == "end";
        reply = std::move(waiting);
        if (!reply) {
          if (queue.size() >= 64) {
            queue.clear();
            ended = true;
            queue.emplace_back(Value::Map{
                {"event", "error"},
                {"code", "overflow"},
                {"message", "Native event buffer exceeded 64 events"}});
          } else
            queue.push_back(std::move(packet));
          return;
        }
      }
      reply->Success(packet);
    }
  };
  std::shared_ptr<State> state_;

public:
  EventChannel(const PluginRegistrar &registrar, std::string_view name,
               Listen listen, Cancel cancel, uint32_t version = 1)
      : EventChannel(registrar, detail::ChannelIdentity(name, version),
                     std::move(listen), std::move(cancel)) {}
  EventChannel(const PluginRegistrar &registrar, ChannelDescriptor descriptor,
               Listen listen, Cancel cancel)
      : EventChannel(registrar, descriptor.name, std::move(listen),
                     std::move(cancel), descriptor.version) {}
  EventChannel(const PluginRegistrar &registrar, uint32_t id, Listen listen,
               Cancel cancel = {})
      : state_(std::make_shared<State>()) {
    auto state = state_;
    state->listen = std::move(listen);
    state->cancel = std::move(cancel);
    auto &channel = registrar.Channel(id);
    channel.OnReset = [state] { state->Close(); };
    channel.Handle(
        "listen", [state](const Value &args, std::shared_ptr<Reply> reply) {
          uint64_t token;
          {
            std::lock_guard<std::mutex> lock(state->mutex);
            if (state->active) {
              reply->Error("busy", "Event channel already has a consumer");
              return;
            }
            state->active = true;
            state->ended = false;
            state->arguments = args;
            token = ++state->generation;
          }
          try {
            state->listen(args, EventSink([state, token](Value packet) {
                            state->Emit(token, std::move(packet));
                          }));
            reply->Success();
          } catch (...) {
            state->Close();
            throw;
          }
        });
    channel.Handle(
        "next", [state](const Value &, std::shared_ptr<Reply> reply) {
          Value packet;
          {
            std::lock_guard<std::mutex> lock(state->mutex);
            if (!state->active) {
              reply->Error("not_listening", "No event subscription");
              return;
            }
            if (state->waiting) {
              reply->Error("busy", "An event read is already pending");
              return;
            }
            if (!state->queue.empty()) {
              packet = std::move(state->queue.front());
              state->queue.pop_front();
            } else if (state->ended)
              packet = Value::Map{{"event", "end"}};
            else {
              state->waiting = reply;
              return;
            }
          }
          reply->Success(packet);
        });
    channel.Handle("cancel",
                   [state](const Value &, std::shared_ptr<Reply> reply) {
                     state->Close();
                     reply->Success();
                   });
  }
};
} // namespace dotnative
