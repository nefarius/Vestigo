#include "PipeClient.h"

#include <process.h>
#include <wtsapi32.h>

#include <cstring>

namespace {

void WriteU32(std::vector<BYTE>& buffer, std::uint32_t value)
{
    buffer.push_back(static_cast<BYTE>(value));
    buffer.push_back(static_cast<BYTE>(value >> 8));
    buffer.push_back(static_cast<BYTE>(value >> 16));
    buffer.push_back(static_cast<BYTE>(value >> 24));
}

void WriteU16(std::vector<BYTE>& buffer, std::uint16_t value)
{
    buffer.push_back(static_cast<BYTE>(value));
    buffer.push_back(static_cast<BYTE>(value >> 8));
}

bool ReadU32(const BYTE*& cursor, const BYTE* end, std::uint32_t& value)
{
    if (cursor + 4 > end)
    {
        return false;
    }
    value = static_cast<std::uint32_t>(cursor[0] | (cursor[1] << 8) | (cursor[2] << 16) | (cursor[3] << 24));
    cursor += 4;
    return true;
}

bool ReadU16(const BYTE*& cursor, const BYTE* end, std::uint16_t& value)
{
    if (cursor + 2 > end)
    {
        return false;
    }
    value = static_cast<std::uint16_t>(cursor[0] | (cursor[1] << 8));
    cursor += 2;
    return true;
}

bool ReadExact(HANDLE pipe, BYTE* buffer, DWORD bytes)
{
    DWORD total = 0;
    while (total < bytes)
    {
        DWORD read = 0;
        if (!ReadFile(pipe, buffer + total, bytes - total, &read, nullptr) || read == 0)
        {
            return false;
        }
        total += read;
    }
    return true;
}

unsigned __stdcall ThreadProc(void* context)
{
    static_cast<PipeClient*>(context)->Worker();
    return 0;
}

}  // namespace

PipeClient::PipeClient() = default;

PipeClient::~PipeClient()
{
    Stop();
}

void PipeClient::Start(
    CREDENTIAL_PROVIDER_USAGE_SCENARIO scenario,
    const std::vector<std::wstring>& userSids,
    DispatchCallback callback,
    void* context)
{
    Stop();
    scenario_ = scenario;
    sids_ = userSids;
    callback_ = callback;
    callbackContext_ = context;
    stop_ = CreateEventW(nullptr, TRUE, FALSE, nullptr);
    if (stop_ == nullptr)
    {
        return;
    }
    unsigned id = 0;
    thread_ = reinterpret_cast<HANDLE>(_beginthreadex(nullptr, 0, &ThreadProc, this, 0, &id));
}

void PipeClient::Stop()
{
    if (stop_ != nullptr)
    {
        SetEvent(stop_);
    }
    if (pipe_ != INVALID_HANDLE_VALUE)
    {
        CancelIoEx(pipe_, nullptr);
        CloseHandle(pipe_);
        pipe_ = INVALID_HANDLE_VALUE;
    }
    if (thread_ != nullptr)
    {
        WaitForSingleObject(thread_, INFINITE);
        CloseHandle(thread_);
        thread_ = nullptr;
    }
    if (stop_ != nullptr)
    {
        CloseHandle(stop_);
        stop_ = nullptr;
    }
    std::lock_guard lock(mutex_);
    SecureZeroMemory(dispatch_.password.data(), dispatch_.password.size() * sizeof(wchar_t));
    dispatch_ = {};
    hasDispatch_ = false;
}

bool PipeClient::TryTakeDispatch(DispatchRequest& request)
{
    std::lock_guard lock(mutex_);
    if (!hasDispatch_)
    {
        return false;
    }
    request = std::move(dispatch_);
    dispatch_ = {};
    hasDispatch_ = false;
    return true;
}

void PipeClient::Report(vestigo::protocol::ProviderStatus status, int win32Error, const std::wstring& message)
{
    if (pipe_ == INVALID_HANDLE_VALUE)
    {
        return;
    }
    WriteFrame(vestigo::protocol::MessageType::Status, EncodeStatus(status, win32Error, message));
}

void PipeClient::Worker()
{
    while (stop_ != nullptr && WaitForSingleObject(stop_, 0) != WAIT_OBJECT_0)
    {
        if (ConnectAndRun())
        {
            WaitForSingleObject(stop_, INFINITE);
            return;
        }
        if (WaitForSingleObject(stop_, 1000) == WAIT_OBJECT_0)
        {
            return;
        }
    }
}

bool PipeClient::ConnectAndRun()
{
    HANDLE pipe = CreateFileW(
        vestigo::protocol::kPipeName,
        GENERIC_READ | GENERIC_WRITE,
        0,
        nullptr,
        OPEN_EXISTING,
        FILE_ATTRIBUTE_NORMAL | SECURITY_SQOS_PRESENT | SECURITY_IDENTIFICATION,
        nullptr);
    if (pipe == INVALID_HANDLE_VALUE)
    {
        return false;
    }

    pipe_ = pipe;
    if (!WriteFrame(vestigo::protocol::MessageType::Hello, EncodeHello(scenario_, sids_)))
    {
        CloseHandle(pipe);
        pipe_ = INVALID_HANDLE_VALUE;
        return false;
    }

    while (WaitForSingleObject(stop_, 0) != WAIT_OBJECT_0)
    {
        vestigo::protocol::MessageType type{};
        std::vector<BYTE> payload;
        if (!ReadFrame(type, payload))
        {
            break;
        }
        if (type == vestigo::protocol::MessageType::Cancel || type == vestigo::protocol::MessageType::Goodbye)
        {
            break;
        }
        if (type != vestigo::protocol::MessageType::Dispatch)
        {
            continue;
        }

        DispatchRequest request;
        if (!DecodeDispatch(payload, request))
        {
            SecureZeroMemory(payload.data(), payload.size());
            continue;
        }
        SecureZeroMemory(payload.data(), payload.size());
        {
            std::lock_guard lock(mutex_);
            SecureZeroMemory(dispatch_.password.data(), dispatch_.password.size() * sizeof(wchar_t));
            dispatch_ = std::move(request);
            hasDispatch_ = true;
        }
        if (callback_ != nullptr)
        {
            callback_(callbackContext_);
        }
    }

    if (pipe_ == pipe)
    {
        CloseHandle(pipe);
        pipe_ = INVALID_HANDLE_VALUE;
    }
    return false;
}

bool PipeClient::WriteFrame(vestigo::protocol::MessageType type, const std::vector<BYTE>& payload)
{
    if (pipe_ == INVALID_HANDLE_VALUE || payload.size() > vestigo::protocol::kMaxPayload)
    {
        return false;
    }
    BYTE header[vestigo::protocol::kHeaderSize]{};
    header[0] = static_cast<BYTE>(vestigo::protocol::kMagic);
    header[1] = static_cast<BYTE>(vestigo::protocol::kMagic >> 8);
    header[2] = static_cast<BYTE>(vestigo::protocol::kMagic >> 16);
    header[3] = static_cast<BYTE>(vestigo::protocol::kMagic >> 24);
    header[4] = static_cast<BYTE>(vestigo::protocol::kVersion);
    header[5] = static_cast<BYTE>(vestigo::protocol::kVersion >> 8);
    const auto message = static_cast<std::uint16_t>(type);
    header[6] = static_cast<BYTE>(message);
    header[7] = static_cast<BYTE>(message >> 8);
    const auto length = static_cast<std::int32_t>(payload.size());
    header[8] = static_cast<BYTE>(length);
    header[9] = static_cast<BYTE>(length >> 8);
    header[10] = static_cast<BYTE>(length >> 16);
    header[11] = static_cast<BYTE>(length >> 24);

    DWORD written = 0;
    if (!WriteFile(pipe_, header, sizeof(header), &written, nullptr) || written != sizeof(header))
    {
        return false;
    }
    if (!payload.empty() &&
        (!WriteFile(pipe_, payload.data(), static_cast<DWORD>(payload.size()), &written, nullptr) ||
         written != payload.size()))
    {
        return false;
    }
    return FlushFileBuffers(pipe_) != FALSE;
}

bool PipeClient::ReadFrame(vestigo::protocol::MessageType& type, std::vector<BYTE>& payload)
{
    BYTE header[vestigo::protocol::kHeaderSize]{};
    if (!ReadExact(pipe_, header, sizeof(header)))
    {
        return false;
    }
    const std::uint32_t magic = static_cast<std::uint32_t>(
        header[0] | (header[1] << 8) | (header[2] << 16) | (header[3] << 24));
    const std::uint16_t version = static_cast<std::uint16_t>(header[4] | (header[5] << 8));
    type = static_cast<vestigo::protocol::MessageType>(header[6] | (header[7] << 8));
    const std::int32_t length = header[8] | (header[9] << 8) | (header[10] << 16) | (header[11] << 24);
    if (magic != vestigo::protocol::kMagic || version != vestigo::protocol::kVersion ||
        length < 0 || length > vestigo::protocol::kMaxPayload)
    {
        return false;
    }
    payload.assign(static_cast<size_t>(length), 0);
    return length == 0 || ReadExact(pipe_, payload.data(), static_cast<DWORD>(length));
}

std::vector<BYTE> PipeClient::EncodeHello(
    CREDENTIAL_PROVIDER_USAGE_SCENARIO scenario,
    const std::vector<std::wstring>& sids)
{
    std::vector<BYTE> buffer;
    const auto mapped = scenario == CPUS_UNLOCK_WORKSTATION
        ? vestigo::protocol::UsageScenario::Unlock
        : vestigo::protocol::UsageScenario::Logon;
    WriteU32(buffer, static_cast<std::uint32_t>(mapped));
    WriteU32(buffer, WTSGetActiveConsoleSessionId());
    WriteU32(buffer, static_cast<std::uint32_t>(sids.size()));
    for (const std::wstring& sid : sids)
    {
        WriteUtf16(buffer, sid);
    }
    return buffer;
}

bool PipeClient::DecodeDispatch(const std::vector<BYTE>& payload, DispatchRequest& request)
{
    const BYTE* cursor = payload.data();
    const BYTE* end = cursor + payload.size();
    return ReadUtf16(cursor, end, request.userName) &&
           ReadUtf16(cursor, end, request.domain) &&
           ReadUtf16(cursor, end, request.password) &&
           ReadUtf16(cursor, end, request.targetSid);
}

std::vector<BYTE> PipeClient::EncodeStatus(
    vestigo::protocol::ProviderStatus status,
    int win32Error,
    const std::wstring& message)
{
    std::vector<BYTE> buffer;
    WriteU32(buffer, static_cast<std::uint32_t>(status));
    WriteU32(buffer, static_cast<std::uint32_t>(win32Error));
    WriteUtf16(buffer, message);
    return buffer;
}

void PipeClient::WriteUtf16(std::vector<BYTE>& buffer, const std::wstring& value)
{
    const auto length = static_cast<std::uint16_t>(value.size() > vestigo::protocol::kMaxString
        ? vestigo::protocol::kMaxString
        : value.size());
    WriteU16(buffer, length);
    for (std::uint16_t index = 0; index < length; ++index)
    {
        WriteU16(buffer, static_cast<std::uint16_t>(value[index]));
    }
}

bool PipeClient::ReadUtf16(const BYTE*& cursor, const BYTE* end, std::wstring& value)
{
    std::uint16_t length = 0;
    if (!ReadU16(cursor, end, length) || length > vestigo::protocol::kMaxString)
    {
        return false;
    }
    value.clear();
    value.reserve(length);
    for (std::uint16_t index = 0; index < length; ++index)
    {
        std::uint16_t ch = 0;
        if (!ReadU16(cursor, end, ch))
        {
            return false;
        }
        value.push_back(static_cast<wchar_t>(ch));
    }
    return true;
}
