/*
 ============================================================================
 Name        : hev-logger.c
 Author      : hev <r@hev.cc>
 Copyright   : Copyright (c) 2019 - 2023 hev
 Description : Logger
 ============================================================================
 */

#include <time.h>
#include <fcntl.h>
#include <stdio.h>
#include <stdarg.h>
#include <string.h>
#include <unistd.h>
#include <sys/uio.h>
#include <sys/stat.h>

#include "hev-logger.h"

static int fd = -1;
static int active;
static HevLoggerLevel req_level;

#if defined(__MINGW32__) && !defined(__MSYS__)
/* Under MinGW the fd-based path cannot work: close()/writev() are socket
 * redirects (hev-mingw.h). Instead honour "stderr"/"stdout" by writing
 * straight to the console via Win32, or open a real file via CreateFile. */
#include <windows.h>

static HANDLE hev_logger_win_handle = INVALID_HANDLE_VALUE;
static int hev_logger_win_is_file = 0;

static void
hev_logger_win_write (const char *buf, int len)
{
    DWORD written;
    if (hev_logger_win_handle != INVALID_HANDLE_VALUE)
        WriteFile (hev_logger_win_handle, buf, (DWORD)len, &written, NULL);
}
#endif

int
hev_logger_init (HevLoggerLevel level, const char *path)
{
    req_level = level;

#if defined(__MINGW32__) && !defined(__MSYS__)
    if (!path || !path[0])
        return 0;

    if (0 == strcmp (path, "stderr")) {
        hev_logger_win_handle = GetStdHandle (STD_ERROR_HANDLE);
        fd = -2;
        active = 1;
        return 0;
    }
    if (0 == strcmp (path, "stdout")) {
        hev_logger_win_handle = GetStdHandle (STD_OUTPUT_HANDLE);
        fd = -2;
        active = 1;
        return 0;
    }

    /* Open regular file for logging on Windows */
    hev_logger_win_handle = CreateFileA (path, FILE_APPEND_DATA,
                                        FILE_SHARE_READ | FILE_SHARE_WRITE,
                                        NULL, OPEN_ALWAYS,
                                        FILE_ATTRIBUTE_NORMAL, NULL);
    if (hev_logger_win_handle != INVALID_HANDLE_VALUE) {
        hev_logger_win_is_file = 1;
        fd = -2;
        active = 1;
        return 0;
    }
    return 0;
#else
    if (0 == strcmp (path, "stdout"))
        fd = dup (1);
    else if (0 == strcmp (path, "stderr"))
        fd = dup (2);
    else
        fd = open (path, O_WRONLY | O_APPEND | O_CREAT, 0640);

    if (fd < 0)
        return -1;

    active = 1;
    return 0;
#endif
}

void
hev_logger_fini (void)
{
#if defined(__MINGW32__) && !defined(__MSYS__)
    if (hev_logger_win_is_file && hev_logger_win_handle != INVALID_HANDLE_VALUE) {
        CloseHandle (hev_logger_win_handle);
        hev_logger_win_handle = INVALID_HANDLE_VALUE;
        hev_logger_win_is_file = 0;
    }
#endif
    close (fd);
}

int
hev_logger_enabled (HevLoggerLevel level)
{
    if (level >= req_level && active)
        return 1;

    return 0;
}

void
hev_logger_log (HevLoggerLevel level, const char *fmt, ...)
{
    struct iovec iov[4];
    const char *ts_fmt;
    char msg[1024];
    struct tm *ti;
    char ts[32];
    time_t now;
    va_list ap;
    int len;

    if (level < req_level || !active)
        return;

    time (&now);
    ti = localtime (&now);

    ts_fmt = "[%04u-%02u-%02u %02u:%02u:%02u] ";
    len = snprintf (ts, sizeof (ts), ts_fmt, 1900 + ti->tm_year, 1 + ti->tm_mon,
                    ti->tm_mday, ti->tm_hour, ti->tm_min, ti->tm_sec);

    iov[0].iov_base = ts;
    iov[0].iov_len = len;

    switch (level) {
    case HEV_LOGGER_DEBUG:
        iov[1].iov_base = "[D] ";
        break;
    case HEV_LOGGER_INFO:
        iov[1].iov_base = "[I] ";
        break;
    case HEV_LOGGER_WARN:
        iov[1].iov_base = "[W] ";
        break;
    case HEV_LOGGER_ERROR:
        iov[1].iov_base = "[E] ";
        break;
    case HEV_LOGGER_UNSET:
        iov[1].iov_base = "[?] ";
        break;
    }
    iov[1].iov_len = 4;

    va_start (ap, fmt);
    iov[2].iov_base = msg;
    iov[2].iov_len = vsnprintf (msg, 1024, fmt, ap);
    va_end (ap);

    iov[3].iov_base = "\n";
    iov[3].iov_len = 1;

#if defined(__MINGW32__) && !defined(__MSYS__)
    if (fd == -2) {
        /* fd-based writev() is a socket redirect under MinGW; write the
         * already-formatted pieces straight to the console handle instead. */
        char line[1200];
        int n;

        n = snprintf (line, sizeof (line), "%.*s%.*s%.*s%.*s",
                      (int)iov[0].iov_len, (const char *)iov[0].iov_base,
                      (int)iov[1].iov_len, (const char *)iov[1].iov_base,
                      (int)iov[2].iov_len, (const char *)iov[2].iov_base,
                      (int)iov[3].iov_len, (const char *)iov[3].iov_base);
        if (n > 0)
            hev_logger_win_write (line, n < (int)sizeof (line) ? n
                                                               : (int)sizeof (line) - 1);
        return;
    }
#endif

    if (writev (fd, iov, 4)) {
        /* ignore return value */
    }
}
