import Foundation

/// Per-thread "last error" for the C ABI: a failing dc_* call returns 0/NULL
/// and leaves a human-readable reason here for the caller to fetch.
enum LastError {
    private static let key = "DocuClickMac.lastError"

    static func set(_ message: String) {
        Thread.current.threadDictionary[key] = message
    }

    static func take() -> String? {
        let message = Thread.current.threadDictionary[key] as? String
        Thread.current.threadDictionary[key] = nil
        return message
    }
}

struct NativeError: Error, CustomStringConvertible {
    let description: String
    init(_ description: String) { self.description = description }
}

/// Runs an async operation synchronously. Only ever called from .NET
/// worker threads (the writer queue), never from the main thread.
func runBlocking<T>(_ operation: @escaping () async throws -> T) throws -> T {
    let semaphore = DispatchSemaphore(value: 0)
    var result: Result<T, Error>!
    Task.detached {
        do { result = .success(try await operation()) } catch { result = .failure(error) }
        semaphore.signal()
    }
    semaphore.wait()
    return try result.get()
}

func duplicateCString(_ string: String?) -> UnsafeMutablePointer<CChar>? {
    guard let string else { return nil }
    return strdup(string)
}

/// Returns and clears this thread's last error (caller frees with dc_free), or NULL.
@_cdecl("dc_last_error")
public func dc_last_error() -> UnsafeMutablePointer<CChar>? {
    duplicateCString(LastError.take())
}

@_cdecl("dc_free")
public func dc_free(_ pointer: UnsafeMutableRawPointer?) {
    free(pointer)
}
