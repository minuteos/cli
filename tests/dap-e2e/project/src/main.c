#include <stdio.h>

volatile int counter;
int limit = 5;

static int bump(int base)
{
    counter = counter + base;
    return counter;
}

int main(void)
{
    for (int i = 0; i < limit; i++)
    {
        int total = bump(i + 1);
        printf("tick %d total %d\n", i, total);
    }
    printf("done counter=%d\n", counter);
    for (;;)
        ;
}
