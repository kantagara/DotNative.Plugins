import Foundation
import CryptoKit
@MainActor enum StandaloneProbe {
  static func register() {
    let channel = NativeChannels.channel("dotnative.standalone.probe")
    channel.handle("sha256") { value, reply in
      guard case .bytes(let bytes) = value else { reply.failure("arguments", "Expected bytes"); return }
      reply.success(.bytes(Data(SHA256.hash(data: bytes))))
    }
    channel.handle("wait") { _, _ in }
    channel.handle("echo") { value, reply in reply.success(value) }
    channel.handle("fail") { _, reply in reply.failure("fixture", "Native failure") }
  }
}
