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

struct ScopedEvent
{
    HANDLE handle = nullptr;

    explicit ScopedEvent(HANDLE value) : handle(value) {}

    ~ScopedEvent()
    {
        if (handle != nullptr)
        {
            CloseHandle(handle);
        }
    }

    ScopedEvent(const ScopedEvent&) = delete;
    ScopedEvent& operator=(const ScopedEvent&) = delete;
};

bool ReadExactOverlapped(HANDLE pipe, HANDLE stopEvent, BYTE* buffer, DWORD bytes)
{
    ScopedEvent ioEvent(CreateEventW(nullptr, TRUE, FALSE, nullptr));
    if (ioEvent.handle == nullptr)
    {
        return false;
    }

    DWORD total = 0;
    while (total < bytes)
    {
        ResetEvent(ioEvent.handle);
        OVERLAPPED ov{};
        ov.hEvent = ioEvent.handle;
        DWORD read = 0;
        const BOOL ok = ReadFile(pipe, buffer + total, bytes - total, &read, &ov);
        if (!ok)
        {
            if (GetLastError() != ERROR_IO_PENDING)
            {
                return false;
            }
            HANDLE waits[2] = { ioEvent.handle, stopEvent };
            const DWORD waitCount = stopEvent != nullptr ? 2u : 1u;
            const DWORD waited = WaitForMultipleObjects(waitCount, waits, FALSE, INFINITE);
            if (waited != WAIT_OBJECT_0)
            {
                CancelIoEx(pipe, &ov);
                GetOverlappedResult(pipe, &ov, &read, TRUE);
                return false;
            }
            if (!GetOverlappedResult(pipe, &ov, &read, FALSE))
            {
                return false;
            }
        }
        if (read == 0)
        {
            return false;
        }
        total += read;
    }
    return true;
}

bool WriteExactOverlapped(HANDLE pipe, HANDLE stopEvent, const BYTE* buffer, DWORD bytes, DWORD timeoutMs)
{
    ScopedEvent ioEvent(CreateEventW(nullptr, TRUE, FALSE, nullptr));
    if (ioEvent.handle == nullptr)
    {
        return false;
    }

    DWORD total = 0;
    while (total < bytes)
    {
        ResetEvent(ioEvent.handle);
        OVERLAPPED ov{};
        ov.hEvent = ioEvent.handle;
        DWORD written = 0;
        const BOOL ok = WriteFile(pipe, buffer + total, bytes - total, &written, &ov);
        if (!ok)
        {
            if (GetLastError() != ERROR_IO_PENDING)
            {
                return false;
            }
            HANDLE waits[2] = { ioEvent.handle, stopEvent };
            const DWORD waitCount = stopEvent != nullptr ? 2u : 1u;
            const DWORD waited = WaitForMultipleObjects(waitCount, waits, FALSE, timeoutMs);
            if (waited != WAIT_OBJECT_0)
            {
                CancelIoEx(pipe, &ov);
                if (GetOverlappedResult(pipe, &ov, &written, TRUE) && written == bytes - total)
                {
                    return true;
                }
                return false;
            }
            if (!GetOverlappedResult(pipe, &ov, &written, FALSE))
            {
                return false;
            }
        }
        if (written == 0)
        {
            return false;
        }
        total += written;
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
    HANDLE pipe = CurrentPipe();
    if (pipe != INVALID_HANDLE_VALUE)
    {
        CancelIoEx(pipe, nullptr);
    }
    if (thread_ != nullptr)
    {
        WaitForSingleObject(thread_, 10000);
        CloseHandle(thread_);
        thread_ = nullptr;
    }
    ClosePipe();
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

bool PipeClient::IsGenerationActive(std::uint64_t generation) const
{
    std::lock_guard lock(mutex_);
    return pipe_ != INVALID_HANDLE_VALUE && generation != 0 && generation_ == generation;
}

bool PipeClient::Report(
    vestigo::protocol::ProviderStatus status,
    int win32Error,
    const std::wstring& message,
    std::uint64_t generation)
{
    return WriteFrame(vestigo::protocol::MessageType::Status, EncodeStatus(status, win32Error, message), generation);
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
        FILE_FLAG_OVERLAPPED | SECURITY_SQOS_PRESENT | SECURITY_IDENTIFICATION,
        nullptr);
    if (pipe == INVALID_HANDLE_VALUE)
    {
        return false;
    }

    if (!PublishPipe(pipe))
    {
        CloseHandle(pipe);
        return false;
    }
    if (!WriteFrame(vestigo::protocol::MessageType::Hello, EncodeHello(scenario_, sids_)))
    {
        ClosePipe();
        return false;
    }

    bool dispatchedOnConnection = false;
    while (WaitForSingleObject(stop_, 0) != WAIT_OBJECT_0)
    {
        vestigo::protocol::MessageType type{};
        std::vector<BYTE> payload;
        if (!ReadFrame(pipe, type, payload))
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
            dispatch_.generation = generation_;
            hasDispatch_ = true;
        }
        dispatchedOnConnection = true;
        if (callback_ != nullptr)
        {
            callback_(callbackContext_);
        }
    }

    ClosePipe();
    if (dispatchedOnConnection && callback_ != nullptr &&
        (stop_ == nullptr || WaitForSingleObject(stop_, 0) != WAIT_OBJECT_0))
    {
        callback_(callbackContext_);
    }
    return false;
}

bool PipeClient::PublishPipe(HANDLE pipe)
{
    std::lock_guard lock(mutex_);
    if (stop_ != nullptr && WaitForSingleObject(stop_, 0) == WAIT_OBJECT_0)
    {
        return false;
    }
    generation_++;
    pipe_ = pipe;
    return true;
}

HANDLE PipeClient::TakePipe()
{
    std::lock_guard lock(mutex_);
    HANDLE pipe = pipe_;
    pipe_ = INVALID_HANDLE_VALUE;
    SecureZeroMemory(dispatch_.password.data(), dispatch_.password.size() * sizeof(wchar_t));
    dispatch_ = {};
    hasDispatch_ = false;
    return pipe;
}

HANDLE PipeClient::CurrentPipe()
{
    std::lock_guard lock(mutex_);
    return pipe_;
}

void PipeClient::ClosePipe()
{
    HANDLE pipe = TakePipe();
    if (pipe == INVALID_HANDLE_VALUE)
    {
        return;
    }
    CancelIoEx(pipe, nullptr);
    {
        std::lock_guard writeLock(writeMutex_);
    }
    CloseHandle(pipe);
}

bool PipeClient::WriteFrame(
    vestigo::protocol::MessageType type,
    const std::vector<BYTE>& payload,
    std::uint64_t requiredGeneration)
{
    if (payload.size() > static_cast<size_t>(vestigo::protocol::kMaxPayload))
    {
        return false;
    }

    std::lock_guard writeLock(writeMutex_);
    HANDLE pipe = INVALID_HANDLE_VALUE;
    {
        std::lock_guard lock(mutex_);
        if (requiredGeneration != 0 && requiredGeneration != generation_)
        {
            return false;
        }

        pipe = pipe_;
    }

    if (pipe == INVALID_HANDLE_VALUE)
    {
        return false;
    }

    std::vector<BYTE> frame(vestigo::protocol::kHeaderSize + payload.size());
    frame[0] = static_cast<BYTE>(vestigo::protocol::kMagic);
    frame[1] = static_cast<BYTE>(vestigo::protocol::kMagic >> 8);
    frame[2] = static_cast<BYTE>(vestigo::protocol::kMagic >> 16);
    frame[3] = static_cast<BYTE>(vestigo::protocol::kMagic >> 24);
    frame[4] = static_cast<BYTE>(vestigo::protocol::kVersion);
    frame[5] = static_cast<BYTE>(vestigo::protocol::kVersion >> 8);
    const auto message = static_cast<std::uint16_t>(type);
    frame[6] = static_cast<BYTE>(message);
    frame[7] = static_cast<BYTE>(message >> 8);
    const auto length = static_cast<std::int32_t>(payload.size());
    frame[8] = static_cast<BYTE>(length);
    frame[9] = static_cast<BYTE>(length >> 8);
    frame[10] = static_cast<BYTE>(length >> 16);
    frame[11] = static_cast<BYTE>(length >> 24);
    if (!payload.empty())
    {
        std::memcpy(frame.data() + vestigo::protocol::kHeaderSize, payload.data(), payload.size());
    }

    return WriteExactOverlapped(pipe, stop_, frame.data(), static_cast<DWORD>(frame.size()), 5000);
}

bool PipeClient::ReadFrame(HANDLE pipe, vestigo::protocol::MessageType& type, std::vector<BYTE>& payload)
{
    BYTE header[vestigo::protocol::kHeaderSize]{};
    if (pipe == INVALID_HANDLE_VALUE || !ReadExactOverlapped(pipe, stop_, header, sizeof(header)))
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
    return length == 0 || ReadExactOverlapped(pipe, stop_, payload.data(), static_cast<DWORD>(length));
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
