#include <stdio.h>

volatile int counter;
int limit = 5;

/* A "peripheral" register for the SVD end-to-end test: the test .svd maps
 * TESTP.MAGIC at this address, well above .data/.bss and below the stack. */
#define TESTP_MAGIC (*(volatile unsigned int *)0x2000F000)

#ifdef USE_ITM
/* ITM stimulus port 0. Under renode this lands in the MinuteItmCapture
 * overlay, which frames each write as an ITM source packet over SWO. */
#define ITM_STIM0_U8 (*(volatile unsigned char *)0xE0000000)

/* MinuteItmCapture's DWT PC-sample emit register: a value written here is
 * framed exactly like a hardware DWT PC-sample packet, feeding the SWO
 * profiler (renode models no DWT of their own). */
#define ITM_EMIT_PC (*(volatile unsigned int *)0xE0000F00)

static void itm_puts(const char *s)
{
    while (*s)
        ITM_STIM0_U8 = (unsigned char)*s++;
}

/* Two functions with a 3:1 sample skew for the profiling test. Each "samples
 * itself": it reports a PC just inside its own body. */
__attribute__((noinline)) static void profiled_hot(void)
{
    ITM_EMIT_PC = (unsigned int)&profiled_hot + 2;
}

__attribute__((noinline)) static void profiled_cold(void)
{
    ITM_EMIT_PC = (unsigned int)&profiled_cold + 2;
}
#endif

static int bump(int base)
{
    counter = counter + base;
    return counter;
}

int main(void)
{
    TESTP_MAGIC = 0xCAFE;

    for (int i = 0; i < limit; i++)
    {
        int total = bump(i + 1);
        printf("tick %d total %d\n", i, total);
    }
    printf("done counter=%d\n", counter);

#ifdef USE_ITM
    itm_puts("swo-hello\n");

    for (;;)
    {
        for (int j = 0; j < 3; j++)
            profiled_hot();
        profiled_cold();
        for (volatile int d = 0; d < 500; d++)
            ;
    }
#endif

    for (;;)
        ;
}
