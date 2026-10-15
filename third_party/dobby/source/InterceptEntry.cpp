#include "InterceptEntry.h"
#include "Interceptor.h"

InterceptEntry::InterceptEntry(InterceptEntryType type, addr_t address) {
  this->type = type;

#if defined(TARGET_ARCH_ARM)
  if (address % 2) {
    address -= 1;
    this->thumb_mode = true;
  } else {
    this->thumb_mode = false;
  }
#endif

  this->patched_addr = address;
  this->routing = nullptr;
  this->patched_size = 0;
  this->relocated_addr = 0;
  this->relocated_size = 0;
  this->origin_insn_size = 0;
  this->id = Interceptor::SharedInstance()->count();
}
