#ifndef _HEV_COMPAT_SYS_UIO_H
#define _HEV_COMPAT_SYS_UIO_H
/* struct iovec for readv/writev; MinGW has no sys/uio.h. */
#include <stddef.h>
#if !defined(_IOVEC_DEFINED) && !defined(__IOVEC_DEFINED)
#define _IOVEC_DEFINED
struct iovec {
    void *iov_base;
    size_t iov_len;
};
#endif
#endif
