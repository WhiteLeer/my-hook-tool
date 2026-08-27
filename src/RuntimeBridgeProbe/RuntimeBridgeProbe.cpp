#include <windows.h>

#include <fstream>
#include <string>

namespace
{
std::string GetEnvironment(const char *name)
{
    char value[32768]{};
    const DWORD length = GetEnvironmentVariableA(name, value, sizeof(value));
    return length == 0 || length >= sizeof(value) ? std::string{} : std::string(value, length);
}

std::string GetModulePath(HMODULE module)
{
    wchar_t value[MAX_PATH]{};
    const DWORD length = GetModuleFileNameW(module, value, MAX_PATH);
    if (length == 0 || length >= MAX_PATH)
        return {};

    const int bytes = WideCharToMultiByte(CP_UTF8, 0, value, static_cast<int>(length), nullptr, 0, nullptr, nullptr);
    std::string result(bytes, '\0');
    WideCharToMultiByte(CP_UTF8, 0, value, static_cast<int>(length), result.data(), bytes, nullptr, nullptr);
    return result;
}

std::string JsonEscape(const std::string &value)
{
    std::string result;
    result.reserve(value.size());
    for (const char character : value)
    {
        if (character == '\\' || character == '"')
            result.push_back('\\');
        result.push_back(character);
    }
    return result;
}

void WriteProbeRecord(HMODULE module)
{
    const auto eventsPath = GetEnvironment("MY_HOOK_EVENTS");
    if (eventsPath.empty())
        return;

    std::ofstream output(eventsPath, std::ios::out | std::ios::app);
    if (!output)
        return;

    output << "{\"schema\":\"my-hook.runtime-event.v1\","
        << "\"kind\":\"bridge_probe\","
        << "\"source\":{\"layer\":\"windows-host\",\"profile\":\""
        << JsonEscape(GetEnvironment("MY_HOOK_PROFILE")) << "\"},"
        << "\"payload\":{\"processId\":" << GetCurrentProcessId()
        << ",\"modulePath\":\"" << JsonEscape(GetModulePath(module))
        << "\",\"dataStatus\":\"probe_only\"}}\n";
}

DWORD WINAPI ProbeThread(void *parameter)
{
    WriteProbeRecord(static_cast<HMODULE>(parameter));
    return 0;
}
}

BOOL APIENTRY DllMain(HMODULE module, DWORD reason, LPVOID)
{
    if (reason == DLL_PROCESS_ATTACH)
    {
        DisableThreadLibraryCalls(module);
        const auto thread = CreateThread(nullptr, 0, ProbeThread, module, 0, nullptr);
        if (thread != nullptr)
            CloseHandle(thread);
    }
    return TRUE;
}
