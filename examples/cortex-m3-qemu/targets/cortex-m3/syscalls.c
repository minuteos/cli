#include <stdint.h>
#include <sys/stat.h>

/* ARM semihosting call: BKPT 0xAB with op in r0, arg block in r1. */
static int semihost(int op, void *arg)
{
    register int r0 __asm("r0") = op;
    register void *r1 __asm("r1") = arg;
    __asm volatile("bkpt 0xAB" : "+r"(r0) : "r"(r1) : "memory");
    return r0;
}

#define SYS_WRITEC 0x03
#define SYS_EXIT   0x18

void _exit(int code)
{
    (void)code;
    /* ADP_Stopped_ApplicationExit = 0x20026 -> qemu exits cleanly. */
    semihost(SYS_EXIT, (void *)0x20026);
    while (1) { }
}

int _write(int fd, const char *buf, int len)
{
    (void)fd;
    for (int i = 0; i < len; i++)
    {
        char c = buf[i];
        semihost(SYS_WRITEC, &c);
    }
    return len;
}

/* minimal heap for newlib's stdio */
static char heap[8192];
static char *heap_ptr = heap;

void *_sbrk(int incr)
{
    char *prev = heap_ptr;
    if (heap_ptr + incr > heap + sizeof(heap)) return (void *)-1;
    heap_ptr += incr;
    return prev;
}

/* Override newlib's atexit: with -nostartfiles the reentrancy struct is not
   set up, and crtbegin's register_fini calls atexit() during static init,
   which would corrupt memory. Tests exit via semihosting, so dtors are moot. */
int atexit(void (*fn)(void)) { (void)fn; return 0; }

int _read(int fd, char *buf, int len) { (void)fd; (void)buf; (void)len; return 0; }
int _close(int fd) { (void)fd; return -1; }
int _lseek(int fd, int off, int whence) { (void)fd; (void)off; (void)whence; return 0; }
int _fstat(int fd, struct stat *st) { (void)fd; st->st_mode = S_IFCHR; return 0; }
int _isatty(int fd) { (void)fd; return 1; }
int _kill(int pid, int sig) { (void)pid; (void)sig; return -1; }
int _getpid(void) { return 1; }
