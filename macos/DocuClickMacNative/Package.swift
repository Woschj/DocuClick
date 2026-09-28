// swift-tools-version:5.9
// Native macOS layer for DocuClick.Mac: everything that needs Apple's
// Swift/Objective-C frameworks (ScreenCaptureKit, Accessibility, CGEventTap,
// Carbon hotkeys, AppKit window tweaks), exposed as a small C ABI (@_cdecl
// "dc_*" functions) that the .NET app calls via [LibraryImport].
// See include/docuclick_mac.h for the full contract.
import PackageDescription

let package = Package(
    name: "DocuClickMacNative",
    platforms: [.macOS(.v14)],
    products: [
        .library(name: "DocuClickMac", type: .dynamic, targets: ["DocuClickMac"])
    ],
    targets: [
        .target(
            name: "DocuClickMac",
            linkerSettings: [
                .linkedFramework("AppKit"),
                .linkedFramework("ApplicationServices"),
                .linkedFramework("Carbon"),
                .linkedFramework("CoreMedia"),
                .linkedFramework("ScreenCaptureKit")
            ])
    ]
)
