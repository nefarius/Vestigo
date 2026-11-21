#include "Credential.h"

#include "guid.h"
#include "PipeClient.h"
#include "Serialization.h"

#include <shlwapi.h>

#include <cstring>

extern "C" void DllAddRef();
extern "C" void DllRelease();

namespace {

HRESULT CopyString(const std::wstring& value, LPWSTR* dest)
{
    if (dest == nullptr)
    {
        return E_INVALIDARG;
    }
    return SHStrDupW(value.c_str(), dest);
}

}  // namespace

VestigoCredential::VestigoCredential()
{
    DllAddRef();
}

VestigoCredential::~VestigoCredential()
{
    SecureZeroMemory(password_.data(), password_.size() * sizeof(wchar_t));
    DllRelease();
}

void VestigoCredential::Initialize(
    CREDENTIAL_PROVIDER_USAGE_SCENARIO scenario,
    const std::wstring& userName,
    const std::wstring& domain,
    std::wstring password,
    const std::wstring& userSid,
    PipeClient* pipe,
    std::uint64_t generation)
{
    scenario_ = scenario;
    userName_ = userName;
    domain_ = domain;
    SecureZeroMemory(password_.data(), password_.size() * sizeof(wchar_t));
    password_ = std::move(password);
    userSid_ = userSid;
    pipe_ = pipe;
    generation_ = generation;
    consumed_ = false;
}

IFACEMETHODIMP VestigoCredential::QueryInterface(REFIID riid, void** ppv)
{
    if (ppv == nullptr)
    {
        return E_POINTER;
    }
    if (riid == IID_IUnknown || riid == IID_ICredentialProviderCredential)
    {
        *ppv = static_cast<ICredentialProviderCredential*>(this);
    }
    else if (riid == IID_ICredentialProviderCredential2)
    {
        *ppv = static_cast<ICredentialProviderCredential2*>(this);
    }
    else
    {
        *ppv = nullptr;
        return E_NOINTERFACE;
    }
    AddRef();
    return S_OK;
}

IFACEMETHODIMP_(ULONG) VestigoCredential::AddRef()
{
    return InterlockedIncrement(&refs_);
}

IFACEMETHODIMP_(ULONG) VestigoCredential::Release()
{
    const LONG value = InterlockedDecrement(&refs_);
    if (value == 0)
    {
        delete this;
    }
    return static_cast<ULONG>(value);
}

IFACEMETHODIMP VestigoCredential::Advise(ICredentialProviderCredentialEvents*)
{
    return S_OK;
}

IFACEMETHODIMP VestigoCredential::UnAdvise()
{
    return S_OK;
}

IFACEMETHODIMP VestigoCredential::SetSelected(BOOL* autoLogon)
{
    if (autoLogon == nullptr)
    {
        return E_INVALIDARG;
    }
    *autoLogon = TRUE;
    return S_OK;
}

IFACEMETHODIMP VestigoCredential::SetDeselected()
{
    return S_OK;
}

IFACEMETHODIMP VestigoCredential::GetFieldState(
    DWORD field,
    CREDENTIAL_PROVIDER_FIELD_STATE* state,
    CREDENTIAL_PROVIDER_FIELD_INTERACTIVE_STATE* interactive)
{
    if (state == nullptr || interactive == nullptr)
    {
        return E_INVALIDARG;
    }
    if (field != 0)
    {
        return E_INVALIDARG;
    }
    *state = CPFS_DISPLAY_IN_BOTH;
    *interactive = CPFIS_NONE;
    return S_OK;
}

IFACEMETHODIMP VestigoCredential::GetStringValue(DWORD field, LPWSTR* value)
{
    if (field != 0)
    {
        return E_INVALIDARG;
    }
    return CopyString(L"Vestigo", value);
}

IFACEMETHODIMP VestigoCredential::GetBitmapValue(DWORD, HBITMAP* bitmap)
{
    if (bitmap == nullptr)
    {
        return E_INVALIDARG;
    }
    *bitmap = nullptr;
    return E_NOTIMPL;
}

IFACEMETHODIMP VestigoCredential::GetCheckboxValue(DWORD, BOOL*, LPWSTR*)
{
    return E_NOTIMPL;
}

IFACEMETHODIMP VestigoCredential::GetSubmitButtonValue(DWORD, DWORD*)
{
    return E_NOTIMPL;
}

IFACEMETHODIMP VestigoCredential::GetComboBoxValueCount(DWORD, DWORD*, DWORD*)
{
    return E_NOTIMPL;
}

IFACEMETHODIMP VestigoCredential::GetComboBoxValueAt(DWORD, DWORD, LPWSTR*)
{
    return E_NOTIMPL;
}

IFACEMETHODIMP VestigoCredential::SetStringValue(DWORD, LPCWSTR)
{
    return E_NOTIMPL;
}

IFACEMETHODIMP VestigoCredential::SetCheckboxValue(DWORD, BOOL)
{
    return E_NOTIMPL;
}

IFACEMETHODIMP VestigoCredential::SetComboBoxSelectedValue(DWORD, DWORD)
{
    return E_NOTIMPL;
}

IFACEMETHODIMP VestigoCredential::CommandLinkClicked(DWORD)
{
    return E_NOTIMPL;
}

IFACEMETHODIMP VestigoCredential::GetSerialization(
    CREDENTIAL_PROVIDER_GET_SERIALIZATION_RESPONSE* response,
    CREDENTIAL_PROVIDER_CREDENTIAL_SERIALIZATION* serialization,
    LPWSTR* optionalStatusText,
    CREDENTIAL_PROVIDER_STATUS_ICON* optionalStatusIcon)
{
    if (response == nullptr || serialization == nullptr)
    {
        return E_INVALIDARG;
    }
    if (optionalStatusText != nullptr)
    {
        *optionalStatusText = nullptr;
    }
    if (optionalStatusIcon != nullptr)
    {
        *optionalStatusIcon = CPSI_NONE;
    }

    std::memset(serialization, 0, sizeof(*serialization));
    consumed_ = true;
    HRESULT hr = PackInteractiveLogon(
        scenario_, domain_, userName_, password_, &serialization->rgbSerialization, &serialization->cbSerialization);
    SecureZeroMemory(password_.data(), password_.size() * sizeof(wchar_t));
    password_.clear();
    if (FAILED(hr))
    {
        *response = CPGSR_NO_CREDENTIAL_NOT_FINISHED;
        if (pipe_ != nullptr)
        {
            pipe_->Report(vestigo::protocol::ProviderStatus::Failed, HRESULT_CODE(hr), L"pack_failed", generation_);
        }
        return hr;
    }

    ULONG authPackage = 0;
    hr = RetrieveNegotiateAuthPackage(&authPackage);
    if (FAILED(hr))
    {
        CoTaskMemFree(serialization->rgbSerialization);
        serialization->rgbSerialization = nullptr;
        serialization->cbSerialization = 0;
        *response = CPGSR_NO_CREDENTIAL_NOT_FINISHED;
        if (pipe_ != nullptr)
        {
            pipe_->Report(vestigo::protocol::ProviderStatus::Failed, HRESULT_CODE(hr), L"auth_package", generation_);
        }
        return hr;
    }

    serialization->ulAuthenticationPackage = authPackage;
    serialization->clsidCredentialProvider = CLSID_VestigoCredentialProvider;
    *response = CPGSR_RETURN_CREDENTIAL_FINISHED;
    if (pipe_ != nullptr)
    {
        pipe_->Report(vestigo::protocol::ProviderStatus::Submitted, 0, L"", generation_);
    }
    return S_OK;
}

IFACEMETHODIMP VestigoCredential::ReportResult(
    NTSTATUS status,
    NTSTATUS,
    LPWSTR* optionalStatusText,
    CREDENTIAL_PROVIDER_STATUS_ICON* optionalStatusIcon)
{
    lastStatus_ = status;
    if (optionalStatusText != nullptr)
    {
        *optionalStatusText = nullptr;
    }
    if (optionalStatusIcon != nullptr)
    {
        *optionalStatusIcon = NT_SUCCESS(status) ? CPSI_SUCCESS : CPSI_ERROR;
    }
    return S_OK;
}

IFACEMETHODIMP VestigoCredential::GetUserSid(LPWSTR* sid)
{
    if (userSid_.empty())
    {
        if (sid != nullptr)
        {
            *sid = nullptr;
        }
        return S_FALSE;
    }
    return CopyString(userSid_, sid);
}
