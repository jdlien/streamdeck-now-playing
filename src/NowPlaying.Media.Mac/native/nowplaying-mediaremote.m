// The MediaRemote helper.
//
// macOS stopped answering MRMediaRemoteGetNowPlayingInfo for ordinary processes
// in 15.4: the callback fires and the dictionary is empty. It still answers an
// Apple platform binary, so this is built as a dylib and loaded into
// /usr/bin/perl, which runs it from a constructor. The plugin never calls
// MediaRemote itself; it supervises this and talks line-JSON over the pipes.
//
// Verified on macOS 26.5.2: the identical code returns 0 keys in our own
// process and full metadata, transport commands and change notifications when
// hosted by perl.
//
// Protocol, one JSON object per line:
//
//   out  {"type":"now", "bundle":..., "title":..., "artist":..., "album":...,
//         "duration":s, "elapsed":s, "rate":n, "artwork":"<sha1>", "stale":bool}
//   out  {"type":"artwork", "key":"<sha1>", "mime":..., "data":"<base64>"}
//   out  {"type":"hello", "pid":n}
//   in   next | previous | toggle | refresh | artwork <sha1> | quit
//
// "stale" marks the case that forced the router's existence: with Music.app
// owning the session, GetNowPlayingClient and IsPlaying answer but
// GetNowPlayingInfo never calls back at all. Rather than hang, the helper
// reports which app owns the session and says the metadata is unavailable, and
// the plugin asks Music directly over AppleScript.
#import <Foundation/Foundation.h>
#include <dlfcn.h>
#include <CommonCrypto/CommonDigest.h>

typedef void (*MRGetInfo)(dispatch_queue_t, void (^)(NSDictionary *));
typedef void (*MRGetClient)(dispatch_queue_t, void (^)(id));
typedef void (*MRGetIsPlaying)(dispatch_queue_t, void (^)(Boolean));
typedef Boolean (*MRSendCommand)(int, NSDictionary *);
typedef void (*MRRegisterNotifications)(dispatch_queue_t);
typedef CFStringRef (*MRClientBundleID)(id);

static MRGetInfo gGetInfo;
static MRGetClient gGetClient;
static MRGetIsPlaying gGetIsPlaying;
static MRSendCommand gSendCommand;
static MRClientBundleID gClientBundleID;

static NSString *gArtworkKey;   // sha1 of the artwork last announced
static NSData *gArtworkData;
static NSString *gArtworkMime;
static NSString *gLastLine;     // so unchanged state is not re-sent

/// A metadata read that never returns is the Music.app case; bound every call.
static const NSTimeInterval kCallTimeout = 1.5;

static void emit(NSDictionary *object) {
    NSError *error = nil;
    NSData *json = [NSJSONSerialization dataWithJSONObject:object options:0 error:&error];
    if (!json) { return; }
    NSMutableData *line = [json mutableCopy];
    [line appendBytes:"\n" length:1];
    fwrite(line.bytes, 1, line.length, stdout);
    fflush(stdout);
}

static NSString *sha1Hex(NSData *data) {
    unsigned char digest[CC_SHA1_DIGEST_LENGTH];
    CC_SHA1(data.bytes, (CC_LONG)data.length, digest);
    NSMutableString *hex = [NSMutableString stringWithCapacity:CC_SHA1_DIGEST_LENGTH * 2];
    for (int i = 0; i < CC_SHA1_DIGEST_LENGTH; i++) { [hex appendFormat:@"%02x", digest[i]]; }
    return hex;
}

/// The bundle id of whatever owns the session. Answers even for Music, which is
/// what lets the plugin route around the metadata call that never returns.
static NSString *currentBundleId(void) {
    if (!gGetClient) { return nil; }
    __block NSString *result = nil;
    dispatch_semaphore_t done = dispatch_semaphore_create(0);
    gGetClient(dispatch_get_global_queue(0, 0), ^(id client) {
        if (client && gClientBundleID) {
            CFStringRef bundle = gClientBundleID(client);
            if (bundle) { result = [(__bridge NSString *)bundle copy]; }
        }
        dispatch_semaphore_signal(done);
    });
    dispatch_semaphore_wait(done, dispatch_time(DISPATCH_TIME_NOW, (int64_t)(kCallTimeout * NSEC_PER_SEC)));
    return result;
}

static BOOL currentIsPlaying(void) {
    if (!gGetIsPlaying) { return NO; }
    __block BOOL playing = NO;
    dispatch_semaphore_t done = dispatch_semaphore_create(0);
    gGetIsPlaying(dispatch_get_global_queue(0, 0), ^(Boolean value) {
        playing = value ? YES : NO;
        dispatch_semaphore_signal(done);
    });
    dispatch_semaphore_wait(done, dispatch_time(DISPATCH_TIME_NOW, (int64_t)(kCallTimeout * NSEC_PER_SEC)));
    return playing;
}

static void publish(void) {
    NSString *bundle = currentBundleId();

    __block NSDictionary *info = nil;
    dispatch_semaphore_t done = dispatch_semaphore_create(0);
    gGetInfo(dispatch_get_global_queue(0, 0), ^(NSDictionary *value) {
        info = [value copy];
        dispatch_semaphore_signal(done);
    });
    BOOL timedOut = dispatch_semaphore_wait(
        done, dispatch_time(DISPATCH_TIME_NOW, (int64_t)(kCallTimeout * NSEC_PER_SEC))) != 0;

    NSMutableDictionary *out = [NSMutableDictionary dictionary];
    out[@"type"] = @"now";
    out[@"bundle"] = bundle ?: [NSNull null];
    // No metadata and something is playing means the provider is refusing to
    // answer for this app, not that nothing is on.
    // The (BOOL) cast matters: without it the expression is an int and
    // NSNumber boxes it as a number, so this arrives as 0/1 rather than
    // false/true and a strict JSON reader rejects the whole message.
    out[@"stale"] = @((BOOL)(timedOut || (info.count == 0 && bundle != nil)));
    out[@"playing"] = @(currentIsPlaying());

    if (info.count > 0) {
        NSString *title = info[@"kMRMediaRemoteNowPlayingInfoTitle"];
        NSString *artist = info[@"kMRMediaRemoteNowPlayingInfoArtist"];
        NSString *album = info[@"kMRMediaRemoteNowPlayingInfoAlbum"];
        if (title) { out[@"title"] = title; }
        if (artist) { out[@"artist"] = artist; }
        if (album) { out[@"album"] = album; }

        NSNumber *duration = info[@"kMRMediaRemoteNowPlayingInfoDuration"];
        NSNumber *elapsed = info[@"kMRMediaRemoteNowPlayingInfoElapsedTime"];
        NSNumber *rate = info[@"kMRMediaRemoteNowPlayingInfoPlaybackRate"];
        if (duration) { out[@"duration"] = duration; }
        if (elapsed) { out[@"elapsed"] = elapsed; }
        if (rate) { out[@"rate"] = rate; }

        // The player's own clock for the elapsed value, so the plugin can
        // extrapolate rather than trust a number that may be seconds old.
        NSDate *stamp = info[@"kMRMediaRemoteNowPlayingInfoTimestamp"];
        if ([stamp isKindOfClass:NSDate.class]) {
            out[@"elapsedAt"] = @([stamp timeIntervalSince1970]);
        }

        NSData *art = info[@"kMRMediaRemoteNowPlayingInfoArtworkData"];
        if ([art isKindOfClass:NSData.class] && art.length > 0) {
            NSString *key = sha1Hex(art);
            out[@"artwork"] = key;
            if (![key isEqualToString:gArtworkKey]) {
                gArtworkKey = key;
                gArtworkData = art;
                gArtworkMime = info[@"kMRMediaRemoteNowPlayingInfoArtworkMIMEType"] ?: @"image/jpeg";
            }
        }
    }

    NSData *encoded = [NSJSONSerialization dataWithJSONObject:out options:NSJSONWritingSortedKeys error:nil];
    NSString *line = [[NSString alloc] initWithData:encoded encoding:NSUTF8StringEncoding];
    if ([line isEqualToString:gLastLine]) { return; }   // nothing moved
    gLastLine = line;
    emit(out);
}

static void sendArtwork(NSString *key) {
    if (!gArtworkData || ![key isEqualToString:gArtworkKey]) { return; }
    emit(@{
        @"type": @"artwork",
        @"key": gArtworkKey,
        @"mime": gArtworkMime ?: @"image/jpeg",
        @"data": [gArtworkData base64EncodedStringWithOptions:0],
    });
}

/// Commands arrive on stdin. Read on a separate thread so the run loop stays
/// free to service the notifications.
static void readCommands(void) {
    char buffer[512];
    while (fgets(buffer, sizeof(buffer), stdin)) {
        NSString *line = [[NSString stringWithUTF8String:buffer]
            stringByTrimmingCharactersInSet:NSCharacterSet.whitespaceAndNewlineCharacterSet];
        if (line.length == 0) { continue; }

        if ([line isEqualToString:@"quit"]) { exit(0); }
        if ([line isEqualToString:@"refresh"]) { gLastLine = nil; dispatch_async(dispatch_get_main_queue(), ^{ publish(); }); continue; }
        if ([line hasPrefix:@"artwork "]) {
            NSString *key = [line substringFromIndex:8];
            dispatch_async(dispatch_get_main_queue(), ^{ sendArtwork(key); });
            continue;
        }

        // MRMediaRemoteCommand: 0 play, 1 pause, 2 toggle, 4 next, 5 previous.
        int command = -1;
        if ([line isEqualToString:@"toggle"]) { command = 2; }
        else if ([line isEqualToString:@"next"]) { command = 4; }
        else if ([line isEqualToString:@"previous"]) { command = 5; }
        if (command >= 0 && gSendCommand) {
            Boolean accepted = gSendCommand(command, nil);
            emit(@{ @"type": @"command", @"name": line, @"accepted": @(accepted ? YES : NO) });
        }
    }
    exit(0);   // the plugin closed the pipe
}

__attribute__((constructor))
static void start(void) {
    @autoreleasepool {
        void *handle = dlopen("/System/Library/PrivateFrameworks/MediaRemote.framework/MediaRemote", RTLD_NOW);
        if (!handle) {
            emit(@{ @"type": @"fatal", @"error": @"MediaRemote could not be loaded" });
            exit(1);
        }

        gGetInfo = (MRGetInfo)dlsym(handle, "MRMediaRemoteGetNowPlayingInfo");
        gGetClient = (MRGetClient)dlsym(handle, "MRMediaRemoteGetNowPlayingClient");
        gGetIsPlaying = (MRGetIsPlaying)dlsym(handle, "MRMediaRemoteGetNowPlayingApplicationIsPlaying");
        gSendCommand = (MRSendCommand)dlsym(handle, "MRMediaRemoteSendCommand");
        gClientBundleID = (MRClientBundleID)dlsym(handle, "MRNowPlayingClientGetBundleIdentifier");
        MRRegisterNotifications registerFn =
            (MRRegisterNotifications)dlsym(handle, "MRMediaRemoteRegisterForNowPlayingNotifications");

        if (!gGetInfo || !registerFn) {
            emit(@{ @"type": @"fatal", @"error": @"MediaRemote symbols are missing" });
            exit(1);
        }

        emit(@{ @"type": @"hello", @"pid": @(getpid()) });

        registerFn(dispatch_get_main_queue());
        for (NSString *name in @[@"kMRMediaRemoteNowPlayingInfoDidChangeNotification",
                                 @"kMRMediaRemoteNowPlayingApplicationIsPlayingDidChangeNotification",
                                 @"kMRNowPlayingPlaybackQueueChangedNotification"]) {
            [NSNotificationCenter.defaultCenter addObserverForName:name object:nil queue:nil
                usingBlock:^(NSNotification *note) { publish(); }];
        }

        [NSThread detachNewThreadWithBlock:^{ readCommands(); }];
        publish();

        // A slow backstop for players that change without notifying. The Windows
        // build re-syncs every 30 s for the same reason.
        [NSTimer scheduledTimerWithTimeInterval:30.0 repeats:YES block:^(NSTimer *t) { publish(); }];

        CFRunLoopRun();
        exit(0);
    }
}
