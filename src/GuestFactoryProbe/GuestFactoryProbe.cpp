#include "dobby.h"
#include <algorithm>
#include <array>
#include <atomic>
#include <chrono>
#include <cstring>
#include <dlfcn.h>
#include <elf.h>
#include <fcntl.h>
#include <fstream>
#include <iomanip>
#include <map>
#include <sstream>
#include <signal.h>
#include <string>
#include <thread>
#include <vector>
#include <unistd.h>
#include <sys/syscall.h>

namespace {
constexpr size_t Budget = 128;
struct Sample { uint64_t x0, x1, lr, originalLr, sp, time; pid_t tid; std::atomic<bool> ready{false}; };
std::array<Sample, Budget> samples;
std::atomic<uint64_t> calls{0};
std::atomic<bool> accepted{false};
std::atomic<bool> failCodePatchOnce{false};
extern "C" bool MyHookFailCodePatchOnce() { return failCodePatchOnce.exchange(false); }
struct TestContext { uint64_t x0 = 0, x1 = 0, sp = 0, originalLr = 0; } testContext;
uint64_t original_lr(const DobbyRegisterContext *context) {
    uint64_t lr = 0;
    // Owned Dobby closure stores its incoming LR at originalSP - 8.
    // The local live self-test below must verify this contract before a game patch.
    std::memcpy(&lr, reinterpret_cast<const void *>(context->sp - 8), sizeof(lr));
    return lr;
}
extern "C" __attribute__((naked, noinline)) uint64_t myhook_local_test(uint64_t, uint64_t) {
    asm volatile("str x30, [sp, #-16]!\nadd x0, x0, x1\nldr x30, [sp], #16\nret");
}
extern "C" __attribute__((naked, noinline)) uint64_t myhook_local_call(uint64_t, uint64_t, uint64_t *) {
    asm volatile("stp x19, x30, [sp, #-16]!\nmov x19, x2\nadr x9, 1f\nstr x9, [x19]\n"
        "mov x9, sp\nstr x9, [x19, #16]\nbl myhook_local_test\n1:\nstr x0, [x19, #8]\n"
        "ldp x19, x30, [sp], #16\nret");
}
void test_callback(void *, DobbyRegisterContext *context) {
    testContext = {context->general.regs.x0, context->general.regs.x1, context->sp, original_lr(context)};
}
uint64_t now() {
    timespec time{};
    clock_gettime(CLOCK_MONOTONIC, &time);
    return uint64_t(time.tv_sec) * 1000000000 + time.tv_nsec;
}
std::string hex(const void *data, size_t size) {
    std::ostringstream out;
    out << std::hex << std::setfill('0');
    for (size_t i = 0; i < size; ++i) out << std::setw(2) << unsigned(static_cast<const uint8_t *>(data)[i]);
    return out.str();
}
std::string address(uint64_t value) { std::ostringstream out; out << "0x" << std::hex << value; return out.str(); }
bool read(uintptr_t address, void *buffer, size_t size) {
    const int fd = open("/proc/self/mem", O_RDONLY | O_CLOEXEC);
    if (fd < 0) return false;
    const auto count = pread(fd, buffer, size, off_t(address));
    close(fd);
    return count == ssize_t(size);
}
struct Map { uint64_t begin, end, offset; std::string perms, path; };
std::vector<Map> mappings() {
    std::vector<Map> result;
    std::ifstream file("/proc/self/maps");
    std::string line;
    while (std::getline(file, line)) {
        std::istringstream input(line);
        std::string range, offset, device, inode;
        Map map{};
        if (!(input >> range >> map.perms >> offset >> device >> inode)) continue;
        auto dash = range.find('-');
        if (dash == std::string::npos) continue;
        std::getline(input, map.path);
        map.path.erase(0, map.path.find_first_not_of(" \t"));
        try {
            map.begin = std::stoull(range.substr(0, dash), nullptr, 16);
            map.end = std::stoull(range.substr(dash + 1), nullptr, 16);
            map.offset = std::stoull(offset, nullptr, 16);
        } catch (...) { continue; }
        if (map.end > map.begin) result.push_back(map);
    }
    return result;
}
bool elf(const std::string &path, Elf64_Ehdr &header, std::vector<Elf64_Phdr> &programs, std::string &buildId) {
    std::ifstream file(path, std::ios::binary);
    if (!file.read(reinterpret_cast<char *>(&header), sizeof(header))
        || std::memcmp(header.e_ident, ELFMAG, SELFMAG) || header.e_ident[EI_CLASS] != ELFCLASS64
        || header.e_ident[EI_DATA] != ELFDATA2LSB || header.e_machine != EM_AARCH64
        || header.e_phentsize != sizeof(Elf64_Phdr) || !header.e_phnum || header.e_phnum > 1024) return false;
    programs.resize(header.e_phnum);
    file.seekg(header.e_phoff);
    if (!file.read(reinterpret_cast<char *>(programs.data()), programs.size() * sizeof(Elf64_Phdr))) return false;
    for (const auto &program : programs) {
        if (program.p_type != PT_NOTE || program.p_filesz < 12 || program.p_filesz > 1024 * 1024) continue;
        std::vector<uint8_t> bytes(program.p_filesz);
        file.seekg(program.p_offset);
        if (!file.read(reinterpret_cast<char *>(bytes.data()), bytes.size())) return false;
        size_t cursor = 0;
        while (cursor + sizeof(Elf64_Nhdr) <= bytes.size()) {
            Elf64_Nhdr note{};
            std::memcpy(&note, bytes.data() + cursor, sizeof(note)); cursor += sizeof(note);
            const auto nameSize = (size_t(note.n_namesz) + 3) & ~size_t(3);
            const auto descSize = (size_t(note.n_descsz) + 3) & ~size_t(3);
            if (nameSize > bytes.size() - cursor) break;
            const auto *name = bytes.data() + cursor; cursor += nameSize;
            if (descSize > bytes.size() - cursor) break;
            if (note.n_type == NT_GNU_BUILD_ID && note.n_namesz == 4 && std::memcmp(name, "GNU\0", 4) == 0 && note.n_descsz) {
                buildId = hex(bytes.data() + cursor, note.n_descsz); return true;
            }
            cursor += descSize;
        }
    }
    return false;
}
// The callback neither dereferences arguments nor formats/writes log records.
void callback(void *, DobbyRegisterContext *context) {
    if (!context || !accepted.load(std::memory_order_acquire)) return;
    const auto index = calls.fetch_add(1, std::memory_order_relaxed);
    if (index >= Budget) return;
    auto &sample = samples[index];
    sample.x0 = context->general.regs.x0; sample.x1 = context->general.regs.x1;
    sample.lr = context->lr; sample.sp = context->sp;
    sample.originalLr = original_lr(context);
    sample.time = now(); sample.tid = pid_t(syscall(SYS_gettid));
    sample.ready.store(true, std::memory_order_release);
}
void worker() {
    std::map<std::string, std::string> config;
    std::ifstream file("/data/local/tmp/typetree-ripper.conf");
    std::string line;
    while (std::getline(file, line)) {
        if (!line.empty() && line.back() == '\r') line.pop_back();
        const auto split = line.find('=');
        if (split != std::string::npos) config[line.substr(0, split)] = line.substr(split + 1);
    }
    const auto name = config["events_file"];
    if (name.empty() || name.find('/') != std::string::npos || name.find("..") != std::string::npos || config["capture_session_id"].empty()) return;
    const auto outputPath = config["output_directory"] + "/" + name;
    const int fd = open(outputPath.c_str(), O_WRONLY | O_APPEND | O_CREAT | O_CLOEXEC, 0600);
    if (fd < 0) return;
    uint64_t sequence = 0;
    auto emit = [&](const std::string &kind, const std::string &fields) {
        std::ostringstream output;
        output << "{\"format\":\"my-hook.runtime-event.v1\",\"kind\":" << std::quoted(kind)
            << ",\"producer\":\"GuestFactoryProbe\",\"runtimeArchitecture\":\"arm64\",\"processId\":" << getpid()
            << ",\"threadId\":" << syscall(SYS_gettid) << ",\"eventSequence\":" << sequence++
            << ",\"monotonicTimeNs\":" << now() << ",\"captureSessionId\":" << std::quoted(config["capture_session_id"])
            << fields << "}\n";
        const auto text = output.str();
        return write(fd, text.data(), text.size()) == ssize_t(text.size());
    };
    Dl_info own{};
    Elf64_Ehdr ownHeader{}, loadedHeader{};
    std::vector<Elf64_Phdr> ownPrograms;
    std::string ownBuildId;
    const bool located = dladdr(reinterpret_cast<void *>(callback), &own) != 0;
    const bool diskHeaderRead = located && own.dli_fname && elf(own.dli_fname, ownHeader, ownPrograms, ownBuildId);
    const bool memoryHeaderRead = located && read(reinterpret_cast<uintptr_t>(own.dli_fbase), &loadedHeader, sizeof(loadedHeader));
    std::ostringstream loaded;
    loaded << ",\"probeEnabled\":" << (config["factory_entry_probe"] == "1" ? "true" : "false")
        << ",\"ownMappedPath\":" << std::quoted(located && own.dli_fname ? own.dli_fname : "")
        << ",\"ownBackingBuildId\":" << std::quoted(ownBuildId)
        << ",\"ownLoadedHeaderReadable\":" << (memoryHeaderRead ? "true" : "false")
        << ",\"ownHeadersEqual\":" << (diskHeaderRead && memoryHeaderRead && std::memcmp(&ownHeader, &loadedHeader, sizeof(loadedHeader)) == 0 ? "true" : "false");
    emit("guest-factory-probe-loaded", loaded.str());
    if (config["factory_entry_probe"] != "1") { close(fd); return; }
    if (config["catalog_only"] != "1" || config["disable_log_hook"] != "1"
        || config["observe_transfers"] != "0" || config["enable_native_transfer"] != "0") {
        emit("factory-entry-rejected", ",\"reason\":\"unsafe_type_tree_configuration\""); close(fd); return;
    }
    uint64_t va = 0;
    try { va = std::stoull(config["factory_entry_elf_va"], nullptr, 0); } catch (...) { close(fd); return; }
    const auto expected = config["factory_entry_code_hex"];
    if (!va || va % 4 || expected.size() != 32 || config["factory_entry_build_id"].empty()) { close(fd); return; }
    const bool controlledFault = config["factory_entry_fail_code_write_once"] == "1";
    if (controlledFault) {
        // Let the catalog become available before testing an owned-code failure.
        std::this_thread::sleep_for(std::chrono::seconds(22));
        failCodePatchOnce.store(true);
    }
    dobby_enable_near_branch_trampoline();
    std::array<uint8_t, 32> testBefore{}, testAfter{}, testRestored{};
    const auto testSite = reinterpret_cast<uintptr_t>(myhook_local_test);
    const bool testReadable = read(testSite, testBefore.data(), testBefore.size());
    const int testStatus = testReadable ? DobbyInstrument(reinterpret_cast<void *>(testSite), test_callback) : -1;
    uint32_t testBranch = 0;
    const bool testPatched = read(testSite, testAfter.data(), testAfter.size());
    std::memcpy(&testBranch, testAfter.data(), 4);
    const bool testNearOnly = testStatus == 0 && testPatched && (testBranch & 0xfc000000) == 0x14000000
        && std::equal(testBefore.begin() + 4, testBefore.end(), testAfter.begin() + 4);
    uint64_t control[3]{};
    const auto result = testNearOnly ? myhook_local_call(0x1234, 0xabcd, control) : 0;
    const int destroyStatus = DobbyDestroy(reinterpret_cast<void *>(testSite));
    const bool testBytesRestored = read(testSite, testRestored.data(), testRestored.size()) && testRestored == testBefore;
    const bool testRestore = testBytesRestored && (testStatus == -2 || destroyStatus == 0);
    const bool testPass = testNearOnly && destroyStatus == 0 && testRestore && result == 0xbe01
        && control[1] == result && testContext.x0 == 0x1234 && testContext.x1 == 0xabcd
        && testContext.originalLr == control[0] && testContext.sp == control[2];
    std::ostringstream selfTest;
    selfTest << ",\"passed\":" << (testPass ? "true" : "false") << ",\"dobbyStatus\":" << testStatus
        << ",\"nearOnly\":" << (testNearOnly ? "true" : "false") << ",\"originalRestored\":" << (testRestore ? "true" : "false")
        << ",\"expectedCallerLr\":" << std::quoted(address(control[0])) << ",\"observedCallerLr\":" << std::quoted(address(testContext.originalLr))
        << ",\"expectedSp\":" << std::quoted(address(control[2])) << ",\"observedSp\":" << std::quoted(address(testContext.sp))
        << ",\"returnValue\":" << result << ",\"scope\":\"owned ARM64 stub only; not a game factory call\"";
    selfTest << ",\"controlledCodeWriteFault\":" << (controlledFault ? "true" : "false");
    const bool testLogged = emit("factory-entry-self-test", selfTest.str());
    if (!testPass || !testLogged) {
        emit("factory-entry-rejected", ",\"reason\":\"local_abi_self_test_failed\"");
        close(fd);
        if (!testRestore) kill(getpid(), SIGKILL);
        return;
    }
    uintptr_t site = 0;
    std::array<uint8_t, 32> before{}, after{};
    const auto deadline = now() + 45ULL * 1000000000;
    std::string targetPath;
    while (!site && now() < deadline) {
        const auto maps = mappings();
        for (const auto &map : maps) {
            if (map.offset || !map.path.ends_with("/libunity.so")) continue;
            Elf64_Ehdr header{}; std::vector<Elf64_Phdr> programs; std::string buildId;
            if (!elf(map.path, header, programs, buildId)) continue;
            if (buildId != config["factory_entry_build_id"]) {
                emit("factory-entry-rejected", ",\"reason\":\"module_build_id_mismatch\""); close(fd); return;
            }
            for (const auto &program : programs) {
                if (program.p_type != PT_LOAD || !(program.p_flags & PF_X) || va < program.p_vaddr || va - program.p_vaddr + before.size() > program.p_filesz) continue;
                const auto offset = program.p_offset + va - program.p_vaddr;
                for (const auto &code : maps) {
                    if (code.path != map.path || offset < code.offset || code.perms[0] != 'r' || offset - code.offset + before.size() > code.end - code.begin) continue;
                    const auto candidate = code.begin + offset - code.offset;
                    if (!read(candidate, before.data(), before.size()) || hex(before.data(), 16) != expected) {
                        emit("factory-entry-rejected", ",\"reason\":\"entry_bytes_mismatch\""); close(fd); return;
                    }
                    if (site && site != candidate) {
                        emit("factory-entry-rejected", ",\"reason\":\"ambiguous_entry_mapping\""); close(fd); return;
                    }
                    site = candidate; targetPath = map.path;
                }
            }
        }
        if (!site) std::this_thread::sleep_for(std::chrono::milliseconds(200));
    }
    if (!site) { emit("factory-entry-not-observed", ",\"reason\":\"module_not_ready\""); close(fd); return; }
    const int status = DobbyInstrument(reinterpret_cast<void *>(site), callback);
    uint32_t branch = 0;
    const bool readable = read(site, after.data(), after.size());
    std::memcpy(&branch, after.data(), 4);
    const bool nearOnly = readable && (branch & 0xfc000000) == 0x14000000
        && std::equal(before.begin() + 4, before.end(), after.begin() + 4);
    if (status != 0 || !nearOnly) {
        const int restored = DobbyDestroy(reinterpret_cast<void *>(site));
        std::array<uint8_t, 32> check{};
        const bool exactRestore = restored == 0 && read(site, check.data(), check.size()) && check == before;
        emit("factory-entry-rejected", ",\"reason\":\"patch_not_verified_near_only\",\"dobbyStatus\":" + std::to_string(status)
            + ",\"restoreStatus\":" + std::to_string(restored) + ",\"originalRestored\":" + (exactRestore ? "true" : "false"));
        close(fd);
        if (!exactRestore) kill(getpid(), SIGKILL);
        return;
    }
    std::ostringstream installed;
    installed << ",\"site\":" << std::quoted(address(site)) << ",\"elfVaddr\":" << std::quoted(config["factory_entry_elf_va"])
        << ",\"targetModulePath\":" << std::quoted(targetPath) << ",\"targetBuildId\":" << std::quoted(config["factory_entry_build_id"])
        << ",\"beforeHex\":" << std::quoted(hex(before.data(), before.size()))
        << ",\"afterHex\":" << std::quoted(hex(after.data(), after.size()))
        << ",\"dobbyStatus\":" << status << ",\"verifiedPatchBytes\":4,\"returnProbe\":false";
    if (!emit("factory-entry-installed", installed.str())) { close(fd); kill(getpid(), SIGKILL); return; }
    accepted.store(true, std::memory_order_release);
    size_t flushed = 0;
    std::map<uint64_t, bool> seenCallers;
    while (now() < deadline) {
        while (flushed < Budget && samples[flushed].ready.load(std::memory_order_acquire)) {
            const auto &sample = samples[flushed];
            if (sample.originalLr >= 4 && !seenCallers.contains(sample.originalLr) && seenCallers.size() < 8) {
                seenCallers[sample.originalLr] = true;
                const auto candidate = sample.originalLr - 4;
                const auto maps = mappings();
                const Map *callerMap = nullptr;
                for (const auto &map : maps)
                    if (candidate >= map.begin && candidate < map.end && 16 <= map.end - candidate && map.perms[0] == 'r') callerMap = &map;
                std::ostringstream caller;
                caller << ",\"originalCallerLrRaw\":" << std::quoted(address(sample.originalLr))
                    << ",\"candidateCallAddress\":" << std::quoted(address(candidate))
                    << ",\"scope\":\"code bytes only; LR alone does not exclude a tail-call chain\"";
                std::array<uint8_t, 16> callerLoaded{}, callerDisk{};
                bool verified = false;
                if (callerMap) {
                    const auto offset = callerMap->offset + candidate - callerMap->begin;
                    const bool memoryRead = read(candidate, callerLoaded.data(), callerLoaded.size());
                    std::ifstream callerFile(callerMap->path, std::ios::binary);
                    callerFile.seekg(offset);
                    const bool diskRead = bool(callerFile.read(reinterpret_cast<char *>(callerDisk.data()), callerDisk.size()));
                    Elf64_Ehdr header{}; std::vector<Elf64_Phdr> programs; std::string buildId;
                    if (memoryRead && diskRead && callerLoaded == callerDisk && elf(callerMap->path, header, programs, buildId)) {
                        for (const auto &program : programs)
                            if (program.p_type == PT_LOAD && (program.p_flags & PF_X) && offset >= program.p_offset
                                && offset - program.p_offset + callerLoaded.size() <= program.p_filesz) {
                                caller << ",\"candidateElfVaddr\":" << std::quoted(address(program.p_vaddr + offset - program.p_offset));
                                verified = true;
                            }
                    }
                    uint32_t instruction = 0;
                    std::memcpy(&instruction, callerLoaded.data(), 4);
                    const auto operation = (instruction & 0xfc000000) == 0x94000000 ? "BL"
                        : (instruction & 0xfffffc1f) == 0xd63f0000 ? "BLR" : "other";
                    caller << ",\"mappingPath\":" << std::quoted(callerMap->path) << ",\"mappingBegin\":" << std::quoted(address(callerMap->begin))
                        << ",\"mappingFileOffset\":" << callerMap->offset << ",\"candidateFileOffset\":" << offset
                        << ",\"backingBuildId\":" << std::quoted(buildId) << ",\"instructionClass\":" << std::quoted(operation)
                        << ",\"loadedHex\":" << std::quoted(memoryRead ? hex(callerLoaded.data(), callerLoaded.size()) : "")
                        << ",\"diskHex\":" << std::quoted(diskRead ? hex(callerDisk.data(), callerDisk.size()) : "");
                }
                caller << ",\"codeBytesVerified\":" << (verified ? "true" : "false");
                if (!emit("factory-entry-caller-window", caller.str())) { accepted.store(false); close(fd); kill(getpid(), SIGKILL); return; }
            }
            std::ostringstream fields;
            fields << ",\"status\":\"observed_raw_registers_only\",\"site\":" << std::quoted(address(site))
                << ",\"targetModulePath\":" << std::quoted(targetPath) << ",\"targetBuildId\":" << std::quoted(config["factory_entry_build_id"])
                << ",\"invocationToken\":" << std::quoted(config["capture_session_id"] + ":" + std::to_string(getpid()) + ":entry:" + std::to_string(flushed))
                << ",\"callbackThreadId\":" << sample.tid << ",\"callbackMonotonicTimeNs\":" << sample.time
                << ",\"x0Raw\":" << std::quoted(address(sample.x0)) << ",\"x1Raw\":" << std::quoted(address(sample.x1))
                << ",\"closureLrRaw\":" << std::quoted(address(sample.lr))
                << ",\"originalCallerLrRaw\":" << std::quoted(address(sample.originalLr))
                << ",\"spRaw\":" << std::quoted(address(sample.sp));
            if (!emit("factory-entry-call", fields.str())) { accepted.store(false); close(fd); kill(getpid(), SIGKILL); return; }
            ++flushed;
        }
        if (flushed == Budget) break;
        std::this_thread::sleep_for(std::chrono::milliseconds(100));
    }
    accepted.store(false, std::memory_order_release);
    const bool summaryLogged = emit("factory-entry-summary", ",\"records\":" + std::to_string(flushed) + ",\"calls\":" + std::to_string(calls.load())
        + ",\"dropped\":" + std::to_string(calls.load() > Budget ? calls.load() - Budget : 0)
        + ",\"lifetime\":\"single_process; collection_disabled_after_deadline; no_return_probe\"");
    close(fd);
    if (!summaryLogged) kill(getpid(), SIGKILL);
}
}
__attribute__((constructor)) static void start() {
    std::thread([] { std::this_thread::sleep_for(std::chrono::milliseconds(500)); worker(); }).detach();
}
