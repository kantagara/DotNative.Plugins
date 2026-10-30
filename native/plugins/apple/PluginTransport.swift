import Foundation

// Host-independent transport. Plugin authors only use the Swift channel API.
private let pluginLimit = 1_048_576
private typealias PluginHandler = @convention(c) (UnsafePointer<UInt8>?, Int) -> Void
private let transportLock = NSLock()
private var transportHandlers: [UInt32: PluginHandler] = [:]
private var transportPending: [UInt64: UInt32] = [:]
private var transportReplies: [Data] = []
private var transportBytes = 0
private func number(_ bytes: UnsafePointer<UInt8>, _ offset: Int, _ count: Int) -> UInt64 {
  var value: UInt64 = 0
  for i in 0..<count { value |= UInt64(bytes[offset + i]) << (8 * i) }
  return value
}
private func appendNumber(_ data: inout Data, _ value: UInt64, _ count: Int) {
  for i in 0..<count { data.append(UInt8(truncatingIfNeeded: value >> (8 * i))) }
}
private func transportFailure(_ id: UInt64, _ code: String, _ message: String) -> Data {
  var data = Data()
  appendNumber(&data, id, 8)
  appendNumber(&data, 1, 4)
  data.append(6)
  appendNumber(&data, 2, 4)
  for text in ["code", code, "message", message] {
    let bytes = Data(text.utf8)
    data.append(4)
    appendNumber(&data, UInt64(bytes.count), 4)
    data.append(bytes)
  }
  return data
}
@_cdecl("dotnative_register_channel")
public func pluginTransportRegister(
  _ id: UInt32, _ handler: (@convention(c) (UnsafePointer<UInt8>?, Int) -> Void)?
) -> Int32 {
  guard Thread.isMainThread, id != 0, let handler else { return -1 }
  transportLock.lock()
  defer { transportLock.unlock() }
  guard transportHandlers[id] == nil, transportHandlers.count < 256 else { return -1 }
  transportHandlers[id] = handler
  return 0
}
@_cdecl("dotnative_plugin_reply")
public func pluginTransportReply(_ bytes: UnsafePointer<UInt8>?, _ count: Int) -> Int32 {
  guard let bytes, count >= 13, count <= pluginLimit + 12 else { return -1 }
  let id = number(bytes, 0, 8)
  guard number(bytes, 8, 4) <= 2 else { return -1 }
  transportLock.lock()
  defer { transportLock.unlock() }
  guard transportPending.removeValue(forKey: id) != nil else { return 1 }
  let overloaded = transportBytes + count > 15 * pluginLimit
  let reply =
    overloaded
    ? transportFailure(id, "busy", "Plugin reply queue is full.") : Data(bytes: bytes, count: count)
  transportBytes += reply.count
  transportReplies.append(reply)
  return overloaded ? 2 : 0
}
@_cdecl("dotnative_send_plugin")
public func pluginTransportSend(_ bytes: UnsafePointer<UInt8>?, _ count: Int) -> Int32 {
  guard Thread.isMainThread, let bytes, count >= 16, count <= pluginLimit + 16 else { return -1 }
  let id = number(bytes, 0, 8)
  let channel = UInt32(number(bytes, 8, 4))
  let kind = number(bytes, 12, 4)
  guard id != 0, kind == 1 || (kind == 2 && count == 16) else { return -1 }
  transportLock.lock()
  if kind == 1 {
    guard transportPending[id] == nil, transportPending.count + transportReplies.count < 256 else {
      transportLock.unlock()
      return -1
    }
    transportPending[id] = channel
  } else {
    guard transportPending[id] == channel else {
      transportLock.unlock()
      return 0
    }
    transportPending.removeValue(forKey: id)
  }
  let handler = transportHandlers[channel]
  transportLock.unlock()
  if let handler {
    handler(bytes, count)
  } else if kind == 1 {
    let failure = transportFailure(
      id, "missing_plugin", "Plugin channel is not registered on this platform.")
    failure.withUnsafeBytes { raw in
      _ = pluginTransportReply(raw.bindMemory(to: UInt8.self).baseAddress, raw.count)
    }
  }
  return 0
}
@_cdecl("dotnative_poll_plugin")
public func pluginTransportPoll(_ buffer: UnsafeMutablePointer<UInt8>?, _ capacity: Int) -> Int {
  transportLock.lock()
  defer { transportLock.unlock() }
  guard let reply = transportReplies.first else { return 0 }
  guard let buffer, capacity >= reply.count else { return reply.count }
  reply.copyBytes(to: buffer, count: reply.count)
  transportReplies.removeFirst()
  transportBytes -= reply.count
  return reply.count
}
@_cdecl("dotnative_plugins_reset")
public func pluginTransportReset() -> Int32 {
  guard Thread.isMainThread else { return -1 }
  transportLock.lock()
  transportPending.removeAll()
  transportReplies.removeAll()
  transportBytes = 0
  let handlers = transportHandlers
  transportLock.unlock()
  for (channel, handler) in handlers {
    var packet = Data()
    appendNumber(&packet, 0, 8)
    appendNumber(&packet, UInt64(channel), 4)
    appendNumber(&packet, 3, 4)
    packet.withUnsafeBytes { handler($0.bindMemory(to: UInt8.self).baseAddress, $0.count) }
  }
  return 0
}

func connectPluginTransportToHost() {
  typealias Send = @convention(c) (UnsafePointer<UInt8>?, Int) -> Int32
  typealias Poll = @convention(c) (UnsafeMutablePointer<UInt8>?, Int) -> Int
  typealias Reset = @convention(c) () -> Int32
  typealias Connect = @convention(c) (Send, Poll, Reset) -> Void
  guard
    let symbol = dlsym(
      UnsafeMutableRawPointer(bitPattern: -2), "dotnative_app_set_service_transport")
  else { return }
  unsafeBitCast(symbol, to: Connect.self)(
    pluginTransportSend, pluginTransportPoll, pluginTransportReset)
}
