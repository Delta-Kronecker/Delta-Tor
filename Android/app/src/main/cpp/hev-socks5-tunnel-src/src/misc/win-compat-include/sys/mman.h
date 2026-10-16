#ifndef _HEV_COMPAT_SYS_MMAN_H
#define _HEV_COMPAT_SYS_MMAN_H
/* MinGW has no mmap. The only in-tree user (task stack backend) is switched
 * to the heap backend under MinGW (hev-task-system/configs.mk), so nothing
 * needs these definitions - this stub only satisfies the unguarded
 * #include <sys/mman.h> at the top of hev-task-stack-mmap.c, whose body is
 * compiled out on this toolchain. */
#define PROT_NONE  0x0
#define PROT_READ  0x1
#define PROT_WRITE 0x2
#define PROT_EXEC  0x4
#define MAP_FAILED ((void *)-1)
#define MAP_FIXED  0x10
#define MAP_PRIVATE 0x02
#define MAP_ANON   0x20
#define MAP_ANONYMOUS MAP_ANON
#define MAP_STACK  0x0
#define MS_ASYNC 0x1
#define MS_SYNC 0x4
#define MADV_DONTNEED 4
#endif
