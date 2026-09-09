// Why does MRMediaRemoteGetNowPlayingInfo not call back when Music.app owns
// the session, while QuickTime works? Longer timeout + client identity.
#import <Foundation/Foundation.h>
#include <dlfcn.h>
typedef void (*MRGetInfo)(dispatch_queue_t, void (^)(NSDictionary *));
typedef void (*MRGetClient)(dispatch_queue_t, void (^)(id));
typedef CFStringRef (*MRClientBundle)(id);
typedef void (*MRGetIsPlaying)(dispatch_queue_t, void (^)(Boolean));

__attribute__((constructor))
static void probe(void) { @autoreleasepool {
    void *h = dlopen("/System/Library/PrivateFrameworks/MediaRemote.framework/MediaRemote", RTLD_NOW);
    MRGetInfo getInfo = (MRGetInfo)dlsym(h, "MRMediaRemoteGetNowPlayingInfo");
    MRGetClient getClient = (MRGetClient)dlsym(h, "MRMediaRemoteGetNowPlayingClient");
    MRClientBundle bundle = (MRClientBundle)dlsym(h, "MRNowPlayingClientGetBundleIdentifier");
    MRGetIsPlaying isPlaying = (MRGetIsPlaying)dlsym(h, "MRMediaRemoteGetNowPlayingApplicationIsPlaying");

    fprintf(stderr, "-- client identity (5s) --\n");
    dispatch_semaphore_t s1 = dispatch_semaphore_create(0);
    getClient(dispatch_get_global_queue(0,0), ^(id c) {
        CFStringRef b = (c && bundle) ? bundle(c) : NULL;
        fprintf(stderr, "  client=%s bundle=%s\n", c ? "present" : "nil",
                b ? [(__bridge NSString *)b UTF8String] : "(nil)");
        dispatch_semaphore_signal(s1);
    });
    fprintf(stderr, "  %s\n", dispatch_semaphore_wait(s1, dispatch_time(DISPATCH_TIME_NOW, 5*NSEC_PER_SEC)) ? "TIMED OUT" : "ok");

    fprintf(stderr, "-- isPlaying (5s) --\n");
    dispatch_semaphore_t s2 = dispatch_semaphore_create(0);
    isPlaying(dispatch_get_global_queue(0,0), ^(Boolean p){ fprintf(stderr, "  isPlaying=%s\n", p?"YES":"NO"); dispatch_semaphore_signal(s2); });
    fprintf(stderr, "  %s\n", dispatch_semaphore_wait(s2, dispatch_time(DISPATCH_TIME_NOW, 5*NSEC_PER_SEC)) ? "TIMED OUT" : "ok");

    fprintf(stderr, "-- getNowPlayingInfo, 30s patience --\n");
    __block NSDictionary *r = nil;
    dispatch_semaphore_t s3 = dispatch_semaphore_create(0);
    NSDate *t0 = [NSDate date];
    getInfo(dispatch_get_global_queue(0,0), ^(NSDictionary *i){ r = [i copy]; dispatch_semaphore_signal(s3); });
    long to = dispatch_semaphore_wait(s3, dispatch_time(DISPATCH_TIME_NOW, 30*NSEC_PER_SEC));
    fprintf(stderr, "  elapsed=%.1fs %s keys=%lu\n", -[t0 timeIntervalSinceNow],
            to ? "TIMED OUT" : "returned", (unsigned long)r.count);
    for (NSString *k in [r.allKeys sortedArrayUsingSelector:@selector(compare:)]) {
        id v = r[k];
        NSString *d = [v isKindOfClass:NSData.class] ? [NSString stringWithFormat:@"<NSData %lu bytes>", (unsigned long)[(NSData*)v length]] : [NSString stringWithFormat:@"%@", v];
        if (d.length > 90) d = [[d substringToIndex:90] stringByAppendingString:@"..."];
        fprintf(stderr, "    %s = %s\n", k.UTF8String, d.UTF8String);
    }
}}
