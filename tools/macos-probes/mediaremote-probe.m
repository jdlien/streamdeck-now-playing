// S1 spike: does MediaRemote return now-playing data when the *host process*
// is an Apple-signed system binary?
//
// Built as a dylib with a constructor, so merely loading it into a host runs
// the probe. That lets the same code run in our own binary and in /usr/bin/perl
// without the host needing to call anything.
//
//   clang -dynamiclib -framework Foundation -o mediaremote-probe.dylib mediaremote-probe.m
//   ./mrhost ./mediaremote-probe.dylib                      # unsigned host (baseline)
//   /usr/bin/perl -e 'use DynaLoader; DynaLoader::dl_load_file("'"$PWD"'/mediaremote-probe.dylib", 0x01);'
#import <Foundation/Foundation.h>
#include <dlfcn.h>

typedef void (*MRGetNowPlayingInfo)(dispatch_queue_t, void (^)(NSDictionary *));
typedef void (*MRGetIsPlaying)(dispatch_queue_t, void (^)(Boolean));

__attribute__((constructor))
static void probe(void) {
    @autoreleasepool {
        const char *host = [[[NSProcessInfo processInfo] processName] UTF8String];
        fprintf(stderr, "\n=== MediaRemote probe, host process: %s (pid %d) ===\n", host, getpid());

        void *h = dlopen("/System/Library/PrivateFrameworks/MediaRemote.framework/MediaRemote", RTLD_NOW);
        if (!h) { fprintf(stderr, "dlopen FAILED: %s\n", dlerror()); return; }

        MRGetNowPlayingInfo getInfo = (MRGetNowPlayingInfo)dlsym(h, "MRMediaRemoteGetNowPlayingInfo");
        MRGetIsPlaying isPlayingFn  = (MRGetIsPlaying)dlsym(h, "MRMediaRemoteGetNowPlayingApplicationIsPlaying");
        if (!getInfo) { fprintf(stderr, "symbol missing\n"); return; }

        dispatch_semaphore_t sem = dispatch_semaphore_create(0);
        __block NSDictionary *result = nil;
        getInfo(dispatch_get_global_queue(0, 0), ^(NSDictionary *info) {
            result = [info copy];
            dispatch_semaphore_signal(sem);
        });
        long timedOut = dispatch_semaphore_wait(sem, dispatch_time(DISPATCH_TIME_NOW, 5 * NSEC_PER_SEC));

        if (timedOut) { fprintf(stderr, "RESULT: callback never fired\n"); }
        else {
            fprintf(stderr, "RESULT: %lu keys\n", (unsigned long)result.count);
            for (NSString *k in [[result allKeys] sortedArrayUsingSelector:@selector(compare:)]) {
                id v = result[k];
                NSString *d = [v isKindOfClass:NSData.class]
                    ? [NSString stringWithFormat:@"<NSData %lu bytes>", (unsigned long)[(NSData *)v length]]
                    : [NSString stringWithFormat:@"%@", v];
                if (d.length > 100) d = [[d substringToIndex:100] stringByAppendingString:@"..."];
                fprintf(stderr, "  %s = %s\n", k.UTF8String, d.UTF8String);
            }
        }

        if (isPlayingFn) {
            dispatch_semaphore_t s2 = dispatch_semaphore_create(0);
            __block Boolean playing = false;
            isPlayingFn(dispatch_get_global_queue(0, 0), ^(Boolean p) { playing = p; dispatch_semaphore_signal(s2); });
            if (!dispatch_semaphore_wait(s2, dispatch_time(DISPATCH_TIME_NOW, 3 * NSEC_PER_SEC)))
                fprintf(stderr, "  isPlaying = %s\n", playing ? "YES" : "NO");
        }
        fprintf(stderr, "=== end (%s) ===\n", host);
    }
}
