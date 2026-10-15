#include "renderdoc_replay.h"

#include <windows.h>

#include <charconv>
#include <chrono>
#include <filesystem>
#include <iostream>
#include <optional>
#include <exception>
#include <string>

void RENDERDOC_OutOfMemory(uint64_t)
{
    std::terminate();
}

namespace
{
struct Options
{
    std::filesystem::path renderDocDll;
    std::filesystem::path captureFile;
    uint32_t frames = 1;
    uint32_t waitSeconds = 120;
    std::optional<uint32_t> pid;
    std::optional<uint32_t> ident;
    std::string target;
    bool list = false;
    bool queryOnly = false;
};

void PrintUsage()
{
    std::cout
        << "my-hook-renderdoc-trigger --renderdoc-dll <renderdoc.dll> "
           "[--ident <port> | --pid <pid> | --target <name>] [--frames <n>] "
           "[--capture-file <path>] [--wait-seconds <n>] [--query-only | --list]\n"
        << "  通过 RenderDoc TargetControl 查询目标，或向已经注入的目标触发捕获。\n"
        << "  不枚举或猜测 Unity 资源身份；默认触发 1 帧。\n";
}

bool ParseUInt(const std::string &text, uint32_t &value)
{
    const auto *begin = text.data();
    const auto *end = begin + text.size();
    const auto result = std::from_chars(begin, end, value);
    return result.ec == std::errc{} && result.ptr == end;
}

std::optional<Options> ParseOptions(int argc, char **argv)
{
    Options result;
    for (int index = 1; index < argc; index++)
    {
        const std::string argument = argv[index];
        if (argument == "--help" || argument == "-h")
        {
            PrintUsage();
            return std::nullopt;
        }
        if (argument == "--query-only")
        {
            result.queryOnly = true;
            continue;
        }
        if (argument == "--list")
        {
            result.list = true;
            continue;
        }
        if (index + 1 >= argc)
        {
            std::cerr << "缺少参数值: " << argument << '\n';
            return std::nullopt;
        }
        const std::string value = argv[++index];
        if (argument == "--renderdoc-dll")
            result.renderDocDll = value;
        else if (argument == "--pid")
        {
            uint32_t pid = 0;
            if (!ParseUInt(value, pid) || pid == 0)
            {
                std::cerr << "--pid 必须是正整数\n";
                return std::nullopt;
            }
            result.pid = pid;
        }
        else if (argument == "--ident")
        {
            uint32_t ident = 0;
            if (!ParseUInt(value, ident) || ident == 0)
            {
                std::cerr << "--ident 必须是正整数\n";
                return std::nullopt;
            }
            result.ident = ident;
        }
        else if (argument == "--target")
            result.target = value;
        else if (argument == "--frames")
        {
            uint32_t frames = 0;
            if (!ParseUInt(value, frames) || frames == 0)
            {
                std::cerr << "--frames 必须是正整数\n";
                return std::nullopt;
            }
            result.frames = frames;
        }
        else if (argument == "--capture-file")
            result.captureFile = value;
        else if (argument == "--wait-seconds")
        {
            uint32_t waitSeconds = 0;
            if (!ParseUInt(value, waitSeconds) || waitSeconds == 0)
            {
                std::cerr << "--wait-seconds 必须是正整数\n";
                return std::nullopt;
            }
            result.waitSeconds = waitSeconds;
        }
        else
        {
            std::cerr << "未知参数: " << argument << '\n';
            return std::nullopt;
        }
    }

    if (result.renderDocDll.empty() || !std::filesystem::is_regular_file(result.renderDocDll))
    {
        std::cerr << "必须指定存在的 --renderdoc-dll\n";
        return std::nullopt;
    }
    if (!result.list && result.queryOnly && result.captureFile.empty())
    {
        // Query mode still requires a target selector, validated below.
    }
    const auto selectors = (result.ident.has_value() ? 1 : 0) +
        (result.pid.has_value() ? 1 : 0) + (!result.target.empty() ? 1 : 0);
    if (!result.list && selectors == 0)
    {
        std::cerr << "必须指定 --ident、--pid 或 --target\n";
        return std::nullopt;
    }
    if (!result.list && selectors > 1)
    {
        std::cerr << "--ident、--pid、--target 必须且只能指定一个\n";
        return std::nullopt;
    }
    return result;
}

int Run(const Options &options)
{
    const auto renderDocDirectory = options.renderDocDll.parent_path();
    const auto previousDirectory = std::filesystem::current_path();
    std::filesystem::current_path(renderDocDirectory);

    uint32_t nextIdent = 0;
    if (options.list)
    {
        while ((nextIdent = RENDERDOC_EnumerateRemoteTargets("", nextIdent)) != 0)
        {
            auto *candidate = RENDERDOC_CreateTargetControl("", nextIdent, "my-hook-tool", false);
            if (!candidate)
                continue;
            std::cout << "ident=" << nextIdent
                      << " pid=" << candidate->GetPID()
                      << " target=" << candidate->GetTarget().c_str()
                      << " api=" << candidate->GetAPI().c_str() << '\n';
            candidate->Shutdown();
        }
        std::filesystem::current_path(previousDirectory);
        return 0;
    }
    ITargetControl *connection = nullptr;
    uint32_t selectedIdent = 0;
    while (true)
    {
        if (options.ident.has_value())
            nextIdent = *options.ident;
        else
            nextIdent = RENDERDOC_EnumerateRemoteTargets("", nextIdent);
        if (nextIdent == 0)
            break;

        auto *candidate = RENDERDOC_CreateTargetControl("", nextIdent, "my-hook-tool", false);
        if (candidate == nullptr)
        {
            if (options.ident.has_value())
                break;
            continue;
        }

        const auto candidatePid = candidate->GetPID();
        const std::string candidateTarget = candidate->GetTarget().c_str();
        const bool identMatches = options.ident.has_value();
        const bool pidMatches = options.pid.has_value() && candidatePid == *options.pid;
        const bool nameMatches = !options.target.empty() && candidateTarget.find(options.target) != std::string::npos;
        if (identMatches || pidMatches || nameMatches)
        {
            connection = candidate;
            selectedIdent = nextIdent;
            std::cout << "连接 RenderDoc 目标 ident=" << selectedIdent
                      << " pid=" << candidatePid << " target=" << candidateTarget
                      << " api=" << connection->GetAPI().c_str() << '\n';
            break;
        }
        candidate->Shutdown();
    }

    if (connection == nullptr)
    {
        std::cerr << "没有找到匹配的 RenderDoc 目标\n";
        std::filesystem::current_path(previousDirectory);
        return 3;
    }

    if (options.queryOnly)
        std::cout << "target-pid=" << connection->GetPID() << '\n';
    else
    {
        connection->TriggerCapture(options.frames);
        std::cout << "已请求捕获帧数: " << options.frames << '\n';

        const auto deadline = std::chrono::steady_clock::now() +
            std::chrono::seconds(options.waitSeconds);
        uint32_t captures = 0;
        while (std::chrono::steady_clock::now() < deadline && connection->Connected())
        {
            const auto message = connection->ReceiveMessage(nullptr);
            if (message.type != TargetControlMessageType::NewCapture)
                continue;

            captures++;
            const auto source = std::filesystem::path(message.newCapture.path.c_str());
            std::cout << "捕获完成 captureId=" << message.newCapture.captureId
                      << " frame=" << message.newCapture.frameNumber
                      << " size=" << message.newCapture.byteSize
                      << " path=" << source.string() << '\n';

            if (!options.captureFile.empty())
            {
                std::error_code error;
                std::filesystem::create_directories(options.captureFile.parent_path(), error);
                if (error)
                {
                    std::cerr << "无法创建捕获目录: " << error.message() << '\n';
                    connection->Shutdown();
                    std::filesystem::current_path(previousDirectory);
                    return 5;
                }

                if (message.newCapture.local)
                {
                    std::filesystem::copy_file(
                        source, options.captureFile,
                        std::filesystem::copy_options::overwrite_existing, error);
                    if (error)
                    {
                        std::cerr << "无法复制本地捕获文件: " << error.message() << '\n';
                        connection->Shutdown();
                        std::filesystem::current_path(previousDirectory);
                        return 6;
                    }
                }
                else
                {
                    connection->CopyCapture(message.newCapture.captureId,
                                            options.captureFile.string().c_str());
                }

                std::cout << "捕获文件已写入: " << options.captureFile.string() << '\n';
            }

            if (captures >= options.frames)
                break;
        }

        if (captures < options.frames)
        {
            std::cerr << "等待捕获完成超时，已收到 " << captures << "/"
                      << options.frames << " 条 NewCapture 消息\n";
            connection->Shutdown();
            std::filesystem::current_path(previousDirectory);
            return 7;
        }
    }
    connection->Shutdown();
    std::filesystem::current_path(previousDirectory);
    return 0;
}

int Finish(int code)
{
    std::cout.flush();
    std::cerr.flush();
    // RenderDoc installs capture hooks from DllMain; bypass CRT teardown for this one-shot client.
    TerminateProcess(GetCurrentProcess(), static_cast<UINT>(code));
    return code;
}
}

int main(int argc, char **argv)
{
    const auto options = ParseOptions(argc, argv);
    if (!options.has_value())
        return Finish(argc > 1 && (std::string(argv[1]) == "--help" || std::string(argv[1]) == "-h") ? 0 : 2);
    return Finish(Run(*options));
}
