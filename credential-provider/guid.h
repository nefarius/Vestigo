#pragma once

#include <guiddef.h>

// Keep in sync with Vestigo.SystemInfo.CredentialProviderIdentity.
// CLSID {A3F1C8E2-4D7B-4E19-9C6A-8B2F1D5E7A90}
static constexpr CLSID CLSID_VestigoCredentialProvider = {
    0xa3f1c8e2, 0x4d7b, 0x4e19, {0x9c, 0x6a, 0x8b, 0x2f, 0x1d, 0x5e, 0x7a, 0x90}};

static constexpr wchar_t kClsidString[] = L"{A3F1C8E2-4D7B-4E19-9C6A-8B2F1D5E7A90}";
static constexpr wchar_t kProviderName[] = L"Vestigo Credential Provider";
