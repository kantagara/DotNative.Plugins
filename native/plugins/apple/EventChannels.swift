import Foundation

/// Emits DotNative event envelopes over the native request/reply transport.
@MainActor final class NativeEventSink {
  private let emit: (PluginValue, Bool) -> Void

  fileprivate init(_ emit: @escaping (PluginValue, Bool) -> Void) {
    self.emit = emit
  }

  func success(_ value: PluginValue) {
    emit(.map(["event": .string("data"), "value": value]), false)
  }

  func failure(_ code: String, _ message: String, _ details: PluginValue = .null) {
    emit(
      .map([
        "event": .string("error"), "code": .string(code),
        "message": .string(message), "details": details,
      ]), true)
  }

  func endOfStream() {
    emit(.map(["event": .string("end")]), true)
  }
}

/// One subscription, one pending read and at most 64 buffered events per channel.
@MainActor final class NativeEventChannel {
  private let onListen: (PluginValue, NativeEventSink) throws -> Void
  private let onCancel: (PluginValue) -> Void
  private var active = false
  private var ended = false
  private var producing = false
  private var generation: UInt64 = 0
  private var arguments: PluginValue = .null
  private var queue: [PluginValue] = []
  private var waiting: PluginReply?

  init(
    _ descriptor: ChannelDescriptor,
    onListen: @escaping (PluginValue, NativeEventSink) throws -> Void,
    onCancel: @escaping (PluginValue) -> Void
  ) {
    self.onListen = onListen
    self.onCancel = onCancel
    let channel = NativeChannels.channel(descriptor)
    channel.onReset = { [self] in close() }
    channel.handle("listen") { [self] args, reply in listen(args, reply) }
    channel.handle("next") { [self] _, reply in next(reply) }
    channel.handle("cancel") { [self] _, reply in
      close()
      reply.success()
    }
  }

  convenience init(
    _ name: String,
    version: UInt32 = 1,
    onListen: @escaping (PluginValue, NativeEventSink) throws -> Void,
    onCancel: @escaping (PluginValue) -> Void
  ) {
    self.init(
      ChannelDescriptor(name: name, version: version), onListen: onListen, onCancel: onCancel)
  }

  private func listen(_ args: PluginValue, _ reply: PluginReply) {
    guard !active else {
      reply.failure("busy", "Event channel already has a consumer")
      return
    }
    active = true
    ended = false
    producing = true
    arguments = args
    generation &+= 1
    let token = generation
    let sink = NativeEventSink { [weak self] value, terminal in
      self?.enqueue(token, value, terminal)
    }
    do {
      try onListen(args, sink)
      reply.success()
    } catch {
      close()
      reply.failure("stream_error", error.localizedDescription)
    }
  }

  private func next(_ reply: PluginReply) {
    guard active else {
      reply.failure("not_listening", "No event subscription")
      return
    }
    if waiting != nil {
      reply.failure("busy", "An event read is already pending")
    } else if !queue.isEmpty {
      reply.success(queue.removeFirst())
    } else if ended {
      reply.success(.map(["event": .string("end")]))
    } else {
      waiting = reply
      reply.onCancel = { [weak self] in self?.close() }
    }
  }

  private func enqueue(_ token: UInt64, _ value: PluginValue, _ terminal: Bool) {
    guard active, !ended, token == generation else { return }
    var packet = value
    var stop = terminal
    var encoded = Data()
    do {
      try PluginCodec.encode(packet, into: &encoded)
    } catch {
      packet = .map([
        "event": .string("error"), "code": .string("invalid_event"),
        "message": .string("Native event exceeds codec limits"),
      ])
      stop = true
    }
    if let reply = waiting {
      waiting = nil
      reply.success(packet)
    } else if queue.count >= 64 {
      queue = [
        .map([
          "event": .string("error"), "code": .string("overflow"),
          "message": .string("Native event buffer exceeded 64 events"),
        ])
      ]
      stop = true
    } else {
      queue.append(packet)
    }
    if stop {
      ended = true
      stopProducer()
    }
  }

  private func stopProducer() {
    guard producing else { return }
    producing = false
    onCancel(arguments)
  }

  private func close() {
    active = false
    ended = false
    generation &+= 1
    queue.removeAll()
    let reply = waiting
    waiting = nil
    reply?.success(.map(["event": .string("end")]))
    stopProducer()
    arguments = .null
  }
}
