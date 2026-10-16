#ifndef _HEV_COMPAT_SYS_IOCTL_H
#define _HEV_COMPAT_SYS_IOCTL_H
/* FIONBIO for socket ioctls; the ioctl() calls themselves are redirected to
 * hev_mw_ioctl (hev-mingw.h), so no ioctl() declaration is provided here on
 * purpose. */
#include <winsock2.h>
#endif
