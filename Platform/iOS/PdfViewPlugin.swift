import PDFKit
import UIKit

@MainActor final class PdfViewPlugin {
    private static var instance: PdfViewPlugin?
    private var viewer: PdfController?
    static func register() { if instance == nil { instance = PdfViewPlugin() } }
    private init() {
        NativeChannels.channel("dotnative.pdf-view").handle("present") { [weak self] args, reply in
            guard let self else {
                reply.failure("unavailable", "PDF plugin unavailable")
                return
            }
            guard self.viewer == nil else {
                reply.failure("busy", "A PDF viewer is already open")
                return
            }
            guard let path = args.fields["path"]?.string, let title = args.fields["title"]?.string,
                let presenter = NativeChannels.presenter
            else {
                reply.failure("invalid_argument", "PDF source and presenter required")
                return
            }
            let url = URL(fileURLWithPath: path).standardizedFileURL
            guard
                url.path.hasPrefix(
                    URL(fileURLWithPath: NSTemporaryDirectory()).standardizedFileURL.path + "/")
            else {
                reply.failure("invalid_argument", "PDF file must be in the private cache")
                return
            }
            let index = Int(args.fields["initialPage"]?.integer ?? 0)
            guard let document = PDFDocument(url: url), !document.isLocked, index >= 0,
                index < document.pageCount
            else {
                reply.failure(
                    "invalid_pdf", "Invalid, password-protected PDF or page outside document")
                return
            }
            let controller = PdfController(
                document: document, page: index, title: title,
                closeLabel: args.fields["closeLabel"]?.string ?? "Close"
            ) {
                [weak self] in
                self?.viewer = nil
                reply.success()
            }
            self.viewer = controller
            reply.onCancel = { [weak controller] in controller?.finish() }
            presenter.present(controller, animated: true)
        }
    }
}

@MainActor
private final class PdfController: UIViewController, UIAdaptivePresentationControllerDelegate {
    private let pdf = PDFView()
    private let pageLabel = UILabel()
    private var document: PDFDocument?
    private let initial: Int
    private let caption: String
    private let closeLabel: String
    private var onClose: (() -> Void)?
    private var pageObserver: NSObjectProtocol?
    private var backgroundObserver: NSObjectProtocol?
    init(
        document: PDFDocument, page: Int, title: String, closeLabel: String,
        onClose: @escaping () -> Void
    ) {
        self.closeLabel = closeLabel
        self.document = document
        initial = page
        caption = title
        self.onClose = onClose
        super.init(nibName: nil, bundle: nil)
        modalPresentationStyle = .fullScreen
    }
    required init?(coder: NSCoder) { fatalError("init(coder:) is unavailable") }
    override func viewDidLoad() {
        super.viewDidLoad()
        view.backgroundColor = .systemBackground
        let close = UIButton(type: .system)
        close.setTitle(closeLabel, for: .normal)
        close.addTarget(self, action: #selector(finish), for: .touchUpInside)
        let title = UILabel()
        title.text = caption
        title.numberOfLines = 2
        let toolbar = UIStackView(arrangedSubviews: [close, title, pageLabel])
        toolbar.spacing = 12
        toolbar.translatesAutoresizingMaskIntoConstraints = false
        view.addSubview(toolbar)
        pdf.translatesAutoresizingMaskIntoConstraints = false
        view.addSubview(pdf)
        NSLayoutConstraint.activate([
            toolbar.topAnchor.constraint(equalTo: view.safeAreaLayoutGuide.topAnchor),
            toolbar.leadingAnchor.constraint(equalTo: view.leadingAnchor, constant: 16),
            toolbar.trailingAnchor.constraint(equalTo: view.trailingAnchor, constant: -16),
            toolbar.heightAnchor.constraint(greaterThanOrEqualToConstant: 44),
            pdf.topAnchor.constraint(equalTo: toolbar.bottomAnchor),
            pdf.leadingAnchor.constraint(equalTo: view.leadingAnchor),
            pdf.trailingAnchor.constraint(equalTo: view.trailingAnchor),
            pdf.bottomAnchor.constraint(equalTo: view.safeAreaLayoutGuide.bottomAnchor),
        ])
        if let document {
            for index in 0..<document.pageCount {
                if let page = document.page(at: index) {
                    page.annotations.filter { $0.type == PDFAnnotationSubtype.link.rawValue }
                        .forEach { page.removeAnnotation($0) }
                }
            }
        }
        pdf.document = document
        pdf.autoScales = true
        pdf.displayMode = .singlePageContinuous
        pdf.displayDirection = .vertical
        if let page = document?.page(at: initial) { pdf.go(to: page) }
        // PDF annotations cannot launch arbitrary URL schemes; this viewer disables annotation interaction.
        pdf.isUserInteractionEnabled = true
        pageObserver = NotificationCenter.default.addObserver(
            forName: .PDFViewPageChanged, object: pdf, queue: .main
        ) { [weak self] _ in
            Task { @MainActor in self?.updatePage() }
        }
        backgroundObserver = NotificationCenter.default.addObserver(
            forName: UIApplication.didEnterBackgroundNotification, object: nil, queue: .main
        ) { [weak self] _ in
            Task { @MainActor in self?.finish() }
        }
        updatePage()
        presentationController?.delegate = self
    }
    private func updatePage() {
        if let doc = document, let page = pdf.currentPage {
            pageLabel.text = "\(doc.index(for: page) + 1) / \(doc.pageCount)"
        }
    }
    @objc func finish() {
        guard let completion = onClose else { return }
        onClose = nil
        if let pageObserver { NotificationCenter.default.removeObserver(pageObserver) }
        if let backgroundObserver { NotificationCenter.default.removeObserver(backgroundObserver) }
        pdf.document = nil
        document = nil
        dismiss(animated: true, completion: completion)
    }
    func presentationControllerDidDismiss(_ presentationController: UIPresentationController) {
        finish()
    }
}
