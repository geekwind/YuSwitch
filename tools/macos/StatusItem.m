// Menu-bar status item + window hide/show for the macOS desktop shell.
//
// Photino.NET has no status-item/tray API (open feature request
// tryphotino/photino.NET#171), so a tiny compiled helper owns the AppKit
// pieces: an NSStatusItem with a menu (打开界面 / 在浏览器中打开 / 退出) and
// orderOut/makeKeyAndOrderFront for close-to-menu-bar. Compiled by
// tools/make_macos_app.sh into Contents/MacOS/libYuSwitchHelper.dylib and
// P/Invoked from Gui/Photino/PhotinoShell.cs.

#import <Cocoa/Cocoa.h>

typedef void (*YSVoidCallback)(void);

static NSWindow *g_window = nil;
static NSStatusItem *g_statusItem = nil; // strong refs — NSStatusBar keeps the
static id g_target = nil;                // item alive, but ARC would collect locals

static YSVoidCallback g_onOpenBrowser = NULL;
static YSVoidCallback g_onQuit = NULL;

void YSShowWindow(void *window) {
    NSWindow *w = (NSWindow *)window;
    if (!w) return;
    [w makeKeyAndOrderFront:nil];
    [NSApp activateIgnoringOtherApps:YES];
}

void YSHideWindow(void *window) {
    NSWindow *w = (NSWindow *)window;
    if (!w) return;
    [w orderOut:nil];
}

// NSMenuItem actions need an object target; one shared instance suffices.
@interface YSStatusTarget : NSObject
@end

@implementation YSStatusTarget
- (void)ysOpenAction:(id)sender { YSShowWindow(g_window); }
- (void)ysBrowserAction:(id)sender { if (g_onOpenBrowser) g_onOpenBrowser(); }
- (void)ysQuitAction:(id)sender { if (g_onQuit) g_onQuit(); }
@end

// Installs the menu-bar item. `window` is the Photino NSWindow*; the two
// callbacks fire on the main (AppKit) thread and must stay valid for the app's
// lifetime (the managed side keeps the underlying delegates rooted).
void YSInstallStatusItem(void *window, YSVoidCallback onOpenBrowser, YSVoidCallback onQuit) {
    g_window = (NSWindow *)window;
    g_onOpenBrowser = onOpenBrowser;
    g_onQuit = onQuit;

    g_target = [[YSStatusTarget alloc] init];

    g_statusItem = [[NSStatusBar systemStatusBar] statusItemWithLength:NSVariableStatusItemLength];
    g_statusItem.button.title = @"禹枢";

    NSMenu *menu = [[NSMenu alloc] init];
    [menu addItemWithTitle:@"打开界面" action:@selector(ysOpenAction:) keyEquivalent:@""];
    [menu addItemWithTitle:@"在浏览器中打开" action:@selector(ysBrowserAction:) keyEquivalent:@""];
    [menu addItem:[NSMenuItem separatorItem]];
    [menu addItemWithTitle:@"退出" action:@selector(ysQuitAction:) keyEquivalent:@""];
    for (NSMenuItem *item in menu.itemArray) {
        item.target = g_target;
    }
    g_statusItem.menu = menu;
}
