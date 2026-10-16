/*
  ============================================================================
  Name        : hev-mingw-init.c
  Description : Winsock bootstrap for the MinGW build.

  The MSYS/Cygwin runtime auto-initializes winsock, but a plain MinGW DLL
  must call WSAStartup() before any socket API. The tunnel opens its first
  socket from a worker thread (event_task_init's socketpair), where a lazy
  init would be racy; a DLL-load constructor runs once, single-threaded,
  before any user code. WSAStartup is refcounted and lives for the process,
  so no WSACleanup is paired here.
  ============================================================================
 */

#if defined(__MINGW32__) && !defined(__MSYS__)

#include <winsock2.h>

__attribute__((constructor)) static void
hev_mw_wsa_startup (void)
{
    WSADATA data;
    WSAStartup (MAKEWORD (2, 2), &data);
}

#endif /* defined(__MINGW32__) && !defined(__MSYS__) */
