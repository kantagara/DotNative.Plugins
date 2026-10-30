import FirebaseCore
import FirebaseRemoteConfig
import Foundation

@MainActor final class FirebaseRemoteConfigPlugin {
  private static var registered = false
  static func register() {
    guard !registered else { return }
    registered = true
    let channel = NativeChannels.channel("dotnative.firebase.remoteconfig")
    channel.handle("initialize") { args, reply in
      let fields = args.fields
      guard let apiKey = fields["apiKey"]?.string, !apiKey.isEmpty,
        let appID = fields["applicationId"]?.string, !appID.isEmpty,
        let projectID = fields["projectId"]?.string, !projectID.isEmpty,
        let senderID = fields["senderId"]?.string, !senderID.isEmpty
      else {
        reply.failure("invalid_argument", "Firebase options are incomplete")
        return
      }
      if let current = FirebaseApp.app() {
        guard current.options.googleAppID == appID && current.options.projectID == projectID else {
          reply.failure("firebase", "Firebase is already configured for a different app")
          return
        }
      } else {
        let options = FirebaseOptions(googleAppID: appID, gcmSenderID: senderID)
        options.apiKey = apiKey
        options.projectID = projectID
        FirebaseApp.configure(options: options)
      }
      reply.success()
    }
    channel.handle("setDefaults") { args, reply in
      guard FirebaseApp.app() != nil else {
        reply.failure("not_initialized", "Initialize Firebase before using Remote Config")
        return
      }
      let values = args.fields.compactMapValues { $0.string }
      RemoteConfig.remoteConfig().ensureInitialized { error in
        DispatchQueue.main.async {
          if let error {
            reply.failure("firebase", error.localizedDescription)
          } else {
            RemoteConfig.remoteConfig().setDefaults(values.mapValues { $0 as NSString })
            reply.success()
          }
        }
      }
    }
    channel.handle("fetch") { _, reply in
      guard FirebaseApp.app() != nil else {
        reply.failure("not_initialized", "Initialize Firebase before using Remote Config")
        return
      }
      RemoteConfig.remoteConfig().fetchAndActivate { _, error in
        DispatchQueue.main.async {
          if let error {
            reply.failure("firebase", error.localizedDescription)
          } else {
            reply.success()
          }
        }
      }
    }
    channel.handle("getString") { args, reply in
      guard FirebaseApp.app() != nil else {
        reply.failure("not_initialized", "Initialize Firebase before using Remote Config")
        return
      }
      guard let key = args.fields["key"]?.string, !key.isEmpty else {
        reply.failure("invalid_argument", "Missing key")
        return
      }
      RemoteConfig.remoteConfig().ensureInitialized { error in
        DispatchQueue.main.async {
          if let error {
            reply.failure("firebase", error.localizedDescription)
          } else {
            reply.success(.string(RemoteConfig.remoteConfig().configValue(forKey: key).stringValue))
          }
        }
      }
    }
  }
}
