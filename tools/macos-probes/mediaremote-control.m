// S1 part 2: beyond metadata. Does the Apple-signed host also get
// transport commands, change notifications, and the owning app's identity?
// Metadata access does not prove control works.
#import <Foundation/Foundation.h>
#include <dlfcn.h>

typedef void (*MRGetInfo)(dispatch_queue_t, void (^)(NSDictionary *));
typedef Boolean (*MRSendCommand)(int, NSDictionary *);
typedef void (*MRRegisterNotifications)(dispatch_queue_t);
typedef void (*MRGetClient)(dispatch_queue_t, void (^)(id));
typedef CFStringRef (*MRClientGetBundleID)(id);
typedef CFStringRef (*MRClientGetParentAppBundleID)(id);

static MRGetInfo getInfo;

static void dumpNowPlaying(const char *label) {
    dispatch_semaphore_t sem = dispatch_semaphore_create(0);
    __block NSDictionary *r = nil;
    getInfo(dispatch_get_global_queue(0, 0), ^(NSDictionary *i) { r = [i copy]; dispatch_semaphore_signal(sem); });
    dispatch_semaphore_wait(sem, dispatch_time(DISPATCH_TIME_NOW, 3 * NSEC_PER_SEC));
    NSString *t = r[@"kMRMediaRemoteNowPlayingInfoTitle"] ?: @"(none)";
    NSString *a = r[@"kMRMediaRemoteNowPlayingInfoArtist"] ?: @"(none)";
    id rate = r[@"kMRMediaRemoteNowPlayingInfoPlaybackRate"] ?: @"?";
    id el   = r[@"kMRMediaRemoteNowPlayingInfoElapsedTime"] ?: @"?";
    NSData *art = r[@"kMRMediaRemoteNowPlayingInfoArtworkData"];
    fprintf(stderr, "  [%s] keys=%lu title=%s artist=%s rate=%s elapsed=%s artwork=%s\n",
            label, (unsigned long)r.count, t.UTF8String, a.UTF8String,
            [[rate description] UTF8String], [[el description] UTF8String],
            art ? [[NSString stringWithFormat:@"%lu bytes", (unsigned long)art.length] UTF8String] : "none");
}

__attribute__((constructor))
static void probe(void) {
    @autoreleasepool {
        fprintf(stderr, "\n=== host: %s ===\n", [[NSProcessInfo processInfo].processName UTF8String]);
        void *h = dlopen("/System/Library/PrivateFrameworks/MediaRemote.framework/MediaRemote", RTLD_NOW);
        if (!h) { fprintf(stderr, "dlopen failed\n"); return; }
        getInfo = (MRGetInfo)dlsym(h, "MRMediaRemoteGetNowPlayingInfo");
        MRSendCommand sendCommand = (MRSendCommand)dlsym(h, "MRMediaRemoteSendCommand");
        MRRegisterNotifications reg = (MRRegisterNotifications)dlsym(h, "MRMediaRemoteRegisterForNowPlayingNotifications");
        MRGetClient getClient = (MRGetClient)dlsym(h, "MRMediaRemoteGetNowPlayingClient");
        MRClientGetBundleID clientBundle = (MRClientGetBundleID)dlsym(h, "MRNowPlayingClientGetBundleIdentifier");
        MRClientGetParentAppBundleID clientParent = (MRClientGetParentAppBundleID)dlsym(h, "MRNowPlayingClientGetParentAppBundleIdentifier");

        fprintf(stderr, "-- 1. owning app identity --\n");
        if (getClient) {
            dispatch_semaphore_t s = dispatch_semaphore_create(0);
            getClient(dispatch_get_global_queue(0, 0), ^(id client) {
                if (!client) fprintf(stderr, "  client = nil\n");
                else {
                    CFStringRef b = clientBundle ? clientBundle(client) : NULL;
                    CFStringRef p = clientParent ? clientParent(client) : NULL;
                    fprintf(stderr, "  bundleID = %s\n", b ? [(__bridge NSString *)b UTF8String] : "(nil)");
                    fprintf(stderr, "  parentAppBundleID = %s\n", p ? [(__bridge NSString *)p UTF8String] : "(nil)");
                }
                dispatch_semaphore_signal(s);
            });
            dispatch_semaphore_wait(s, dispatch_time(DISPATCH_TIME_NOW, 3 * NSEC_PER_SEC));
        }

        fprintf(stderr, "-- 2. change notifications --\n");
        __block int events = 0;
        if (reg) {
            reg(dispatch_get_main_queue());
            for (NSString *n in @[@"kMRMediaRemoteNowPlayingInfoDidChangeNotification",
                                  @"kMRMediaRemoteNowPlayingApplicationIsPlayingDidChangeNotification",
                                  @"kMRNowPlayingPlaybackQueueChangedNotification"]) {
                [[NSNotificationCenter defaultCenter] addObserverForName:n object:nil queue:nil
                    usingBlock:^(NSNotification *note) { events++; fprintf(stderr, "  EVENT: %s\n", n.UTF8String); }];
            }
            fprintf(stderr, "  registered\n");
        }

        fprintf(stderr, "-- 3. transport commands --\n");
        dumpNowPlaying("before");
        if (sendCommand) {
            // 2 = togglePlayPause
            Boolean ok = sendCommand(2, nil);
            fprintf(stderr, "  SendCommand(togglePlayPause) -> %s\n", ok ? "accepted" : "REJECTED");
            [[NSRunLoop currentRunLoop] runUntilDate:[NSDate dateWithTimeIntervalSinceNow:1.5]];
            dumpNowPlaying("after toggle");
            ok = sendCommand(2, nil);
            fprintf(stderr, "  SendCommand(togglePlayPause back) -> %s\n", ok ? "accepted" : "REJECTED");
            [[NSRunLoop currentRunLoop] runUntilDate:[NSDate dateWithTimeIntervalSinceNow:1.5]];
            dumpNowPlaying("restored");
        }
        fprintf(stderr, "-- notifications received during test: %d --\n=== end ===\n", events);
    }
}
