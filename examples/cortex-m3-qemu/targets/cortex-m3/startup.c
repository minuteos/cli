#include <stdint.h>

extern uint32_t __data_load;
extern uint32_t __data_start;
extern uint32_t __data_end;
extern uint32_t __bss_start;
extern uint32_t __bss_end;
extern uint32_t __stack_top;

extern int main(void);
void _exit(int code);

static void Default_Handler(void) { while (1) { } }

void Reset_Handler(void);

/* Full Cortex-M vector table. A short table is dangerous: any stray exception
   (SysTick, IRQ) would fetch a vector from beyond the table - i.e. from code -
   and branch into garbage, corrupting memory mid-run. Every slot points at a
   trap so unexpected exceptions halt instead. */
__attribute__((section(".isr_vector"), used))
void (* const g_vectors[])(void) = {
    (void (*)(void))&__stack_top, /* 0: initial stack pointer */
    Reset_Handler,                /* 1: reset */
    Default_Handler,              /* 2: NMI */
    Default_Handler,              /* 3: HardFault */
    Default_Handler,              /* 4: MemManage */
    Default_Handler,              /* 5: BusFault */
    Default_Handler,              /* 6: UsageFault */
    0, 0, 0, 0,                   /* 7-10: reserved */
    Default_Handler,              /* 11: SVCall */
    Default_Handler,              /* 12: Debug Monitor */
    0,                            /* 13: reserved */
    Default_Handler,              /* 14: PendSV */
    Default_Handler,              /* 15: SysTick */
    /* 16+: external IRQs (LM3S has dozens; provide a generous block) */
    Default_Handler, Default_Handler, Default_Handler, Default_Handler,
    Default_Handler, Default_Handler, Default_Handler, Default_Handler,
    Default_Handler, Default_Handler, Default_Handler, Default_Handler,
    Default_Handler, Default_Handler, Default_Handler, Default_Handler,
    Default_Handler, Default_Handler, Default_Handler, Default_Handler,
    Default_Handler, Default_Handler, Default_Handler, Default_Handler,
    Default_Handler, Default_Handler, Default_Handler, Default_Handler,
    Default_Handler, Default_Handler, Default_Handler, Default_Handler,
    Default_Handler, Default_Handler, Default_Handler, Default_Handler,
    Default_Handler, Default_Handler, Default_Handler, Default_Handler,
};

/* Referenced only from the naked Reset_Handler's inline asm, so the compiler
   cannot see the use - keep it. */
__attribute__((used))
void _start_c(void)
{
    /* copy initialized data from FLASH to RAM */
    uint32_t *src = &__data_load;
    for (uint32_t *dst = &__data_start; dst < &__data_end; ) *dst++ = *src++;

    /* zero bss */
    for (uint32_t *b = &__bss_start; b < &__bss_end; b++) *b = 0;

    /* No .init_array needed: TEST_CASE descriptors are placed in the
       test_cases section at link time and walked directly by main. */
    _exit(main());
}

/* Set the stack pointer explicitly, then enter the C runtime. */
__attribute__((naked, noreturn, used))
void Reset_Handler(void)
{
    __asm volatile(
        "cpsid i\n"            /* mask interrupts during/after startup */
        "ldr sp, =__stack_top\n"
        "bl  _start_c\n"
        "b   .\n");
}
