import UIKit
@MainActor enum PPluginNativeSymbolPlugin {
    static func register() {
        NativeChannels.channel("dotnative.plugins.pluginchannel").handle("read") { _, reply in
            reply.success(.string("iOS: " + UIDevice.current.systemVersion))
        }
    }
}