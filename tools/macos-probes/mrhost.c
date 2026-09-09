// Minimal unsigned host: loads the probe dylib and exits. The baseline.
#include <dlfcn.h>
#include <stdio.h>
int main(int argc, char **argv) {
    if (argc < 2) { fprintf(stderr, "usage: mrhost <dylib>\n"); return 2; }
    if (!dlopen(argv[1], RTLD_NOW)) { fprintf(stderr, "dlopen failed: %s\n", dlerror()); return 1; }
    return 0;
}
