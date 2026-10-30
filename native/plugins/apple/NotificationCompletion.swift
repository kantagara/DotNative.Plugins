import Foundation
@preconcurrency import UserNotifications

/// The OS completion belongs to the runtime, never to an individual plugin.
/// Duplicate replies are ignored and a stalled plugin cannot hold it forever.
@MainActor final class NotificationCompletion {
  private var remaining: Set<Int>
  private var options: UNNotificationPresentationOptions = []
  private var completion: ((UNNotificationPresentationOptions) -> Void)?
  init(count: Int, completion: @escaping (UNNotificationPresentationOptions) -> Void) {
    remaining = Set(0..<count)
    self.completion = completion
    if count == 0 { finish() }
    else {
      Task { @MainActor in
        try? await Task.sleep(nanoseconds: 5_000_000_000)
        self.finish()
      }
    }
  }
  func receive(_ index: Int, options: UNNotificationPresentationOptions) {
    guard completion != nil, remaining.remove(index) != nil else { return }
    self.options.formUnion(options)
    if remaining.isEmpty { finish() }
  }
  func finish() {
    guard let callback = completion else { return }
    completion = nil
    callback(options)
  }
}
