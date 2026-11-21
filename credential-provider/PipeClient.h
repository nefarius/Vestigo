#pragma once

#include "Platform.h"
#include "Protocol.h"

#include <credentialprovider.h>

#include <mutex>
#include <string>
#include <vector>

struct DispatchRequest
{
    std::wstring userName;
    std::wstring domain;
    std::wstring password;
    std::wstring targetSid;
    std::uint64_t generation = 0;
};

class PipeClient
{
public:
    PipeClient();
    ~PipeClient();

    PipeClient(const PipeClient&) = delete;
    PipeClient& operator=(const PipeClient&) = delete;

    using DispatchCallback = void (*)(void* context);

    void Start(
        CREDENTIAL_PROVIDER_USAGE_SCENARIO scenario,
        const std::vector<std::wstring>& userSids,
        DispatchCallback callback,
        void* context);
    void Stop();
    bool TryTakeDispatch(DispatchRequest& request);
    void Report(
        vestigo::protocol::ProviderStatus status,
        int win32Error,
        const std::wstring& message,
        std::uint64_t generation);
    void Worker();

private:
    bool ConnectAndRun();
    bool PublishPipe(HANDLE pipe);
    HANDLE TakePipe();
    HANDLE CurrentPipe();
    void ClosePipe();
    bool WriteFrame(
        vestigo::protocol::MessageType type,
        const std::vector<BYTE>& payload,
        std::uint64_t requiredGeneration = 0);
    bool ReadFrame(HANDLE pipe, vestigo::protocol::MessageType& type, std::vector<BYTE>& payload);
    static std::vector<BYTE> EncodeHello(CREDENTIAL_PROVIDER_USAGE_SCENARIO scenario, const std::vector<std::wstring>& sids);
    static bool DecodeDispatch(const std::vector<BYTE>& payload, DispatchRequest& request);
    static std::vector<BYTE> EncodeStatus(vestigo::protocol::ProviderStatus status, int win32Error, const std::wstring& message);
    static void WriteUtf16(std::vector<BYTE>& buffer, const std::wstring& value);
    static bool ReadUtf16(const BYTE*& cursor, const BYTE* end, std::wstring& value);

    HANDLE thread_ = nullptr;
    HANDLE stop_ = nullptr;
    HANDLE pipe_ = INVALID_HANDLE_VALUE;
    CREDENTIAL_PROVIDER_USAGE_SCENARIO scenario_ = CPUS_INVALID;
    std::vector<std::wstring> sids_;
    DispatchCallback callback_ = nullptr;
    void* callbackContext_ = nullptr;
    std::mutex writeMutex_;
    std::mutex mutex_;
    std::uint64_t generation_ = 0;
    bool hasDispatch_ = false;
    DispatchRequest dispatch_{};
};
