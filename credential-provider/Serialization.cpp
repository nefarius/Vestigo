#include "Serialization.h"

#include <sspi.h>

#include <cstring>
#include <vector>

namespace {

void AppendUnicodeString(KERB_INTERACTIVE_LOGON* logon, UNICODE_STRING* field, wchar_t*& cursor, const std::wstring& value)
{
    field->Buffer = cursor;
    field->Length = static_cast<USHORT>(value.size() * sizeof(wchar_t));
    field->MaximumLength = field->Length;
    if (!value.empty())
    {
        std::memcpy(cursor, value.data(), field->Length);
        cursor += value.size();
    }
}

}  // namespace

HRESULT PackInteractiveLogon(
    CREDENTIAL_PROVIDER_USAGE_SCENARIO scenario,
    const std::wstring& domain,
    const std::wstring& userName,
    const std::wstring& password,
    BYTE** serialization,
    DWORD* serializationSize)
{
    if (serialization == nullptr || serializationSize == nullptr)
    {
        return E_INVALIDARG;
    }

    *serialization = nullptr;
    *serializationSize = 0;

    const size_t extra = (domain.size() + userName.size() + password.size()) * sizeof(wchar_t);
    const size_t bytes = sizeof(KERB_INTERACTIVE_UNLOCK_LOGON) + extra;
    auto* packed = static_cast<KERB_INTERACTIVE_UNLOCK_LOGON*>(CoTaskMemAlloc(bytes));
    if (packed == nullptr)
    {
        return E_OUTOFMEMORY;
    }

    std::memset(packed, 0, bytes);
    packed->LogonId.LowPart = 0;
    packed->LogonId.HighPart = 0;
    packed->Logon.MessageType = scenario == CPUS_UNLOCK_WORKSTATION
        ? KerbWorkstationUnlockLogon
        : KerbInteractiveLogon;

    auto* cursor = reinterpret_cast<wchar_t*>(packed + 1);
    AppendUnicodeString(&packed->Logon, &packed->Logon.LogonDomainName, cursor, domain);
    AppendUnicodeString(&packed->Logon, &packed->Logon.UserName, cursor, userName);
    AppendUnicodeString(&packed->Logon, &packed->Logon.Password, cursor, password);

    packed->Logon.LogonDomainName.Buffer = reinterpret_cast<PWSTR>(
        reinterpret_cast<BYTE*>(packed->Logon.LogonDomainName.Buffer) - reinterpret_cast<BYTE*>(packed));
    packed->Logon.UserName.Buffer = reinterpret_cast<PWSTR>(
        reinterpret_cast<BYTE*>(packed->Logon.UserName.Buffer) - reinterpret_cast<BYTE*>(packed));
    packed->Logon.Password.Buffer = reinterpret_cast<PWSTR>(
        reinterpret_cast<BYTE*>(packed->Logon.Password.Buffer) - reinterpret_cast<BYTE*>(packed));

    *serialization = reinterpret_cast<BYTE*>(packed);
    *serializationSize = static_cast<DWORD>(bytes);
    return S_OK;
}

HRESULT RetrieveNegotiateAuthPackage(ULONG* authPackage)
{
    if (authPackage == nullptr)
    {
        return E_INVALIDARG;
    }

    *authPackage = 0;
    HANDLE lsa = nullptr;
    NTSTATUS status = LsaConnectUntrusted(&lsa);
    if (status != 0)
    {
        return HRESULT_FROM_NT(status);
    }

    LSA_STRING name{};
    name.Buffer = const_cast<PCHAR>(NEGOSSP_NAME_A);
    name.Length = static_cast<USHORT>(strlen(NEGOSSP_NAME_A));
    name.MaximumLength = static_cast<USHORT>(name.Length + 1);
    ULONG package = 0;
    status = LsaLookupAuthenticationPackage(lsa, &name, &package);
    LsaDeregisterLogonProcess(lsa);
    if (status != 0)
    {
        return HRESULT_FROM_NT(status);
    }

    *authPackage = package;
    return S_OK;
}
