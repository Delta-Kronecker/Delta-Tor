/*
  ============================================================================
  Name        : hev-mingw-setjmp-x86_64.s
  Description : Coroutine-safe _setjmp/_longjmp for MinGW-w64 (x86_64).

  hev-task-system switches coroutines with _setjmp/_longjmp: the scheduler
  setjmps on the kernel stack, each task setjmps on its own heap-allocated
  stack, and longjmp bounces between them. That requires a setjmp/longjmp
  that does NOT validate the destination frame is an ancestor on the current
  stack.

  Cygwin/newlib's _setjmp/_longjmp are exactly that (plain register
  save/restore), so upstream works under MSYS. MSVCRT's longjmp, however,
  walks the SEH/stack chain and rejects a jump onto a different stack --
  which shows up as an access violation (0xC0000005) the first time the
  scheduler resumes a task. So under MinGW we supply our own, matching the
  Win64 ABI callee-saved set (RBX, RBP, RSI, RDI, R12-R15, XMM6-XMM15, MXCSR,
  x87 control word) plus RSP/RIP. No unwinding, no stack validation.

  Layout must match hev_mw_jmp_buf in src/misc/hev-mingw.h:
    0x00 rbx  0x08 rbp  0x10 rsi  0x18 rdi
    0x20 r12  0x28 r13  0x30 r14  0x38 r15
    0x40 rsp  0x48 rip
    0x50 xmm6 .. 0xe0 xmm15   (10 x 16 bytes)
    0xf0 mxcsr (4)  0xf4 x87 cw (2)
  ============================================================================
  */

#if defined(__x86_64__) && defined(__MINGW32__) && !defined(__MSYS__)

    .text

    .globl hev_mw_setjmp
    .def hev_mw_setjmp; .scl 2; .type 32; .endef
    .p2align 4
hev_mw_setjmp:
    movq  %rbx,  0x00(%rcx)
    movq  %rbp,  0x08(%rcx)
    movq  %rsi,  0x10(%rcx)
    movq  %rdi,  0x18(%rcx)
    movq  %r12,  0x20(%rcx)
    movq  %r13,  0x28(%rcx)
    movq  %r14,  0x30(%rcx)
    movq  %r15,  0x38(%rcx)
    leaq  0x08(%rsp), %rdx          /* rsp after returning past retaddr */
    movq  %rdx,  0x40(%rcx)
    movq  0x00(%rsp), %rdx          /* return address */
    movq  %rdx,  0x48(%rcx)
    movups %xmm6,  0x50(%rcx)
    movups %xmm7,  0x60(%rcx)
    movups %xmm8,  0x70(%rcx)
    movups %xmm9,  0x80(%rcx)
    movups %xmm10, 0x90(%rcx)
    movups %xmm11, 0xa0(%rcx)
    movups %xmm12, 0xb0(%rcx)
    movups %xmm13, 0xc0(%rcx)
    movups %xmm14, 0xd0(%rcx)
    movups %xmm15, 0xe0(%rcx)
    stmxcsr 0xf0(%rcx)
    fnstcw 0xf4(%rcx)
    xorl  %eax, %eax
    retq

    .globl hev_mw_longjmp
    .def hev_mw_longjmp; .scl 2; .type 32; .endef
    .p2align 4
hev_mw_longjmp:
    movl  %edx, %eax                /* setjmp/longjmp return value */
    testl %eax, %eax
    jnz   1f
    movl  $1, %eax                  /* longjmp(buf, 0) returns 1 */
1:
    movups 0x50(%rcx), %xmm6
    movups 0x60(%rcx), %xmm7
    movups 0x70(%rcx), %xmm8
    movups 0x80(%rcx), %xmm9
    movups 0x90(%rcx), %xmm10
    movups 0xa0(%rcx), %xmm11
    movups 0xb0(%rcx), %xmm12
    movups 0xc0(%rcx), %xmm13
    movups 0xd0(%rcx), %xmm14
    movups 0xe0(%rcx), %xmm15
    ldmxcsr 0xf0(%rcx)
    fldcw  0xf4(%rcx)
    movq  0x00(%rcx), %rbx
    movq  0x08(%rcx), %rbp
    movq  0x10(%rcx), %rsi
    movq  0x18(%rcx), %rdi
    movq  0x20(%rcx), %r12
    movq  0x28(%rcx), %r13
    movq  0x30(%rcx), %r14
    movq  0x38(%rcx), %r15
    movq  0x48(%rcx), %rdx          /* rip */
    movq  0x40(%rcx), %rsp          /* switch stack last */
    jmpq  *%rdx

#endif /* __x86_64__ && __MINGW32__ && !__MSYS__ */
