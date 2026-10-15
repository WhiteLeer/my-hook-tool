
#include "dobby/dobby_internal.h"
#include "core/arch/Cpu.h"

#include <unistd.h>
#include <sys/mman.h>
#include <string.h>

#if !defined(__APPLE__)
#if defined(MYHOOK_STRICT_ENTRY_ONLY)
extern "C" bool MyHookFailCodePatchOnce();
#endif
PUBLIC int DobbyCodePatch(void *address, uint8_t *buffer, uint32_t buffer_size) {
#if defined(MYHOOK_STRICT_ENTRY_ONLY)
  if (MyHookFailCodePatchOnce()) return -1;
#endif
#if defined(__ANDROID__) || defined(__linux__)
  int page_size = (int)sysconf(_SC_PAGESIZE);
  uintptr_t patch_page = ALIGN_FLOOR(address, page_size);
  uintptr_t patch_end_page = ALIGN_FLOOR((uintptr_t)address + buffer_size, page_size);

  // change page permission as rwx
  if (mprotect((void *)patch_page, page_size, PROT_READ | PROT_WRITE | PROT_EXEC) != 0) return -1;
  if (patch_page != patch_end_page) {
    if (mprotect((void *)patch_end_page, page_size, PROT_READ | PROT_WRITE | PROT_EXEC) != 0) return -1;
  }

  // patch buffer
  #if defined(MYHOOK_STRICT_ENTRY_ONLY) && defined(__aarch64__)
  if (buffer_size == 4 && (reinterpret_cast<uintptr_t>(address) & 3) == 0) {
    uint32_t instruction;
    memcpy(&instruction, buffer, sizeof(instruction));
    __atomic_store_n(reinterpret_cast<uint32_t *>(address), instruction, __ATOMIC_RELEASE);
  } else
  #endif
  memcpy(address, buffer, buffer_size);

  // restore page permission
  bool restored_permissions = mprotect((void *)patch_page, page_size, PROT_READ | PROT_EXEC) == 0;
  if (patch_page != patch_end_page) {
    restored_permissions = mprotect((void *)patch_end_page, page_size, PROT_READ | PROT_EXEC) == 0 && restored_permissions;
  }

  addr_t clear_start_ = (addr_t)address;
  ClearCache((void *)clear_start_, (void *)(clear_start_ + buffer_size));
  if (!restored_permissions) return -1;
#endif
  return 0;
}

#endif
