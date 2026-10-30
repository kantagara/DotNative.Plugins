import CryptoKit
import Foundation

#if os(iOS)
  import UIKit
#elseif os(macOS)
  import AppKit
#endif

indirect enum PluginValue {
  case null
  case bool(Bool)
  case integer(Int64)
  case double(Double)
  case string(String)
  case bytes(Data)
  case map([String: PluginValue])
  case list([PluginValue])
  var string: String? {
    if case .string(let s) = self { return s }
    return nil
  }
  var integer: Int64? {
    if case .integer(let n) = self { return n }
    return nil
  }
  var fields: [String: PluginValue] {
    if case .map(let m) = self { return m }
    return [:]
  }
}
struct ChannelDescriptor {
  fileprivate let id: UInt32

  init(name: String, version: UInt32 = 1) {
    precondition(
      !name.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty && version > 0,
      "Channel name and version must be valid")
    let digest = Array(SHA256.hash(data: Data("\(name)/v\(version)".utf8)))
    id =
      UInt32(digest[0]) | UInt32(digest[1]) << 8 | UInt32(digest[2]) << 16 | UInt32(digest[3]) << 24
    precondition(id != 0, "Channel name resolves to a reserved transport ID")
  }

  init(id: UInt32) { self.id = id }
}
struct PluginCodec {
  private var data: Data
  private var offset = 0
  init(_ data: Data) { self.data = data }
  mutating func number(_ count: Int) throws -> UInt64 {
    guard count <= data.count - offset else { throw CocoaError(.fileReadCorruptFile) }
    var n: UInt64 = 0
    for i in 0..<count { n |= UInt64(data[offset + i]) << (8 * i) }
    offset += count
    return n
  }
  mutating func blob() throws -> Data {
    let count = Int(try number(4))
    guard count <= 1_048_576, count <= data.count - offset else {
      throw CocoaError(.fileReadCorruptFile)
    }
    defer { offset += count }
    return data.subdata(in: offset..<offset + count)
  }
  mutating func value(_ depth: Int = 0) throws -> PluginValue {
    guard depth <= 16 else { throw CocoaError(.fileReadCorruptFile) }
    switch try number(1) {
    case 0: return .null
    case 1: return .bool(false)
    case 2: return .bool(true)
    case 3: return .integer(Int64(bitPattern: try number(8)))
    case 8: return .double(Double(bitPattern: try number(8)))
    case 4:
      guard let text = String(data: try blob(), encoding: .utf8) else {
        throw CocoaError(.fileReadCorruptFile)
      }
      return .string(text)
    case 5: return .bytes(try blob())
    case 6:
      let count = Int(try number(4))
      guard count <= 4096 else { throw CocoaError(.fileReadCorruptFile) }
      var map: [String: PluginValue] = [:]
      for _ in 0..<count {
        guard let key = try value(depth + 1).string, map[key] == nil else {
          throw CocoaError(.fileReadCorruptFile)
        }
        map[key] = try value(depth + 1)
      }
      return .map(map)
    case 7:
      let count = Int(try number(4))
      guard count <= 4096 else { throw CocoaError(.fileReadCorruptFile) }
      var list: [PluginValue] = []
      for _ in 0..<count { list.append(try value(depth + 1)) }
      return .list(list)
    default: throw CocoaError(.fileReadCorruptFile)
    }
  }
  var exhausted: Bool { offset == data.count }
  static func append(_ n: UInt64, count: Int, to data: inout Data) {
    for i in 0..<count { data.append(UInt8(truncatingIfNeeded: n >> (8 * i))) }
  }
  static func encode(_ value: PluginValue, into data: inout Data, depth: Int = 0) throws {
    guard depth <= 16, data.count <= 1_048_576 else { throw CocoaError(.coderInvalidValue) }
    switch value {
    case .null: data.append(0)
    case .bool(let b): data.append(b ? 2 : 1)
    case .integer(let n):
      data.append(3)
      append(UInt64(bitPattern: n), count: 8, to: &data)
    case .double(let n):
      data.append(8)
      append(n.bitPattern, count: 8, to: &data)
    case .string(let s):
      data.append(4)
      let text = Data(s.utf8)
      guard text.count <= 1_048_576 - data.count - 4 else { throw CocoaError(.coderInvalidValue) }
      append(UInt64(text.count), count: 4, to: &data)
      data.append(text)
    case .bytes(let bytes):
      guard bytes.count <= 1_048_576 - data.count - 5 else { throw CocoaError(.coderInvalidValue) }
      data.append(5)
      append(UInt64(bytes.count), count: 4, to: &data)
      data.append(bytes)
    case .map(let map):
      guard map.count <= 4096 else { throw CocoaError(.coderInvalidValue) }
      data.append(6)
      append(UInt64(map.count), count: 4, to: &data)
      for (key, value) in map {
        try encode(.string(key), into: &data, depth: depth + 1)
        try encode(value, into: &data, depth: depth + 1)
      }
    case .list(let list):
      guard list.count <= 4096 else { throw CocoaError(.coderInvalidValue) }
      data.append(7)
      append(UInt64(list.count), count: 4, to: &data)
      for value in list { try encode(value, into: &data, depth: depth + 1) }
    }
    guard data.count <= 1_048_576 else { throw CocoaError(.coderInvalidValue) }
  }
}
@MainActor final class PluginReply {
  let id: UInt64
  private var completed = false
  var onCancel: (() -> Void)?
  init(_ id: UInt64) { self.id = id }
  @discardableResult func success(_ value: PluginValue = .null) -> Bool { finish(0, value) }
  func failure(_ code: String, _ message: String, _ details: PluginValue = .null) {
    _ = finish(1, .map(["code": .string(code), "message": .string(message), "details": details]))
  }
  func cancel() {
    guard !completed else { return }
    completed = true
    onCancel?()
    onCancel = nil
  }
  private func finish(_ status: UInt64, _ value: PluginValue) -> Bool {
    guard !completed else { return false }
    completed = true
    onCancel = nil
    NativeChannels.pending.removeValue(forKey: id)
    var payload = Data()
    var responseStatus = status
    do {
      try PluginCodec.encode(value, into: &payload)
      guard payload.count <= 1_048_576 else { throw CocoaError(.coderInvalidValue) }
    } catch {
      payload = Data()
      responseStatus = 1
      try! PluginCodec.encode(
        .map([
          "code": .string("invalid_reply"),
          "message": .string("Plugin reply exceeds codec limits."),
        ]),
        into: &payload)
    }
    var data = Data()
    PluginCodec.append(id, count: 8, to: &data)
    PluginCodec.append(responseStatus, count: 4, to: &data)
    data.append(payload)
    return data.withUnsafeBytes { raw in
      pluginTransportReply(raw.bindMemory(to: UInt8.self).baseAddress!, data.count) == 0
    }
  }
}
@MainActor final class NativeChannel {
  var onReset: (() -> Void)?
  var methodCallHandler: ((String, PluginValue, PluginReply) -> Void)?
  var handlers: [String: (PluginValue, PluginReply) -> Void] = [:]
  func handle(_ method: String, _ handler: @escaping (PluginValue, PluginReply) -> Void) {
    precondition(handlers[method] == nil)
    handlers[method] = handler
  }
}
@MainActor enum NativeChannels {
  static var channels: [UInt32: NativeChannel] = [:]
  static var pending: [UInt64: PluginReply] = [:]
  static func channel(_ name: String, version: UInt32 = 1) -> NativeChannel {
    channel(ChannelDescriptor(name: name, version: version))
  }

  static func channel(_ descriptor: ChannelDescriptor) -> NativeChannel {
    if let channel = channels[descriptor.id] { return channel }
    let channel = NativeChannel()
    precondition(
      pluginTransportRegister(descriptor.id, pluginDispatch) == 0, "Duplicate plugin channel ID")
    channels[descriptor.id] = channel
    return channel
  }
  static func dispatch(_ bytes: Data) {
    var reader = PluginCodec(bytes)
    guard let id = try? reader.number(8), let channel = try? reader.number(4),
      let kind = try? reader.number(4)
    else { return }
    if kind == 3 {
      let calls = Array(pending.values)
      pending.removeAll()
      for call in calls { call.cancel() }
      channels[UInt32(channel)]?.onReset?()
      return
    }
    if kind == 2 {
      pending.removeValue(forKey: id)?.cancel()
      return
    }
    let reply = PluginReply(id)
    pending[id] = reply
    do {
      let call = try reader.value().fields
      guard reader.exhausted, let method = call["method"]?.string else {
        throw CocoaError(.fileReadCorruptFile)
      }
      if let handler = channels[UInt32(channel)]?.handlers[method] {
        handler(call["arguments"] ?? .null, reply)
      } else if let handler = channels[UInt32(channel)]?.methodCallHandler {
        handler(method, call["arguments"] ?? .null, reply)
      } else {
        reply.failure("not_implemented", "Unknown plugin method")
      }
    } catch { reply.failure("invalid_arguments", error.localizedDescription) }
  }
  #if os(iOS)
    static var presenter: UIViewController? {
      guard
        let scene = UIApplication.shared.connectedScenes.compactMap({ $0 as? UIWindowScene }).first(
          where: { $0.activationState == .foregroundActive }),
        var controller = scene.windows.first(where: { $0.isKeyWindow })?.rootViewController
      else { return nil }
      while let presented = controller.presentedViewController { controller = presented }
      return controller
    }
  #elseif os(macOS)
    static var window: NSWindow? {
      NSApp.keyWindow ?? NSApp.mainWindow
        ?? NSApp.windows.first { $0.isVisible && $0.canBecomeMain }
    }
    static var presenter: NSViewController? { window?.contentViewController }
  #endif
}
private func pluginDispatch(_ bytes: UnsafePointer<UInt8>?, _ count: Int) {
  guard let bytes = bytes else { return }
  let owned = Data(bytes: bytes, count: count)
  // The platform transport guarantees the OS main thread. Copy before any defer.
  MainActor.assumeIsolated { NativeChannels.dispatch(owned) }
}
