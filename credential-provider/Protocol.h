#pragma once

#include <cstdint>

// Keep in sync with Vestigo.SystemInfo.CredentialProviderProtocol.
namespace vestigo::protocol {

inline constexpr wchar_t kPipeName[] = L"\\\\.\\pipe\\Vestigo.CredentialProvider.v1";
inline constexpr std::uint32_t kMagic = 0x314C5456;
inline constexpr std::uint16_t kVersion = 1;
inline constexpr int kHeaderSize = 12;
inline constexpr int kMaxPayload = 16 * 1024;
inline constexpr int kMaxString = 2048;

enum class MessageType : std::uint16_t
{
    Hello = 1,
    Dispatch = 2,
    Status = 3,
    Cancel = 4,
    Goodbye = 5
};

enum class ProviderStatus : std::uint32_t
{
    Connected = 0,
    Submitted = 1,
    Succeeded = 2,
    Failed = 3,
    Cancelled = 4
};

enum class UsageScenario : std::uint32_t
{
    Logon = 1,
    Unlock = 2
};

}  // namespace vestigo::protocol
