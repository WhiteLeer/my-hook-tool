#define _FILE_OFFSET_BITS 64
#include <dirent.h>
#include <fcntl.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/stat.h>
#include <unistd.h>

static uint32_t be32(const unsigned char *p) { return (uint32_t)p[0]<<24|(uint32_t)p[1]<<16|(uint32_t)p[2]<<8|p[3]; }
static uint64_t be64(const unsigned char *p) { return (uint64_t)be32(p)<<32|be32(p+4); }
static void write_num(uint64_t n,int bytes) { for(int i=0;i<bytes;i++) putchar((int)((n>>(i*8))&255)); }

// Filesystem-only reader. Never opens a game process or writes game resources.
int main(int argc,char **argv) {
    if(argc!=2) return 2;
    DIR *dir=opendir(argv[1]); if(!dir) { perror("opendir"); return 3; }
    fwrite("BHD1",1,4,stdout);
    struct dirent *entry; unsigned files=0,records=0,errors=0;
    uint64_t total_headers=0;
    while((entry=readdir(dir))) {
        size_t len=strlen(entry->d_name);
        if(len!=38||strcmp(entry->d_name+32,".block")) continue;
        if(++files>4096) return 4;
        int fd=openat(dirfd(dir),entry->d_name,O_RDONLY|O_CLOEXEC); if(fd<0) { errors++;continue; }
        struct stat st; if(fstat(fd,&st)) { close(fd);errors++;continue; }
        uint64_t offset=0;
        while(offset<(uint64_t)st.st_size) {
            unsigned char h[25];
            if(pread(fd,h,25,(off_t)offset)!=25||memcmp(h,"ENCR\0",5)) {errors++;break;}
            uint64_t size=be64(h+5); uint32_t compressed=be32(h+13),uncompressed=be32(h+17),flags=be32(h+21);
            if(size<25||size>(uint64_t)st.st_size-offset||compressed>262144||uncompressed>1048576||compressed>size-25) {errors++;break;}
            uint64_t start=(flags&128)?offset+size-compressed:offset+25;
            unsigned char *info=malloc(compressed?compressed:1);
            if(!info||pread(fd,info,compressed,(off_t)start)!=(ssize_t)compressed) {free(info);errors++;break;}
            if(++records>1000000||(total_headers+=compressed)>256ull*1024*1024) {free(info);close(fd);return 5;}
            write_num(len,2);fwrite(entry->d_name,1,len,stdout);
            write_num(offset,8);write_num(size,8);write_num(flags,4);write_num(uncompressed,4);write_num(compressed,4);
            fwrite(info,1,compressed,stdout);free(info);offset+=size;
        }
        close(fd);
    }
    closedir(dir);
    // exec-out may merge remote stderr into stdout. Keep diagnostics binary-framed.
    write_num(0,2);write_num(files,4);write_num(records,4);write_num(errors,4);write_num(total_headers,8);
    return ferror(stdout)?6:0;
}
