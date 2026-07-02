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

static void itm_puts(const char *s)
{
    while (*s)
        ITM_STIM0_U8 = (unsigned char)*s++;
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
#endif

    for (;;)
        ;
}
