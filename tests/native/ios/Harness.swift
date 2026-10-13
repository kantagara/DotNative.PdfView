import PDFKit
import UIKit

indirect enum PluginValue {
    case null
    case string(String)
    case integer(Int64)
    case map([String: PluginValue])
    var fields: [String: PluginValue] {
        if case .map(let x) = self { return x }
        return [:]
    }
    var string: String? {
        if case .string(let x) = self { return x }
        return nil
    }
    var integer: Int64? {
        if case .integer(let x) = self { return x }
        return nil
    }
}
@MainActor final class PluginReply {
    var onCancel: (() -> Void)?
    var error: String?
    var done = false
    func success(_ value: PluginValue = .null) {
        done = true
        onCancel = nil
    }
    func failure(_ code: String, _ message: String) {
        error = code
        done = true
        onCancel = nil
    }
}
@MainActor final class Channel {
    var handlers: [String: (PluginValue, PluginReply) -> Void] = [:]
    func handle(_ method: String, _ call: @escaping (PluginValue, PluginReply) -> Void) {
        handlers[method] = call
    }
}
@MainActor enum NativeChannels {
    static var presenter: UIViewController?
    static let api = Channel()
    static func channel(_ name: String) -> Channel { api }
}
@main @MainActor final class AppDelegate: UIResponder, UIApplicationDelegate {
    var window: UIWindow?
    func application(
        _ app: UIApplication,
        didFinishLaunchingWithOptions opts: [UIApplication.LaunchOptionsKey: Any]? = nil
    ) -> Bool {
        let w = UIWindow(frame: UIScreen.main.bounds)
        let controller = UIViewController()
        controller.view.backgroundColor = .white
        w.rootViewController = controller
        w.makeKeyAndVisible()
        window = w
        NativeChannels.presenter = controller
        PdfViewPlugin.register()
        Task {
            try? await Task.sleep(nanoseconds: 500_000_000)
            await run(controller)
        }
        return true
    }
    func check(_ ok: Bool, _ reason: String) throws {
        if !ok { throw NSError(domain: reason, code: 1) }
    }
    func pdf(_ view: UIView) -> PDFView? {
        if let x = view as? PDFView { return x }
        for child in view.subviews { if let p = pdf(child) { return p } }
        return nil
    }
    func run(_ controller: UIViewController) async {
        do {
            let path = NSTemporaryDirectory() + "smoke.pdf"
            let renderer = UIGraphicsPDFRenderer(
                bounds: CGRect(x: 0, y: 0, width: 400, height: 600))
            try renderer.writePDF(to: URL(fileURLWithPath: path)) { context in
                for n in 1...2 {
                    context.beginPage()
                    ("PDF PAGE \(n)" as NSString).draw(
                        at: CGPoint(x: 40, y: 100),
                        withAttributes: [.font: UIFont.systemFont(ofSize: 32)])
                }
            }
            func present(_ page: Int64) -> PluginReply {
                let reply = PluginReply()
                NativeChannels.api.handlers["present"]!(
                    .map([
                        "path": .string(path), "title": .string("Native PDF test"),
                        "initialPage": .integer(page),
                    ]), reply)
                return reply
            }
            let reply = present(0)
            try await Task.sleep(nanoseconds: 700_000_000)
            try check(!reply.done, "presentation completed early")
            guard let modal = controller.presentedViewController, let viewer = pdf(modal.view),
                let document = viewer.document
            else { throw NSError(domain: "no native PDFView", code: 1) }
            try check(document.pageCount == 2, "page count")
            viewer.go(to: document.page(at: 1)!)
            try await Task.sleep(nanoseconds: 200_000_000)
            try check(document.index(for: viewer.currentPage!) == 1, "navigation")
            reply.onCancel?()
            try await Task.sleep(nanoseconds: 600_000_000)
            try check(
                reply.done && controller.presentedViewController == nil, "cancel did not dismiss")
            let invalid = present(9)
            try check(invalid.error == "invalid_pdf", "invalid page")
            let again = present(0)
            try await Task.sleep(nanoseconds: 500_000_000)
            try check(!again.done, "reopen")
            again.onCancel?()
            try await Task.sleep(nanoseconds: 600_000_000)
            try
                "PASS native iOS PDF: PDFKit view, 2 pages, navigation, cancellation, invalid page, reopen"
                .write(
                    toFile: NSHomeDirectory() + "/Documents/result.txt", atomically: true,
                    encoding: .utf8)
        } catch {
            try? "FAIL: \(error)".write(
                toFile: NSHomeDirectory() + "/Documents/result.txt", atomically: true,
                encoding: .utf8)
        }
    }
}
