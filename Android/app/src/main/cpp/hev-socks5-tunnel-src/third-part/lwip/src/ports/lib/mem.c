/*
 ============================================================================
 Name        : mem.c
 Author      : hev <r@hev.cc>
 Copyright   : Copyright (c) 2021 hev
 Description : Memory
 ============================================================================
 */

#include <stdlib.h>

/* These are weak fallbacks so the real allocator (hev-task-system) can
 * override them. MinGW GCC supports __attribute__((weak)) on PE/COFF too;
 * the old MSVC-oriented "#ifdef _WIN32 -> no weak" made them strong, which
 * collides with hev-task-system's definitions once both objects are linked
 * (the Windows DLL link uses --whole-archive to pull the API objects in). */
#define WEAK __attribute__((weak))

WEAK void
hev_free (void *ptr)
{
    free (ptr);
}

WEAK void *
hev_malloc (size_t size)
{
    return malloc (size);
}

WEAK void *
hev_calloc (size_t nmemb, size_t size)
{
    return calloc (nmemb, size);
}
