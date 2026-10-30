import Foundation

/// Process-level APNs registration fan-out, independent of any vendor SDK.
@MainActor final class APNsObservers {
  static let shared = APNsObservers()
  private struct Observer {
    let registered: (Data) -> Void
    let failed: (String) -> Void
  }
  private var observers: [String: Observer] = [:]
  private var deviceToken: Data?
  func observe(_ id: String, registered: @escaping (Data) -> Void,
               failed: @escaping (String) -> Void) {
    observers[id] = Observer(registered: registered, failed: failed)
    if let token = deviceToken { registered(token) }
  }
  func remove(_ id: String) { observers.removeValue(forKey: id) }
  func didRegister(_ token: Data) {
    deviceToken = token
    for observer in Array(observers.values) { observer.registered(token) }
  }
  func didFail(_ message: String) {
    for observer in Array(observers.values) { observer.failed(message) }
  }
}
