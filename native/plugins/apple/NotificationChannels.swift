#if os(iOS)
import Foundation
import UIKit
@preconcurrency import UserNotifications

/// One owner for the process-wide notification delegate. Plugins register by ID;
/// replacing/removing one registration does not affect the other plugins.
@MainActor enum NativeNotifications {
  static func register(_ id: String, delegate: UNUserNotificationCenterDelegate) {
    NotificationMultiplexer.shared.register(id, delegate: delegate)
  }
  static func unregister(_ id: String) {
    NotificationMultiplexer.shared.delegates.removeValue(forKey: id)
  }

  static func observeAPNs(_ id: String, registered: @escaping (Data) -> Void,
                          failed: @escaping (String) -> Void) {
    APNsObservers.shared.observe(id, registered: registered, failed: failed)
  }
  static func removeAPNsObserver(_ id: String) { APNsObservers.shared.remove(id) }

}

@MainActor private final class NotificationMultiplexer: NSObject, UNUserNotificationCenterDelegate {
  static let shared = NotificationMultiplexer()
  var delegates: [String: UNUserNotificationCenterDelegate] = [:]
  private var previous: UNUserNotificationCenterDelegate?

  func register(_ id: String, delegate: UNUserNotificationCenterDelegate) {
    let center = UNUserNotificationCenter.current()
    if center.delegate !== self {
      // Preserve a host delegate installed before the runtime takes ownership.
      previous = center.delegate
      center.delegate = self
    }
    delegates[id] = delegate
  }

  private var snapshot: [UNUserNotificationCenterDelegate] {
    var result = delegates.sorted { $0.key < $1.key }.map { $0.value }
    if let previous, !result.contains(where: { $0 === previous }) { result.append(previous) }
    return result
  }

  nonisolated func userNotificationCenter(_ center: UNUserNotificationCenter,
      willPresent notification: UNNotification,
      withCompletionHandler completionHandler: @escaping (UNNotificationPresentationOptions) -> Void) {
    Task { @MainActor in
      let targets = snapshot.filter {
        $0.responds(to: #selector(UNUserNotificationCenterDelegate.userNotificationCenter(_:willPresent:withCompletionHandler:)))
      }
      let replies = NotificationCompletion(count: targets.count, completion: completionHandler)
      for (index, target) in targets.enumerated() {
        target.userNotificationCenter?(center, willPresent: notification) { options in
          Task { @MainActor in replies.receive(index, options: options) }
        }
      }
    }
  }

  nonisolated func userNotificationCenter(_ center: UNUserNotificationCenter,
      didReceive response: UNNotificationResponse, withCompletionHandler completionHandler: @escaping () -> Void) {
    Task { @MainActor in
      let targets = snapshot.filter {
        $0.responds(to: #selector(UNUserNotificationCenterDelegate.userNotificationCenter(_:didReceive:withCompletionHandler:)))
      }
      let replies = NotificationCompletion(count: targets.count) { _ in completionHandler() }
      for (index, target) in targets.enumerated() {
        target.userNotificationCenter?(center, didReceive: response) {
          Task { @MainActor in replies.receive(index, options: []) }
        }
      }
    }
  }

  nonisolated func userNotificationCenter(_ center: UNUserNotificationCenter,
                                          openSettingsFor notification: UNNotification?) {
    Task { @MainActor in
      for target in snapshot { target.userNotificationCenter?(center, openSettingsFor: notification) }
    }
  }
}

@_cdecl("dotnative_apns_registered")
@MainActor public func dotnativeAPNsRegistered(_ bytes: UnsafePointer<UInt8>?, _ count: Int) {
  guard let bytes, count > 0 else { return }
  APNsObservers.shared.didRegister(Data(bytes: bytes, count: count))
}

@_cdecl("dotnative_apns_failed")
@MainActor public func dotnativeAPNsFailed(_ message: UnsafePointer<CChar>?) {
  APNsObservers.shared.didFail(message.map { String(cString: $0) } ?? "APNs registration failed")
}
#endif
