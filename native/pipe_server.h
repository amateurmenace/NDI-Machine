#pragma once
#include "win_support.h"
#include <sddl.h>
#include <nlohmann/json.hpp>
#include <functional>
#include <vector>
namespace nm
{
// Versioned length-prefixed JSON, max 64 KiB, local current-user + SYSTEM ACL.
// All IO and JSON allocation runs on the control thread, never media callbacks.
class PipeServer
{
    std::wstring name_;
    PSECURITY_DESCRIPTOR security_{};
    bool transfer(HANDLE pipe, void* bytes, DWORD length, bool write, HANDLE stop)
    {
        DWORD offset{};
        while (offset < length) {
            WinHandle event(CreateEventW(nullptr, TRUE, FALSE, nullptr));
            OVERLAPPED operation{}; operation.hEvent = event;
            DWORD count{};
            const BOOL immediate = write ? WriteFile(pipe, static_cast<char*>(bytes) + offset, length - offset, &count, &operation)
                                         : ReadFile(pipe, static_cast<char*>(bytes) + offset, length - offset, &count, &operation);
            if (!immediate) {
                if (GetLastError() != ERROR_IO_PENDING) return false;
                HANDLE waits[]{stop, event};
                if (WaitForMultipleObjects(2, waits, FALSE, 2500) != WAIT_OBJECT_0 + 1) {
                    CancelIoEx(pipe, &operation); GetOverlappedResult(pipe, &operation, &count, TRUE); return false;
                }
                if (!GetOverlappedResult(pipe, &operation, &count, FALSE)) return false;
            }
            if (!count) return false;
            offset += count;
        }
        return true;
    }
public:
    explicit PipeServer(std::wstring name) : name_(std::move(name))
    {
        HANDLE tokenRaw{};
        if (!OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY, &tokenRaw)) throw std::runtime_error("Cannot query IPC owner");
        WinHandle token(tokenRaw); DWORD needed{};
        GetTokenInformation(token, TokenUser, nullptr, 0, &needed);
        std::vector<BYTE> data(needed);
        if (!GetTokenInformation(token, TokenUser, data.data(), needed, &needed)) throw std::runtime_error("Cannot query IPC user SID");
        LPWSTR sid{};
        if (!ConvertSidToStringSidW(reinterpret_cast<TOKEN_USER*>(data.data())->User.Sid, &sid)) throw std::runtime_error("IPC SID conversion failed");
        std::wstring sddl = L"D:P(A;;GA;;;SY)(A;;GA;;;" + std::wstring(sid) + L")";
        LocalFree(sid);
        if (!ConvertStringSecurityDescriptorToSecurityDescriptorW(sddl.c_str(), SDDL_REVISION_1, &security_, nullptr)) throw std::runtime_error("IPC ACL creation failed");
    }
    ~PipeServer() { if (security_) LocalFree(security_); }
    void serve(HANDLE stop, const std::function<nlohmann::json(const nlohmann::json&)>& handler,
        const std::function<void()>& tick)
    {
        SECURITY_ATTRIBUTES security{sizeof(SECURITY_ATTRIBUTES), security_, FALSE};
        WinHandle pipe(CreateNamedPipeW(name_.c_str(), PIPE_ACCESS_DUPLEX | FILE_FLAG_OVERLAPPED | FILE_FLAG_FIRST_PIPE_INSTANCE,
            PIPE_TYPE_BYTE | PIPE_READMODE_BYTE | PIPE_WAIT | PIPE_REJECT_REMOTE_CLIENTS, 1, 65540, 65540, 2500, &security));
        if (pipe.value == INVALID_HANDLE_VALUE) throw std::runtime_error("Worker pipe creation failed");
        while (WaitForSingleObject(stop, 0) != WAIT_OBJECT_0) {
            tick();
            DisconnectNamedPipe(pipe);
            WinHandle event(CreateEventW(nullptr, TRUE, FALSE, nullptr));
            OVERLAPPED connection{}; connection.hEvent = event;
            bool connected = ConnectNamedPipe(pipe, &connection) != FALSE;
            if (!connected) {
                const auto error = GetLastError();
                if (error == ERROR_PIPE_CONNECTED) connected = true;
                else if (error == ERROR_IO_PENDING) {
                    HANDLE waits[]{stop, event};
                    const auto status = WaitForMultipleObjects(2, waits, FALSE, 250);
                    DWORD ignored{};
                    if (status == WAIT_OBJECT_0 + 1) connected = GetOverlappedResult(pipe, &connection, &ignored, FALSE) != FALSE;
                    else { CancelIoEx(pipe, &connection); GetOverlappedResult(pipe, &connection, &ignored, TRUE); }
                }
            }
            if (!connected) continue;
            uint32_t length{};
            if (!transfer(pipe, &length, sizeof(length), false, stop) || !length || length > 65536) continue;
            std::string input(length, '\0');
            if (!transfer(pipe, input.data(), length, false, stop)) continue;
            nlohmann::json reply, requestId = nullptr;
            try {
                const auto request = nlohmann::json::parse(input);
                if (request.value("schemaVersion", 0) != 1 || !request.contains("id") || !request["id"].is_string()) throw std::runtime_error("IPC schema/id invalid");
                requestId = request["id"];
                reply = handler(request); reply["schemaVersion"] = 1; reply["id"] = request["id"];
            } catch (const std::exception& error) { reply = {{"schemaVersion", 1}, {"id", requestId}, {"ok", false}, {"error", error.what()}}; }
            const auto output = reply.dump();
            length = static_cast<uint32_t>(output.size());
            if (length <= 65536 && transfer(pipe, &length, sizeof(length), true, stop) && transfer(pipe, const_cast<char*>(output.data()), length, true, stop)) {
                // The client sends one receipt byte after reading, then closes.
                // This avoids both discarded replies and unbounded FlushFileBuffers.
                BYTE receipt{}; transfer(pipe, &receipt, 1, false, stop);
            }
            // No FlushFileBuffers: a stalled client must not hold the server.
        }
    }
};
} // namespace nm
