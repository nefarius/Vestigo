#pragma once

#include <windows.h>
#include <wincred.h>
#include <ntsecapi.h>

#include <string>

HRESULT PackInteractiveLogon(
    CREDENTIAL_PROVIDER_USAGE_SCENARIO scenario,
    const std::wstring& domain,
    const std::wstring& userName,
    const std::wstring& password,
    BYTE** serialization,
    DWORD* serializationSize);

HRESULT RetrieveNegotiateAuthPackage(ULONG* authPackage);
